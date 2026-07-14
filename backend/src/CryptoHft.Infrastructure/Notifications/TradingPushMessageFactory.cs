using System.Globalization;
using CryptoHft.Application.Account;
using CryptoHft.Application.Notifications;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Infrastructure.Notifications;

internal sealed record TradingPushMessage(
    string Title,
    string Body,
    string EventType,
    string Symbol);

internal static class TradingPushMessageFactory
{
    public static TradingPushMessage PositionOpened(FuturesPositionInfo position)
    {
        var side = position.PositionAmount > 0 ? TradeSide.Long : TradeSide.Short;
        var body = string.Create(CultureInfo.InvariantCulture,
            $"{side.ToString().ToUpperInvariant()} {Math.Abs(position.PositionAmount):0.######} {position.Symbol} opened at {position.EntryPrice:N2} · {position.Leverage:0}x");
        return new TradingPushMessage(
            "New Position Opened",
            body,
            "position_opened",
            position.Symbol);
    }

    public static TradingPushMessage PositionClosed(ClosedPositionPush position)
    {
        var (title, eventType) = position.CloseReason switch
        {
            PositionCloseReason.TakeProfit => ("Take Profit Hit", "take_profit"),
            PositionCloseReason.StopLoss => ("Stop Loss Hit", "stop_loss"),
            PositionCloseReason.TrailingStop => ("Trailing Stop Hit", "trailing_stop"),
            PositionCloseReason.AutoClose => ("Position Auto-Closed", "auto_close"),
            PositionCloseReason.ManualClose => ("Position Closed", "manual_close"),
            _ => ("Position Closed", "position_closed")
        };

        var body = string.Create(CultureInfo.InvariantCulture,
            $"{position.Side.ToString().ToUpperInvariant()} {position.Symbol} closed · PnL {position.RealizedPnl:+0.00;-0.00;0.00}");
        return new TradingPushMessage(title, body, eventType, position.Symbol);
    }
}
