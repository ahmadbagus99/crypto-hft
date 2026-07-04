# Context — Penggunaan & Biaya API (Anthropic + LunarCrush)

> Update terakhir (2026-07-04): **AI Learning dirombak total — semua komponen
> belajar dari Position History (realized PnL)**, bukan lagi cuma price movement
> 60 menit: bobot faktor, SL/TP baseline, leverage baseline, dan Claude (diberi
> "memori" track record via prompt). Verdict Claude dicatat per decision dan bisa
> diaudit vs hasil nyata. Lihat Bagian 8.
> Sebelumnya (2026-07-02): confidence jadi SATU-SATUNYA gate buka order, Claude
> advisory murni (tak bisa veto) & selalu menentukan size/leverage/SL-TP,
> affordable-leverage untuk akun kecil, analisa di-PAUSE selama posisi terbuka,
> SL/TP via Binance **Algo Order API** (Bagian 7); scoring engine 10-kategori
> simetris (Bagian 6), model dropdown, confidence configurable, persistence
> settings ke DB, usage tracking, LunarCrush, decision caching.

## 1. Anthropic API key dipakai untuk apa saja?

Hanya **2 tempat**:

### a) Validasi keputusan trading (fungsi utama)
File: `backend/src/CryptoHft.Infrastructure/Ai/ClaudeDecisionValidator.cs`

- Lapisan "Hybrid" — Claude jadi **second opinion** atas sinyal rule-based.
- Dipanggil hanya jika sinyal **kuat & actionable**: `confidence >= ConfidenceThreshold`
  DAN `action != NoTrade`. Threshold ini **bisa di-set** dari dashboard
  (Settings → "Min Confidence Open Order"), default 80. Sama dengan gate buka order.
  Catatan: `confidence` sekarang = **conviction sisi terpilih** (lihat Bagian 6), jadi
  SHORT kuat juga bisa lolos gate — beda dgn versi lama yg bias bullish.
- System prompt sekarang persona **"institutional quantitative crypto trader"**; dikirim
  ke Claude: 10 skor kategori, confidence BUY/SELL/HOLD, market regime, entry/SL/TP, R:R,
  funding rate, open interest, long/short ratio, orderbook imbalance, Fear & Greed,
  headline berita. Kategori tanpa data (saat provider gagal) ditandai netral, Claude
  diminta tidak mengarang nilainya.
- Claude balas JSON: `{confirmed, adjusted_confidence, size_multiplier, leverage,
  stop_loss, take_profit, narrative, risks}`.
- **Claude TIDAK bisa veto** (sejak 2026-07-01/02). `confirmed` cuma label backdrop
  (bersih vs ragu). Sizing SELALU dari Claude (size_multiplier/leverage/SL-TP,
  dalam hard cap), lepas dari confirmed — lihat Bagian 7. Narasi + risiko tampil
  di panel AI Decision. Frontend label: CONFIRMED / "HESITANT — DEFENSIVE SIZING".
- **Model bisa dipilih** dari dashboard (Settings → dropdown): Opus / Sonnet / Haiku.
- **Tiap call dicatat** ke tabel `trading.AiUsage` (token + cost) → tampil di panel
  "Claude API Usage".

### b) Test Connection
File: `backend/src/CryptoHft.Infrastructure/Ai/ConnectionTester.cs` (`TestAnthropicAsync`)
- Kirim 1 request kecil (`MaxTokens=8`, "ping") buat verifikasi key valid. Cost ~nol.

### Yang TIDAK pakai Anthropic key
- Adaptive learning / Bayesian (`AdaptiveWeightService`) → murni matematika lokal.
- SMC (Order Block, FVG, liquidity sweep) → deteksi pola lokal.
- Data harga, funding, OI, berita, Fear & Greed → Binance public API + RSS gratis.
- **Tanpa key → engine tetap jalan full rule-based (passthrough, Llm.Used=false), $0.**

---

## 2. Siapa yang memicu panggilan Claude (PENTING)

Sejak decision caching, **hanya 1 sumber**: `AutoTradingWorker` (tiap 30 detik).

