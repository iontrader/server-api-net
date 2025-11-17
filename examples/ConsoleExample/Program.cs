using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IonTrader;
using Newtonsoft.Json.Linq;

class Program
{
    static async Task Main()
    {
        // -----------------------------------------------------------------
        // 1. Init
        // -----------------------------------------------------------------
        const string url  = "broker.iontrader.com:8080";
        const string name = "ion-csharp-demo";
        const string token = "your-jwt-auth-token";

        var platform = new IONPlatform(
            url,
            name,
            new Dictionary<string, object>
            {
                ["autoSubscribe"] = new List<string> { "EURUSD", "BTCUSD" },
                ["ignoreEvents"]  = false,
                ["prefix"]        = "ion",
                ["mode"]          = "live"
            },
            broker: null,
            ctx:    null,
            token:  token
        );

        // -----------------------------------------------------------------
        // 2. Subscribe
        // -----------------------------------------------------------------
        platform.Quote += (s, q) => Console.WriteLine($"[QUOTE] {q.Symbol}: {q.Bid}/{q.Ask}");

        platform.Quote += (s, q) =>
        {
            if (q.Symbol.Equals("EURUSD", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[EURUSD] Bid: {q.Bid}");
        };

        platform.Notify += (s, n) =>
        {
            var level = n.Level switch
            {
                10 => "INFO",
                20 => "WARN",
                30 => "ERROR",
                40 => "PROMO",
                _  => n.Level.ToString()
            };
            Console.WriteLine($"[NOTIFY:{level}] {n.Message}");
        };

        platform.TradeEvent += (s, e) =>
        {
            var d = e.Data;
            var cmd = d?["cmd"]?.Value<int>() == 0 ? "BUY" : d?["cmd"]?.Value<int>() == 1 ? "SELL" : "UNKNOWN";
            Console.WriteLine($"[TRADE #{d?["order"]}] {cmd} {d?["volume"]} {d?["symbol"]} @ {d?["open_price"]} (P&L: {d?["profit"]})");
        };

        platform.BalanceEvent += (s, e) =>
        {
            var d = e.Data;
            Console.WriteLine($"[BALANCE] {d?["login"]} | Balance: {d?["balance"]} | Equity: {d?["equity"]} | Margin: {d?["margin_level"]}%");
        };

        platform.UserEvent += (s, e) =>
        {
            var d = e.Data;
            Console.WriteLine($"[USER] {d?["login"]} | {d?["name"]} | Group: {d?["group"]} | Leverage: {d?["leverage"]}");
        };

        platform.SymbolsReindex += (s, list) =>
            Console.WriteLine($"[REINDEX] {list[1].Count()} symbols updated");

        // -----------------------------------------------------------------
        // 3. Waiting for connection
        // -----------------------------------------------------------------
        while (!platform.Connected) await Task.Delay(50);
        Console.WriteLine("Connected!");

        // -----------------------------------------------------------------
        // 4. COMMANDS
        // -----------------------------------------------------------------
        // 4.1 Optimized subscribe
        await platform.SubscribeAsync("GBPUSD");
        Console.WriteLine("Subscribed to GBPUSD");

        // 4.2 Create user (dynamic proxy)
        dynamic cmd = platform.Command;
        var user = await cmd.AddUser(new
        {
            group   = "TestGroup",
            name    = "John Doe",
            password= "pass123",
            leverage= 100,
            enable  = 1,
            email   = "john@example134412.com"
        });
        Console.WriteLine($"User created: {user}");

        // 4.3 Unsubscribe after 10 s
        _ = Task.Delay(10_000).ContinueWith(_ => platform.UnsubscribeAsync("BTCUSD")
            .ContinueWith(t => Console.WriteLine("Unsubscribed from BTCUSD")));

        // -----------------------------------------------------------------
        // 5. Disconnect
        // -----------------------------------------------------------------
        await Task.Delay(30_000);
        Console.WriteLine("Shutting down...");
        platform.Dispose();
    }
}