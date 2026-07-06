using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Persistence;

namespace CryptoHft.Infrastructure.Trading;

public sealed class PaperTradingExecutor(TradingDbContext dbContext, IRealtimePublisher publisher) : ITradingExecutor
{
    public async Task<TradeOrderResult> PlaceAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        var result = new TradeOrderResult(
            request.Symbol,
            $"PAPER-{Guid.NewGuid():N}",
            OrderStatus.Filled,
            request.Quantity,
            request.Price,
            true,
            request.Reason,
            DateTimeOffset.UtcNow);

        dbContext.Orders.Add(new Domain.Entities.Order
        {
            Symbol = request.Symbol,
            Side = request.Side,
            Kind = request.Kind,
            Status = OrderStatus.Filled,
            Quantity = request.Quantity,
            Price = request.Price,
            StopPrice = request.StopPrice,
            TakeProfit = request.TakeProfit,
            StopLoss = request.StopLoss,
            ReduceOnly = request.ReduceOnly,
            IsPaper = true,
            ExchangeOrderId = result.OrderId,
            Reason = request.Reason
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);
        return result;
    }

    public Task<TradeOrderResult> ClosePositionAsync(ClosePositionRequest request, CancellationToken cancellationToken)
    {
        var result = new TradeOrderResult(
            request.Symbol,
            $"PAPER-CLOSE-{Guid.NewGuid():N}",
            OrderStatus.Filled,
            request.Quantity ?? 0,
            null,
            true,
            request.Reason,
            DateTimeOffset.UtcNow);

        return Task.FromResult(result);
    }

    public Task<TradeOrderResult> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new TradeOrderResult(
            symbol,
            orderId,
            OrderStatus.Cancelled,
            0,
            null,
            true,
            "Paper order cancelled",
            DateTimeOffset.UtcNow));
    }

    public Task<TradeOrderResult> AmendStopLossAsync(AmendStopLossRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new TradeOrderResult(
            request.Symbol,
            $"PAPER-SL-{Guid.NewGuid():N}",
            OrderStatus.New,
            request.Quantity,
            request.NewStopPrice,
            true,
            request.Reason,
            DateTimeOffset.UtcNow));
    }
}