- Worker jalankan analisa 1× per tick → simpan ke cache `ILatestDecisionStore`.
- Order hanya di-place kalau mode **Auto** aktif; analisa tetap jalan di mode Manual.
- Dashboard baca cache via `/api/ai/decision` (read-only, 204 kalau belum ada) —
  **buka dashboard / banyak tab = $0 tambahan**, tidak memicu analisa baru.
- Endpoint lama `/api/ai/analyze` masih ada (trigger analisa baru) tapi tidak dipakai
  dashboard.

**Konsekuensi:** biaya Claude konstan & terprediksi (~1 analisa/30 detik), terlepas
dari jumlah dashboard. Analisa jalan di kedua mode (Manual & Auto) — kalau mau Manual
benar-benar $0 saat idle, tambahkan gate `if (!AutoTradingEnabled) return;` sebelum
analisa di `AutoTradingWorker`.

**PAUSE saat posisi terbuka (2026-07-02):** `AutoTradingWorker` cek posisi di AWAL tiap
tick; kalau ADA posisi terbuka → langsung `return` SEBELUM `AnalyzeAsync`, jadi **nol
panggilan Claude & nol biaya selama memegang posisi** (toh aturan 1-posisi bikin tak
bisa trade lagi). Analisa lanjut otomatis di tick pertama setelah posisi closed;
dashboard tetap menampilkan decision terakhir yang membuka trade.

---

## 3. Estimasi biaya Anthropic per hari

### Parameter (dari kode)
| Faktor | Nilai |
|---|---|
| Loop analisa | tiap 30 detik → 2.880 cycle/hari (AutoTradingWorker) |
| Gate panggil Claude | confidence >= ConfidenceThreshold (default 80) DAN action != NoTrade |
| Model | dipilih di Settings: opus/sonnet/haiku |
| Token per call | input ~600, output max 1024 (realistis ~400) |

### Biaya per 1 panggilan (Opus 4.8: $5/1M in, $25/1M out)
- Input ~600 tok × $5/1M = $0.003
- Output ~400 tok × $25/1M = $0.010
- **≈ $0.013 per validasi (~Rp 210)**

### Estimasi per hari (tergantung % cycle yang lolos gate)
| Skenario | % cycle | Calls/hari | Biaya/hari | ≈ Rupiah/hari |
|---|---|---|---|---|
| Pasar sepi | ~3% | ~85 | ~$1.1 | ~Rp 18 rb |
| Normal | ~10% | ~290 | ~$3.8 | ~Rp 62 rb |
| Tren kuat | ~25% | ~720 | ~$9.4 | ~Rp 153 rb |
| Worst case | 100% | 2.880 | ~$37 | ~Rp 605 rb |

**Realistis: $2–$5/hari (~Rp 30rb–80rb) untuk operasi normal 24 jam.**

### Cara nekan biaya
1. Naikkan **Min Confidence** di Settings 80 → 88 (pengontrol paling efektif).
2. Ganti **Model** ke Haiku di Settings (5x lebih murah → ~$0.5–1/hari).
3. Perlambat loop AutoTradingWorker 30s → 60s (potong call setengah).
4. Kosongkan Anthropic key → full rule-based, $0.

> **Catatan saldo:** Anthropic TIDAK punya API sisa saldo/credit. Panel "Claude API
> Usage" hanya menampilkan **estimasi pemakaian** (spend) yang dihitung lokal dari token.
> Sisa credit tetap cek manual di console.anthropic.com.

---

## 4. LunarCrush (social sentiment) — opsional

File: `backend/src/CryptoHft.Infrastructure/Ai/FreeSentimentProvider.cs`

- API key diisi di Settings (tersimpan di DB seperti Anthropic key).
- Kalau key ada → fetch sentimen BTC LunarCrush v4 → **blend 50/50 dengan Fear & Greed**
  jadi faktor `SocialScore`. Tanpa key / error → fallback Fear & Greed saja.
- Faktor **Social** bobotnya kecil (0.02–0.04 per regime), jadi dampak ke confidence
  ±2–4 poin (naik kalau searah sinyal, turun kalau berlawanan).
- Adaptive learning akan naikkan bobot Social otomatis kalau terbukti prediktif
  (pantau `/api/ai/performance`).

---

## 5. Persistence & deployment

