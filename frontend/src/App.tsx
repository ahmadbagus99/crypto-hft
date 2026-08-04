import { useQuery } from "@tanstack/react-query";
import {
  CategoryScale,
  Chart as ChartJS,
  Filler,
  Legend,
  LinearScale,
  LineController,
  LineElement,
  PointElement,
  Tooltip
} from "chart.js";
import type { ChartData, ChartOptions } from "chart.js";
import { CandlestickSeries, ColorType, createChart } from "lightweight-charts";
import type { CandlestickData, IChartApi, ISeriesApi, UTCTimestamp } from "lightweight-charts";
import { Activity, Bot, CheckCircle2, Clock3, Radio, Settings, ShieldAlert, ShieldCheck, Wallet } from "lucide-react";
import type { ReactNode } from "react";
import React, { useEffect, useMemo, useRef, useState } from "react";
import { Line } from "react-chartjs-2";
import { createTradingConnection } from "./lib/signalr";
import type {
  AccountUpdateEvent,
  AiDecision,
  AggTradeTick,
  AutoTradeRiskStatus,
  BacktestResult,
  FuturesPositionInfo,
  FuturesSymbolRules,
  FuturesWalletBalance,
  JournalResponse,
  KillSwitchState,
  KlineTick,
  MarginCallEvent,
  MarkPriceTick,
  OpenPositionRevalidationSnapshot,
  OrderBookSnapshot,
  Overview,
  PositionHistoryItem,
  PositionHistoryResponse,
  PriceTick,
  RiskDetailResponse,
  TradingSettings,
  TrailingStopSnapshot,
  UserDataStreamExpiredEvent,
  AiUsageSummary
} from "./lib/types";

const symbol = "BTCUSDT";
const APP_PASSWORD = import.meta.env.VITE_APP_PASSWORD || "admin";
type PositionHistoryPeriod = "day" | "week" | "month" | "year" | "all";
const chartIntervals = ["1m", "5m", "15m", "1h", "4h", "1d"] as const;
type ChartInterval = (typeof chartIntervals)[number];
const positionHistoryPeriods: Array<{ value: PositionHistoryPeriod; label: string }> = [
  { value: "day", label: "Harian" },
  { value: "week", label: "Mingguan" },
  { value: "month", label: "Bulanan" },
  { value: "year", label: "Tahunan" },
  { value: "all", label: "Semua" },
];

ChartJS.register(LineController, CategoryScale, LinearScale, LineElement, PointElement, Filler, Tooltip, Legend);

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
  targetMarginUsdt?: number;
  autoSizingMode?: number;
  targetLeverage?: number;
  apiKey?: string;
  apiSecret?: string;
  anthropicApiKey?: string;
  aiModel?: string;
  confidenceThreshold?: number;
  positionCheckIntervalMinutes?: number;
  trailingStopDistanceR?: number;
  lunarCrushApiKey?: string;
  tradingStyle?: number;
  accountRiskGuardEnabled?: boolean;
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

