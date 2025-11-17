<div align="center">

# ion-server-api-net

**Ultra‑low latency .NET TCP client for [IonTrader](https://iontrader.com)**  
Real‑time market data, trade execution, balance & user management via TCP.

![NuGet](https://img.shields.io/nuget/v/IonTrader.ServerApi?color=green)
![.NET](https://img.shields.io/badge/.NET-8.0-blue)
![License](https://img.shields.io/badge/license-MIT-blue)
![Downloads](https://img.shields.io/nuget/dt/IonTrader.ServerApi)

> **Server‑to‑Server (S2S) integration** — perfect for brokers, CRMs, HFT bots, back‑office systems.

[Documentation](https://iontrader.com/tcp) · [Examples](./examples) · [Report Bug](https://github.com/iontrader/server-api-net/issues)

</div>

---

## Features

| Feature | Description |
|---------|-------------|
| **TCP S2S** | Direct TCP – no HTTP overhead |
| **Real‑time Events** | Quotes, trades, balance, user & symbol updates |
| **Optimized Subscribe** | `platform.SubscribeAsync(...)` / `UnsubscribeAsync(...)` |
| **Dynamic Commands** | `platform.Command.AddUser(...)`, `platform.Command.GetTrades()` |
| **Auto‑reconnect** | Robust reconnection with back‑off |
| **Event Filtering** | `ignoreEvents`, per‑symbol listeners |
| **extID Tracking** | Reliable command responses |
| **JSON Repair** | Handles malformed packets gracefully |

---

## Installation

Clone:

```bash
git clone https://github.com/iontrader/server-api-net
cd server-api-net
dotnet build
```

OR

```bash
dotnet add package IonTrader.ServerApi
```

Run example:
```bash
dotnet run --project examples/ConsoleExample
```

---

## Quick Start

```csharp
using IonTrader;

var platform = new IONPlatform(
    "broker.iontrader.com:8080",
    "my-trading-bot",
    new() { ["autoSubscribe"] = new List<string> { "EURUSD", "BTCUSD" } },
    token: "your-jwt-auth-token"
);

// Real‑time quotes
platform.Quote += (s, q) => Console.WriteLine($"{q.Symbol}: {q.Bid}/{q.Ask}");

// Trade events
platform.TradeEvent += (s, e) =>
{
    var d = e.Data;
    Console.WriteLine($"#{d["order"]} {(d["cmd"].Value<int>() == 0 ? "BUY" : "SELL")} {d["volume"]} {d["symbol"]}");
};

// Subscribe to a new symbol
await platform.SubscribeAsync("XAUUSD");

// Create user (dynamic proxy)
dynamic cmd = platform.Command;
await cmd.AddUser(new { name = "John Doe", group = "VIP", leverage = 500, email = "john@example.com" });

// Graceful shutdown
platform.Dispose();
```

---

## Supported Events

| Event | Description | Example |
|-------|-------------|---------|
| `Quote` | Real‑time tick | `{ Symbol: "EURUSD", Bid: 1.085, Ask: 1.086 }` |
| `Quote` (per‑symbol) | Filter via `if (q.Symbol == "EURUSD")` | — |
| `Notify` | System alerts | `Notify:20` (warning) |
| `TradeEvent` | Order open/close/modify | `Data.order`, `Data.profit` |
| `BalanceEvent` | Balance & margin update | `Data.equity`, `Data.margin_level` |
| `UserEvent` | User profile change | `Data.leverage`, `Data.group` |
| `SymbolsReindex` | Symbol index map | `[[symbol, sym_index, sort_index], ...]` |
| `SecurityReindex` | Security group map | `[[sec_index, sort_index], ...]` |

---

### Methods

| Method | Description |
|--------|-------------|
| `SubscribeAsync(params string[] channels)` | Fast subscribe |
| `UnsubscribeAsync(params string[] channels)` | Fast unsubscribe |
| `platform.Command.CommandName(data)` | Dynamic command |
| `SendAsync(payload)` | Legacy format `{ command, data }` |
| `Dispose()` | Close connection |

---

## Examples

### Subscribe & Unsubscribe

```csharp
await platform.SubscribeAsync("GBPUSD", "USDJPY");
await platform.UnsubscribeAsync("BTCUSD");
```

### Get All Users

```csharp
var users = await platform.Command.GetUsers(new { });
Console.WriteLine(users);
```

### Listen to Balance Changes

```csharp
platform.BalanceEvent += (s, e) =>
    Console.WriteLine($"User {e.Data["login"]}: Equity = {e.Data["equity"]}");
```

### Full Example

See [`examples/ConsoleExample/Program.cs`](./examples/ConsoleExample/Program.cs)

---

## Configuration

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `autoSubscribe` | `List<string>` | `[]` | Auto‑subscribe on connect |
| `ignoreEvents` | `bool` | `false` | Disable all event emission |
| `mode` | `string` | `"live"` | Environment mode |

---

## Documentation

- **TCP API**: [https://iontrader.com/tcp](https://iontrader.com/tcp)
- **Client API**: [https://iontrader.com/client-api](https://iontrader.com/client-api)
- **FIX API**: [https://iontrader.com/fix-api](https://iontrader.com/fix-api)

---

## Requirements

- .NET **8.0** or higher
- Valid **IonTrader JWT token**

---

## License

Distributed under the **MIT License**.  
See [`LICENSE`](LICENSE) for more information.

---

<div align="center">

**Made with passion for high‑frequency trading**

[iontrader.com](https://iontrader.com) · [GitHub](https://github.com/iontrader/server-api-net)

</div>