- **Settings + API keys** (Binance, Anthropic, LunarCrush, model, confidence) tersimpan
  di tabel `trading.TradingSettings` (single row) → tahan restart/redeploy. Dihydrate
  ke memory saat startup. Tabel/kolom dibuat idempotent (CREATE TABLE / ALTER TABLE IF NOT EXISTS).
- **Dashboard mengikuti trading mode**: Paper → akun simulasi (100000), Live → saldo
  real Binance (agregasi semua stablecoin USD: USDT/USDC/dll).
- **Deploy**: build/test di lokal; deploy ke VPS via Jenkins (`Jenkinsfile`) atau manual
  (`git pull && docker compose -f docker-compose.prod.yml up --build -d`).
- VPS pakai PostgreSQL + Redis yang sudah ada (`creatio-postgres`, `creatio-redis`),
  network `shared_creatio-shared`. Frontend :5005, API :5006.

---

## 6. Decision engine — scoring 10 kategori & confidence BUY/SELL/HOLD

File inti: `backend/src/CryptoHft.Application/DecisionEngine/AdvancedDecisionEngine.cs`

### Alur
1. **Data realtime** (tiap 30 detik via `AutoTradingWorker` → `AiDecisionService`):
   multi-timeframe candle, derivatives, sentiment, **macro**, **on-chain**, harga.
2. **10 kategori skor** (0-100, >50 = bullish), masing-masing dirakit dari komponen internal:
   | Kategori | Bobot | Sumber |
   |---|---|---|
   | technical | 20% | Trend (EMA) + Momentum (RSI/MACD/Stoch) + Volume (OBV) |
   | structure | 15% | SMC (FVG/OB/liquidity sweep) + market structure |
   | orderbook | 15% | book imbalance + taker buy/sell |
   | derivatives | 15% | funding, OI, long/short |
   | onchain | 10% | mempool.space (lihat di bawah) |
   | macro | 10% | Yahoo Finance (lihat di bawah) |
   | sentiment | 5% | Fear & Greed / LunarCrush |
   | news | 5% | RSS headline |
   | liquidity | 5% | spread + imbalance |
   | volatility | 5% | ATR% |
   Bobot tetap (`CategoryWeights`), masih bisa di-tweak adaptive multiplier (Bayesian)
   per kategori. Regime sekarang dipakai utk SL/TP/leverage & display, bukan pilih bobot.
3. **Directional score D** = Σ(skor × bobot), 0-100, 50 = netral.
4. **Confidence simetris**:
   - `confidence_buy  = D`
   - `confidence_sell = 100 − D`
   - `confidence_hold = 100 − |D−50|×2`
   - `Confidence` (field lama) = conviction sisi terpilih (buy bila long, sell bila short).
     → inilah fix bug lama: dulu `confidence` skor bullish mentah, SHORT tak pernah ≥ threshold.
5. **Decision rule** (`ToAction`, simetris): D ≥ 65 LONG, D ≤ 35 SHORT, 45-55 HOLD.
6. **Gate buka order** — SUPERSEDED oleh Bagian 7 (2026-07-02): confidence kini
   satu-satunya hard gate; cek kualitas lain jadi `Cautions` (advisory), Claude
   tidak bisa veto.

### Sumber data gratis baru (tanpa API key)
- **Macro** — `FreeMacroProvider.cs`, Yahoo Finance chart API (endpoint publik unofficial).
  Tarik daily close S&P500 (^GSPC), NASDAQ (^IXIC), DXY (DX-Y.NYB), Gold (GC=F),
  hitung momentum ~5 hari → skor risk-on 0-100 (`50 + equity%×8 − DXY%×10 + gold%×2`).
  Cache 15 menit. Catatan path JSON: `chart.result[0].indicators.quote[0].close` (plural).
- **On-chain** — `FreeOnchainProvider.cs`, mempool.space (gratis, no key). 3 sinyal:
  hashrate trend 1-bulan (`/v1/mining/hashrate/1m`), difficulty adjustment
  (`/v1/difficulty-adjustment` → `difficultyChange`), fee demand (`/v1/fees/recommended`).
  Skor: `50 + hashrate%×1.5 + diff%×1.5 ± fee_nudge`, clamp. Cache 20 menit.
  **Keterbatasan jujur:** ini proxy network-health/miner-confidence, BUKAN valuasi
  (tidak ada MVRV/SOPR/NUPL — itu butuh Glassnode/CryptoQuant berbayar).
