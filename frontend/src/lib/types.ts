export type PriceTick = {
  symbol: string;
  price: number;
  time: string;
};

export type MarkPriceTick = {
  symbol: string;
  markPrice: number;
  markPriceMovingAverage: number;
  indexPrice: number;
  estimatedSettlePrice: number;
  fundingRate: number;
  nextFundingTime: string;
  time: string;
};

export type OrderBookLevel = {
  price: number;
  quantity: number;
};

export type OrderBookSnapshot = {
  symbol: string;
  bids: OrderBookLevel[];
  asks: OrderBookLevel[];
  spread: number;
  imbalance: number;
  time: string;
};

export type AggTradeTick = {
  symbol: string;
  price: number;
  quantity: number;
  buyerIsMaker: boolean;
  time: string;
};

export type KlineTick = {
  symbol: string;
  interval: string;
  openTime: string;
  closeTime: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  quoteVolume: number;
  numberOfTrades: number;
  takerBuyBaseVolume: number;
  takerBuyQuoteVolume: number;
  isClosed: boolean;
  eventTime: string;
};

export type Overview = {
  symbol: string;
  mode: string;
  walletBalance: number;
  availableBalance: number;
  dailyPnl: number;
  weeklyPnl: number;
  monthlyPnl: number;
  winRate: number;
  profitFactor: number;
  sharpeRatio: number;
  maxDrawdown: number;
};

export type FuturesWalletBalance = {
  accountAlias: string;
  asset: string;
  balance: number;
  crossWalletBalance: number;
  crossUnrealizedPnl: number;
  availableBalance: number;
  maxWithdrawAmount: number;
  isMarginAvailable: boolean;
  updateTime: string;
};

export type FuturesPositionInfo = {
  symbol: string;
  positionSide: string;
  positionAmount: number;
  entryPrice: number;
  breakEvenPrice: number;
  markPrice: number;
  unrealizedProfit: number;
  liquidationPrice: number;
  leverage: number;
  maxNotionalValue: number;
  marginType: string;
  isolatedMargin: number;
  isAutoAddMargin: boolean;
  updateTime: string;
};

export type MarginCallPosition = {
  symbol: string;
  positionSide: string;
  positionAmount: number;
  marginType: string;
  isolatedWallet: number;
  markPrice: number;
  unrealizedPnl: number;
  maintenanceMarginRequired: number;
};

export type MarginCallEvent = {
  symbol: string;
  crossWalletBalance: number;
  positions: MarginCallPosition[];
  eventTime: string;
};

export type UserDataStreamExpiredEvent = {
  symbol: string;
  listenKey: string;
  eventTime: string;
};

export type AccountPositionUpdate = {
  symbol: string;
  positionSide: string;
  positionAmount: number;
  entryPrice: number;
  breakEvenPrice: number;
  accumulatedRealized: number;
  unrealizedProfit: number;
  marginType: string;
  isolatedWallet: number;
};

export type AccountBalanceUpdate = {
  asset: string;
  walletBalance: number;
  crossWalletBalance: number;
  balanceChange: number;
};

export type AccountUpdateEvent = {
  symbol: string;
  reason: string;
  balances: AccountBalanceUpdate[];
  positions: AccountPositionUpdate[];
  eventTime: string;
  transactionTime: string;
};

export type OrderUpdateEvent = {
  symbol: string;
  orderId: number | string;
  clientOrderId: string;
  side: string;
  orderType: string;
  executionType: string;
  orderStatus: string;
  timeInForce: string;
  originalQuantity: number;
  originalPrice: number;
  averagePrice: number;
  stopPrice: number;
  lastFilledQuantity: number;
  accumulatedFilledQuantity: number;
  lastFilledPrice: number;
  realizedProfit: number;
  reduceOnly: boolean;
  positionSide: string;
  workingType: string;
  orderTradeTime: string;
  eventTime: string;
  source?: string;
};

export type TradeOrderResult = {
  symbol: string;
  orderId: string;
  status: number | string;
  quantity: number;
  price: number | null;
  isPaper: boolean;
  message: string;
  time: string;
};

