using CryptoHft.Application.Abstractions;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.MarketData;
using CryptoHft.Application.Trading;
using Microsoft.AspNetCore.SignalR;

namespace CryptoHft.Api.Hubs;

public sealed class SignalRRealtimePublisher(IHubContext<TradingHub> hubContext) : IRealtimePublisher
{
    public Task PublishPriceAsync(PriceTick tick, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(tick.Symbol).SendAsync("price", tick, cancellationToken);

    public Task PublishMarkPriceAsync(MarkPriceTick tick, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(tick.Symbol).SendAsync("markPrice", tick, cancellationToken);

    public Task PublishOrderBookAsync(OrderBookSnapshot snapshot, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(snapshot.Symbol).SendAsync("orderBook", snapshot, cancellationToken);

    public Task PublishAggTradeAsync(AggTradeTick tick, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(tick.Symbol).SendAsync("aggTrade", tick, cancellationToken);

    public Task PublishKlineAsync(KlineTick tick, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(tick.Symbol).SendAsync("kline", tick, cancellationToken);

    public Task PublishMarginCallAsync(MarginCallEvent marginCall, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(marginCall.Symbol).SendAsync("marginCall", marginCall, cancellationToken);

    public Task PublishUserDataStreamExpiredAsync(UserDataStreamExpiredEvent expired, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(expired.Symbol).SendAsync("userDataStreamExpired", expired, cancellationToken);

    public Task PublishAccountUpdateAsync(AccountUpdateEvent accountUpdate, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(accountUpdate.Symbol).SendAsync("accountUpdate", accountUpdate, cancellationToken);

    public Task PublishOrderUpdateAsync(OrderUpdateEvent orderUpdate, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(orderUpdate.Symbol).SendAsync("orderUpdate", orderUpdate, cancellationToken);

    public Task PublishDecisionAsync(DecisionResult decision, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(decision.Symbol).SendAsync("decision", decision, cancellationToken);

    public Task PublishOrderAsync(TradeOrderResult order, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(order.Symbol).SendAsync("order", order, cancellationToken);
}
