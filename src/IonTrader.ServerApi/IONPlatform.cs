using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IonTrader
{
    public class IONPlatform : IDisposable
    {
        private const int ReconnectDelayMs = 4000;
        private const int ResponseTimeoutMs = 30000;
        private const int AutoSubscribeDelayMs = 500;
        private static readonly bool SocketKeepAlive = true;
        private static readonly bool SocketNoDelay = true;

        private readonly string _url, _name, _token, _prefix;
        private readonly bool _ignoreEvents;
        private readonly List<string> _autoSubscribeChannels;
        private readonly Dictionary<string, object> _options, _broker, _ctx;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly Timer _reconnectTimer;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending = new();
        private readonly ConcurrentDictionary<string, bool> _seenNotifyTokens = new();
        private readonly object _writeLock = new();

        private bool _connected = false;
        private bool _disposed = false;
        private string _buffer = string.Empty;

        // === Events ===
        public event EventHandler<QuoteEventArgs>? Quote;
        public event EventHandler<NotifyEventArgs>? Notify;
        public event EventHandler<UserEventArgs>? TradeEvent;
        public event EventHandler<UserEventArgs>? BalanceEvent;
        public event EventHandler<UserEventArgs>? UserEvent;
        public event EventHandler<JArray>? SymbolsReindex;
        public event EventHandler<JArray>? SecurityReindex;
        public event EventHandler<JObject>? OnEvent; // Universal

        public dynamic Command => new CommandProxy(this);
        public bool Connected => _connected && !_disposed;

        public IONPlatform(
            string url,
            string name,
            Dictionary<string, object>? options = null,
            Dictionary<string, object>? broker = null,
            Dictionary<string, object>? ctx = null,
            string? token = null)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _options = options ?? new();
            _broker = broker ?? new();
            _ctx = ctx ?? new();

            _autoSubscribeChannels = GetOption<List<string>>("autoSubscribe") ?? new();
            _ignoreEvents = GetOption<bool>("ignoreEvents");
            _prefix = GetOption<string>("prefix") ?? "ion";

            _reconnectTimer = new Timer(_ => Reconnect(), null, Timeout.Infinite, Timeout.Infinite);
            Connect();
        }

        private T GetOption<T>(string key, T defaultValue = default!) =>
            _options.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        private void Connect()
        {
            if (_disposed) return;
            _connected = false;
            _buffer = string.Empty;
            _seenNotifyTokens.Clear();

            try
            {
                var parts = _url.Split(':');
                var host = parts[0];
                var port = int.Parse(parts[1]);

                _client = new TcpClient { NoDelay = SocketNoDelay };
                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, SocketKeepAlive);
                _client.BeginConnect(host, port, ConnectCallback, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ION:{_name}] Connect error: {ex.Message}");
                ScheduleReconnect();
            }
        }

        private void ConnectCallback(IAsyncResult ar)
        {
            try
            {
                _client?.EndConnect(ar);
                _stream = _client.GetStream();
                _connected = true;
                Console.WriteLine($"[ION:{_name}] Connected to {_url}");

                if (_autoSubscribeChannels.Count > 0)
                {
                    Task.Delay(AutoSubscribeDelayMs).ContinueWith(_ =>
                        _ = SubscribeAsync(_autoSubscribeChannels.ToArray()));
                }

                BeginRead();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ION:{_name}] Connection failed: {ex.Message}");
                ScheduleReconnect();
            }
        }

        private void BeginRead()
        {
            if (!_connected || _stream == null) return;
            var buffer = new byte[8192];
            _stream.BeginRead(buffer, 0, buffer.Length, ReadCallback, buffer);
        }

        private void ReadCallback(IAsyncResult ar)
        {
            if (_disposed || !_connected || _stream == null) return;

            try
            {
                var bytesRead = _stream.EndRead(ar);
                if (bytesRead <= 0)
                {
                    Console.WriteLine($"[ION:{_name}] Disconnected.");
                    _connected = false;
                    ScheduleReconnect();
                    return;
                }

                var data = Encoding.UTF8.GetString((byte[])ar.AsyncState!, 0, bytesRead);
                _buffer += data;
                ProcessBuffer();
                BeginRead();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ION:{_name}] Read error: {ex.Message}");
                _connected = false;
                ScheduleReconnect();
            }
        }

        private void ProcessBuffer()
        {
            while (true)
            {
                var pos = _buffer.IndexOf("\r\n", StringComparison.Ordinal);
                if (pos == -1) break;

                var line = _buffer[..pos];
                _buffer = _buffer[(pos + 2)..];
                if (string.IsNullOrWhiteSpace(line)) continue;

                ProcessLine(line);
            }
        }

        private void ProcessLine(string line)
        {
            try
            {
                var json = RepairAndParseJson(line.Trim());
                if (json == null) return;

                if (json is JArray arr)
                {
                    var marker = arr[0]?.Value<string>();
                    if (marker == "t" && arr.Count >= 4) { HandleQuote(arr); return; }
                    if (marker == "n" && arr.Count >= 8) { HandleNotify(arr); return; }
                    if (marker == "sr" && arr.Count == 2) { SymbolsReindex?.Invoke(this, arr); return; }
                    if (marker == "sc" && arr.Count == 2) { SecurityReindex?.Invoke(this, arr); return; }
                }
                else if (json is JObject obj)
                {
                    if (obj["event"] != null) { HandleUserEvent(obj); return; }
                    if (obj["extID"] != null) { HandleResponse(obj); return; }
                }

                Console.WriteLine($"[ION:{_name}] Unknown: {json}");
            }
            catch (Exception ex)
            {
            Console.WriteLine($"[ION:{_name}] Parse error: {ex.Message} | Raw: {line}");
            }
        }

        private JToken? RepairAndParseJson(string input)
        {
            try { return JToken.Parse(input); }
            catch
            {
                var fixedJson = Regex.Replace(input, @"(?<!\\)'", "\"");
                fixedJson = Regex.Replace(fixedJson, @",(?=\s*[}\]])", "");
                try { return JToken.Parse(fixedJson); }
                catch { return null; }
            }
        }

        private void HandleQuote(JArray arr)
        {
            if (arr.Count < 4) return;
            var symbol = arr[1]?.ToString();
            if (string.IsNullOrEmpty(symbol)) return;

            if (!double.TryParse(arr[2]?.ToString(), out var bid)) return;
            if (!double.TryParse(arr[3]?.ToString(), out var ask)) return;

            DateTime? ts = arr.Count > 4 && long.TryParse(arr[4]?.ToString(), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : null;

            var quote = new QuoteEventArgs { Symbol = symbol, Bid = bid, Ask = ask, Timestamp = ts };
            Quote?.Invoke(this, quote);
        }

        private void HandleNotify(JArray arr)
        {
            var token = arr[3]?.ToString();
            if (string.IsNullOrEmpty(token) || _seenNotifyTokens.ContainsKey(token)) return;
            _seenNotifyTokens[token] = true;

            var dataNode = arr[8];
            var isObject = dataNode?.Type == JTokenType.Object;

            var notify = new NotifyEventArgs
            {
                Message = arr[1]?.ToString() ?? "",
                Description = arr[2]?.ToString() ?? "",
                Token = token,
                Status = arr[4]?.ToString() ?? "",
                Level = arr[5]?.Type == JTokenType.Integer ? arr[5].Value<int>() : 0,
                UserId = arr[6]?.ToString() ?? "",
                CreateTime = arr[7]?.Type == JTokenType.Integer
                    ? DateTimeOffset.FromUnixTimeSeconds(arr[7].Value<long>()).UtcDateTime
                    : null,
                Data = isObject ? (JObject)dataNode : null,
                Code = isObject
                    ? (arr.Count > 9 ? arr[9].Value<int>() : 0)
                    : (dataNode?.Type == JTokenType.Integer ? dataNode.Value<int>() : 0)
            };

            Notify?.Invoke(this, notify);
        }

        private void HandleUserEvent(JObject obj)
        {
            var eventName = obj["event"]?.ToString() ?? "";
            var payload = new UserEventArgs
            {
                Event = eventName,
                Type = obj["type"]?.ToString() ?? "",
                Data = obj["data"] as JObject
            };

            OnEvent?.Invoke(this, obj);

            switch (eventName)
            {
                case "trade": TradeEvent?.Invoke(this, payload); break;
                case "balance": BalanceEvent?.Invoke(this, payload); break;
                case "user": UserEvent?.Invoke(this, payload); break;
            }
        }

        private void HandleResponse(JObject obj)
        {
            var extId = obj["extID"]?.ToString();
            if (string.IsNullOrEmpty(extId)) return;
            if (_pending.TryRemove(extId, out var tcs))
                tcs.SetResult(obj);
        }

        public async Task<JObject> CallCommandAsync(string command, object? data = null) =>
            await SendInternalAsync(new JObject { ["command"] = command, ["data"] = data != null ? JToken.FromObject(data) : new JObject() });

        public async Task<JObject> SendAsync(object payload) => await SendInternalAsync(JObject.FromObject(payload));

        private async Task<JObject> SendInternalAsync(JObject payload)
        {
            if (_disposed || !_connected || _stream == null)
                throw new InvalidOperationException("Not connected");

            var extId = payload["extID"]?.ToString() ?? Guid.NewGuid().ToString("N")[..12];
            payload["extID"] = extId;
            payload["__token"] = _token;

            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource(ResponseTimeoutMs);
            _pending[extId] = tcs;

            cts.Token.Register(() =>
            {
                if (_pending.TryRemove(extId, out var removed) && removed == tcs)
                    tcs.TrySetException(new TimeoutException($"Timeout: {extId}"));
            });

            try
            {
                var json = payload.ToString(Formatting.None) + "\r\n";
                var bytes = Encoding.UTF8.GetBytes(json);
                lock (_writeLock) _stream.Write(bytes, 0, bytes.Length);
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                _pending.TryRemove(extId, out _);
                throw;
            }
        }

        public Task<JObject> SubscribeAsync(params string[] channels) =>
            CallCommandAsync("Subscribe", new { chanels = channels });

        public Task<JObject> UnsubscribeAsync(params string[] channels) =>
            CallCommandAsync("Unsubscribe", new { chanels = channels });

        private void ScheduleReconnect()
        {
            if (_disposed) return;
            _connected = false;
            try { _client?.Close(); } catch { }
            _reconnectTimer.Change(ReconnectDelayMs, Timeout.Infinite);
        }

        private void Reconnect()
        {
            Console.WriteLine($"[ION:{_name}] Reconnecting...");
            Connect();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reconnectTimer.Dispose();
            try { _client?.Close(); } catch { }
            try { _stream?.Close(); } catch { }
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }

        private class CommandProxy : DynamicObject
        {
            private readonly IONPlatform _p;
            public CommandProxy(IONPlatform p) => _p = p;
            public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
            {
                result = _p.CallCommandAsync(binder.Name, args?.Length > 0 ? args[0] : null);
                return true;
            }
        }
    }

    // === EventArgs ===
    public class QuoteEventArgs : EventArgs
    {
        public string Symbol { get; set; } = "";
        public double Bid { get; set; }
        public double Ask { get; set; }
        public DateTime? Timestamp { get; set; }
    }

    public class NotifyEventArgs : EventArgs
    {
        public string Message { get; set; } = "";
        public string Description { get; set; } = "";
        public string Token { get; set; } = "";
        public string Status { get; set; } = "";
        public int Level { get; set; }
        public string UserId { get; set; } = "";
        public DateTime? CreateTime { get; set; }
        public JObject? Data { get; set; }
        public int Code { get; set; }
    }

    public class UserEventArgs : EventArgs
    {
        public string Event { get; set; } = "";
        public string Type { get; set; } = "";
        public JObject? Data { get; set; }
    }
}