- Semua provider: kalau gagal → fallback skor 50 + flag `[no data source — neutral]`
  di reasoning, tanpa error. `Available=false` saat tidak ada sumber yang merespons.

### Catatan tuning
- Min Confidence default masih **80** → dgn semantik baru artinya butuh D ≥ 80 (long) /
  D ≤ 20 (short), sangat ketat & jarang tercapai. Spec menyarankan **65**. Belum diubah
  di kode (default `TradingEntities.cs` / `RuntimeTradingSettingsService.cs` = 80) —
  bisa diturunkan via dashboard atau ubah default. (Server live saat ini di-set ~62.)
- Frontend: `AiDecision` type + panel sudah menampilkan baris BUY/HOLD/SELL confidence.

---

## 7. Eksekusi auto-trade (gate, sizing, leverage, SL/TP) — rombakan 2026-07-02

File: `AiDecisionService.cs`, `AdvancedDecisionEngine.cs`, `AutoTradingWorker.cs`,
`BinanceFuturesTradingExecutor.cs`.

### Gate buka order (disederhanakan)
- **Confidence = SATU-SATUNYA hard gate.** `ShouldTrade` di engine hanya blok kalau
  (a) sinyal netral/Hold, atau (b) conviction < Min Confidence. Cek kualitas lama
  (RR<2, trend HTF tidak selaras, funding ekstrem, spread lebar) TIDAK lagi memblok —
  jadi `Cautions` yang dikirim ke Claude supaya dia kecilkan size defensif.

### Claude advisory + sizing (tidak bisa veto)
- `ApplyValidation`: selama `Used && ShouldTrade`, size/leverage/SL-TP dari Claude
  SELALU diterapkan (dalam hard cap: size 0.1–1.5× baseline, leverage 1–20x, SL/TP
  hanya dipakai bila sisi benar & RR ≥ 2.0). `confirmed` cuma label.
- qty dibulatkan 6 dp (bukan 3) supaya baseline kecil tak jadi 0.

### Affordable leverage (akun kecil)
- `ResolveAffordableLeverageAsync` di executor: order minimum exchange ≈ 0.001 BTC
  (~$60 notional). Kalau margin di leverage terpilih tak muat `UsdAvailableBalance`,
  leverage dinaikkan otomatis sampai margin ≈ `TargetMarginUsdt` (const 3) & muat,
  clamp ke `MaxAffordableLeverage` (20). Akun besar (margin sudah muat) tak diubah.
- `BinanceExchangeRuleValidator` menaikkan qty terlalu kecil ke floor exchange.

### Aturan 1 posisi + pause analisa
- `AutoTradingWorker`: kalau ada posisi terbuka → return di awal tick (tak buka posisi
  ke-2, dan tak panggil Claude). Lihat Bagian 2.

### SL/TP via Binance Algo Order API (PENTING — quirk akun)
- Akun futures live ini **menolak stop order di endpoint biasa** (`order.place` /
  `/fapi/v1/order`) dgn **-4120** ("use Algo Order API"). Hanya MARKET/LIMIT diterima.
- Solusi: protective SL/TP dipasang lewat WS **`algoOrder.place`**, param
  **`algoType:"CONDITIONAL"`**, trigger pakai **`triggerPrice`** (BUKAN `stopPrice`),
  `closePosition:"true"`, `workingType:MARK_PRICE`. Response balikkan `algoId`.
- **Signing:** semua harga dikirim sebagai STRING (`PriceParam`) — kalau angka JSON,
  Binance buang trailing-zero tick (`61777.00`→`61777`) saat verifikasi → **-1022**.
- Order entry (MARKET) tetap lewat `order.place`.
- Read posisi pakai REST `/fapi/v2/positionRisk`; order-updates pakai REST
  `/fapi/v1/allOrders` (signing REST men-sign query string persis yang dikirim).

> Detail quirk & langkah debug tersimpan di memory `binance-sltp-quirks`.

