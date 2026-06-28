# BTCUSDT Perpetual Futures Trading System

Full-stack trading platform scaffold untuk BTCUSDT USD-M Perpetual Futures. Sistem ini testnet-first, realtime-first, dan dibuat modular agar bisa dikembangkan ke auto trading, manual trading, paper trading, news analysis, backtesting, dan machine-learning-ready decision engine.

> Tidak ada strategi yang menjamin profit. Default sistem ini paper/testnet dan harus tetap memprioritaskan risk management, audit log, dan kill switch sebelum dipakai live.

## Stack

- Backend: .NET 9 Web API, Clean Architecture, SignalR, BackgroundService, EF Core, PostgreSQL, Redis-ready, Hangfire-ready.
- Binance: USD-M Futures mainnet public market data, WebSocket realtime stream, paper-safe execution boundary.
- Frontend: React, TypeScript, Tailwind, TanStack Query, SignalR client, Recharts.
- Deployment: Docker Compose.

## Ports

- Frontend dashboard: `http://localhost:5005`
- API / Swagger: `http://localhost:5006/swagger`
- Health check: `http://localhost:5006/health`
- SignalR hub: `http://localhost:5006/hubs/trading`

## Run

```bash
docker compose up --build --remove-orphans
```

Run detached:

```bash
docker compose up -d --build --remove-orphans
```

Stop:

```bash
docker compose down
```

## Services

- `api`: .NET API, SignalR hub, realtime Binance worker, paper/manual trading endpoints.
- `frontend`: React dashboard served by Nginx.
- `postgres`: persistence for users, API keys, orders, positions, signals, wallet, news, logs, risk settings.
- `redis`: cache/pub-sub foundation.
- `binance-integration`: optional profile for separated integration worker.

Optional dedicated integration service:

```bash
docker compose --profile integration up --build
```

## Current Implementation

Implemented foundation:

- Binance USD-M Futures WebSocket stream for BTCUSDT:
  - agg trades
  - mark price
  - depth/order book
  - klines for 1m, 5m, 15m, 1h, 4h, 1d
- SignalR realtime broadcasting.
- Paper trading executor.
- Manual trading endpoints.
- Risk manager:
  - max daily loss
  - max consecutive loss
  - max open positions
  - max exposure
  - risk per trade
  - minimum RR
  - confidence threshold
- Multi-factor decision engine scaffold:
  - trend
  - momentum
  - orderflow
  - funding
  - news score
  - volatility
- EF Core schema foundation for:
  - Users
  - ApiKeys
  - Orders
  - Positions
  - Signals
  - Wallet
  - RiskManagement
  - News
  - NewsSentiment
  - Logs
- React dashboard:
  - overview metrics
  - realtime price
  - realtime order book
  - realtime trade tape
  - realtime chart stream
  - manual paper order panel

## API Examples

Manual paper order:

```bash
curl -X POST "http://localhost:5006/api/manual/order" \
  -H "Content-Type: application/json" \
  -d '{
    "side": "Long",
    "kind": "Market",
    "quantity": 0.001,
    "price": null,
    "stopPrice": null,
    "takeProfit": 65000,
    "stopLoss": 59000,
    "leverage": 3,
    "reduceOnly": false,
    "reason": "Manual testnet paper long"
  }'
```

Decision evaluation:

```bash
curl -X POST "http://localhost:5006/api/decision/evaluate" \
  -H "Content-Type: application/json" \
  -d '{
    "symbol": "BTCUSDT",
    "lastPrice": 60000,
    "ema9": 61000,
    "ema20": 60500,
    "ema50": 59000,
    "ema200": 56000,
    "rsi": 62,
    "macd": 120,
    "macdSignal": 80,
    "atr": 500,
    "vwap": 59500,
    "orderBookImbalance": 0.2,
    "fundingRate": 0.0001,
    "openInterestChange": 1,
    "newsScore": 70,
    "volatilityScore": 50
  }'
```

## Market Data And Execution Safety

Default market data follows Binance Futures mainnet so prices match the public Binance Futures website. Execution remains paper-only.

```json
{
  "Binance": {
    "Environment": "MainnetMarketDataPaperTrading",
    "RestBaseUrl": "https://fapi.binance.com",
    "WebSocketBaseUrl": "wss://fstream.binance.com/ws",
    "PaperTradingOnly": true
  }
}
```

Live execution switch later:

- Add encrypted API key storage.
- Disable `PaperTradingOnly` only after paper trading, risk limits, audit log, and kill switch are verified.

## Next Milestones

1. Add authenticated Binance account stream and encrypted API-key vault.
2. Add live wallet/position/order sync.
3. Add proper Binance.Net order executor behind `ITradingExecutor`.
4. Add migrations and seed settings.
5. Add RSS/news ingestion and AI sentiment scoring.
6. Add backtesting engine with CSV export.
7. Add paper-trade matching engine and PnL ledger.
8. Add JWT/refresh token, rate limiter, retry policy, circuit breaker, and notification channels.
