using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Exchange;
using CryptoHft.Application.MarketData;
using CryptoHft.Application.Trading;

namespace CryptoHft.Application.Abstractions;

public interface IRealtimePublisher
{
    Task PublishPriceAsync(PriceTick tick, CancellationToken cancellationToken);
    Task PublishMarkPriceAsync(MarkPriceTick tick, CancellationToken cancellationToken);
    Task PublishOrderBookAsync(OrderBookSnapshot snapshot, CancellationToken cancellationToken);
    Task PublishAggTradeAsync(AggTradeTick tick, CancellationToken cancellationToken);
    Task PublishKlineAsync(KlineTick tick, CancellationToken cancellationToken);
    Task PublishMarginCallAsync(MarginCallEvent marginCall, CancellationToken cancellationToken);
    Task PublishUserDataStreamExpiredAsync(UserDataStreamExpiredEvent expired, CancellationToken cancellationToken);
    Task PublishAccountUpdateAsync(AccountUpdateEvent accountUpdate, CancellationToken cancellationToken);
    Task PublishOrderUpdateAsync(OrderUpdateEvent orderUpdate, CancellationToken cancellationToken);
    Task PublishDecisionAsync(DecisionResult decision, CancellationToken cancellationToken);
    Task PublishOrderAsync(TradeOrderResult order, CancellationToken cancellationToken);
}

public interface IMarketDataStream
{
    Task RunAsync(CancellationToken cancellationToken);
}

public interface IUserDataStream
{
    Task RunAsync(CancellationToken cancellationToken);
}

public interface ITradingExecutor
{
    Task<TradeOrderResult> PlaceAsync(TradeOrderRequest request, CancellationToken cancellationToken);
    Task<TradeOrderResult> ClosePositionAsync(ClosePositionRequest request, CancellationToken cancellationToken);
    Task<TradeOrderResult> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken);
}

public interface IFuturesExchangeInfoClient
{
    Task<FuturesSymbolRules> GetSymbolRulesAsync(string symbol, CancellationToken cancellationToken);
}

public interface IExchangeRuleValidator
{
    Task<NormalizedOrder> NormalizeAndValidateAsync(TradeOrderRequest request, CancellationToken cancellationToken);
}

public interface IKillSwitchService
{
    KillSwitchState GetState();
    Task<KillSwitchState> EnableAsync(KillSwitchRequest request, CancellationToken cancellationToken);
    Task<KillSwitchState> DisableAsync(CancellationToken cancellationToken);
    Task<KillSwitchState> SendHeartbeatAsync(CancellationToken cancellationToken);
}
