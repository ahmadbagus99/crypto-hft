namespace CryptoHft.Domain.Enums;

public enum TradingMode
{
    Manual = 1,
    Auto = 2,
    Paper = 3
}

public enum TradeSide
{
    Long = 1,
    Short = 2
}

public enum OrderKind
{
    Market = 1,
    Limit = 2,
    StopMarket = 3,
    StopLimit = 4,
    TakeProfit = 5,
    TrailingStop = 6
}

public enum OrderStatus
{
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Cancelled = 4,
    Rejected = 5,
    Expired = 6
}

public enum DecisionAction
{
    StrongSell = 1,
    Sell = 2,
    WeakSell = 3,
    NoTrade = 4,
    WeakBuy = 5,
    Buy = 6,
    StrongBuy = 7
}

// Why a closed position closed — the signal that lets SL/TP geometry learn from real exits.
// Unknown stays 0 so pre-existing rows and unclassifiable closes never teach the wrong lesson.
public enum PositionCloseReason
{
    Unknown = 0,
    TakeProfit = 1,
    StopLoss = 2,
    AutoClose = 3,    // app-initiated: open-position revalidation flipped against the side
    ManualClose = 4,  // app-initiated: user closed from the dashboard
    TrailingStop = 5  // exchange-side fill of a ratcheted (breakeven/profit-lock) stop
}
