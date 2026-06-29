import { useQuery } from "@tanstack/react-query";
import { Activity, Bot, Radio, Settings, ShieldCheck, Wallet } from "lucide-react";
import type { ReactNode } from "react";
import React, { useEffect, useState } from "react";
import { createTradingConnection } from "./lib/signalr";
import type {
  AccountUpdateEvent,
  AiDecision,
  AggTradeTick,
  BacktestResult,
  FuturesPositionInfo,
  FuturesSymbolRules,
  FuturesWalletBalance,
  JournalResponse,
  KillSwitchState,
  KlineTick,
  MarginCallEvent,
  MarkPriceTick,
  OrderUpdateEvent,
  OrderBookSnapshot,
  Overview,
  PriceTick,
  RiskDetailResponse,
  TradeOrderResult,
  TradingSettings,
  UserDataStreamExpiredEvent
} from "./lib/types";

const symbol = "BTCUSDT";
const APP_PASSWORD = import.meta.env.VITE_APP_PASSWORD || "admin";

async function fetchTradingSettings(): Promise<TradingSettings> {
  const response = await fetch("/api/settings/trading", { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load settings");
  return response.json();
}

async function saveTradingSettings(payload: {
  paperTradingOnly: boolean;
  autoTradingEnabled: boolean;
  maxDailyLossPercent: number;
  riskPerTradePercent: number;
  maxExposurePercent: number;
  defaultLeverage: number;
  apiKey?: string;
  apiSecret?: string;
  anthropicApiKey?: string;
  aiModel?: string;
}): Promise<TradingSettings> {
  const response = await fetch("/api/settings/trading", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  if (!response.ok) throw new Error("Failed to save settings");
  return response.json();
}

async function testConnection(target: "binance" | "anthropic") {
  const response = await fetch(`/api/settings/test/${target}`, { method: "POST" });
  if (!response.ok) throw new Error("Test request failed");
  return response.json() as Promise<{ connected: boolean; message: string; detail?: string | null }>;
}

async function fetchOverview(): Promise<Overview> {
  const response = await fetch("/api/overview");
  if (!response.ok) throw new Error("Failed to load overview");
  return response.json();
}

async function fetchWallet(): Promise<FuturesWalletBalance[]> {
  const response = await fetch("/api/account/wallet", { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load wallet");
  return response.json();
}

async function fetchPositions(): Promise<FuturesPositionInfo[]> {
  const response = await fetch(`/api/account/positions?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load positions");
  return response.json();
}

async function fetchExchangeRules(): Promise<FuturesSymbolRules> {
  const response = await fetch(`/api/exchange/rules?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load exchange rules");
  return response.json();
}

async function fetchKillSwitch(): Promise<KillSwitchState> {
  const response = await fetch("/api/kill-switch", { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load kill switch state");
  return response.json();
}

async function fetchJournal(): Promise<JournalResponse> {
  const response = await fetch(`/api/journal/orders?symbol=${symbol}&limit=30`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load trade journal");
  return response.json();
}

async function fetchRiskDetails(): Promise<RiskDetailResponse> {
  const response = await fetch(`/api/risk/positions?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load risk details");
  return response.json();
}

async function fetchAiDecision(): Promise<AiDecision> {
  const response = await fetch(`/api/ai/analyze?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to run AI analysis");
  return response.json();
}

async function fetchBacktest(): Promise<BacktestResult> {
  const response = await fetch(`/api/backtest/run?symbol=${symbol}&interval=1h&limit=1000&initialEquity=10000&leverage=5`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to run backtest");
  return response.json();
}

async function enableKillSwitch() {
  const response = await fetch("/api/kill-switch/enable", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      symbol,
      countdownTimeMs: 120000,
      heartbeatIntervalMs: 30000
    })
  });
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail ?? body.title ?? "Kill switch rejected");
  return body as KillSwitchState;
}

async function disableKillSwitch() {
  const response = await fetch("/api/kill-switch/disable", { method: "POST" });
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail ?? body.title ?? "Kill switch disable rejected");
  return body as KillSwitchState;
}

async function fetchInitialMarkPrice(): Promise<MarkPriceTick> {
  const response = await fetch(`/api/market/mark-price?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load mark price");
  return response.json();
}

async function fetchInitialKlines(): Promise<KlineTick[]> {
  const response = await fetch(`/api/market/klines?symbol=${symbol}&interval=1m&limit=120`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load klines");
  return response.json();
}

async function fetchInitialAggTrades(): Promise<AggTradeTick[]> {
  const response = await fetch(`/api/market/agg-trades?symbol=${symbol}&limit=28`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load agg trades");
  return response.json();
}

async function placeManualOrder(side: 1 | 2, quantity: number, leverage: number, takeProfit?: number, stopLoss?: number) {
  const response = await fetch("/api/manual/order", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      side,
      kind: 1,
      quantity,
      price: null,
      stopPrice: null,
      takeProfit: takeProfit || null,
      stopLoss: stopLoss || null,
      leverage,
      reduceOnly: false,
      reason: side === 1 ? "Manual open long" : "Manual open short"
    })
  });

  const body = await response.json();
  if (!response.ok) {
    throw new Error(body.detail ?? body.title ?? "Order rejected");
  }

  return body as { orderId: string; status: number; message: string; isPaper: boolean };
}

async function closeManualPosition(side: 1 | 2, quantity: number) {
  const response = await fetch("/api/manual/close", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      side,
      quantity,
      reason: side === 1 ? "Manual close long" : "Manual close short"
    })
  });

  const body = await response.json();
  if (!response.ok) {
    throw new Error(body.detail ?? body.title ?? "Close rejected");
  }

  return body as { orderId: string; status: number; message: string; isPaper: boolean };
}

function LoginPage({ onLogin }: { onLogin: () => void }) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    if (password === APP_PASSWORD) {
      sessionStorage.setItem("hft_auth", "1");
      onLogin();
    } else {
      setError("Password salah");
      setPassword("");
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-[#0b101a]">
      <div className="w-full max-w-sm rounded-xl border border-slate-800 bg-[#0e1522] p-8">
        <div className="mb-6 flex items-center gap-3">
          <Bot className="h-7 w-7 text-exchangeGreen" />
          <h1 className="text-lg font-semibold text-slate-100">BTCUSDT HFT Dashboard</h1>
        </div>
        <form onSubmit={submit} className="grid gap-4">
          <div>
            <label className="mb-1 block text-xs text-slate-400">Password</label>
            <input
              type="password"
              autoFocus
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:outline-none focus:ring-1 focus:ring-emerald-500"
              placeholder="Masukkan password"
            />
          </div>
          {error && <div className="text-sm text-red-400">{error}</div>}
          <button
            type="submit"
            className="rounded-md bg-emerald-600 px-4 py-2 font-semibold text-white hover:bg-emerald-500"
          >
            Login
          </button>
        </form>
        <p className="mt-4 text-xs text-slate-600">Default password: admin — ubah via env VITE_APP_PASSWORD</p>
      </div>
    </div>
  );
}

function SettingsPage() {
  const { data: current, refetch } = useQuery({
    queryKey: ["trading-settings"],
    queryFn: fetchTradingSettings,
    retry: false,
  });

  const [paper, setPaper] = useState(true);
  const [auto, setAuto] = useState(false);
  const [maxDailyLoss, setMaxDailyLoss] = useState("30");
  const [riskPerTrade, setRiskPerTrade] = useState("1");
  const [maxExposure, setMaxExposure] = useState("25");
  const [leverage, setLeverage] = useState("5");
  const [apiKey, setApiKey] = useState("");
  const [apiSecret, setApiSecret] = useState("");
  const [anthropicKey, setAnthropicKey] = useState("");
  const [aiModel, setAiModel] = useState("claude-opus-4-8");
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState("");
  const [initialized, setInitialized] = useState(false);
  const [binanceTest, setBinanceTest] = useState<{ connected: boolean; message: string; detail?: string | null } | null>(null);
  const [anthropicTest, setAnthropicTest] = useState<{ connected: boolean; message: string; detail?: string | null } | null>(null);
  const [testing, setTesting] = useState("");

  const runTest = async (target: "binance" | "anthropic") => {
    setTesting(target);
    try {
      const result = await testConnection(target);
      if (target === "binance") setBinanceTest(result); else setAnthropicTest(result);
    } catch {
      const fail = { connected: false, message: "Test request failed" };
      if (target === "binance") setBinanceTest(fail); else setAnthropicTest(fail);
    } finally {
      setTesting("");
    }
  };

  useEffect(() => {
    if (current && !initialized) {
      setPaper(current.paperTradingOnly);
      setAuto(current.autoTradingEnabled);
      setMaxDailyLoss(String(Math.round(current.maxDailyLossPercent * 100)));
      setRiskPerTrade(String(Math.round(current.riskPerTradePercent * 100)));
      setMaxExposure(String(Math.round(current.maxExposurePercent * 100)));
      setLeverage(String(current.defaultLeverage));
      setAiModel(current.aiModel ?? "claude-opus-4-8");
      setInitialized(true);
    }
  }, [current, initialized]);

  const save = async (event: React.FormEvent) => {
    event.preventDefault();
    setSaving(true);
    setMessage("");
    try {
      await saveTradingSettings({
        paperTradingOnly: paper,
        autoTradingEnabled: auto,
        maxDailyLossPercent: Number(maxDailyLoss) / 100,
        riskPerTradePercent: Number(riskPerTrade) / 100,
        maxExposurePercent: Number(maxExposure) / 100,
        defaultLeverage: Number(leverage),
        apiKey: apiKey || undefined,
        apiSecret: apiSecret || undefined,
        anthropicApiKey: anthropicKey || undefined,
        aiModel: aiModel || undefined,
      });
      setApiKey("");
      setApiSecret("");
      setAnthropicKey("");
      setMessage("Settings tersimpan.");
      refetch();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Gagal menyimpan");
    } finally {
      setSaving(false);
    }
  };

  return (
    <main className="mx-auto max-w-2xl px-4 py-8">
      <h2 className="mb-6 text-lg font-semibold text-slate-100">Settings</h2>
      <form onSubmit={save} className="grid gap-6">

        {/* Trading Mode */}
        <section className="rounded-lg border border-slate-800 bg-panel p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-300">Trading Mode</h3>
          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <label className="mb-1 block text-xs text-slate-400">Execution Mode</label>
              <div className="flex gap-2">
                <ModeButton active={paper} onClick={() => setPaper(true)} label="Paper" />
                <ModeButton active={!paper} onClick={() => setPaper(false)} label="Live" danger />
              </div>
              {!paper && (
                <p className="mt-2 text-xs text-red-400">Mode Live akan mengirim order nyata ke Binance.</p>
              )}
            </div>
            <div>
              <label className="mb-1 block text-xs text-slate-400">Bot Mode</label>
              <div className="flex gap-2">
                <ModeButton active={!auto} onClick={() => setAuto(false)} label="Manual" />
                <ModeButton active={auto} onClick={() => setAuto(true)} label="Auto" />
              </div>
              {auto && (
                <p className="mt-2 text-xs text-amber-400">Mode Auto: bot akan place order berdasarkan sinyal AI.</p>
              )}
            </div>
          </div>
        </section>

        {/* Risk Config */}
        <section className="rounded-lg border border-slate-800 bg-panel p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-300">Risk Configuration</h3>
          <div className="grid gap-4 sm:grid-cols-2">
            <SettingInput
              label="Max Daily Loss (%)"
              value={maxDailyLoss}
              onChange={setMaxDailyLoss}
              hint="Batas loss harian sebelum auto-stop"
              type="number"
              min="1"
              max="100"
            />
            <SettingInput
              label="Risk Per Trade (%)"
              value={riskPerTrade}
              onChange={setRiskPerTrade}
              hint="Persentase equity yang dirisiko per trade"
              type="number"
              min="0.1"
              max="20"
              step="0.1"
            />
            <SettingInput
              label="Max Exposure (%)"
              value={maxExposure}
              onChange={setMaxExposure}
              hint="Max total notional dibanding equity"
              type="number"
              min="1"
              max="500"
            />
            <SettingInput
              label="Default Leverage"
              value={leverage}
              onChange={setLeverage}
              hint="Leverage default untuk manual & auto order"
              type="number"
              min="1"
              max="125"
            />
          </div>
        </section>

        {/* Binance API */}
        <section className="rounded-lg border border-slate-800 bg-panel p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-300">Binance API</h3>
          {current && (
            <div className="mb-4 grid gap-2 text-sm">
              <div className="flex items-center gap-2">
                <span className={`h-2 w-2 rounded-full ${current.hasApiKey ? "bg-emerald-400" : "bg-slate-600"}`} />
                <span className="text-slate-400">API Key: </span>
                <span className="text-slate-200">{current.hasApiKey ? current.apiKeyPreview : "Belum diset"}</span>
              </div>
              <div className="flex items-center gap-2">
                <span className={`h-2 w-2 rounded-full ${current.hasApiSecret ? "bg-emerald-400" : "bg-slate-600"}`} />
                <span className="text-slate-400">Secret: </span>
                <span className="text-slate-200">{current.hasApiSecret ? "••••••••" : "Belum diset"}</span>
              </div>
            </div>
          )}
          <div className="grid gap-4">
            <SettingInput
              label="Binance API Key (kosongkan jika tidak diubah)"
              value={apiKey}
              onChange={setApiKey}
              hint="Dari Binance → API Management"
              type="text"
            />
            <SettingInput
              label="Binance API Secret (kosongkan jika tidak diubah)"
              value={apiSecret}
              onChange={setApiSecret}
              hint="Hanya dikirm ke backend lokal, tidak tersimpan di browser"
              type="password"
            />
          </div>
          <div className="mt-4 flex items-center gap-3">
            <button
              type="button"
              onClick={() => runTest("binance")}
              disabled={testing === "binance"}
              className="rounded-md border border-slate-700 px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-slate-800 disabled:opacity-50"
            >
              {testing === "binance" ? "Testing..." : "Test Connection"}
            </button>
            {binanceTest && <ConnectionBadge result={binanceTest} />}
          </div>
          <p className="mt-3 text-xs text-slate-600">API key hanya dipakai jika mode Live aktif. Paper trading tidak butuh API key.</p>
        </section>

        {/* Anthropic / Claude AI */}
        <section className="rounded-lg border border-slate-800 bg-panel p-5">
          <h3 className="mb-4 text-sm font-semibold text-slate-300">Claude AI (Anthropic)</h3>
          {current && (
            <div className="mb-4 flex items-center gap-2 text-sm">
              <span className={`h-2 w-2 rounded-full ${current.hasAnthropicKey ? "bg-emerald-400" : "bg-slate-600"}`} />
              <span className="text-slate-400">API Key: </span>
              <span className="text-slate-200">{current.hasAnthropicKey ? current.anthropicKeyPreview : "Belum diset"}</span>
            </div>
          )}
          <SettingInput
            label="Anthropic API Key (kosongkan jika tidak diubah)"
            value={anthropicKey}
            onChange={setAnthropicKey}
            hint="Dari console.anthropic.com — dipakai untuk validasi keputusan AI (hybrid LLM)"
            type="password"
          />
          <div className="mt-4">
            <label className="mb-1 block text-xs text-slate-400">Model Claude</label>
            <select
              value={aiModel}
              onChange={e => setAiModel(e.target.value)}
              className="w-full rounded-md border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-slate-100 focus:outline-none focus:ring-1 focus:ring-emerald-500"
            >
              <option value="claude-opus-4-8">claude-opus-4-8 — Terbaik, ~$0.013/call</option>
              <option value="claude-sonnet-4-6">claude-sonnet-4-6 — Seimbang, lebih murah</option>
              <option value="claude-haiku-4-5-20251001">claude-haiku-4-5-20251001 — Tercepat, ~$0.002/call</option>
            </select>
            <p className="mt-1 text-xs text-slate-600">Pilih model sesuai kebutuhan akurasi vs. biaya. Haiku cocok untuk validasi awal.</p>
          </div>
          <div className="mt-4 flex items-center gap-3">
            <button
              type="button"
              onClick={() => runTest("anthropic")}
              disabled={testing === "anthropic"}
              className="rounded-md border border-slate-700 px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-slate-800 disabled:opacity-50"
            >
              {testing === "anthropic" ? "Testing..." : "Test Connection"}
            </button>
            {anthropicTest && <ConnectionBadge result={anthropicTest} />}
          </div>
          <p className="mt-3 text-xs text-slate-600">Opsional. Tanpa key, engine tetap jalan rule-based saja (tanpa validasi LLM). Simpan key dulu sebelum test.</p>
        </section>

        <div className="flex items-center gap-4">
          <button
            type="submit"
            disabled={saving}
            className="rounded-md bg-emerald-600 px-5 py-2 font-semibold text-white hover:bg-emerald-500 disabled:opacity-50"
          >
            {saving ? "Menyimpan..." : "Simpan Settings"}
          </button>
          {message && <span className="text-sm text-slate-300">{message}</span>}
        </div>
      </form>
    </main>
  );
}

function ConnectionBadge({ result }: { result: { connected: boolean; message: string; detail?: string | null } }) {
  return (
    <span className={`flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-semibold ${result.connected ? "bg-emerald-500/15 text-emerald-300" : "bg-red-500/15 text-red-300"}`}>
      <span className={`h-2 w-2 rounded-full ${result.connected ? "bg-emerald-400" : "bg-red-400"}`} />
      {result.connected ? "✓ " : "✗ "}{result.message}{result.detail ? ` — ${result.detail}` : ""}
    </span>
  );
}

function ModeButton({ active, onClick, label, danger = false }: { active: boolean; onClick: () => void; label: string; danger?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex-1 rounded-md border px-3 py-2 text-sm font-semibold transition-colors ${
        active
          ? danger
            ? "border-red-500/60 bg-red-500/15 text-red-300"
            : "border-emerald-500/60 bg-emerald-500/15 text-emerald-300"
          : "border-slate-700 bg-transparent text-slate-400"
      }`}
    >
      {label}
    </button>
  );
}

function SettingInput({
  label,
  value,
  onChange,
  hint,
  type = "text",
  min,
  max,
  step,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  hint?: string;
  type?: string;
  min?: string;
  max?: string;
  step?: string;
}) {
  return (
    <div>
      <label className="mb-1 block text-xs text-slate-400">{label}</label>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        min={min}
        max={max}
        step={step}
        className="w-full rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 focus:outline-none focus:ring-1 focus:ring-emerald-500"
      />
      {hint && <p className="mt-1 text-xs text-slate-600">{hint}</p>}
    </div>
  );
}

export function App() {
  const [isLoggedIn, setIsLoggedIn] = useState(() => sessionStorage.getItem("hft_auth") === "1");
  const [page, setPage] = useState<"dashboard" | "settings">("dashboard");

  if (!isLoggedIn) {
    return <LoginPage onLogin={() => setIsLoggedIn(true)} />;
  }

  return (
    <div className="min-h-screen bg-[#0b101a] text-slate-100">
      <AppHeader page={page} onPageChange={setPage} onLogout={() => { sessionStorage.removeItem("hft_auth"); setIsLoggedIn(false); }} />
      {page === "settings" ? <SettingsPage /> : <DashboardPage />}
    </div>
  );
}

function AppHeader({ page, onPageChange, onLogout }: { page: string; onPageChange: (p: "dashboard" | "settings") => void; onLogout: () => void }) {
  return (
    <header className="border-b border-slate-800 bg-[#0e1522]">
      <div className="mx-auto flex max-w-[1600px] flex-col gap-4 px-4 py-4 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex items-center gap-3">
          <Bot className="h-7 w-7 text-exchangeGreen" />
          <h1 className="text-xl font-semibold">BTCUSDT Perpetual Auto Trading</h1>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <nav className="flex gap-1">
            <NavTab active={page === "dashboard"} onClick={() => onPageChange("dashboard")} label="Dashboard" />
            <NavTab active={page === "settings"} onClick={() => onPageChange("settings")} label="Settings" icon={<Settings className="h-3.5 w-3.5" />} />
          </nav>
          <button
            type="button"
            onClick={onLogout}
            className="rounded-md border border-slate-700 px-3 py-2 text-sm text-slate-400 hover:text-slate-200"
          >
            Logout
          </button>
        </div>
      </div>
    </header>
  );
}

function NavTab({ active, onClick, label, icon }: { active: boolean; onClick: () => void; label: string; icon?: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex items-center gap-1.5 rounded-md px-3 py-2 text-sm font-medium transition-colors ${
        active ? "bg-slate-700 text-slate-100" : "text-slate-400 hover:text-slate-200"
      }`}
    >
      {icon}
      {label}
    </button>
  );
}

function DashboardPage() {
  const [connectionState, setConnectionState] = useState("connecting");
  const [price, setPrice] = useState<PriceTick | null>(null);
  const [markPrice, setMarkPrice] = useState<MarkPriceTick | null>(null);
  const [orderBook, setOrderBook] = useState<OrderBookSnapshot | null>(null);
  const [marginCall, setMarginCall] = useState<MarginCallEvent | null>(null);
  const [streamExpired, setStreamExpired] = useState<UserDataStreamExpiredEvent | null>(null);
  const [trades, setTrades] = useState<AggTradeTick[]>([]);
  const [klines, setKlines] = useState<KlineTick[]>([]);
  const [realtimePositions, setRealtimePositions] = useState<FuturesPositionInfo[]>([]);
  const [orderUpdates, setOrderUpdates] = useState<OrderUpdateEvent[]>([]);
  const { data: overview } = useQuery({ queryKey: ["overview"], queryFn: fetchOverview });
  const { data: walletBalances } = useQuery({ queryKey: ["wallet"], queryFn: fetchWallet, refetchInterval: 5000, retry: false });
  const { data: positions } = useQuery({ queryKey: ["positions", symbol], queryFn: fetchPositions, refetchInterval: 5000, retry: false });
  const { data: exchangeRules } = useQuery({ queryKey: ["exchange-rules", symbol], queryFn: fetchExchangeRules, refetchInterval: 60 * 60 * 1000, retry: false });
  const { data: killSwitch, refetch: refetchKillSwitch } = useQuery({ queryKey: ["kill-switch"], queryFn: fetchKillSwitch, refetchInterval: 5000, retry: false });
  const { data: journal } = useQuery({ queryKey: ["journal", symbol], queryFn: fetchJournal, refetchInterval: 5000, retry: false });
  const { data: riskDetails } = useQuery({ queryKey: ["risk-details", symbol], queryFn: fetchRiskDetails, refetchInterval: 5000, retry: false });
  const { data: backtest } = useQuery({ queryKey: ["backtest", symbol, "1h"], queryFn: fetchBacktest, retry: false });
  const { data: aiDecision } = useQuery({ queryKey: ["ai-decision", symbol], queryFn: fetchAiDecision, refetchInterval: 30000, retry: false });
  const usdtWallet = walletBalances?.find((wallet) => wallet.asset === "USDT");
  const displayedPositions = realtimePositions.length ? realtimePositions : positions ?? [];

  useEffect(() => {
    const connection = createTradingConnection();
    const subscribe = () => connection.invoke("SubscribeSymbol", symbol);
    let isMounted = true;

    fetchInitialMarkPrice()
      .then((tick) => {
        if (isMounted) setMarkPrice(tick);
      })
      .catch(() => undefined);

    fetchInitialKlines()
      .then((candles) => {
        if (isMounted) setKlines(candles);
      })
      .catch(() => undefined);

    fetchInitialAggTrades()
      .then((initialTrades) => {
        if (isMounted) setTrades(initialTrades);
      })
      .catch(() => undefined);

    const markPricePoll = window.setInterval(() => {
      fetchInitialMarkPrice()
        .then((tick) => {
          if (isMounted) setMarkPrice(tick);
        })
        .catch(() => undefined);
    }, 1000);

    const klinePoll = window.setInterval(() => {
      fetchInitialKlines()
        .then((candles) => {
          if (isMounted) setKlines(candles);
        })
        .catch(() => undefined);
    }, 2000);

    const tradePoll = window.setInterval(() => {
      fetchInitialAggTrades()
        .then((initialTrades) => {
          if (isMounted) setTrades((current) => (current.length ? current : initialTrades));
        })
        .catch(() => undefined);
    }, 5000);

    connection.on("price", (tick: PriceTick) => setPrice(tick));
    connection.on("markPrice", (tick: MarkPriceTick) => setMarkPrice(tick));
    connection.on("marginCall", (event: MarginCallEvent) => setMarginCall(event));
    connection.on("userDataStreamExpired", (event: UserDataStreamExpiredEvent) => setStreamExpired(event));
    connection.on("accountUpdate", (event: AccountUpdateEvent) => setRealtimePositions(mapAccountPositions(event)));
    connection.on("orderUpdate", (event: OrderUpdateEvent) => {
      setOrderUpdates((current) => [event, ...current.filter((order) => order.orderId !== event.orderId)].slice(0, 12));
    });
    connection.on("order", (event: TradeOrderResult) => {
      const update = mapTradeOrderResult(event);
      setOrderUpdates((current) => [update, ...current.filter((order) => order.orderId !== update.orderId)].slice(0, 12));
    });
    connection.on("orderBook", (snapshot: OrderBookSnapshot) => setOrderBook(snapshot));
    connection.on("aggTrade", (tick: AggTradeTick) => {
      setTrades((current) => [tick, ...current].slice(0, 28));
    });
    connection.on("kline", (tick: KlineTick) => {
      if (tick.interval !== "1m") return;
      setKlines((current) => {
        const last = current[current.length - 1];
        if (last?.openTime === tick.openTime) {
          return [...current.slice(0, -1), tick];
        }
        return [...current.slice(-119), tick];
      });
    });

    connection
      .start()
      .then(subscribe)
      .then(() => setConnectionState("live"))
      .catch(() => setConnectionState("offline"));

    connection.onreconnecting(() => setConnectionState("reconnecting"));
    connection.onreconnected(() => {
      subscribe()
        .then(() => setConnectionState("live"))
        .catch(() => setConnectionState("offline"));
    });
    connection.onclose(() => setConnectionState("offline"));

    return () => {
      isMounted = false;
      window.clearInterval(markPricePoll);
      window.clearInterval(klinePoll);
      window.clearInterval(tradePoll);
      connection.stop();
    };
  }, []);

  return (
    <main className="mx-auto grid max-w-[1600px] gap-4 px-4 py-4">
      <div className="flex flex-wrap items-center gap-2 pt-2">
        <StatusPill label={connectionState} />
        <span className="rounded-md border border-slate-700 px-3 py-2 text-sm text-slate-300">Mode: Mainnet Data / Paper Trading</span>
        <span className="rounded-md border border-slate-700 px-3 py-2 text-sm text-slate-300">Symbol: {symbol}</span>
      </div>
        {marginCall && <MarginCallAlert event={marginCall} onDismiss={() => setMarginCall(null)} />}
        {streamExpired && <UserStreamExpiredAlert event={streamExpired} onDismiss={() => setStreamExpired(null)} />}

        <section className="grid gap-3 md:grid-cols-2 xl:grid-cols-6">
          <Metric title="Wallet" value={usdtWallet?.balance ?? overview?.walletBalance ?? 0} icon={<Wallet />} />
          <Metric title="Available" value={usdtWallet?.availableBalance ?? overview?.availableBalance ?? 0} icon={<ShieldCheck />} />
          <Metric title="Mark Price" value={markPrice?.markPrice ?? price?.price ?? 0} icon={<Activity />} />
          <Metric title="Index Price" value={markPrice?.indexPrice ?? 0} icon={<Activity />} />
          <Metric title="Funding" value={(markPrice?.fundingRate ?? 0) * 100} suffix="%" icon={<Radio />} />
          <Metric title="Unreal PnL" value={usdtWallet?.crossUnrealizedPnl ?? 0} danger={(usdtWallet?.crossUnrealizedPnl ?? 0) < 0} />
        </section>

        <section className="grid gap-4 xl:grid-cols-[1.5fr_0.8fr_0.9fr]">
          <Panel title="Realtime Chart">
            <div className="h-[420px]">
              <CandlestickChart candles={klines} />
            </div>
          </Panel>

          <Panel title="Order Book">
            <OrderBook snapshot={orderBook} />
          </Panel>

          <Panel title="Manual Paper Order">
            <ManualOrder />
          </Panel>
        </section>

        <section className="grid gap-4 xl:grid-cols-[1fr_1fr_1fr]">
          <Panel title="Trade Tape">
            <TradeTape trades={trades} />
          </Panel>
          <Panel title="Open Position">
            <OpenPositions positions={displayedPositions} />
          </Panel>
          <Panel title="Order Updates">
            <OrderUpdates updates={orderUpdates} />
          </Panel>
        </section>

        <section className="grid gap-4 xl:grid-cols-[1fr_1fr_1fr]">
          <Panel title="Exchange Rules">
            <ExchangeRules rules={exchangeRules} />
          </Panel>
          <Panel title="Kill Switch">
            <KillSwitchPanel state={killSwitch} onChanged={refetchKillSwitch} />
          </Panel>
          <Panel title="AI Decision">
            <AiDecisionPanel decision={aiDecision} />
          </Panel>
        </section>

        <section className="grid gap-4 xl:grid-cols-[1fr_1fr_1fr]">
          <Panel title="Trade Journal">
            <TradeJournal journal={journal} />
          </Panel>
          <Panel title="Position Risk">
            <PositionRiskPanel risk={riskDetails} />
          </Panel>
          <Panel title="Backtest">
            <BacktestPanel result={backtest} />
          </Panel>
        </section>
    </main>
  );
}

function StatusPill({ label }: { label: string }) {
  const isLive = label === "live";
  return (
    <span className={`rounded-md px-3 py-2 text-sm font-semibold ${isLive ? "bg-emerald-500/15 text-emerald-300" : "bg-amber-500/15 text-amber-300"}`}>
      {label.toUpperCase()}
    </span>
  );
}

function MarginCallAlert({ event, onDismiss }: { event: MarginCallEvent; onDismiss: () => void }) {
  const firstPosition = event.positions[0];
  return (
    <section className="rounded-lg border border-red-500/40 bg-red-950/40 p-4 text-sm">
      <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div>
          <div className="font-semibold text-red-200">Margin Call Alert</div>
          <div className="mt-1 text-red-100">
            {event.symbol} cross wallet {formatNumber(event.crossWalletBalance, 4)}
            {firstPosition && ` | ${firstPosition.positionSide} size ${formatNumber(firstPosition.positionAmount, 4)} | MM ${formatNumber(firstPosition.maintenanceMarginRequired, 4)}`}
          </div>
          <div className="mt-1 text-xs text-red-200/70">{new Date(event.eventTime).toLocaleString()}</div>
        </div>
        <button type="button" onClick={onDismiss} className="rounded-md border border-red-400/40 px-3 py-2 font-semibold text-red-100">
          Dismiss
        </button>
      </div>
    </section>
  );
}

function UserStreamExpiredAlert({ event, onDismiss }: { event: UserDataStreamExpiredEvent; onDismiss: () => void }) {
  return (
    <section className="rounded-lg border border-amber-500/40 bg-amber-950/40 p-4 text-sm">
      <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div>
          <div className="font-semibold text-amber-200">User Data Stream Expired</div>
          <div className="mt-1 text-amber-100">{event.symbol} private stream expired. Reconnecting with a new listen key.</div>
          <div className="mt-1 text-xs text-amber-200/70">{new Date(event.eventTime).toLocaleString()}</div>
        </div>
        <button type="button" onClick={onDismiss} className="rounded-md border border-amber-400/40 px-3 py-2 font-semibold text-amber-100">
          Dismiss
        </button>
      </div>
    </section>
  );
}

function mapAccountPositions(event: AccountUpdateEvent): FuturesPositionInfo[] {
  return event.positions.map((position) => ({
    symbol: position.symbol,
    positionSide: position.positionSide,
    positionAmount: position.positionAmount,
    entryPrice: position.entryPrice,
    breakEvenPrice: position.breakEvenPrice,
    markPrice: 0,
    unrealizedProfit: position.unrealizedProfit,
    liquidationPrice: 0,
    leverage: 0,
    maxNotionalValue: 0,
    marginType: position.marginType,
    isolatedMargin: position.isolatedWallet,
    isAutoAddMargin: false,
    updateTime: event.transactionTime
  }));
}

function mapTradeOrderResult(event: TradeOrderResult): OrderUpdateEvent {
  const orderStatus = mapOrderStatus(event.status);
  const filledQuantity = orderStatus === "FILLED" ? event.quantity : 0;

  return {
    symbol: event.symbol,
    orderId: event.orderId,
    clientOrderId: event.orderId,
    side: event.message.toLowerCase().includes("short") ? "SELL" : "BUY",
    orderType: "LOCAL",
    executionType: event.isPaper ? "PAPER" : "SUBMITTED",
    orderStatus,
    timeInForce: "",
    originalQuantity: event.quantity,
    originalPrice: event.price ?? 0,
    averagePrice: event.price ?? 0,
    stopPrice: 0,
    lastFilledQuantity: filledQuantity,
    accumulatedFilledQuantity: filledQuantity,
    lastFilledPrice: event.price ?? 0,
    realizedProfit: 0,
    reduceOnly: event.message.toLowerCase().includes("close") || event.message.toLowerCase().includes("protective"),
    positionSide: "BOTH",
    workingType: "",
    orderTradeTime: event.time,
    eventTime: event.time,
    source: event.isPaper ? "Paper" : "Local"
  };
}

function mapOrderStatus(status: number | string) {
  if (typeof status === "string") return status.toUpperCase();

  const statuses: Record<number, string> = {
    1: "NEW",
    2: "PARTIALLY_FILLED",
    3: "FILLED",
    4: "CANCELED",
    5: "REJECTED",
    6: "EXPIRED"
  };

  return statuses[status] ?? String(status);
}

function Metric({ title, value, suffix = "", icon, danger = false }: { title: string; value: number; suffix?: string; icon?: ReactNode; danger?: boolean }) {
  return (
    <div className="rounded-lg border border-slate-800 bg-panel p-4">
      <div className="flex items-center justify-between text-sm text-slate-400">
        <span>{title}</span>
        <span className="text-slate-500">{icon}</span>
      </div>
      <div className={`mt-3 text-2xl font-semibold ${danger ? "text-exchangeRed" : "text-slate-50"}`}>
        {Number(value).toLocaleString(undefined, { maximumFractionDigits: 4 })}
        {suffix}
      </div>
    </div>
  );
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="rounded-lg border border-slate-800 bg-panel">
      <div className="border-b border-slate-800 px-4 py-3 text-sm font-semibold text-slate-200">{title}</div>
      <div className="p-4">{children}</div>
    </div>
  );
}

function CandlestickChart({ candles }: { candles: KlineTick[] }) {
  const width = 980;
  const height = 420;
  const padding = { top: 18, right: 82, bottom: 26, left: 18 };
  const visibleCandles = candles.slice(-90);

  if (!visibleCandles.length) {
    return <EmptyState text="Waiting for realtime candlestick data..." />;
  }

  const highs = visibleCandles.map((candle) => Number(candle.high));
  const lows = visibleCandles.map((candle) => Number(candle.low));
  const maxPrice = Math.max(...highs);
  const minPrice = Math.min(...lows);
  const priceRange = Math.max(maxPrice - minPrice, 1);
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const slotWidth = plotWidth / visibleCandles.length;
  const bodyWidth = Math.max(3, Math.min(slotWidth * 0.68, 12));
  const last = visibleCandles[visibleCandles.length - 1];
  const lastPrice = Number(last.close);

  const yForPrice = (price: number) => padding.top + ((maxPrice - price) / priceRange) * plotHeight;
  const priceTicks = Array.from({ length: 6 }, (_, index) => maxPrice - (priceRange / 5) * index);

  return (
    <div className="h-full w-full overflow-hidden rounded-md bg-slate-950">
      <svg viewBox={`0 0 ${width} ${height}`} className="h-full w-full" role="img" aria-label="Realtime BTCUSDT candlestick chart">
        <rect x="0" y="0" width={width} height={height} fill="#020617" />

        {priceTicks.map((tick) => {
          const y = yForPrice(tick);
          return (
            <g key={tick}>
              <line x1={padding.left} x2={width - padding.right + 8} y1={y} y2={y} stroke="#1e293b" strokeDasharray="4 6" />
              <text x={width - padding.right + 14} y={y + 4} fill="#64748b" fontSize="12">
                {tick.toLocaleString(undefined, { maximumFractionDigits: 2 })}
              </text>
            </g>
          );
        })}

        {visibleCandles.map((candle, index) => {
          const open = Number(candle.open);
          const high = Number(candle.high);
          const low = Number(candle.low);
          const close = Number(candle.close);
          const x = padding.left + index * slotWidth + slotWidth / 2;
          const yHigh = yForPrice(high);
          const yLow = yForPrice(low);
          const yOpen = yForPrice(open);
          const yClose = yForPrice(close);
          const bullish = close >= open;
          const color = bullish ? "#16c784" : "#ea3943";
          const bodyY = Math.min(yOpen, yClose);
          const bodyHeight = Math.max(Math.abs(yClose - yOpen), 1.5);
          const time = new Date(candle.openTime).toLocaleTimeString();

          return (
            <g key={`${candle.openTime}-${index}`}>
              <title>{`${time} O:${open} H:${high} L:${low} C:${close}`}</title>
              <line x1={x} x2={x} y1={yHigh} y2={yLow} stroke={color} strokeWidth="1.5" />
              <rect
                x={x - bodyWidth / 2}
                y={bodyY}
                width={bodyWidth}
                height={bodyHeight}
                rx="1"
                fill={bullish ? "rgba(22,199,132,0.75)" : "rgba(234,57,67,0.75)"}
                stroke={color}
                strokeWidth="1"
              />
            </g>
          );
        })}

        <line
          x1={padding.left}
          x2={width - padding.right + 8}
          y1={yForPrice(lastPrice)}
          y2={yForPrice(lastPrice)}
          stroke="#f8fafc"
          strokeDasharray="6 5"
          opacity="0.65"
        />
        <rect x={width - padding.right + 10} y={yForPrice(lastPrice) - 12} width="70" height="24" rx="4" fill="#111827" stroke="#334155" />
        <text x={width - padding.right + 45} y={yForPrice(lastPrice) + 4} textAnchor="middle" fill="#f8fafc" fontSize="12" fontWeight="700">
          {lastPrice.toLocaleString(undefined, { maximumFractionDigits: 2 })}
        </text>

        <text x={padding.left} y={height - 8} fill="#64748b" fontSize="12">
          {new Date(visibleCandles[0].openTime).toLocaleTimeString()}
        </text>
        <text x={width - padding.right - 10} y={height - 8} textAnchor="end" fill="#64748b" fontSize="12">
          {new Date(last.openTime).toLocaleTimeString()}
        </text>
      </svg>
    </div>
  );
}

function OrderBook({ snapshot }: { snapshot: OrderBookSnapshot | null }) {
  const asks = snapshot?.asks.slice(0, 10).reverse() ?? [];
  const bids = snapshot?.bids.slice(0, 10) ?? [];
  return (
    <div className="grid gap-2 text-sm">
      {asks.map((level) => (
        <BookRow key={`ask-${level.price}`} level={level} side="ask" />
      ))}
      <div className="rounded bg-slate-900 px-3 py-2 text-center font-semibold text-slate-200">
        Spread {snapshot?.spread?.toFixed(2) ?? "-"} | Imb {snapshot?.imbalance?.toFixed(3) ?? "-"}
      </div>
      {bids.map((level) => (
        <BookRow key={`bid-${level.price}`} level={level} side="bid" />
      ))}
    </div>
  );
}

function BookRow({ level, side }: { level: { price: number; quantity: number }; side: "bid" | "ask" }) {
  return (
    <div className="grid grid-cols-2 rounded px-3 py-1.5" style={{ background: side === "bid" ? "rgba(22,199,132,0.08)" : "rgba(234,57,67,0.08)" }}>
      <span className={side === "bid" ? "text-exchangeGreen" : "text-exchangeRed"}>{Number(level.price).toFixed(2)}</span>
      <span className="text-right text-slate-300">{Number(level.quantity).toFixed(4)}</span>
    </div>
  );
}

function TradeTape({ trades }: { trades: AggTradeTick[] }) {
  return (
    <div className="max-h-[310px] overflow-y-auto text-sm">
      {trades.map((trade, index) => (
        <div key={`${trade.time}-${index}`} className="grid grid-cols-3 border-b border-slate-800 py-2">
          <span className={trade.buyerIsMaker ? "text-exchangeRed" : "text-exchangeGreen"}>{Number(trade.price).toFixed(2)}</span>
          <span className="text-right text-slate-300">{Number(trade.quantity).toFixed(4)}</span>
          <span className="text-right text-slate-500">{new Date(trade.time).toLocaleTimeString()}</span>
        </div>
      ))}
      {!trades.length && <EmptyState text="Waiting for realtime trades..." />}
    </div>
  );
}

function OrderUpdates({ updates }: { updates: OrderUpdateEvent[] }) {
  return (
    <div className="max-h-[310px] overflow-y-auto text-sm">
      {updates.map((update) => {
        const isBuy = update.side === "BUY";
        const isDone = ["FILLED", "CANCELED", "EXPIRED"].includes(update.orderStatus);
        const pnl = Number(update.realizedProfit);

        return (
          <div key={`${update.orderId}-${update.orderStatus}-${update.accumulatedFilledQuantity}`} className="border-b border-slate-800 py-3 last:border-b-0">
            <div className="flex items-start justify-between gap-3">
              <div>
                <div className="font-semibold text-slate-100">{update.symbol}</div>
                <div className="mt-1 text-xs uppercase text-slate-500">
                  {update.source ?? "Binance"} / {update.orderType} / {update.executionType}
                </div>
              </div>
              <span className={`rounded px-2 py-1 text-xs font-semibold ${isDone ? "bg-slate-700 text-slate-200" : "bg-amber-500/15 text-amber-300"}`}>
                {update.orderStatus}
              </span>
            </div>
            <div className="mt-3 grid grid-cols-2 gap-x-4 gap-y-2 text-slate-400">
              <PositionStat label="Side" value={`${update.reduceOnly ? "Close" : "Open"} ${isBuy ? "Buy" : "Sell"}`} />
              <PositionStat label="Filled" value={`${formatNumber(update.accumulatedFilledQuantity, 4)} / ${formatNumber(update.originalQuantity, 4)}`} />
              <PositionStat label="Avg Price" value={formatNumber(update.averagePrice || update.lastFilledPrice || update.originalPrice)} />
              <PositionStat label="Realized PnL" value={formatSignedNumber(pnl)} />
            </div>
            <div className="mt-2 text-xs text-slate-500">{new Date(update.orderTradeTime).toLocaleTimeString()}</div>
          </div>
        );
      })}
      {!updates.length && <EmptyState text="No realtime order updates yet." />}
    </div>
  );
}

function ExchangeRules({ rules }: { rules?: FuturesSymbolRules }) {
  if (!rules) {
    return <EmptyState text="Loading Binance exchangeInfo rules..." />;
  }

  return (
    <div className="grid gap-3 text-sm md:grid-cols-2 xl:grid-cols-5">
      <PositionStat label="Status" value={rules.status} />
      <PositionStat label="Tick Size" value={formatNumber(rules.tickSize, 8)} />
      <PositionStat label="Step Size" value={formatNumber(rules.stepSize, 8)} />
      <PositionStat label="Min Qty" value={formatNumber(rules.minQuantity, 8)} />
      <PositionStat label="Min Notional" value={formatNumber(rules.minNotional, 2)} />
    </div>
  );
}

function KillSwitchPanel({ state, onChanged }: { state?: KillSwitchState; onChanged: () => void }) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [message, setMessage] = useState("");

  const submit = async (enabled: boolean) => {
    setIsSubmitting(true);
    setMessage("");
    try {
      const result = enabled ? await enableKillSwitch() : await disableKillSwitch();
      setMessage(result.message);
      onChanged();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Kill switch request rejected");
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <div className="grid gap-3 text-sm">
      <div className="flex items-center justify-between gap-3">
        <div>
          <div className={`font-semibold ${state?.enabled ? "text-red-300" : "text-slate-200"}`}>
            {state?.enabled ? "ACTIVE" : "DISABLED"}
          </div>
          <div className="mt-1 text-xs text-slate-500">{state?.isPaper ? "Paper heartbeat" : "Binance futures countdownCancelAll"}</div>
        </div>
        <StatusPill label={state?.enabled ? "armed" : "idle"} />
      </div>
      <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-slate-400">
        <PositionStat label="Countdown" value={`${formatNumber((state?.countdownTimeMs ?? 0) / 1000, 0)}s`} />
        <PositionStat label="Heartbeat" value={`${formatNumber((state?.heartbeatIntervalMs ?? 0) / 1000, 0)}s`} />
        <PositionStat label="Last" value={state?.lastHeartbeatAt ? new Date(state.lastHeartbeatAt).toLocaleTimeString() : "-"} />
        <PositionStat label="Next" value={state?.nextHeartbeatAt ? new Date(state.nextHeartbeatAt).toLocaleTimeString() : "-"} />
      </div>
      <div className="grid grid-cols-2 gap-2">
        <button type="button" disabled={isSubmitting} onClick={() => submit(true)} className="rounded-md border border-red-500/50 bg-red-500/10 px-3 py-2 font-semibold text-red-200 disabled:opacity-50">
          Enable
        </button>
        <button type="button" disabled={isSubmitting} onClick={() => submit(false)} className="rounded-md border border-slate-700 px-3 py-2 font-semibold text-slate-200 disabled:opacity-50">
          Disable
        </button>
      </div>
      {(message || state?.message) && <div className="rounded-md border border-slate-800 bg-slate-950 px-3 py-2 text-xs text-slate-300">{message || state?.message}</div>}
    </div>
  );
}

function TradeJournal({ journal }: { journal?: JournalResponse }) {
  if (!journal) {
    return <EmptyState text="Loading trade journal..." />;
  }

  return (
    <div className="grid gap-3 text-sm">
      <div className="grid grid-cols-3 gap-2">
        <PositionStat label="Orders" value={formatNumber(journal.summary.totalOrders, 0)} />
        <PositionStat label="Filled" value={formatNumber(journal.summary.filledOrders, 0)} />
        <PositionStat label="Paper" value={formatNumber(journal.summary.paperOrders, 0)} />
      </div>
      <div className="max-h-[300px] overflow-y-auto">
        {journal.orders.map((order) => (
          <div key={order.id} className="border-b border-slate-800 py-3 last:border-b-0">
            <div className="flex items-start justify-between gap-3">
              <div>
                <div className="font-semibold text-slate-100">{order.side} {order.kind}</div>
                <div className="mt-1 text-xs text-slate-500">{order.reason || order.exchangeOrderId || order.id}</div>
              </div>
              <span className={`rounded px-2 py-1 text-xs font-semibold ${order.status === "Filled" ? "bg-emerald-500/15 text-emerald-300" : "bg-slate-700 text-slate-200"}`}>
                {order.status}
              </span>
            </div>
            <div className="mt-2 grid grid-cols-3 gap-2 text-slate-400">
              <PositionStat label="Qty" value={formatNumber(order.quantity, 4)} />
              <PositionStat label="Price" value={order.price ? formatNumber(order.price) : "-"} />
              <PositionStat label="Time" value={new Date(order.createdAt).toLocaleTimeString()} />
            </div>
          </div>
        ))}
        {!journal.orders.length && <EmptyState text="No journal orders yet." />}
      </div>
    </div>
  );
}

function PositionRiskPanel({ risk }: { risk?: RiskDetailResponse }) {
  if (!risk) {
    return <EmptyState text="Loading position risk..." />;
  }

  const riskClass = risk.portfolioRiskLevel === "critical"
    ? "text-red-300"
    : risk.portfolioRiskLevel === "warning"
      ? "text-amber-300"
      : "text-emerald-300";

  return (
    <div className="grid gap-3 text-sm">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className={`font-semibold uppercase ${riskClass}`}>{risk.portfolioRiskLevel}</div>
          <div className="mt-1 text-xs text-slate-500">Max daily loss {formatPercent(risk.maxDailyLossPercent)} / leverage {formatNumber(risk.defaultLeverage, 0)}x</div>
        </div>
        <span className="rounded bg-slate-900 px-2 py-1 text-xs text-slate-300">{risk.symbol}</span>
      </div>
      <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-slate-400">
        <PositionStat label="Equity" value={formatNumber(risk.equity)} />
        <PositionStat label="Available" value={formatNumber(risk.availableBalance)} />
        <PositionStat label="Exposure" value={formatPercent(risk.exposureRatio)} />
        <PositionStat label="Daily Loss Used" value={formatPercent(risk.dailyLossUsedPercent)} />
        <PositionStat label="Notional" value={formatNumber(risk.totalNotional)} />
        <PositionStat label="Unreal PnL" value={formatSignedNumber(risk.totalUnrealizedProfit)} />
      </div>
      <div className="max-h-[190px] overflow-y-auto">
        {risk.positions.map((position) => (
          <div key={`${position.symbol}-${position.positionSide}`} className="border-t border-slate-800 py-3">
            <div className="flex justify-between">
              <span className="font-semibold text-slate-200">{position.positionSide} / {position.marginType}</span>
              <span className={position.riskLevel === "normal" ? "text-emerald-300" : position.riskLevel === "warning" ? "text-amber-300" : "text-red-300"}>{position.riskLevel}</span>
            </div>
            <div className="mt-2 grid grid-cols-2 gap-2 text-slate-400">
              <PositionStat label="Margin Ratio" value={formatPercent(position.marginRatio)} />
              <PositionStat label="Liq Buffer" value={formatPercent(position.liquidationBufferPercent)} />
            </div>
          </div>
        ))}
        {!risk.positions.length && <EmptyState text="No active risk exposure." />}
      </div>
    </div>
  );
}

function BacktestPanel({ result }: { result?: BacktestResult }) {
  if (!result) {
    return <EmptyState text="Running multi-indicator backtest..." />;
  }

  return (
    <div className="grid gap-3 text-sm">
      <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-slate-400">
        <PositionStat label="Net PnL" value={formatSignedNumber(result.netPnl)} />
        <PositionStat label="Return" value={formatPercent(result.netPnlPercent)} />
        <PositionStat label="Win Rate" value={formatPercent(result.winRate)} />
        <PositionStat label="Max DD" value={formatPercent(result.maxDrawdownPercent)} />
        <PositionStat label="Trades" value={formatNumber(result.totalTrades, 0)} />
        <PositionStat label="Profit Factor" value={formatNumber(result.profitFactor, 2)} />
      </div>
      <div className="rounded-md border border-slate-800 bg-slate-950 p-3 text-xs text-slate-400">
        {result.indicators.join(" / ")}
      </div>
      <div className="max-h-[165px] overflow-y-auto">
        {result.trades.slice(0, 6).map((trade, index) => (
          <div key={`${trade.entryTime}-${index}`} className="grid grid-cols-4 border-b border-slate-800 py-2 text-xs last:border-b-0">
            <span className={trade.side === "LONG" ? "text-exchangeGreen" : "text-exchangeRed"}>{trade.side}</span>
            <span className="text-slate-300">{formatSignedNumber(trade.pnl)}</span>
            <span className="text-slate-500">{trade.exitReason}</span>
            <span className="text-right text-slate-500">{new Date(trade.exitTime).toLocaleDateString()}</span>
          </div>
        ))}
        {!result.trades.length && <EmptyState text="No trades generated by backtest." />}
      </div>
    </div>
  );
}

function OpenPositions({ positions }: { positions: FuturesPositionInfo[] }) {
  const activePositions = positions.filter((position) => Math.abs(Number(position.positionAmount)) > 0);
  const [closingKey, setClosingKey] = useState("");
  const [message, setMessage] = useState("");

  if (!activePositions.length) {
    return <EmptyState text="No open position." />;
  }

  const closePosition = async (position: FuturesPositionInfo) => {
    const key = `${position.symbol}-${position.positionSide}`;
    setClosingKey(key);
    setMessage("");
    try {
      const quantity = Math.abs(Number(position.positionAmount));
      const side = position.positionAmount >= 0 ? 1 : 2;
      const result = await closeManualPosition(side, quantity);
      setMessage(`${result.isPaper ? "Paper" : "Binance"} close ${result.orderId} submitted`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Close rejected");
    } finally {
      setClosingKey("");
    }
  };

  return (
    <div className="grid gap-3 text-sm">
      {activePositions.map((position) => {
        const pnl = Number(position.unrealizedProfit);
        const key = `${position.symbol}-${position.positionSide}`;
        return (
          <div key={key} className="rounded-md border border-slate-800 bg-slate-950 p-3">
            <div className="mb-3 flex items-center justify-between">
              <div>
                <div className="font-semibold text-slate-100">{position.symbol}</div>
                <div className="text-xs uppercase text-slate-500">{position.positionSide} / {position.marginType}</div>
              </div>
              <span className={`rounded px-2 py-1 text-xs font-semibold ${pnl >= 0 ? "bg-emerald-500/15 text-emerald-300" : "bg-red-500/15 text-red-300"}`}>
                {formatNumber(pnl)}
              </span>
            </div>
            <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-slate-400">
              <PositionStat label="Size" value={formatNumber(position.positionAmount, 4)} />
              <PositionStat label="Leverage" value={`${formatNumber(position.leverage, 0)}x`} />
              <PositionStat label="Entry" value={formatNumber(position.entryPrice)} />
              <PositionStat label="Break Even" value={formatNumber(position.breakEvenPrice)} />
              <PositionStat label="Mark" value={formatNumber(position.markPrice)} />
              <PositionStat label="Liq" value={formatNumber(position.liquidationPrice)} />
            </div>
            <button
              type="button"
              disabled={closingKey === key}
              onClick={() => closePosition(position)}
              className="mt-3 w-full rounded-md border border-slate-700 px-3 py-2 font-semibold text-slate-200 disabled:opacity-50"
            >
              Close Position
            </button>
          </div>
        );
      })}
      {message && <div className="rounded-md border border-slate-800 bg-slate-950 px-3 py-2 text-xs text-slate-300">{message}</div>}
    </div>
  );
}

function PositionStat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-xs text-slate-500">{label}</div>
      <div className="font-medium text-slate-200">{value}</div>
    </div>
  );
}

function ManualOrder() {
  const [quantity, setQuantity] = useState("0.001");
  const [leverage, setLeverage] = useState("5");
  const [takeProfit, setTakeProfit] = useState("");
  const [stopLoss, setStopLoss] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [message, setMessage] = useState("");

  const submit = async (side: 1 | 2) => {
    setIsSubmitting(true);
    setMessage("");
    try {
      const result = await placeManualOrder(
        side,
        Number(quantity),
        Number(leverage),
        takeProfit ? Number(takeProfit) : undefined,
        stopLoss ? Number(stopLoss) : undefined
      );
      setMessage(`${result.isPaper ? "Paper" : "Binance"} order ${result.orderId} submitted`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Order rejected");
    } finally {
      setIsSubmitting(false);
    }
  };

  const close = async (side: 1 | 2) => {
    setIsSubmitting(true);
    setMessage("");
    try {
      const result = await closeManualPosition(side, Number(quantity));
      setMessage(`${result.isPaper ? "Paper" : "Binance"} close ${result.orderId} submitted`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Close rejected");
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <form className="grid gap-3">
      <div className="grid grid-cols-2 gap-2">
        <button type="button" disabled={isSubmitting} onClick={() => submit(1)} className="rounded-md bg-exchangeGreen px-3 py-2 font-semibold text-slate-950 disabled:opacity-50">Open Long</button>
        <button type="button" disabled={isSubmitting} onClick={() => submit(2)} className="rounded-md bg-exchangeRed px-3 py-2 font-semibold text-white disabled:opacity-50">Open Short</button>
      </div>
      <input className="rounded-md border border-slate-700 bg-slate-950 px-3 py-2" placeholder="Lot / quantity" value={quantity} onChange={(event) => setQuantity(event.target.value)} />
      <input className="rounded-md border border-slate-700 bg-slate-950 px-3 py-2" placeholder="Leverage" value={leverage} onChange={(event) => setLeverage(event.target.value)} />
      <input className="rounded-md border border-slate-700 bg-slate-950 px-3 py-2" placeholder="Take profit" value={takeProfit} onChange={(event) => setTakeProfit(event.target.value)} />
      <input className="rounded-md border border-slate-700 bg-slate-950 px-3 py-2" placeholder="Stop loss" value={stopLoss} onChange={(event) => setStopLoss(event.target.value)} />
      <div className="grid grid-cols-2 gap-2">
        <button type="button" disabled={isSubmitting} onClick={() => close(1)} className="rounded-md border border-slate-700 px-3 py-2 font-semibold text-slate-200 disabled:opacity-50">Close Long</button>
        <button type="button" disabled={isSubmitting} onClick={() => close(2)} className="rounded-md border border-slate-700 px-3 py-2 font-semibold text-slate-200 disabled:opacity-50">Close Short</button>
      </div>
      {message && <div className="rounded-md border border-slate-800 bg-slate-950 px-3 py-2 text-xs text-slate-300">{message}</div>}
    </form>
  );
}

const ACTION_LABELS: Record<number, string> = {
  1: "STRONG SELL", 2: "SELL", 3: "WEAK SELL", 4: "HOLD", 5: "WEAK BUY", 6: "BUY", 7: "STRONG BUY"
};
const REGIME_LABELS: Record<number, string> = {
  0: "Trending", 1: "Ranging", 2: "High Volatility", 3: "Low Volatility"
};

function AiDecisionPanel({ decision }: { decision?: AiDecision }) {
  if (!decision) {
    return <EmptyState text="Running multi-factor AI analysis (technical, order flow, derivatives, sentiment)..." />;
  }

  const action = ACTION_LABELS[decision.action] ?? "UNKNOWN";
  const isBuy = decision.action >= 5;
  const isSell = decision.action <= 3;
  const actionColor = isBuy ? "text-emerald-300" : isSell ? "text-red-300" : "text-slate-300";

  return (
    <div className="grid gap-3 text-sm">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className={`text-lg font-bold ${actionColor}`}>{action}</div>
          <div className="mt-1 text-xs text-slate-500">{REGIME_LABELS[decision.regime] ?? "?"} regime</div>
        </div>
        <div className="text-right">
          <div className="text-2xl font-semibold text-slate-100">{formatNumber(decision.confidence, 0)}</div>
          <div className="text-xs text-slate-500">confidence</div>
        </div>
      </div>

      <div className={`rounded-md px-3 py-2 text-xs font-semibold ${decision.shouldTrade ? "bg-emerald-500/15 text-emerald-300" : "bg-slate-700/50 text-slate-300"}`}>
        {decision.shouldTrade ? "✓ TRADE SIGNAL ACTIVE" : `NO TRADE — ${decision.noTradeReason || "conditions not met"}`}
      </div>

      <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-slate-400">
        <PositionStat label="Probability" value={`${formatNumber(decision.probabilityOfSuccess, 0)}%`} />
        <PositionStat label="Risk/Reward" value={`1:${formatNumber(decision.riskReward, 2)}`} />
        <PositionStat label="Entry" value={formatNumber(decision.entryPrice)} />
        <PositionStat label="Leverage" value={`${decision.leverage}x`} />
        <PositionStat label="Stop Loss" value={formatNumber(decision.stopLoss)} />
        <PositionStat label="Take Profit" value={formatNumber(decision.takeProfit)} />
        <PositionStat label="Size" value={formatNumber(decision.positionSizeQuantity, 4)} />
        <PositionStat label="Trailing" value={`${formatNumber(decision.trailingStopPercent, 2)}%`} />
      </div>

      <div>
        <div className="mb-1 text-xs text-slate-500">Factor Scores (0-100)</div>
        <div className="grid gap-1">
          {Object.entries(decision.scores).map(([name, score]) => (
            <div key={name} className="flex items-center gap-2">
              <span className="w-24 text-xs text-slate-400">{name}</span>
              <div className="h-2 flex-1 overflow-hidden rounded bg-slate-800">
                <div
                  className={`h-full ${score >= 60 ? "bg-emerald-500" : score <= 40 ? "bg-red-500" : "bg-slate-500"}`}
                  style={{ width: `${Math.max(0, Math.min(100, score))}%` }}
                />
              </div>
              <span className="w-7 text-right text-xs text-slate-300">{formatNumber(score, 0)}</span>
            </div>
          ))}
        </div>
      </div>

      {decision.llm.used && (
        <div className="rounded-md border border-indigo-500/30 bg-indigo-950/30 p-3">
          <div className="mb-1 flex items-center gap-2 text-xs font-semibold text-indigo-300">
            <Bot className="h-3.5 w-3.5" /> Claude validation: {decision.llm.confirmed ? "CONFIRMED" : "VETOED"}
          </div>
          {decision.llm.narrative && <div className="text-xs text-slate-300">{decision.llm.narrative}</div>}
          {decision.llm.risks.length > 0 && (
            <ul className="mt-2 list-disc pl-4 text-xs text-amber-300/80">
              {decision.llm.risks.map((r, i) => <li key={i}>{r}</li>)}
            </ul>
          )}
        </div>
      )}

      <div className="max-h-[160px] overflow-y-auto rounded-md border border-slate-800 bg-slate-950 p-2 text-xs text-slate-400">
        {decision.reasons.map((r, i) => <div key={i} className="border-b border-slate-800/50 py-1 last:border-0">{r}</div>)}
      </div>
    </div>
  );
}

function formatNumber(value: number, maximumFractionDigits = 2) {
  return Number(value).toLocaleString(undefined, { maximumFractionDigits });
}

function formatSignedNumber(value: number, maximumFractionDigits = 4) {
  const formatted = formatNumber(value, maximumFractionDigits);
  return value > 0 ? `+${formatted}` : formatted;
}

function formatPercent(value: number, maximumFractionDigits = 2) {
  return `${formatNumber(value * 100, maximumFractionDigits)}%`;
}

function EmptyState({ text }: { text: string }) {
  return <div className="rounded-md border border-dashed border-slate-700 p-6 text-center text-sm text-slate-500">{text}</div>;
}