---

## 8. AI Learning dari Position History — semua komponen belajar (2026-07-04)

**Tujuan owner:** sistem + AI menjalankan trading mandiri menggantikan manusia,
optimal tapi tetap risk-managed. Prinsip arsitektur: *sistem menentukan sinyal,
learning mengkalibrasi parameter dari hasil NYATA, AI menghaluskan eksekusi,
risk gate menjaga survival.* Tidak ada komponen yang bergantung pada "kepintaran"
satu model.

File inti: `AdaptiveWeightService.cs`, `AiLearningWorker.cs`, `ExecutionTuningPolicy.cs`,
`PositionCloseClassifier.cs`, `PositionHistoryService.cs`, `AdvancedDecisionEngine.cs`,
`ClaudeDecisionValidator.cs`.

### a) Realized-outcome learning (pengganti price-movement 60 menit)
- Dulu: decision dievaluasi 60 menit kemudian, "win" = harga bergerak searah ≥ 0.3%.
  Kasar & sering salah (posisi bisa TP di jam ke-3 padahal menit-60 masih merah).
- Sekarang: saat posisi tertutup masuk Position History, di-MATCH ke decision
  pembukanya (kolom `AiDecisionLogs.MatchedPositionId`; match = symbol + arah
  BUY↔LONG/SELL↔SHORT + CreatedAt dalam window 10 menit sebelum OpenedAt).
- **Win = realizedPnl > 0** (≤ 0 = loss). Update Alpha/Beta faktor **berbobot
  besaran**: increment = clamp(1 + |ROI|, 1, 3) — profit/loss besar mengajar
  lebih keras.
- **Anti double-count:** 1 posisi hanya match 1 decision 1×; decision matched
  di-set Evaluated sehingga dilewati horizon 60 menit.
- **Fallback tetap ada:** decision yang tak pernah jadi trade → evaluasi price
  60-menit lama (bobot 1). Decision yang MEMBUKA order live (ada row Orders
  entry non-paper ≤ 5 mnt setelahnya) di-DEFER dari fallback s/d closed position
  muncul (deadline fail-safe 48 jam). Order paper tetap fallback.
- `AiLearningWorker` (5 menit): pass closed-position dulu (tak butuh price feed),
  lalu fallback — try/catch terpisah.

### b) Verdict Claude dicatat & diadili data
- Kolom baru di `AiDecisionLogs`: `LlmConfirmed`, `LlmSizeMultiplier` (proposal
  mentah), `LlmLeverage`, `LlmStopsApplied` (SL/TP-nya lolos validasi & dipakai).
  Null bila LLM tidak dipanggil.
- **`GET /api/ai/validation-performance`** — realized outcome per verdict
  (confirmed / hesitant / no-validation): samples, winrate, avg ROI, total PnL,
  avg size multiplier. Menjawab empiris: *apakah keraguan Claude prediktif?*
  → dasarnya nanti untuk memperbesar/menetralkan pengaruh multiplier Claude.

### c) Exit-reason attribution (fondasi geometry learning)
- Kolom `Positions.CloseReason`: TakeProfit / StopLoss / AutoClose / ManualClose /
  Unknown. Klasifikasi KONSERVATIF (`PositionCloseClassifier`, pure & tested):
  order reduce-only market dari app (±2 mnt sebelum close) → Auto/ManualClose;
  selain itu mark price terakhir vs level SL/TP (band 0.5%, mark bisa basi ~30s);
  ambigu → Unknown dan **tidak mengajari** geometri.

### d) SL/TP baseline BELAJAR (dulu konstanta mati 2×/4×ATR)
- Tabel baru `trading.ExecutionStats` (per regime): TakeProfitHits, StopLossHits,
  Wins, Losses, SlAtrMultiplier, TpAtrMultiplier, LeverageFactor.
- `ExecutionTuningPolicy` (pure): TP-hit-rate posterior vs break-even geometri.
  TP-rate rendah (≤33%) → geometri defensif (SL melebar s/d 2.6×, TP mendekat
  s/d 3.2×); TP-rate tinggi (≥45%) → agresif (SL 1.8×, TP 5×). Linear di antaranya,
  deterministik dari counter penuh (no drift), butuh **≥10 exit** sebelum bergerak.
