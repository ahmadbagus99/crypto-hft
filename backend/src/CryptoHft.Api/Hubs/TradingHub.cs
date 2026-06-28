using Microsoft.AspNetCore.SignalR;

namespace CryptoHft.Api.Hubs;

public sealed class TradingHub : Hub
{
    public Task SubscribeSymbol(string symbol)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, symbol.ToUpperInvariant());
    }

    public Task UnsubscribeSymbol(string symbol)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, symbol.ToUpperInvariant());
    }
}
