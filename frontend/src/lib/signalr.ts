import * as signalR from "@microsoft/signalr";

export function createTradingConnection() {
  return new signalR.HubConnectionBuilder()
    .withUrl("/hubs/trading")
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();
}