- **$ risk per trade KONSTAN:** qty = riskBudget / jarakSL, jadi SL melebar ⇒
  qty otomatis mengecil. Geometry berubah, risiko dolar tidak.
- Engine (`AdvancedDecisionEngine.Evaluate`) terima param `ExecutionTuning` baru;
  default mereproduksi 2×/4× lama.

### e) Leverage baseline BELAJAR
- Tier confidence tetap (≥90→10x, ≥80→5x, else 3x) tapi diskalakan
  `LeverageFactor` dari winrate realized per regime: winrate 50%→1.0×, klem
  **0.5–1.2×**, butuh ≥10 trade, cap absolut 20x tetap. Winrate jelek ⇒ leverage
  otomatis turun — risk management bergradasi berbasis bukti.

### f) Claude diberi MEMORI (via prompt, bukan veto)
- `LearningSnapshot` di-inject ke payload validator: winrate regime, rasio exit
  TP vs SL, learned baseline yang sedang dipakai, dan **track record verdict
  Claude sendiri** ("Your past 'hesitant' calls: 8 trades, 38% win — calibrate
  your sizing against this record").
- Null sampai ada data realized → prompt identik dgn sebelumnya (fail-safe;
  error snapshot juga cuma di-log debug). Biaya +~200 token/call.

### g) Endpoint observability baru
| Endpoint | Isi |
|---|---|
| `/api/ai/validation-performance` | outcome realized per verdict Claude |
| `/api/ai/execution-tuning` | counter & learned SL/TP/leverage per regime |
| `/api/ai/performance` | (lama) winrate per faktor — kini berbasis realized |

### Kesimpulan diskusi penting (untuk referensi)
- **Claude TIDAK pernah mem-block open posisi** — semua learning ini pasca-fakta
  (baca DB), jalur gate/risk/executor tidak tersentuh. Sejarah: saat Claude punya
  hak gate, dia veto terus (bias risk-averse struktural LLM) dan momen BTC
  60k→58k terlewat → hak gate dicabut permanen.
- **Confidence BISA berubah karena learning** — bukan bonus flat, tapi
  redistribusi bobot: faktor yang terbukti benar suaranya membesar (klem
  0.5–1.5×, renormalisasi). Dua arah: sinyal mirip setup profit → confidence
  naik (bisa lolos threshold), mirip setup loss → turun.
- **Ganti model (Haiku→Sonnet/Opus) TIDAK menaikkan winrate signifikan** di
  arsitektur ini: arah & gate = rule engine; Claude hanya sizing/SL-TP. Haiku
  ($1/$5 per 1M tok) proporsional utk peran sekarang; Sonnet 5 (~3×) layak
  dites utk kualitas SL/TP; keputusan pakai data validation-performance.
- **Keamanan learning:** semua nilai learned di-klem + minimum sampel; exit
  ambigu tidak mengajar; deterministik dari counter (idempotent); tanpa data →
  default = perilaku lama persis.

### Skema DB (idempotent, tanpa reset)
- `AiDecisionLogs`: + MatchedPositionId, LlmConfirmed, LlmSizeMultiplier,
  LlmLeverage, LlmStopsApplied (ALTER TABLE IF NOT EXISTS).
- `Positions`: + CloseReason (int, default 0).
- `ExecutionStats`: tabel baru, unique per Regime.

### Testing
- 60 unit test pass (Docker .NET 9; host Mac tak punya runtime .NET 9).
  Baru: matching & anti-double-count, defer executed decision, arah BUY↔LONG,
  weighted ROI + clamp, verdict logging, classifier exit, policy geometry/leverage.

### Roadmap tersisa (butuh data realized terkumpul dulu)
1. Recency decay FactorStats (belajar tak terjebak masa lalu).
2. Drawdown-aware sizing (size turun bertahap setelah loss beruntun, bukan
   langsung pause).
3. Adaptive confidence threshold per regime.
4. Trailing stop / break-even move via Algo Order API (TrailingStopPercent
   sudah dihitung engine, belum dipakai).
5. Kalibrasi otomatis pengaruh multiplier Claude dari validation-performance
   (~30+ sampel).