async function fetchAiUsage(): Promise<AiUsageSummary> {
  const response = await fetch("/api/ai/usage", { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load AI usage");
  return response.json();
}

async function fetchPositions(): Promise<FuturesPositionInfo[]> {
  const response = await fetch(`/api/account/positions?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load positions");
  return response.json();
}

async function fetchTrailingStops(): Promise<TrailingStopSnapshot> {
  const response = await fetch(`/api/account/trailing-stops?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load trailing stops");
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

async function fetchPositionRevalidations(): Promise<OpenPositionRevalidationSnapshot> {
  const response = await fetch(`/api/account/position-revalidations?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load position checks");
  return response.json();
}

async function fetchPositionHistory(period: PositionHistoryPeriod): Promise<PositionHistoryResponse> {
  const response = await fetch(`/api/positions/history?symbol=${symbol}&limit=100&period=${period}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load position history");
  return response.json();
}

async function fetchRiskDetails(): Promise<RiskDetailResponse> {
  const response = await fetch(`/api/risk/positions?symbol=${symbol}`, { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load risk details");
  return response.json();
}

async function fetchAutoTradeRiskStatus(): Promise<AutoTradeRiskStatus> {
  const response = await fetch("/api/risk/auto-trading-status", { cache: "no-store" });
  if (!response.ok) throw new Error("Failed to load auto-trading risk status");
  return response.json();
}

async function fetchAiDecision(): Promise<AiDecision | null> {
  // Read-only cached decision — does not trigger a Claude-billed analysis.
  const response = await fetch(`/api/ai/decision?symbol=${symbol}`, { cache: "no-store" });
  if (response.status === 204) return null; // analysis loop hasn't produced one yet
  if (!response.ok) throw new Error("Failed to load AI decision");
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

async function fetchInitialKlines(interval: ChartInterval): Promise<KlineTick[]> {
  const response = await fetch(`/api/market/klines?symbol=${symbol}&interval=${interval}&limit=240`, { cache: "no-store" });
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
    <div className="flex min-h-screen items-center justify-center px-4">
      <div className="hud hud-corners w-full max-w-sm animate-riseIn p-8">
        <div className="mb-7 flex items-center gap-3">
          <div className="grid h-11 w-11 place-items-center rounded-md border border-cyan/30 bg-cyan/10 shadow-glowSoft">
            <Bot className="h-6 w-6 text-cyan" />
          </div>
          <div className="leading-tight">
            <h1 className="text-sm font-semibold tracking-[0.16em] text-slate-100">BTCUSDT PERPETUAL</h1>
            <p className="label-micro mt-1">Autonomous Trading Engine</p>
          </div>
        </div>

        <div className="mb-6 flex items-center gap-2 rounded-md border border-hairline bg-void/50 px-3 py-2">
          <span className="h-1.5 w-1.5 rounded-full bg-cyan animate-pulseDot" />
          <span className="label-micro">Authentication required</span>
        </div>

        <form onSubmit={submit} className="grid gap-4">
          <div>
            <label className="label-micro mb-2 block">Access key</label>
            <input
              type="password"
              autoFocus
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="ring-hud w-full rounded-md border border-hairline bg-void px-3 py-2.5 text-sm tracking-[0.2em] text-slate-100 placeholder:tracking-normal placeholder:text-slate-600"
              placeholder="••••••••"
            />
          </div>
          {error && (
            <div className="rounded-md border border-exchangeRed/40 bg-exchangeRed/10 px-3 py-2 text-xs text-exchangeRed">
              {error}
            </div>
          )}
          <button
            type="submit"
            className="rounded-md border border-cyan/40 bg-cyan/15 px-4 py-2.5 text-[11px] font-semibold uppercase tracking-[0.2em] text-cyan transition-all hover:bg-cyan/25 hover:shadow-glow"
          >
            Authenticate
          </button>
        </form>
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
  const [targetMargin, setTargetMargin] = useState("3");
  const [autoSizingMode, setAutoSizingMode] = useState("0");
  const [tradingStyle, setTradingStyle] = useState("0");
  const [riskGuard, setRiskGuard] = useState(true);
  const [targetLeverage, setTargetLeverage] = useState("20");
  const [confidenceThreshold, setConfidenceThreshold] = useState("80");
  const [positionCheckInterval, setPositionCheckInterval] = useState("30");
  const [trailingStopDistance, setTrailingStopDistance] = useState("1.00");
  const [apiKey, setApiKey] = useState("");
  const [apiSecret, setApiSecret] = useState("");
  const [anthropicKey, setAnthropicKey] = useState("");
  const [lunarCrushKey, setLunarCrushKey] = useState("");
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
      setTargetMargin(String(current.targetMarginUsdt ?? 3));
      setAutoSizingMode(String(current.autoSizingMode ?? 0));
      setTradingStyle(String(current.tradingStyle ?? 0));
      setRiskGuard(current.accountRiskGuardEnabled ?? true);
      setTargetLeverage(String(current.targetLeverage ?? 20));
      setAiModel(current.aiModel ?? "claude-opus-4-8");
      setConfidenceThreshold(String(current.confidenceThreshold ?? 80));
      setPositionCheckInterval(String(current.positionCheckIntervalMinutes ?? 30));
      setTrailingStopDistance((current.trailingStopDistanceR ?? 1).toFixed(2));
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
        targetMarginUsdt: Number(targetMargin) || undefined,
        autoSizingMode: Number(autoSizingMode),
        tradingStyle: Number(tradingStyle),
        accountRiskGuardEnabled: riskGuard,
        targetLeverage: Number(targetLeverage) || undefined,
        apiKey: apiKey || undefined,
        apiSecret: apiSecret || undefined,
        anthropicApiKey: anthropicKey || undefined,
        aiModel: aiModel || undefined,
        confidenceThreshold: Number(confidenceThreshold) || undefined,
        positionCheckIntervalMinutes: Number(positionCheckInterval) || undefined,
        trailingStopDistanceR: Number(trailingStopDistance) || undefined,
        lunarCrushApiKey: lunarCrushKey || undefined,
      });
      setApiKey("");
      setApiSecret("");
      setAnthropicKey("");
      setLunarCrushKey("");
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
      <div className="mb-6 flex items-center gap-3"><span className="h-4 w-[2px] bg-cyan shadow-glowSoft" /><h2 className="text-sm font-semibold uppercase tracking-[0.18em] text-slate-100">Settings</h2></div>
      <form onSubmit={save} className="grid gap-6">

        {/* Trading Mode */}
        <section className="hud hud-corners p-5">
          <h3 className="mb-4 text-[11px] font-semibold uppercase tracking-[0.16em] text-cyan/80">Trading Mode</h3>
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
        <section className="hud hud-corners p-5">
          <h3 className="mb-4 text-[11px] font-semibold uppercase tracking-[0.16em] text-cyan/80">Risk Configuration</h3>
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
            {/* Default Leverage sengaja tidak ditampilkan: jalur auto-trading selalu memakai
                leverage dari decision engine (tier confidence x faktor learning, atau saran AI),
                jadi nilai ini tidak pernah berpengaruh. State-nya tetap dikirim saat save agar
                nilai tersimpan di backend tidak berubah. */}
            <SettingInput
              label="Target Margin per Posisi (USDT)"
              value={targetMargin}
              onChange={setTargetMargin}
              hint="Risk mode: acuan margin untuk order minimum. Margin x Leverage mode: notional target = nilai ini × target leverage."
              type="number"
              min="1"
              max="1000"
              step="0.5"
            />
            <div className="flex items-start gap-3 rounded-md border border-hairline bg-void/60 p-3">
              <input
                id="risk-guard"
                type="checkbox"
                checked={riskGuard}
                onChange={e => setRiskGuard(e.target.checked)}
                className="mt-0.5 h-4 w-4 accent-emerald-500"
              />
              <div>
                <label htmlFor="risk-guard" className="block text-sm text-slate-200">Account Risk Guard</label>
                <p className="mt-1 text-xs text-slate-600">
                  Aktif: daily-loss pause, consecutive-loss pause, dan exposure cap memblok/memotong trading saat terpicu.
                  Nonaktif: semua rem akun dilepas — trading tetap jalan apapun kondisinya. ⚠️ Nonaktif = satu hari buruk bisa menghabiskan saldo.
                </p>
              </div>
            </div>
            <div>
              <label className="mb-1 block text-xs text-slate-400">Metode Trading</label>
              <select
                value={tradingStyle}
                onChange={e => setTradingStyle(e.target.value)}
                className="w-full rounded-md border border-hairline bg-void px-3 py-2 text-slate-100 ring-hud"
              >
                <option value="0">Intraday — geometri 1h, target 1–2%, hold beberapa jam</option>
                <option value="1">Scalper — geometri 15m, target 0.4–0.8%, in-out cepat</option>
              </select>
              <p className="mt-1 text-xs text-slate-600">Scalper: konfirmasi 30 detik, cooldown 10/20 menit, SL/TP dari ATR 15m. Fee round-trip ~0.1% — target di bawah itu tidak diambil. Berlaku di scan berikutnya tanpa restart.</p>
            </div>
            <div>
              <label className="mb-1 block text-xs text-slate-400">Auto Position Sizing</label>
              <select
                value={autoSizingMode}
                onChange={e => setAutoSizingMode(e.target.value)}
                className="w-full rounded-md border border-hairline bg-void px-3 py-2 text-slate-100 ring-hud"
              >
                <option value="0">Risk Engine — size dari risk per trade</option>
                <option value="1">Margin × Leverage — pakai notional target</option>
              </select>
              <p className="mt-1 text-xs text-slate-600">Mode kedua memperbesar quantity dari target margin × leverage, lalu tetap dipotong risk gate jika melewati exposure cap.</p>
            </div>
            <div>
              <label className="mb-1 block text-xs text-slate-400">Target Leverage</label>
              <select
                value={targetLeverage}
                onChange={e => setTargetLeverage(e.target.value)}
                className="w-full rounded-md border border-hairline bg-void px-3 py-2 text-slate-100 ring-hud"
              >
                <option value="5">5x</option>
                <option value="10">10x</option>
                <option value="15">15x</option>
                <option value="20">20x</option>
              </select>
              <p className="mt-1 text-xs text-slate-600">Dipakai saat Auto Position Sizing = Margin × Leverage. Cap sistem tetap 20x.</p>
            </div>
            <SettingInput
              label="Min Confidence Open Order (%)"
              value={confidenceThreshold}
              onChange={setConfidenceThreshold}
              hint="Order baru dibuka hanya jika confidence AI ≥ nilai ini"
              type="number"
              min="1"
              max="100"
            />
            <SettingInput
              label="Position Check Interval (menit)"
              value={positionCheckInterval}
              onChange={setPositionCheckInterval}
              hint="Saat posisi terbuka, validasi arah lawan berjalan tiap interval ini. Disarankan 10-15 menit; batas 5-120."
              type="number"
              min="5"
              max="120"
              step="1"
            />
            <div>
              <label className="mb-1 block text-xs text-slate-400">Trailing Stop Distance</label>
              <select
                value={trailingStopDistance}
                onChange={e => setTrailingStopDistance(e.target.value)}
                className="w-full rounded-md border border-hairline bg-void px-3 py-2 text-slate-100 ring-hud"
              >
                <option value="0.50">Agresif — 0.50R</option>
                <option value="0.75">Seimbang — 0.75R</option>
                <option value="1.00">Konservatif — 1.00R</option>
                <option value="1.25">Longgar — 1.25R</option>
              </select>
              <p className="mt-1 text-xs text-slate-600">Jarak SL trailing dari mark price setelah profit melewati +1R. Default lama: 1.00R.</p>
            </div>
          </div>
        </section>

        {/* Binance API */}
        <section className="hud hud-corners p-5">
          <h3 className="mb-4 text-[11px] font-semibold uppercase tracking-[0.16em] text-cyan/80">Binance API</h3>
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
              className="rounded-md border border-hairline px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-slate-800 disabled:opacity-50"
            >
              {testing === "binance" ? "Testing..." : "Test Connection"}
            </button>
            {binanceTest && <ConnectionBadge result={binanceTest} />}
          </div>
          <p className="mt-3 text-xs text-slate-600">API key hanya dipakai jika mode Live aktif. Paper trading tidak butuh API key.</p>
        </section>

        {/* Anthropic / Claude AI */}
        <section className="hud hud-corners p-5">
          <h3 className="mb-4 text-[11px] font-semibold uppercase tracking-[0.16em] text-cyan/80">Claude AI (Anthropic)</h3>
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
              className="w-full rounded-md border border-hairline bg-slate-900 px-3 py-2 text-sm text-slate-100 ring-hud"
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
              className="rounded-md border border-hairline px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-slate-800 disabled:opacity-50"
            >
              {testing === "anthropic" ? "Testing..." : "Test Connection"}
            </button>
            {anthropicTest && <ConnectionBadge result={anthropicTest} />}
          </div>
          <p className="mt-3 text-xs text-slate-600">Opsional. Tanpa key, engine tetap jalan rule-based saja (tanpa validasi LLM). Simpan key dulu sebelum test.</p>
        </section>

        {/* LunarCrush Social Sentiment */}
        <section className="hud hud-corners p-5">
          <h3 className="mb-4 text-[11px] font-semibold uppercase tracking-[0.16em] text-cyan/80">LunarCrush (Social Sentiment)</h3>
          {current && (
            <div className="mb-4 flex items-center gap-2 text-sm">
              <span className={`h-2 w-2 rounded-full ${current.hasLunarCrushKey ? "bg-emerald-400" : "bg-slate-600"}`} />
              <span className="text-slate-400">API Key: </span>
              <span className="text-slate-200">{current.hasLunarCrushKey ? current.lunarCrushKeyPreview : "Belum diset"}</span>
            </div>
          )}
          <SettingInput
            label="LunarCrush API Key (kosongkan jika tidak diubah)"
            value={lunarCrushKey}
            onChange={setLunarCrushKey}
            hint="Dari lunarcrush.com/developers — sentimen sosial BTC di-blend ke faktor Social"
            type="password"
          />
          <p className="mt-3 text-xs text-slate-600">Opsional. Tanpa key, faktor Social pakai Fear &amp; Greed saja. Dengan key, sentimen LunarCrush di-blend 50/50.</p>
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
      className={`flex-1 rounded-md border px-3 py-2 text-[11px] font-semibold uppercase tracking-[0.14em] transition-all ${
        active
          ? danger
            ? "border-exchangeRed/50 bg-exchangeRed/12 text-exchangeRed shadow-down"
            : "border-cyan/50 bg-cyan/12 text-cyan shadow-glowSoft"
          : "border-hairline bg-transparent text-slate-500 hover:text-slate-300"
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
      <label className="label-micro mb-1.5 block">{label}</label>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        min={min}
        max={max}
        step={step}
        className="ring-hud tabular w-full rounded-md border border-hairline bg-void px-3 py-2 text-sm text-slate-100"
      />
      {hint && <p className="mt-1.5 text-[11px] leading-relaxed text-slate-600">{hint}</p>}
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
    <div className="min-h-screen text-slate-100">
      <AppHeader page={page} onPageChange={setPage} onLogout={() => { sessionStorage.removeItem("hft_auth"); setIsLoggedIn(false); }} />
      {page === "settings" ? <SettingsPage /> : <DashboardPage />}
    </div>
  );
}

function AppHeader({ page, onPageChange, onLogout }: { page: string; onPageChange: (p: "dashboard" | "settings") => void; onLogout: () => void }) {
  return (
    <header className="sticky top-0 z-30 border-b border-hairline bg-void/85 backdrop-blur-md">
      <div className="flex w-full max-w-none flex-col gap-3 px-4 py-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex items-center gap-3">
          <div className="relative grid h-9 w-9 place-items-center rounded-md border border-cyan/30 bg-cyan/10 shadow-glowSoft">
            <Bot className="h-5 w-5 text-cyan" />
          </div>
          <div className="leading-tight">
            <h1 className="text-sm font-semibold tracking-[0.14em] text-slate-100">BTCUSDT PERPETUAL</h1>
            <p className="label-micro mt-0.5">Autonomous Trading Engine</p>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <nav className="flex gap-1 rounded-lg border border-hairline bg-panel/70 p-1">
            <NavTab active={page === "dashboard"} onClick={() => onPageChange("dashboard")} label="Dashboard" icon={<Activity className="h-3.5 w-3.5" />} />
            <NavTab active={page === "settings"} onClick={() => onPageChange("settings")} label="Settings" icon={<Settings className="h-3.5 w-3.5" />} />
          </nav>
          <button
            type="button"
            onClick={onLogout}
            className="rounded-md border border-hairline px-3 py-2 text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-500 transition-colors hover:border-exchangeRed/40 hover:text-exchangeRed"
          >
            Logout
          </button>
        </div>
      </div>
      {/* Hairline of system color: the seam between chrome and workspace. */}
      <div className="h-px w-full bg-gradient-to-r from-transparent via-cyan/35 to-transparent" />
    </header>
  );
}

function NavTab({ active, onClick, label, icon }: { active: boolean; onClick: () => void; label: string; icon?: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex items-center gap-1.5 rounded-md px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.14em] transition-all ${
        active
          ? "bg-cyan/12 text-cyan shadow-[inset_0_0_0_1px_rgba(34,211,238,0.35)]"
          : "text-slate-500 hover:text-slate-200"
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
  const [chartInterval, setChartInterval] = useState<ChartInterval>("1m");
  const [realtimePositions, setRealtimePositions] = useState<FuturesPositionInfo[]>([]);
  const [positionHistoryPeriod, setPositionHistoryPeriod] = useState<PositionHistoryPeriod>("week");
  const { data: overview } = useQuery({ queryKey: ["overview"], queryFn: fetchOverview, refetchInterval: 5000 });
  const { data: tradingSettings } = useQuery({ queryKey: ["trading-settings"], queryFn: fetchTradingSettings, refetchInterval: 5000, retry: false });
  const { data: aiUsage } = useQuery({ queryKey: ["ai-usage"], queryFn: fetchAiUsage, refetchInterval: 30000, retry: false });
  const { data: walletBalances } = useQuery({ queryKey: ["wallet"], queryFn: fetchWallet, refetchInterval: 5000, retry: false });
  const { data: positions } = useQuery({ queryKey: ["positions", symbol], queryFn: fetchPositions, refetchInterval: 5000, retry: false });
  const { data: trailingStops } = useQuery({ queryKey: ["trailing-stops", symbol], queryFn: fetchTrailingStops, refetchInterval: 5000, retry: false });
  const { data: exchangeRules } = useQuery({ queryKey: ["exchange-rules", symbol], queryFn: fetchExchangeRules, refetchInterval: 60 * 60 * 1000, retry: false });
  const { data: killSwitch, refetch: refetchKillSwitch } = useQuery({ queryKey: ["kill-switch"], queryFn: fetchKillSwitch, refetchInterval: 5000, retry: false });
  const { data: journal } = useQuery({ queryKey: ["journal", symbol], queryFn: fetchJournal, refetchInterval: 5000, retry: false });
  const { data: positionChecks } = useQuery({ queryKey: ["position-revalidations", symbol], queryFn: fetchPositionRevalidations, refetchInterval: 5000, retry: false });
  const { data: positionHistory } = useQuery({
    queryKey: ["position-history", symbol, positionHistoryPeriod],
    queryFn: () => fetchPositionHistory(positionHistoryPeriod),
    refetchInterval: 30000,
    retry: false
  });
  const { data: riskDetails } = useQuery({ queryKey: ["risk-details", symbol], queryFn: fetchRiskDetails, refetchInterval: 5000, retry: false });
  const { data: autoTradeRiskStatus } = useQuery({
    queryKey: ["auto-trading-risk-status"],
    queryFn: fetchAutoTradeRiskStatus,
    refetchInterval: 30000,
    retry: false
  });
  const { data: backtest } = useQuery({ queryKey: ["backtest", symbol, "1h"], queryFn: fetchBacktest, retry: false });
  const { data: aiDecision } = useQuery({ queryKey: ["ai-decision", symbol], queryFn: fetchAiDecision, refetchInterval: 30000, retry: false });
  // Binance keeps futures margin across several 1:1 USD stablecoins; aggregate them so a
  // USDC-funded account shows its real balance instead of 0.
  const usdtWallet = (() => {
    const usdAssets = ["USDT", "USDC", "BUSD", "FDUSD", "TUSD", "USD1", "BFUSD", "DAI"];
    const stable = walletBalances?.filter((wallet) => usdAssets.includes(wallet.asset)) ?? [];
    if (stable.length === 0) return undefined;
    return {
      asset: "USD",
      balance: stable.reduce((sum, w) => sum + w.balance, 0),
      availableBalance: stable.reduce((sum, w) => sum + w.availableBalance, 0),
      crossUnrealizedPnl: stable.reduce((sum, w) => sum + w.crossUnrealizedPnl, 0),
    };
  })();
  const displayedPositions = realtimePositions.length ? realtimePositions : positions ?? [];
  const isManualMode = tradingSettings?.autoTradingEnabled !== true;

  useEffect(() => {
    const connection = createTradingConnection();
    const subscribe = () => connection.invoke("SubscribeSymbol", symbol);
    let isMounted = true;

    fetchInitialMarkPrice()
      .then((tick) => {
        if (isMounted) setMarkPrice(tick);
      })
      .catch(() => undefined);

    fetchInitialKlines(chartInterval)
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
      fetchInitialKlines(chartInterval)
        .then((candles) => {
          if (isMounted) setKlines(candles);
        })
        .catch(() => undefined);
    }, klinePollIntervalMs(chartInterval));

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
    connection.on("orderBook", (snapshot: OrderBookSnapshot) => setOrderBook(snapshot));
    connection.on("aggTrade", (tick: AggTradeTick) => {
      setTrades((current) => [tick, ...current].slice(0, 28));
    });
    connection.on("kline", (tick: KlineTick) => {
      if (tick.interval !== chartInterval) return;
      setKlines((current) => {
        const last = current[current.length - 1];
        if (last?.openTime === tick.openTime) {
          return [...current.slice(0, -1), tick];
        }
        return [...current.slice(-239), tick];
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
  }, [chartInterval]);

  return (
    <main className="grid w-full max-w-none gap-4 px-4 py-4">
      {/* System strip: the four facts that change how every number below should be
          read — is the feed live, is this real money, who is driving, how selective. */}
      <div className="hud flex flex-wrap items-center gap-2 px-3 py-2">
        <StatusPill label={connectionState} />
        {tradingSettings && (
          <Chip
            tone={tradingSettings.paperTradingOnly ? "info" : "danger"}
            label={tradingSettings.paperTradingOnly ? "Paper" : "Live"}
            value={tradingSettings.paperTradingOnly ? "simulated" : "real funds"}
          />
        )}
        {tradingSettings && (
          <Chip
            tone={tradingSettings.autoTradingEnabled ? "accent" : "muted"}
            label={tradingSettings.autoTradingEnabled ? "Auto" : "Manual"}
            value={tradingSettings.autoTradingEnabled ? "engine driving" : "operator"}
          />
        )}
        <Chip tone="muted" label="Symbol" value={symbol} />
        {tradingSettings && (
          <Chip tone="muted" label="Min conf" value={`${tradingSettings.confidenceThreshold}%`} />
        )}
      </div>
        {marginCall && <MarginCallAlert event={marginCall} onDismiss={() => setMarginCall(null)} />}
        {streamExpired && <UserStreamExpiredAlert event={streamExpired} onDismiss={() => setStreamExpired(null)} />}

        {autoTradeRiskStatus && <AutoTradeRiskStatusCard status={autoTradeRiskStatus} />}

        <section className="grid gap-3 md:grid-cols-2 xl:grid-cols-6">
          <Metric title={tradingSettings?.paperTradingOnly ? "Wallet (Paper)" : "Wallet (Live)"} value={usdtWallet?.balance ?? overview?.walletBalance ?? 0} icon={<Wallet />} />
          <Metric title="Available" value={usdtWallet?.availableBalance ?? overview?.availableBalance ?? 0} icon={<ShieldCheck />} />
          <Metric title="Mark Price" value={markPrice?.markPrice ?? price?.price ?? 0} icon={<Activity />} />
          <Metric title="Index Price" value={markPrice?.indexPrice ?? 0} icon={<Activity />} />
          <Metric title="Funding" value={(markPrice?.fundingRate ?? 0) * 100} suffix="%" icon={<Radio />} />
          <Metric
            title="Unreal PnL"
            value={usdtWallet?.crossUnrealizedPnl ?? 0}
            positive={(usdtWallet?.crossUnrealizedPnl ?? 0) > 0}
            danger={(usdtWallet?.crossUnrealizedPnl ?? 0) < 0}
          />
        </section>

        <section>
          <Panel title="Position History">
            <PositionHistoryPanel
              history={positionHistory}
              period={positionHistoryPeriod}
              onPeriodChange={setPositionHistoryPeriod}
            />
          </Panel>
        </section>

        <section className="grid gap-4 xl:grid-cols-[1.4fr_0.8fr]">
          <Panel title="AI Decision">
            <AiDecisionPanel decision={aiDecision} />
          </Panel>
          <Panel title="Claude API Usage">
            <AiUsagePanel usage={aiUsage} />
          </Panel>
        </section>

        <section className="grid items-start gap-4 xl:grid-cols-[1.45fr_0.75fr_0.75fr_0.75fr]">
          <Panel title="Realtime Chart">
            <RealtimeChartPanel
              candles={klines}
              interval={chartInterval}
              onIntervalChange={setChartInterval}
            />
          </Panel>

          <Panel title="Position Checks">
            <PositionRevalidationPanel snapshot={positionChecks} />
          </Panel>

          <Panel title="Open Position">
            <OpenPositions positions={displayedPositions} journal={journal} />
          </Panel>

          <Panel title="Trailing Stop">
            <TrailingStopPanel snapshot={trailingStops} />
          </Panel>
        </section>

        {isManualMode && (
          <section className="grid gap-4 xl:grid-cols-[0.9fr_0.9fr]">
            <Panel title="Trade Tape">
              <TradeTape trades={trades} />
            </Panel>
            <Panel title={tradingSettings?.paperTradingOnly ? "Manual Paper Order" : "Manual Live Order"}>
              <ManualOrder />
            </Panel>
          </section>
        )}

        {isManualMode && (
          <section className="grid gap-4 xl:grid-cols-[1fr_1fr]">
            <Panel title="Exchange Rules">
              <ExchangeRules rules={exchangeRules} />
            </Panel>
            <Panel title="Kill Switch">
              <KillSwitchPanel state={killSwitch} onChanged={refetchKillSwitch} />
            </Panel>
          </section>
        )}

        {isManualMode && (
          <section className="grid gap-4">
            <Panel title="Position Risk">
              <PositionRiskPanel risk={riskDetails} />
            </Panel>
          </section>
        )}

        {isManualMode && (
          <section className="grid gap-4 xl:grid-cols-[1fr_1fr]">
            <Panel title="Trade Journal">
              <TradeJournal journal={journal} />
            </Panel>
            <Panel title="Backtest">
              <BacktestPanel result={backtest} />
            </Panel>
          </section>
        )}

    </main>
  );
}

function StatusPill({ label }: { label: string }) {
  const isLive = label === "live";
  return (
    <span
      className={`inline-flex items-center gap-2 rounded-md border px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.14em] ${
        isLive
          ? "border-exchangeGreen/40 bg-exchangeGreen/10 text-exchangeGreen"
          : "border-warn/40 bg-warn/10 text-warn"
      }`}
    >
      <span className={`h-1.5 w-1.5 rounded-full ${isLive ? "bg-exchangeGreen animate-pulseDot" : "bg-warn"}`} />
      {label}
    </span>
  );
}

// Label/value pair used across the system strip. Tone carries the meaning, so the
// caller states intent ("this is real money") rather than picking colors each time.
function Chip({ tone, label, value }: { tone: "accent" | "info" | "danger" | "muted"; label: string; value: string }) {
  const tones: Record<string, string> = {
    accent: "border-cyan/35 bg-cyan/10 text-cyan",
    info: "border-sky-500/35 bg-sky-500/10 text-sky-300",
    danger: "border-exchangeRed/40 bg-exchangeRed/10 text-exchangeRed",
    muted: "border-hairline bg-void/40 text-slate-400",
  };
  return (
    <span className={`inline-flex items-baseline gap-2 rounded-md border px-3 py-1.5 ${tones[tone]}`}>
      <span className="text-[10px] font-semibold uppercase tracking-[0.14em] opacity-80">{label}</span>
      <span className="tabular text-xs font-semibold">{value}</span>
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

function Metric({
  title,
  value,
  suffix = "",
  icon,
  danger = false,
  positive = false,
}: {
  title: string;
  value: number;
  suffix?: string;
  icon?: ReactNode;
  danger?: boolean;
  positive?: boolean;
}) {
  const valueClass = danger ? "text-down" : positive ? "text-up" : "text-slate-50";
  // The accent bar carries the state, so the number itself stays legible instead of
  // being tinted for every neutral reading.
  const accent = danger ? "bg-exchangeRed" : positive ? "bg-exchangeGreen" : "bg-cyan/60";

  return (
    <div className="hud group relative overflow-hidden p-4 transition-colors duration-200 hover:border-cyan/30">
      <span className={`absolute inset-y-0 left-0 w-[2px] ${accent}`} />
      <div className="flex items-center justify-between">
        <span className="label-micro">{title}</span>
        <span className="text-slate-600 transition-colors group-hover:text-cyan/70">{icon}</span>
      </div>
      <div className={`tabular val-xl mt-3 ${valueClass}`}>
        {Number(value).toLocaleString(undefined, { maximumFractionDigits: 4 })}
        <span className="ml-0.5 text-base text-slate-500">{suffix}</span>
      </div>
    </div>
  );
}

function Panel({ title, children, accent }: { title: string; children: ReactNode; accent?: ReactNode }) {
  return (
    <div className="hud hud-corners flex min-w-0 flex-col">
      <div className="flex items-center justify-between gap-3 border-b border-hairline px-4 py-2.5">
        <div className="flex items-center gap-2">
          <span className="h-1 w-1 rounded-full bg-cyan shadow-glowSoft" />
          <span className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-300">{title}</span>
        </div>
        {accent}
      </div>
      {/* A one-pixel sweep under the header is the only motion in a resting panel —
          enough to say the surface is live, quiet enough to ignore while reading. */}
      <div className="scan-line h-px w-full opacity-40" />
      <div className="min-w-0 p-4">{children}</div>
    </div>
  );
}

function AutoTradeRiskStatusCard({ status }: { status: AutoTradeRiskStatus }) {
  const paused = !status.tradingAllowed;
  const blockedByLimit = status.status === "daily-loss" || status.status === "consecutive-losses";
  const tone = paused
    ? blockedByLimit
      ? "border-red-800 bg-red-950/20"
      : "border-amber-800 bg-amber-950/20"
    : "border-emerald-900 bg-emerald-950/10";
  const badgeTone = paused
    ? blockedByLimit
      ? "border-red-700 bg-red-500/10 text-red-300"
      : "border-amber-700 bg-amber-500/10 text-amber-300"
    : "border-emerald-800 bg-emerald-500/10 text-emerald-300";
  const title = paused ? "Auto Trading Dijeda" : status.status === "paper" ? "Paper Trading Aktif" : "Auto Trading Siap";
  const resetTime = status.resetsAt
    ? new Date(status.resetsAt).toLocaleString("id-ID", {
        day: "2-digit",
        month: "short",
        year: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        timeZoneName: "short"
      })
    : null;
  const resumesAt = resetTime
    ?? (status.status === "disabled" ? "Setelah diaktifkan" : status.status === "unavailable" ? "Saat data pulih" : "Sekarang");

  return (
    <section className={`rounded-lg border p-4 ${tone}`}>
      <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex min-w-0 items-start gap-3">
          <div className={`mt-0.5 shrink-0 ${paused ? (blockedByLimit ? "text-red-300" : "text-amber-300") : "text-emerald-300"}`}>
            {paused ? <ShieldAlert className="h-5 w-5" /> : <CheckCircle2 className="h-5 w-5" />}
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="text-sm font-semibold text-slate-100">Account Risk Guard</h2>
              <span className={`rounded-md border px-2 py-1 text-xs font-semibold ${badgeTone}`}>{title}</span>
            </div>
            <p className="mt-2 text-sm text-slate-300">{status.reason}</p>
          </div>
        </div>

        <div className="grid shrink-0 grid-cols-2 gap-x-8 gap-y-3 text-sm sm:grid-cols-4">
          <RiskStatusValue label="Daily Loss" value={formatRiskValue(status.dailyLoss, "USDT")} danger={status.status === "daily-loss"} />
          <RiskStatusValue label="Daily Limit" value={formatRiskValue(status.dailyLossLimit, "USDT")} />
          <RiskStatusValue
            label="Loss Beruntun"
            value={status.consecutiveLosses === null ? "-" : `${status.consecutiveLosses} / ${status.maxConsecutiveLosses}`}
          />
          <div>
            <div className="text-xs text-slate-500">Aktif Lagi</div>
            <div className={`mt-1 flex items-center gap-1.5 font-semibold ${resetTime ? "text-amber-200" : "text-slate-300"}`}>
              {resetTime && <Clock3 className="h-3.5 w-3.5 shrink-0" />}
              <span>{resumesAt}</span>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}

function RiskStatusValue({ label, value, danger = false }: { label: string; value: string; danger?: boolean }) {
  return (
    <div>
      <div className="text-xs text-slate-500">{label}</div>
      <div className={`mt-1 font-semibold ${danger ? "text-red-300" : "text-slate-200"}`}>{value}</div>
    </div>
  );
}

function formatRiskValue(value: number | null, suffix: string) {
  return value === null ? "-" : `${Number(value).toLocaleString(undefined, { maximumFractionDigits: 4 })} ${suffix}`;
}

function RealtimeChartPanel({
  candles,
  interval,
  onIntervalChange
}: {
  candles: KlineTick[];
  interval: ChartInterval;
  onIntervalChange: (interval: ChartInterval) => void;
}) {
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-sm font-semibold text-slate-100">BTCUSDT Perp</div>
          <div className="text-xs text-slate-500">{interval}</div>
        </div>
        <div className="flex rounded-md border border-hairline bg-void p-1">
          {chartIntervals.map((item) => (
            <button
              key={item}
              type="button"
              onClick={() => onIntervalChange(item)}
              className={`h-8 min-w-10 rounded px-2 text-xs font-semibold transition-colors ${
                interval === item
                  ? "bg-slate-700 text-slate-100"
                  : "text-slate-500 hover:text-slate-200"
              }`}
            >
              {item}
            </button>
          ))}
        </div>
      </div>
      <div className="h-[360px] overflow-hidden rounded-md border border-hairline bg-void">
        <BinanceStyleChart candles={candles} interval={interval} />
      </div>
    </div>
  );
}

function BinanceStyleChart({ candles, interval }: { candles: KlineTick[]; interval: ChartInterval }) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const candleSeriesRef = useRef<ISeriesApi<"Candlestick"> | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const chart = createChart(containerRef.current, {
      autoSize: true,
      layout: {
        background: { type: ColorType.Solid, color: "#020617" },
        textColor: "#94a3b8",
        fontFamily: "Inter, ui-sans-serif, system-ui"
      },
      grid: {
        vertLines: { color: "#0f172a" },
        horzLines: { color: "#1e293b" }
      },
      crosshair: {
        mode: 0,
        vertLine: { color: "#64748b", width: 1, style: 3, labelBackgroundColor: "#111827" },
        horzLine: { color: "#64748b", width: 1, style: 3, labelBackgroundColor: "#111827" }
      },
      rightPriceScale: {
        borderColor: "#1e293b",
        scaleMargins: { top: 0.08, bottom: 0.12 }
      },
      timeScale: {
        borderColor: "#1e293b",
        timeVisible: interval !== "1d",
        secondsVisible: false,
        rightOffset: 8,
        barSpacing: 8
      },
      handleScale: true,
      handleScroll: true
    });

    const candleSeries = chart.addSeries(CandlestickSeries, {
      upColor: "#16c784",
      downColor: "#ea3943",
      borderUpColor: "#16c784",
      borderDownColor: "#ea3943",
      wickUpColor: "#16c784",
      wickDownColor: "#ea3943",
      priceFormat: {
        type: "price",
        precision: 2,
        minMove: 0.01
      }
    });

    chartRef.current = chart;
    candleSeriesRef.current = candleSeries;
    candleSeries.setData(toChartCandles(candles));
    chart.timeScale().fitContent();

    return () => {
      chart.remove();
      chartRef.current = null;
      candleSeriesRef.current = null;
    };
  }, [interval]);

  useEffect(() => {
    if (!candleSeriesRef.current || !chartRef.current) return;
    const data = toChartCandles(candles);
    candleSeriesRef.current.setData(data);
    if (data.length > 0) {
      chartRef.current.timeScale().scrollToRealTime();
    }
  }, [candles]);

  return (
    <div className="relative h-full w-full">
      <div ref={containerRef} className="h-full w-full" />
      {!candles.length && (
        <div className="absolute inset-0 grid place-items-center">
          <EmptyState text="Waiting for realtime candlestick data..." />
        </div>
      )}
    </div>
  );
}

function toChartCandles(candles: KlineTick[]): CandlestickData[] {
  const unique = new Map<number, CandlestickData>();
  candles.forEach((candle) => {
    const time = Math.floor(new Date(candle.openTime).getTime() / 1000);
    if (!Number.isFinite(time)) return;
    unique.set(time, {
      time: time as UTCTimestamp,
      open: Number(candle.open),
      high: Number(candle.high),
      low: Number(candle.low),
      close: Number(candle.close)
    });
  });
  return Array.from(unique.values()).sort((a, b) => Number(a.time) - Number(b.time));
}

function klinePollIntervalMs(interval: ChartInterval) {
  if (interval === "1m") return 2000;
  if (interval === "5m" || interval === "15m") return 5000;
  if (interval === "1h") return 15000;
  return 30000;
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
        <div key={`${trade.time}-${index}`} className="grid grid-cols-3 border-b border-hairline py-2">
          <span className={trade.buyerIsMaker ? "text-exchangeRed" : "text-exchangeGreen"}>{Number(trade.price).toFixed(2)}</span>
          <span className="text-right text-slate-300">{Number(trade.quantity).toFixed(4)}</span>
          <span className="text-right text-slate-500">{new Date(trade.time).toLocaleTimeString()}</span>
        </div>
      ))}
      {!trades.length && <EmptyState text="Waiting for realtime trades..." />}
    </div>
  );
}

// Riwayat ratchet trailing stop untuk posisi yang SEDANG terbuka. Backend membersihkan
// datanya saat posisi close, jadi card ini selalu milik satu posisi saja.
function TrailingStopPanel({ snapshot }: { snapshot?: TrailingStopSnapshot }) {
  if (!snapshot || snapshot.positionSide === null || snapshot.positionSide === undefined) {
    return <EmptyState text="Tidak ada posisi aktif. Riwayat trailing stop muncul saat posisi terbuka." />;
  }

  const side = snapshot.positionSide === 1 ? "LONG" : "SHORT";
  const slMoved = snapshot.initialStopLoss != null
    && snapshot.currentStopLoss != null
    && snapshot.currentStopLoss !== snapshot.initialStopLoss;

  return (
    <div className="grid gap-3 text-sm">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className={`font-semibold ${snapshot.positionSide === 1 ? "text-emerald-300" : "text-red-300"}`}>{side}</div>
          <div className="mt-1 text-xs text-slate-500">Entry {formatNumber(snapshot.entryPrice)}</div>
          <div className="text-xs text-slate-500">
            SL awal {snapshot.initialStopLoss != null ? formatNumber(snapshot.initialStopLoss) : "-"}
          </div>
        </div>
        <div className="text-right text-xs text-slate-500">
          <div>SL sekarang</div>
          <div className={`font-semibold ${slMoved ? "text-emerald-300" : "text-slate-300"}`}>
            {snapshot.currentStopLoss != null ? formatNumber(snapshot.currentStopLoss) : "-"}
          </div>
        </div>
      </div>

      <div className="max-h-[270px] overflow-y-auto">
        {snapshot.events.map((event) => (
          <div key={event.ratchetedAt} className="border-t border-hairline py-3 first:border-t-0 first:pt-0">
            <div className="flex items-start justify-between gap-3">
              <div>
                <div className="text-xs font-semibold text-emerald-300">
                  SL {event.previousStopLoss != null ? formatNumber(event.previousStopLoss) : "-"} → {formatNumber(event.newStopLoss)}
                </div>
                <div className="mt-1 text-xs text-slate-500">{new Date(event.ratchetedAt).toLocaleTimeString()}</div>
              </div>
              <div className="text-right">
                <div className="text-sm font-semibold text-slate-100">+{formatNumber(event.profitR, 2)}R</div>
                <div className="text-[10px] uppercase text-slate-500">Profit saat ratchet</div>
              </div>
            </div>
            <div className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-slate-400">
              <PositionStat label="Mark" value={formatNumber(event.markPrice)} />
            </div>
          </div>
        ))}
        {!snapshot.events.length && (
          <div className="rounded-md border border-dashed border-hairline p-4 text-center text-xs text-slate-500">
            Belum ada ratchet. SL mulai digeser otomatis saat profit mencapai +1R.
          </div>
        )}
      </div>
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
        <button type="button" disabled={isSubmitting} onClick={() => submit(false)} className="rounded-md border border-hairline px-3 py-2 font-semibold text-slate-200 disabled:opacity-50">
          Disable
        </button>
      </div>
      {(message || state?.message) && <div className="rounded-md border border-hairline bg-void px-3 py-2 text-xs text-slate-300">{message || state?.message}</div>}
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
          <div key={order.id} className="border-b border-hairline py-3 last:border-b-0">
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

function PositionHistoryPanel({
  history,
  period,
  onPeriodChange,
}: {
  history?: PositionHistoryResponse;
  period: PositionHistoryPeriod;
  onPeriodChange: (period: PositionHistoryPeriod) => void;
}) {
  const [page, setPage] = useState(1);

  useEffect(() => {
    setPage(1);
  }, [period, history?.positions.length]);

  if (!history) {
    return <EmptyState text="Loading position history..." />;
  }

  const activePeriodLabel = positionHistoryPeriods.find((item) => item.value === period)?.label ?? "Mingguan";
  const pageSize = 5;
  const totalPages = Math.max(1, Math.ceil(history.positions.length / pageSize));
  const currentPage = Math.min(page, totalPages);
  const pagedPositions = history.positions.slice((currentPage - 1) * pageSize, currentPage * pageSize);

  return (
    <div className="grid gap-4 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="text-xs text-slate-500">
          Closed positions: {activePeriodLabel.toLowerCase()}
        </div>
        <div className="flex flex-wrap rounded-md border border-hairline bg-void p-1">
          {positionHistoryPeriods.map((item) => (
            <button
              key={item.value}
              type="button"
              onClick={() => onPeriodChange(item.value)}
              className={`h-8 rounded px-3 text-xs font-semibold transition-colors ${
                period === item.value ? "bg-slate-700 text-slate-100" : "text-slate-500 hover:text-slate-200"
              }`}
            >
              {item.label}
            </button>
          ))}
        </div>
      </div>

      <div className="grid gap-2 md:grid-cols-5">
        <PositionStat label="Realized PnL" value={formatSignedNumber(history.summary.totalRealizedPnl)} />
        <PositionStat label="Trades" value={formatNumber(history.summary.totalTrades, 0)} />
        <PositionStat label="Win Rate" value={formatPercent(history.summary.winRate)} />
        <PositionStat label="Best" value={formatSignedNumber(history.summary.bestTrade)} />
        <PositionStat label="Worst" value={formatSignedNumber(history.summary.worstTrade)} />
      </div>

      <PnlPerformanceChart title={`${activePeriodLabel} PnL`} positions={history.positions} />

      <div className="max-h-[320px] overflow-auto rounded-md border border-hairline">
        {history.positions.length > 0 && (
          <table className="min-w-[1080px] w-full border-collapse text-left text-xs">
            <thead className="sticky top-0 bg-void text-slate-500">
              <tr>
                <th className="px-3 py-2 font-semibold">Closed</th>
                <th className="px-3 py-2 font-semibold">Side</th>
                <th className="px-3 py-2 font-semibold">Size</th>
                <th className="px-3 py-2 font-semibold">Margin</th>
                <th className="px-3 py-2 font-semibold">Lev</th>
                <th className="px-3 py-2 font-semibold">Entry</th>
                <th className="px-3 py-2 font-semibold">TP</th>
                <th className="px-3 py-2 font-semibold">SL</th>
                <th className="px-3 py-2 font-semibold">Close Reason</th>
                <th className="px-3 py-2 text-right font-semibold">Realized PnL</th>
                <th className="px-3 py-2 text-right font-semibold">ROI</th>
              </tr>
            </thead>
            <tbody>
              {pagedPositions.map((position) => {
                const isLong = position.side === "Long";
                const isProfit = position.realizedPnl >= 0;
                return (
                  <tr key={position.id} className="border-t border-hairline text-slate-300">
                    <td className="px-3 py-3 align-top">
                      <div className="font-medium text-slate-200">{new Date(position.closedAt).toLocaleString()}</div>
                      <div className="mt-1 text-[10px] text-slate-600">Open {new Date(position.openedAt).toLocaleString()}</div>
                    </td>
                    <td className={`px-3 py-3 align-top font-semibold ${isLong ? "text-emerald-300" : "text-red-300"}`}>
                      {position.side.toUpperCase()}
                      <div className="mt-1 text-[10px] font-normal text-slate-600">{position.symbol}</div>
                    </td>
                    <td className="px-3 py-3 align-top">{formatNumber(position.quantity, 4)}</td>
                    <td className="px-3 py-3 align-top">{formatNumber(position.margin)}</td>
                    <td className="px-3 py-3 align-top">{formatNumber(position.leverage, 0)}x</td>
                    <td className="px-3 py-3 align-top">{formatNumber(position.entryPrice)}</td>
                    <td className="px-3 py-3 align-top">{position.takeProfit ? formatNumber(position.takeProfit) : "-"}</td>
                    <td className="px-3 py-3 align-top">{position.stopLoss ? formatNumber(position.stopLoss) : "-"}</td>
                    <td className="px-3 py-3 align-top">{closeReasonLabel(position.closeReason)}</td>
                    <td className={`px-3 py-3 text-right align-top font-semibold ${isProfit ? "text-emerald-300" : "text-red-300"}`}>
                      {formatSignedNumber(position.realizedPnl)}
                    </td>
                    <td className={`px-3 py-3 text-right align-top ${isProfit ? "text-emerald-300" : "text-red-300"}`}>
                      {formatPercent(position.roi)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        {!history.positions.length && <EmptyState text="No closed positions recorded yet." />}
      </div>
      {history.positions.length > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-3 text-xs text-slate-500">
          <div>
            Showing {(currentPage - 1) * pageSize + 1}-{Math.min(currentPage * pageSize, history.positions.length)} of {history.positions.length}
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={currentPage <= 1}
              onClick={() => setPage((value) => Math.max(1, value - 1))}
              className="rounded-md border border-hairline px-3 py-1.5 font-semibold text-slate-300 disabled:cursor-not-allowed disabled:opacity-40"
            >
              Prev
            </button>
            <span className="rounded-md border border-hairline bg-void px-3 py-1.5 text-slate-300">
              {currentPage} / {totalPages}
            </span>
            <button
              type="button"
              disabled={currentPage >= totalPages}
              onClick={() => setPage((value) => Math.min(totalPages, value + 1))}
              className="rounded-md border border-hairline px-3 py-1.5 font-semibold text-slate-300 disabled:cursor-not-allowed disabled:opacity-40"
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function PnlPerformanceChart({ title, positions }: { title: string; positions: PositionHistoryItem[] }) {
  const sortedPositions = useMemo(
    () => [...positions].sort((a, b) => new Date(a.closedAt).getTime() - new Date(b.closedAt).getTime()),
    [positions]
  );
  const totalPnl = sortedPositions.reduce((sum, position) => sum + position.realizedPnl, 0);
  const totalTrades = sortedPositions.length;
  const chartData = useMemo(() => pnlLineChartData(sortedPositions), [sortedPositions]);
  const chartOptions = useMemo(() => pnlLineChartOptions(), []);

  return (
    <div className="rounded-md border border-hairline bg-void p-3">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="font-semibold text-slate-200">{title}</div>
          <div className="text-xs text-slate-500">Line = cumulative realized PnL from closed positions</div>
        </div>
        <div className={`text-xs font-semibold ${totalPnl >= 0 ? "text-emerald-300" : "text-red-300"}`}>
          {formatSignedNumber(totalPnl)} / {totalTrades} trades
        </div>
      </div>
      <div className="relative h-56 overflow-hidden rounded border border-slate-900">
        <Line data={chartData} options={chartOptions} />
        {!sortedPositions.length && (
          <div className="absolute inset-0 grid place-items-center">
            <EmptyState text="No closed positions yet." />
          </div>
        )}
      </div>
    </div>
  );
}

function pnlLineChartData(positions: PositionHistoryItem[]): ChartData<"line", number[], string> {
  let cumulativePnl = 0;

  return {
    labels: positions.map((position, index) => {
      const closedAt = new Date(position.closedAt);
      return `${index + 1}. ${closedAt.toLocaleDateString(undefined, { month: "short", day: "numeric" })} ${closedAt.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}`;
    }),
    datasets: [
      {
        label: "Cumulative Realized PnL",
        data: positions.map((position) => {
          cumulativePnl += position.realizedPnl;
          return Number(cumulativePnl.toFixed(4));
        }),
        borderColor: "#38bdf8",
        backgroundColor: "transparent",
        pointBackgroundColor: positions.map((position) => position.realizedPnl >= 0 ? "#16c784" : "#ea3943"),
        pointBorderColor: "#0f172a",
        pointBorderWidth: 2,
        pointRadius: 4,
        pointHoverRadius: 5,
        borderWidth: 2,
        tension: 0.28,
        fill: false
      }
    ]
  };
}

function pnlLineChartOptions(): ChartOptions<"line"> {
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    resizeDelay: 120,
    interaction: {
      mode: "index",
      intersect: false
    },
    plugins: {
      legend: {
        labels: {
          color: "#94a3b8",
          boxWidth: 10,
          boxHeight: 10,
          usePointStyle: true
        }
      },
      tooltip: {
        backgroundColor: "#020617",
        borderColor: "#334155",
        borderWidth: 1,
        titleColor: "#e2e8f0",
        bodyColor: "#cbd5e1",
        callbacks: {
          label: (context) => `${context.dataset.label}: ${formatSignedNumber(Number(context.raw ?? 0))}`
        }
      }
    },
    scales: {
      x: {
        grid: {
          color: "#0f172a"
        },
        ticks: {
          color: "#64748b",
          maxRotation: 0,
          autoSkip: true
        }
      },
      y: {
        beginAtZero: true,
        grid: {
          color: "#1e293b"
        },
        ticks: {
          color: "#64748b",
          callback: (value) => formatSignedNumber(Number(value), 2)
        }
      }
    }
  };
}

function closeReasonLabel(reason: number | string | null | undefined) {
  if (reason === null || reason === undefined || reason === "") return "-";
  if (typeof reason === "string") {
    const numeric = Number(reason);
    if (!Number.isNaN(numeric)) return closeReasonLabel(numeric);
    return reason;
  }
  const labels: Record<number, string> = {
    0: "Unknown",
    1: "Take Profit",
    2: "Stop Loss",
    3: "Auto Close",
    4: "Manual Close",
    5: "Trailing Stop"
  };
  return labels[reason] ?? String(reason);
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
          <div className="mt-1 text-xs text-slate-500">Max daily loss {formatPercent(risk.maxDailyLossPercent)}</div>
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
          <div key={`${position.symbol}-${position.positionSide}`} className="border-t border-hairline py-3">
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
      <div className="rounded-md border border-hairline bg-void p-3 text-xs text-slate-400">
        {result.indicators.join(" / ")}
      </div>
      <div className="max-h-[165px] overflow-y-auto">
        {result.trades.slice(0, 6).map((trade, index) => (
          <div key={`${trade.entryTime}-${index}`} className="grid grid-cols-4 border-b border-hairline py-2 text-xs last:border-b-0">
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

function OpenPositions({ positions, journal }: { positions: FuturesPositionInfo[]; journal?: JournalResponse }) {
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
        const margin = position.leverage > 0
          ? Math.abs(Number(position.positionAmount)) * Number(position.entryPrice) / Number(position.leverage)
          : Number(position.isolatedMargin);
        const key = `${position.symbol}-${position.positionSide}`;
        const protective = getActiveProtectiveLevels(position, journal);
        return (
          <div key={key} className="rounded-md border border-hairline bg-void p-3">
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
              <PositionStat label="Margin" value={formatNumber(margin)} />
              <PositionStat label="Unreal PnL" value={formatSignedNumber(pnl)} />
              <PositionStat label="Entry" value={formatNumber(position.entryPrice)} />
              <PositionStat label="Break Even" value={formatNumber(position.breakEvenPrice)} />
              <PositionStat label="Mark" value={formatNumber(position.markPrice)} />
              <PositionStat label="Liq" value={formatNumber(position.liquidationPrice)} />
              <PositionStat label="Active TP" value={protective.takeProfit ? formatNumber(protective.takeProfit) : "-"} />
              <PositionStat label="Active SL" value={protective.stopLoss ? formatNumber(protective.stopLoss) : "-"} />
            </div>
            <button
              type="button"
              disabled={closingKey === key}
              onClick={() => closePosition(position)}
              className="mt-3 w-full rounded-md border border-hairline px-3 py-2 font-semibold text-slate-200 disabled:opacity-50"
            >
              Close Position
            </button>
          </div>
        );
      })}
      {message && <div className="rounded-md border border-hairline bg-void px-3 py-2 text-xs text-slate-300">{message}</div>}
    </div>
  );
}

function getActiveProtectiveLevels(position: FuturesPositionInfo, journal?: JournalResponse) {
  const isLong = Number(position.positionAmount) > 0;
  const closeSide = isLong ? "Short" : "Long";
  const activeProtectiveOrders = journal?.orders.filter((order) =>
    order.symbol === position.symbol &&
    order.reduceOnly &&
    order.status === "New" &&
    order.side === closeSide &&
    (order.kind === "TakeProfit" || order.kind === "StopMarket")
  ) ?? [];

  const takeProfit = activeProtectiveOrders.find((order) => order.kind === "TakeProfit")?.stopPrice ?? null;
  const stopLoss = activeProtectiveOrders.find((order) => order.kind === "StopMarket")?.stopPrice ?? null;

  return { takeProfit, stopLoss };
}

function PositionRevalidationPanel({ snapshot }: { snapshot?: OpenPositionRevalidationSnapshot }) {
  if (!snapshot || snapshot.openSide === null) {
    return <EmptyState text="No active position checks." />;
  }

  const side = snapshot.openSide === 1 ? "LONG" : "SHORT";
  const recentRecords = snapshot.records.slice(0, 3);
  return (
    <div className="grid gap-3 text-sm">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className={`font-semibold ${snapshot.openSide === 1 ? "text-emerald-300" : "text-red-300"}`}>{side}</div>
          <div className="mt-1 text-xs text-slate-500">{formatNumber(snapshot.quantity, 4)} {snapshot.symbol}</div>
          <div className="text-xs text-slate-500">Entry {formatNumber(snapshot.entryPrice)}</div>
        </div>
        <div className="text-right text-xs text-slate-500">
          <div>Next</div>
          <div className="font-semibold text-slate-300">
            {snapshot.nextCheckAt ? new Date(snapshot.nextCheckAt).toLocaleTimeString() : "-"}
          </div>
        </div>
      </div>

      <div className="max-h-[270px] overflow-y-auto">
        {recentRecords.map((record) => (
          <div key={`${record.checkedAt}-${record.action}`} className="border-t border-hairline py-3 first:border-t-0 first:pt-0">
            <div className="flex items-start justify-between gap-3">
              <div>
                <div className={`text-xs font-semibold ${revalidationActionClass(record.action)}`}>
                  {revalidationActionLabel(record.action)}
                </div>
                <div className="mt-1 text-xs text-slate-500">
                  {new Date(record.checkedAt).toLocaleTimeString()}
                </div>
              </div>
              <div className="text-right">
                <div className="text-sm font-semibold text-slate-100">{formatNumber(record.oppositeConfidence, 0)}</div>
                <div className="text-[10px] uppercase text-slate-500">Opp Conf</div>
              </div>
            </div>
            <div className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-slate-400">
              <PositionStat label="Mark" value={formatNumber(record.markPrice)} />
              <PositionStat label="PnL" value={formatSignedNumber(record.unrealizedProfit)} />
            </div>
            <div className="mt-2 text-xs text-slate-500">{record.reason}</div>
          </div>
        ))}
        {!recentRecords.length && (
          <div className="rounded-md border border-dashed border-hairline p-4 text-center text-xs text-slate-500">
            First check has not run yet.
          </div>
        )}
      </div>
    </div>
  );
}

function revalidationActionLabel(action: number) {
  if (action === 3) return "CLOSE";
  if (action === 2) return "WARNING";
  return "HOLD";
}

function revalidationActionClass(action: number) {
  if (action === 3) return "text-red-300";
  if (action === 2) return "text-amber-300";
  return "text-emerald-300";
}

function PositionStat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-xs text-slate-500">{label}</div>
      <div className="font-medium text-slate-200">{value}</div>
    </div>
  );
}

function AiUsagePanel({ usage }: { usage?: AiUsageSummary }) {
  if (!usage) {
    return <div className="text-sm text-slate-500">Belum ada panggilan Claude API.</div>;
  }
  const rupiah = (usd: number) => `Rp ${Math.round(usd * 16500).toLocaleString("id-ID")}`;
  return (
    <div className="grid gap-3">
      <div className="grid grid-cols-2 gap-3">
        <PositionStat label="Spend Hari Ini" value={`$${usage.costTodayUsd.toFixed(4)}`} />
        <PositionStat label="Spend Total" value={`$${usage.costTotalUsd.toFixed(4)}`} />
        <PositionStat label="Calls Hari Ini" value={String(usage.callsToday)} />
        <PositionStat label="Calls Total" value={String(usage.callsTotal)} />
      </div>
      <div className="rounded-md border border-hairline bg-slate-900/50 p-3 text-xs text-slate-400">
        <div>≈ {rupiah(usage.costTodayUsd)} hari ini · {rupiah(usage.costTotalUsd)} total</div>
        <div className="mt-1">
          Token: {usage.inputTokensTotal.toLocaleString()} in / {usage.outputTokensTotal.toLocaleString()} out
        </div>
        {usage.lastModel && (
          <div className="mt-1">Model terakhir: {usage.lastModel}</div>
        )}
      </div>
      <p className="text-xs text-slate-600">
        Estimasi pemakaian (bukan sisa saldo — Anthropic tidak menyediakan API saldo). Sisa credit cek di console.anthropic.com.
      </p>
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
      <input className="rounded-md border border-hairline bg-void px-3 py-2" placeholder="Lot / quantity" value={quantity} onChange={(event) => setQuantity(event.target.value)} />
      <input className="rounded-md border border-hairline bg-void px-3 py-2" placeholder="Leverage" value={leverage} onChange={(event) => setLeverage(event.target.value)} />
      <input className="rounded-md border border-hairline bg-void px-3 py-2" placeholder="Take profit" value={takeProfit} onChange={(event) => setTakeProfit(event.target.value)} />
      <input className="rounded-md border border-hairline bg-void px-3 py-2" placeholder="Stop loss" value={stopLoss} onChange={(event) => setStopLoss(event.target.value)} />
      <div className="grid grid-cols-2 gap-2">
        <button type="button" disabled={isSubmitting} onClick={() => close(1)} className="rounded-md border border-hairline px-3 py-2 font-semibold text-slate-200 disabled:opacity-50">Close Long</button>
        <button type="button" disabled={isSubmitting} onClick={() => close(2)} className="rounded-md border border-hairline px-3 py-2 font-semibold text-slate-200 disabled:opacity-50">Close Short</button>
      </div>
      {message && <div className="rounded-md border border-hairline bg-void px-3 py-2 text-xs text-slate-300">{message}</div>}
    </form>
  );
}

const ACTION_LABELS: Record<number, string> = {
  1: "STRONG SELL", 2: "SELL", 3: "WEAK SELL", 4: "HOLD", 5: "WEAK BUY", 6: "BUY", 7: "STRONG BUY"
};
const REGIME_LABELS: Record<number, string> = {
  0: "Trending", 1: "Ranging", 2: "High Volatility", 3: "Low Volatility",
  4: "Trending Up", 5: "Trending Down"
};

function AiDecisionPanel({ decision }: { decision?: AiDecision | null }) {
  if (!decision) {
    return <EmptyState text="Menunggu hasil analisa AI dari worker (jalan tiap 30 detik)..." />;
  }

  const action = ACTION_LABELS[decision.action] ?? "UNKNOWN";
  const isBuy = decision.action >= 5;
  const isSell = decision.action <= 3;
  const actionColor = isBuy ? "text-up" : isSell ? "text-down" : "text-slate-300";
  const actionRing = isBuy ? "border-exchangeGreen/40 bg-exchangeGreen/10" : isSell ? "border-exchangeRed/40 bg-exchangeRed/10" : "border-hairline bg-void/40";

  return (
    <div className="grid gap-3 text-sm">
      <div className={`flex items-center justify-between gap-3 rounded-lg border px-4 py-3 ${actionRing}`}>
        <div>
          <div className={`text-xl font-bold tracking-[0.08em] ${actionColor}`}>{action}</div>
          <div className="label-micro mt-1">{REGIME_LABELS[decision.regime] ?? "?"} regime</div>
        </div>
        <div className="text-right">
          <div className="tabular val-xl text-slate-50">{formatNumber(decision.confidence, 1)}</div>
          <div className="label-micro mt-0.5">conviction</div>
        </div>
      </div>

      {/* One bar instead of three tiles: buy and sell are two ends of the same
          number, and seeing where the needle sits reads faster than comparing digits. */}
      <div>
        <div className="mb-1.5 flex items-center justify-between">
          <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-down">
            Sell {formatNumber(decision.confidenceSell, 0)}
          </span>
          <span className="label-micro">Hold {formatNumber(decision.confidenceHold, 0)}</span>
          <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-up">
            Buy {formatNumber(decision.confidenceBuy, 0)}
          </span>
        </div>
        <div className="relative h-2 overflow-hidden rounded-full bg-void">
          <div className="absolute inset-y-0 left-0 w-1/2 bg-exchangeRed/20" />
          <div className="absolute inset-y-0 right-0 w-1/2 bg-exchangeGreen/20" />
          <span className="absolute inset-y-0 left-1/2 w-px bg-slate-600" />
          <span
            className={`absolute top-1/2 h-3.5 w-1 -translate-y-1/2 rounded-full ${isBuy ? "bg-exchangeGreen shadow-up" : isSell ? "bg-exchangeRed shadow-down" : "bg-slate-400"}`}
            style={{ left: `calc(${Math.max(2, Math.min(98, decision.confidenceBuy))}% - 2px)` }}
          />
        </div>
      </div>

      <div
        className={`flex items-center gap-2 rounded-md border px-3 py-2 text-xs font-semibold ${
          decision.shouldTrade
            ? "border-exchangeGreen/40 bg-exchangeGreen/10 text-exchangeGreen"
            : "border-hairline bg-void/50 text-slate-400"
        }`}
      >
        <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${decision.shouldTrade ? "bg-exchangeGreen animate-pulseDot" : "bg-slate-600"}`} />
        <span className="min-w-0 truncate">
          {decision.shouldTrade ? "TRADE SIGNAL ACTIVE" : decision.noTradeReason || "conditions not met"}
        </span>
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
        <div className="label-micro mb-2">Factor scores — 50 is neutral</div>
        <div className="grid gap-1.5">
          {Object.entries(decision.scores).map(([name, score]) => {
            // Bars grow out from the midpoint rather than from zero: what matters is
            // which way a factor leans and how hard, not its absolute value.
            const clamped = Math.max(0, Math.min(100, score));
            const offset = clamped - 50;
            const width = `${Math.abs(offset)}%`;
            const bullish = offset > 0;
            return (
              <div key={name} className="flex items-center gap-2">
                <span className="w-[86px] shrink-0 truncate text-[11px] text-slate-400">{name}</span>
                <div className="relative h-2 flex-1 overflow-hidden rounded-sm bg-void">
                  <span className="absolute inset-y-0 left-1/2 w-px bg-slate-700" />
                  <div
                    className={`absolute inset-y-0 ${bullish ? "bg-exchangeGreen/80" : "bg-exchangeRed/80"}`}
                    style={bullish ? { left: "50%", width } : { right: "50%", width }}
                  />
                </div>
                <span className={`tabular w-8 shrink-0 text-right text-[11px] ${Math.abs(offset) < 2 ? "text-slate-500" : bullish ? "text-up" : "text-down"}`}>
                  {formatNumber(score, 0)}
                </span>
              </div>
            );
          })}
        </div>
      </div>

      {decision.llm.used && (
        <div className="rounded-md border border-indigo-400/30 bg-indigo-500/[0.07] p-3">
          <div className="mb-1.5 flex items-center gap-2">
            <Bot className="h-3.5 w-3.5 shrink-0 text-indigo-300" />
            <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-indigo-300">Claude</span>
            <span
              className={`rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${
                decision.llm.confirmed ? "bg-exchangeGreen/15 text-exchangeGreen" : "bg-warn/15 text-warn"
              }`}
            >
              {decision.llm.confirmed ? "confirmed" : "hesitant — defensive sizing"}
            </span>
          </div>
          {decision.llm.narrative && <div className="text-xs leading-relaxed text-slate-300">{decision.llm.narrative}</div>}
          {decision.llm.risks.length > 0 && (
            <ul className="mt-2 grid gap-1">
              {decision.llm.risks.map((r, i) => (
                <li key={i} className="flex gap-1.5 text-xs text-warn/85">
                  <span className="text-warn/50">▸</span>
                  <span>{r}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      <div>
        <div className="label-micro mb-1.5">Reasoning trace</div>
        <div className="max-h-[170px] overflow-y-auto rounded-md border border-hairline bg-void/70 p-2">
          {decision.reasons.map((r, i) => (
            <div key={i} className="border-b border-hairline/50 py-1 text-[11px] leading-relaxed text-slate-400 last:border-0">
              {r}
            </div>
          ))}
        </div>
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
  return (
    <div className="flex flex-col items-center gap-2 rounded-md border border-dashed border-hairline bg-void/40 px-6 py-8 text-center">
      <span className="h-1.5 w-1.5 rounded-full bg-cyan/60 animate-pulseDot" />
      <span className="text-xs leading-relaxed text-slate-500">{text}</span>
    </div>
  );
}