export type FuturesSymbolRules = {
  symbol: string;
  status: string;
  minPrice: number;
  maxPrice: number;
  tickSize: number;
  minQuantity: number;
  maxQuantity: number;
  stepSize: number;
  marketMinQuantity: number;
  marketMaxQuantity: number;
  marketStepSize: number;
  minNotional: number;
  pricePrecision: number;
  quantityPrecision: number;
  updatedAt: string;
};

export type KillSwitchState = {
  symbol: string;
  enabled: boolean;
  countdownTimeMs: number;
  heartbeatIntervalMs: number;
  lastHeartbeatAt: string | null;
  nextHeartbeatAt: string | null;
  isPaper: boolean;
  message: string;
};

export type JournalOrder = {
  id: string;
  symbol: string;
  side: string;
  kind: string;
  status: string;
  quantity: number;
  price: number | null;
  stopPrice: number | null;
  takeProfit: number | null;
  stopLoss: number | null;
  reduceOnly: boolean;
  isPaper: boolean;
  exchangeOrderId: string | null;
  reason: string;
  createdAt: string;
};

export type JournalResponse = {
  summary: {
    totalOrders: number;
    filledOrders: number;
    rejectedOrders: number;
    paperOrders: number;
    reduceOnlyOrders: number;
  };
  orders: JournalOrder[];
};

export type PositionRisk = {
  symbol: string;
  positionSide: string;
  marginType: string;
  positionAmount: number;
  entryPrice: number;
  markPrice: number;
  liquidationPrice: number;
  notional: number;
  initialMargin: number;
  unrealizedProfit: number;
  unrealizedProfitPercent: number;
  leverage: number;
  marginRatio: number;
  liquidationBufferPercent: number;
  riskLevel: string;
};

export type RiskDetailResponse = {
  symbol: string;
  equity: number;
  availableBalance: number;
  maxDailyLossPercent: number;
  maxDailyLoss: number;
  dailyLossUsedPercent: number;
  totalNotional: number;
  exposureRatio: number;
  totalUnrealizedProfit: number;
  defaultLeverage: number;
  portfolioRiskLevel: string;
  positions: PositionRisk[];
};

export type BacktestResult = {
  symbol: string;
  interval: string;
  candles: number;
  initialEquity: number;
  finalEquity: number;
  netPnl: number;
  netPnlPercent: number;
  maxDrawdownPercent: number;
  winRate: number;
  profitFactor: number;
  totalTrades: number;
  feeRate: number;
  slippageRate: number;
  leverage: number;
  indicators: string[];
  trades: Array<{
    side: string;
    entryTime: string;
    exitTime: string;
    entryPrice: number;
    exitPrice: number;
    quantity: number;
    pnl: number;
    pnlPercent: number;
    exitReason: string;
  }>;
};

export type AiDecision = {
  symbol: string;
  action: number;
  confidence: number;
  probabilityOfSuccess: number;
  regime: number;
  entryPrice: number;
  stopLoss: number;
  takeProfit: number;
  trailingStopPercent: number;
  riskReward: number;
  positionSizeQuantity: number;
  leverage: number;
  shouldTrade: boolean;
  noTradeReason: string;
  scores: Record<string, number>;
  weights: Record<string, number>;
  reasons: string[];
  llm: {
    confirmed: boolean;
    adjustedConfidence: number;
    narrative: string;
    risks: string[];
    used: boolean;
  };
  time: string;
};

export type TradingSettings = {
  paperTradingOnly: boolean;
  autoTradingEnabled: boolean;
  maxDailyLossPercent: number;
  riskPerTradePercent: number;
  maxExposurePercent: number;
  defaultLeverage: number;
  hasApiKey: boolean;
  hasApiSecret: boolean;
  apiKeyPreview: string;
  hasAnthropicKey: boolean;
  anthropicKeyPreview: string;
};

export type ConnectionTestResult = {
  connected: boolean;
  message: string;
  detail?: string | null;
};
