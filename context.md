# Context — Penggunaan & Biaya API (Anthropic + LunarCrush)

> Update terakhir (2026-07-29): **Batch responsiveness v1.3 (Bagian 18)** — gate
> dilonggarkan atas keputusan owner (konfirmasi 2 menit, strong signal threshold+4,
> hesitant hurdle dihapus; cooldown pasca-close TETAP), analisis level klasik baru
> (Fibonacci retracement, chart patterns triangle/rectangle + breakout, horizontal
> S/R clustering + TP snap), news 5 feed paralel. Latar: production sengaja jalan di
> branch v1 karena gate batch 17 hampir tidak pernah membuka posisi.
> Sebelumnya (2026-07-22): **Entry quality gate (Bagian 17)** — sinyal
> marginal harus terkonfirmasi dua kali dengan jarak 5 menit, sinyal kuat boleh
> langsung lanjut, risk pause dicek sebelum Claude, verdict hesitant menaikkan
> hurdle 2 poin, multiplier Claude menjadi defensive cap untuk target sizing,
> dan entry diberi cooldown 30/60 menit setelah posisi tertutup.
> Sebelumnya (2026-07-05): **Akurasi confidence dirombak (Bagian 9)** —
> volatility/liquidity tak lagi ikut voting arah (jadi condition dampener), OI×harga
> matrix + CVD + liquidation stream + cumulative funding, regime Trending dipecah
> Up/Down, learning per-faktor berbasis directional accuracy + recency decay +
> auto-inversi faktor anti-prediktif, calibration curve `/api/ai/confidence-calibration`,
> kalender FOMC/CPI sebagai caution, F&G contrarian di ekstrem, RSI divergence.
> Sebelumnya (2026-07-04): **AI Learning dirombak total — semua komponen
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
- Di Auto mode, dipanggil setelah sinyal rule-based siap entry: dua konfirmasi searah
  berjarak minimal 5 menit, atau satu sinyal kuat `confidence >= threshold + 7`.
  Risk pause akun diperiksa lebih dulu agar Claude tidak dipanggil untuk order yang
  memang tidak dapat dieksekusi.
  Catatan: `confidence` sekarang = **conviction sisi terpilih** (lihat Bagian 6), jadi
  SHORT kuat juga bisa lolos gate — beda dgn versi lama yg bias bullish.
- System prompt sekarang persona **"institutional quantitative crypto trader"**; dikirim
  ke Claude: 10 skor kategori, confidence BUY/SELL/HOLD, market regime, entry/SL/TP, R:R,
  funding rate, open interest, long/short ratio, orderbook imbalance, Fear & Greed,
  headline berita. Kategori tanpa data (saat provider gagal) ditandai netral, Claude
  diminta tidak mengarang nilainya.
- Claude balas JSON: `{confirmed, adjusted_confidence, size_multiplier, leverage,
  stop_loss, take_profit, narrative, risks}`.
- Claude tetap tidak boleh membalik arah atau menambah risiko. Namun verdict-nya
  sekarang menjadi **bounded entry hurdle**: `confirmed=true` memakai threshold
  normal; `confirmed=false` hanya lolos jika confidence rule engine minimal
  `threshold + 2`. Jika API Claude tidak tersedia, sinyal rule-based yang sudah
  terkonfirmasi tetap dapat lanjut (fail-open terukur, tetap melalui risk gate).
- Pada mode Margin × Leverage, `size_multiplier` Claude diterapkan sebagai cap
  defensif setelah target sizing dan dibatasi maksimal 1.0×, sehingga Claude bisa
  mengecilkan tetapi tidak bisa membesarkan target. SL/TP Claude tetap tunduk pada
  validasi geometri/hard cap yang sudah ada.
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

Sejak decision caching, **hanya 1 sumber**: `AutoTradingWorker`.

- Di Auto mode, worker menjalankan dan mencatat scan rule-based tiap 30 detik.
  Scan ini tidak memakai token Claude.
- Claude baru dipanggil setelah entry candidate lolos konfirmasi 5 menit atau sinyal
  kuat `threshold + 7`, dan status risk gate mengizinkan trading.
- Setelah Claude menolak sinyal marginal, candidate di-reset; percobaan berikutnya
  perlu dua konfirmasi baru. Jika order berhasil, analisa berhenti selama posisi terbuka.
- Di Manual mode, perilaku dashboard lama dipertahankan: `AnalyzeAsync` berjalan tiap
  tick, tetapi validator tetap hanya memanggil API untuk sinyal actionable.
- Dashboard baca cache via `/api/ai/decision` (read-only, 204 kalau belum ada) —
  **buka dashboard / banyak tab = $0 tambahan**, tidak memicu analisa baru.
- Endpoint lama `/api/ai/analyze` masih ada (trigger analisa baru) tapi tidak dipakai
  dashboard.

**Konsekuensi:** biaya Claude di Auto mode mengikuti jumlah entry attempt yang sudah
terkonfirmasi, bukan jumlah tick worker. Jumlah dashboard tetap tidak memengaruhi biaya.

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
| Loop rule scan | tiap 30 detik → 2.880 cycle/hari; tidak memakai token Claude |
| Gate panggil Claude | 2 konfirmasi/5 menit, atau confidence >= threshold + 7; risk gate harus aktif |
| Model | dipilih di Settings: opus/sonnet/haiku |
| Token per call | input ~600, output max 1024 (realistis ~400) |

### Biaya per 1 panggilan (Opus 4.8: $5/1M in, $25/1M out)
- Input ~600 tok × $5/1M = $0.003
- Output ~400 tok × $25/1M = $0.010
- **≈ $0.013 per validasi (~Rp 210)**

### Estimasi per hari

Tidak lagi tepat mengalikan 2.880 tick dengan biaya per call. Di Auto mode, ukur
langsung jumlah entry attempt pada `AiUsage`: satu sinyal marginal yang stabil baru
boleh memicu call setelah 5 menit, dan order yang berhasil menghentikan seluruh call
selama posisi masih terbuka.

### Cara nekan biaya
1. Naikkan **Min Confidence** di Settings 80 → 88 (pengontrol paling efektif).
2. Ganti **Model** ke Haiku di Settings (5x lebih murah → ~$0.5–1/hari).
3. Naikkan confirmation delay di kode jika entry attempt masih terlalu sering.
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
  60-menit, tetapi snapshot berkorelasi di-collapse menjadi maksimal 1 evidence per
  symbol+regime+arah+jam dan berbobot lemah 0.25×. Decision yang MEMBUKA order live (ada row Orders
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

---

## 9. Akurasi confidence — rombakan rule engine & learning (2026-07-05)

File inti: `AdvancedDecisionEngine.cs`, `AdaptiveLearningPolicy.cs` (baru),
`AdaptiveWeightService.cs`, `MarketRegimeDetector.cs`, `EconomicEventCalendar.cs` (baru),
`LiquidationTracker.cs` (baru), `BinanceDerivativesProvider.cs`,
`BinanceMultiTimeframeProvider.cs`, `BinanceFuturesWebSocketStream.cs`.

### a) Volatility & liquidity tak lagi voting arah (bug struktural lama)
- Dulu "volatilitas sehat = skor 65" mendorong D bullish padahal bukan sinyal arah.
- Sekarang keduanya keluar dari bobot directional (bobot dinormalisasi ulang:
  technical 22%, structure/orderbook 17%, derivatives 16%, onchain/macro 10%,
  sentiment/news 4%) dan jadi **condition dampener**: ATR ekstrem / tape mati /
  spread lebar mengkompres conviction dua sisi ke arah 50 (floor 0.65, tak pernah
  memblok — muncul sebagai Caution). Skor keduanya tetap tampil (display + Claude).
- Learning juga meng-exclude keduanya (condition gauge tak diadili soal arah).

### b) Sinyal derivatives kontekstual (bukan snapshot terisolasi)
- **Matriks OI×harga**: OI naik+harga naik = long baru (+12); OI naik+harga turun =
  short baru (−12); OI turun+harga naik = short covering (−5, rally rapuh); OI turun+
  harga turun = long capitulation (+5). Harga dari candle 5m terakhir.
- **Cumulative funding 24h** (3 periode via `/fapi/v1/fundingRate`): crowd persisten
  ≠ satu print stretched.
- **Liquidation stream** `btcusdt@forceOrder` (WS publik, dulu di-subscribe tapi
  dibuang): `LiquidationTracker` singleton rolling 30 menit; window 5 menit masuk
  snapshot. Flush satu sisi ≥ $1M & ≥75% dominan → contrarian ±10 (long flush =
  bullish, short squeeze = bearish). Feed dingin = netral, bukan basi.
- **CVD** dari taker buy volume kline (field 9, sekarang di-parse): net delta 1 jam
  (12×5m) vs pergerakan harga; divergence (rally di atas net selling = absorption
  −10; decline di atas net buying = accumulation +10), konfirmasi ±6.

### c) Regime directional + scoring regime-aware
- `Trending` dipecah **TrendingUp / TrendingDown** (enum di-APPEND, nilai lama tetap
  valid; ADX≥25 + EMA20 vs EMA50). Bucket learning up-trend tak lagi tercampur
  down-trend. Data `Trending` lama dibiarkan (orphan, tak dibaca lagi).
- Bollinger mean-reversion nudge di PriceAction kini **hanya aktif di regime
  Ranging** — di trending market itu knife-catch yang diam-diam melawan faktor Trend.

### d) Learning per-faktor: directional accuracy + decay + auto-inversi
- **Semantik baru**: faktor diadili atas ARAH DIA SENDIRI vs arah realized market
  (dulu: hanya faktor yang "setuju dengan trade" dapat kredit/blame — faktor yang
  konsisten salah arah tak pernah terukur). Win long / loss short = harga naik, dst.
- **Recency decay**: excess evidence di atas prior Beta(1,1) half-life 30 hari
  (roadmap #1 SELESAI). FactorStats di-rebuild deterministik dari source evidence;
  window 120 hari mencakup 4 half-life dan mencegah counter incremental drift.
- **Auto-inversi**: akurasi < 40% dengan ≥15 sampel → engine melipat skor faktor
  (100−s) sebelum blend + multiplier pakai akurasi efektif (1−mean). Weight scaling
  saja tak bisa memperbaiki faktor yang reliably wrong-way (clamp 0.5×).
  `/api/ai/performance` sekarang expose flag `inverted`.
- **Dead band fallback**: evaluasi price-move 60-menit hanya mengajar faktor bila
  |move| ≥ 0.15% (jam datar = noise, bukan bukti).
- Aturan murni di `AdaptiveLearningPolicy` (unit-tested).

### e) Kalibrasi confidence (fondasi adaptive threshold)
- **`GET /api/ai/confidence-calibration`**: realized winrate per bucket confidence
  5-poin (dari decision yang matched ke closed position). Menjawab: apakah
  "confidence 70" benar menang ~70%? Observability dulu; nanti dasar adaptive
  threshold per regime (roadmap #3).

### f) Sentiment & event
- **F&G contrarian di ekstrem**: FGI ≥75 → skor Social di-cap kurva foldback
  (FGI 90 → cap 45); FGI ≤25 → floor simetris. Greed ekstrem = late positioning.
- **`EconomicEventCalendar`** (statis, update tahunan dari federalreserve.gov/bls.gov):
  window ±60 menit sekitar FOMC & CPI 2026 → Caution "scheduled-event volatility
  window" (tak memblok; Claude yang downsize). ⚠️ Tanggal CPI 2026 best-effort —
  cross-check jadwal BLS.

### g) Teknikal halus
- Trend: skor EMA-stack bergradasi (separation EMA20/50 ±8 + slope EMA20 ±8),
  transisi mulus antar tick.
- Momentum: slope histogram MACD (±5) + **RSI/price divergence** 2×7 candle (±12).
- Claude payload: + cumulative funding, + liquidation window.
- `MarketRegimeDetector.WeightsFor` (dead code) dihapus.

### Testing & kompatibilitas
- **92 unit test pass** (60 lama tetap hijau tanpa modifikasi + 32 baru) via Docker
  .NET 9; frontend build OK. `InternalsVisibleTo` ditambah di Application.csproj.
- Tanpa perubahan skema DB. Enum regime di-append (int lama valid). Semua field
  snapshot baru ber-default 0 → provider gagal = netral, fail-safe seperti biasa.

---

## 10. Kalender ekonomi live + Multi-Timeframe Consensus (2026-07-05, batch 2)

### a) Kalender ekonomi otomatis (tidak lagi hardcode-only)
File: `ForexFactoryCalendarProvider.cs` (baru), `EconomicEventCalendar.cs`,
`AiDecisionService.cs`.
- Sumber: **feed publik mingguan Forex Factory** (`nfs.faireconomy.media/
  ff_calendar_thisweek.json`, gratis tanpa key — konsisten filosofi key-free).
- Filter: impact **High** + currency **USD** saja. Refresh tiap 6 jam, di-cache.
- **Fail-safe berlapis**: feed sehat (<24 jam) → feed otoritatif (termasuk minggu
  kosong); feed dingin/gagal → fallback daftar statis FOMC/CPI 2026 (Bagian 9).
  Caution tidak pernah hilang diam-diam karena fetch gagal.
- Engine kini **bebas clock**: window aktif dihitung provider, masuk lewat field
  baru `AdvancedDecisionInput.ActiveEventWindow` (null = tape sepi), engine tinggal
  menaruhnya di Cautions. Fetch paralel dengan provider lain di `AiDecisionService`.

### b) Multi-Timeframe Consensus (voting per timeframe)
File: `AdvancedDecisionEngine.cs`.
- Dulu: Trend & Momentum dinilai **hanya di 1h**, PriceAction hanya 1h; keselarasan
  lintas TF cuma cek EMA20>EMA50 → satu TF noise bisa membalik sinyal.
- Sekarang tiap TF memberi **vote trend/momentum/structure** dengan bobot:
  **5m 10%, 15m 20%, 1h 30%, 4h 25%, 1d 15%** (TF pendek = timing, TF panjang =
  bias; TF hilang/kurang candle otomatis drop + renormalisasi):
  - `Trend` = konsensus berbobot EMA-stack graded per TF.
  - `Momentum` = konsensus RSI+MACD+hist-slope+divergence per TF.
  - `PriceAction` = konsensus market structure (HH/HL) per TF + Bollinger MR
    (tetap hanya regime Ranging, di TF primer).
  - Volume, SMC (15m), orderbook/CVD, derivatives dll tidak berubah.
- **Anti double-counting**: konsensus hidup DI DALAM komponen Trend/Momentum/
  PriceAction (kategori & bobot tidak berubah, key learning stabil);
  `MultiTimeframeAgreement` (utk probability & caution) kini dihitung dari vote
  trend yang sama (share berbobot TF yang searah action).
- **Explainability**: vote per-TF tampil di Reasons
  (`MTF trend votes [5m 62 · 15m 58 · 1h 71 · 4h 66 · 1d 54]`) dan caution
  "Higher timeframe trend not aligned" sekarang menyertakan detail vote — Claude
  ikut melihat TF mana yang tidak setuju.

### Testing
- **100 unit test pass** (92 sebelumnya + 8 baru: parser feed FF (filter High/USD,
  konversi timezone), window logic feed, caution passthrough, dominasi higher-TF
  atas noise lower-TF dua arah, single-TF renormalisasi, exclude TF pendek).

---

## 11. SMC diperdalam — BOS/CHoCH, mitigasi OB, premium/discount (2026-07-05, batch 3)

File: `SmartMoneyConcepts.cs` (kategori `structure`, TF entry 15m).

- **Swing fractal** (`DetectSwings`, wing=2): high/low yang strictly melampaui 2
  tetangga di tiap sisi. Konsekuensi desain: swing butuh 2 candle konfirmasi —
  2 candle terakhir tak pernah jadi swing, pas untuk deteksi break (candle breakout
  belum punya swing sendiri).
- **BOS (Break of Structure)**: trend dari urutan swing (HH+HL = up, LH+LL = down);
  close menembus ekstrem swing SEARAH trend = continuation (±10).
- **CHoCH (Change of Character)**: close menembus swing pelindung MELAWAN trend
  (uptrend kehilangan higher-low / downtrend merebut lower-high) = peringatan
  reversal, bobot terbesar (±14).
- **Mitigasi Order Block**: OB yang harganya sudah kembali disentuh = hangus
  (resting orders terkonsumsi), tidak dihitung. Plus filter relevansi: zona harus
  di sisi pendukung harga dan dalam jangkauan ≤5×ATR. Window scan diperlebar
  10→30 candle karena freshness kini dijaga mitigasi, bukan umur.
- **Premium/discount**: posisi close dalam dealing range 60-candle; ≤0.4 discount
  (+6 bias long), ≥0.6 premium (−6). Summary selalu menyebut zona.
- Bobot skor di-rebalance (OB 12→10, FVG 10→8, sweep 15→12) memberi ruang sinyal
  struktur; sweep & FVG detection tidak berubah.
- `SmcSignals` diperluas (field BOS/CHoCH/RangePosition) — hanya dikonstruksi
  internal; engine tetap baca Score+Summary saja.

### Testing
- **112 unit test pass** (12 baru: swing fractal, BOS, CHoCH dua arah, tanpa-break,
  OB fresh vs mitigated vs out-of-reach, discount/premium/equilibrium, smoke Detect).
- Catatan test: candle sintetis butuh wick asimetris (sisi close 0.5, sisi open 0.2)
  karena open candle berikut = close sebelumnya → wick simetris bikin high kembar
  dan fractal strict tak pernah match.

---

## 12. Volume Profile untuk geometri TP/SL (2026-07-05, batch 4)

File: `VolumeProfile.cs` (baru), `AdvancedDecisionEngine.cs`,
`ClaudeDecisionValidator.cs`.

- Scope sengaja sempit: **Volume Profile bukan sinyal arah** dan tidak masuk voting
  confidence. Ia hanya memberi konteks level serta merapikan geometri TP/SL sebelum
  sizing dihitung.
- Profile dibuat dari OHLCV 1h window ±10 hari (maks 250 candle), volume candle
  dibagi rata ke histogram range candle. Output: **POC, VAH, VAL, HVN, LVN**.
- **TP snap:** jika TP melewati HVN wall pertama, target ditarik ke depan wall
  dengan buffer 0.25×ATR. Guardrail: snap hanya dilakukan jika reward tersisa
  minimal 60%; kalau wall terlalu dekat, TP tidak diubah dan hanya jadi caution
  "target likely optimistic".
- **SL adjustment:** jika SL jatuh di LVN tipis, SL dicoba ditaruh di belakang HVN
  shelf terdekat. Guardrail: pelebaran risiko maksimal 1.3×; kalau terlalu jauh,
  SL tidak diubah dan hanya diberi warning sweep risk.
- Perubahan SL/TP dilakukan **sebelum qty/RR/leverage**, jadi qty otomatis mengecil
  saat SL melebar (`riskBudget / stopDistance`). Risiko dolar tetap konstan.
- `VolumeProfileNote` masuk ke `AdvancedDecision.Reasons` dan payload Claude:
  Claude melihat POC/VA/HVN/LVN + snap yang terjadi, tapi tetap tidak punya hak
  memblok open posisi.
- Evaluasi keberhasilan tidak perlu parameter learning baru: ukur lewat
  `ExecutionStats` yang sudah ada, terutama TP-hit-rate dan close reason.

### Testing
- **125 unit test pass** via Docker .NET 9. Test baru mencakup POC/value area,
  HVN/LVN, TP snap + guardrail, SL tuck + cap 1.3×, confluence VAL/VAH, dan wiring
  engine `VolumeProfileNote`.

---

## 13. Anti-chasing dampener + pooled tuning + filter FactorStats legacy (2026-07-06, batch 5)

File: `AdvancedDecisionEngine.cs`, `ExecutionTuningPolicy.cs`, `AdaptiveWeightService.cs`.

Latar: evaluasi 5 posisi live + 4.743 AiDecisionLogs menemukan (a) confidence
**anti-kalibrasi** — bucket 60-70 win rate 10-13% vs ~24% di bucket 50-60, padahal
entry hanya dibuka di conf ≥ 65; (b) dua loss riil sama-sama pola *chasing* (short
setelah harga sudah jatuh ~3.8×ATR, long dekat puncak lokal); (c) baris FactorStats
generasi lama (nama kapital: `Trend`, `SmartMoney`, `News`, …) mencemari rata-rata
normalisasi multiplier faktor aktif.

- **Anti-chasing dampener** (`ChasingDampener`): ukur pergerakan 6 candle 1h terakhir
  dalam kelipatan ATR. Sinyal SEARAH gerakan yang sudah ≥ 2.5×ATR diredam progresif
  ke lantai 0.4× di 5×ATR — sinyal telat gagal lewat threshold entry. Counter-trend
  dan netral tidak pernah diredam (fading bukan chasing). Caution "Late entry"
  masuk dashboard + payload Claude. Sanity check vs history: membunuh kedua loss,
  meloloskan kedua winner.
- **Pooled execution tuning** (`ResolveStops`/`ResolveLeverageFactor`): regime yang
  belum punya 10 exit sendiri meminjam counter gabungan lintas regime; begitu matang,
  bukti regime sendiri menang. Learning geometry mulai hidup setelah 10 exit total,
  bukan 10 per regime (~berbulan-bulan di laju sekarang). `GetLearningSnapshotAsync`
  ikut memakai resolusi pooled supaya digest Claude = yang dieksekusi.
- **Filter legacy FactorStats**: `DirectionalCategories` (= keys `CategoryWeights`)
  jadi whitelist di `GetFactorAdjustmentsAsync` + `GetPerformanceAsync`. Tanpa filter,
  multiplier orderbook Ranging tertekan 0.72 padahal seharusnya ~1.18. Data lama di
  DB tidak dihapus — hanya diabaikan.

### Testing
- **144 unit test pass** via Docker .NET 9 (19 baru: chasing dampener aligned/counter/
  floor/threshold, RecentMoveInAtr, pooled resolve young/mature/thin).

---

## 14. Trailing Stop Guard + manajemen posisi + settings (2026-07-06 s/d 07-07, batch 6)

File: `TrailingStopPolicy.cs` (baru), `TrailingStopGuardService.cs` (baru),
`TrailingStopActivityStore.cs` (baru), `BinanceFuturesTradingExecutor.cs`,
`AutoTradingWorker.cs`, `PositionCloseClassifier.cs`, `TradingEnums.cs`,
`RuntimeTradingSettingsService.cs`, `App.tsx`.

Dua mekanisme saat posisi terbuka — jangan tertukar:

| | Trailing Stop Guard (baru) | Position Checks (lama) |
|---|---|---|
| Frekuensi | tiap 30 detik | tiap 30 menit |
| Token AI | nol (murni geometri) | nol (rule-based) |
| Tugas | geser SL naik | close jika sinyal lawan konfirmasi 2× |

- **Aturan ratchet** (R = jarak entry→SL awal; fallback |TP−entry|/2 saat restart):
  profit < 1R → SL diam; **+1R → breakeven + fee** (buffer 0.12%); lanjut →
  **trail 1R** di belakang mark, hanya naik, step minimal 0.15R (anti-spam amend);
  **jarak ke TP ≤ 0.25R → guard berhenti**, TP order yang menyelesaikan. Pemicunya
  profit ≥ 1R, BUKAN "mendekati TP" — dekat TP justru berhenti.
- **Amend fail-safe**: SL baru di-place dulu (`algoOrder.place`), baru cancel lama
  (`algoOrder.cancel` by `algoId`). Place gagal → SL lama utuh; cancel gagal → stop
  ketat trigger duluan + exchange auto-expire order `closePosition` saat posisi rata
  (terverifikasi read-only via `GET /fapi/v1/openAlgoOrders`: tidak ada order basi
  dari 5 posisi lama). Posisi tidak pernah telanjang.
- **`PositionCloseReason.TrailingStop = 5`**: SL terisi di sisi PROFIT entry
  diklasifikasi TrailingStop, bukan StopLoss — geometry learner tidak menghitung
  winner terlindungi sebagai SL-hit. Reduce-only close juga tidak lagi menyentuh
  leverage symbol (`SetLeverageAsync` hanya untuk entry).
- **Card "Trailing Stop"** menggantikan Order Updates: riwayat ratchet posisi AKTIF
  saja (`TrailingStopActivityStore` in-memory, endpoint
  `/api/account/trailing-stops`), di-clear worker saat posisi close. Audit permanen
  tetap di tabel Orders (reason "Trailing stop: …").
- **Settings**: `TargetMarginUsdt` kini configurable (default 3, clamp 1-1000, kolom
  DB auto-ALTER saat startup) — menentukan leverage bump akun kecil
  (`ceil(notional/target)`, cap 20×); margin ≠ ukuran posisi, PnL tidak berubah.
  Input **Default Leverage disembunyikan** dari UI (jalur auto selalu pakai leverage
  decision engine; nilai tetap dikirim saat save demi kompatibilitas).
- **Canary check pending setelah deploy**: saat ratchet pertama muncul di card,
  cek `openAlgoOrders` — harus tersisa SATU STOP_MARKET (validasi jalur
  `algoOrder.cancel` yang belum pernah jalan di produksi).

### Testing
- **157 unit test pass** via Docker .NET 9 (13 baru: TrailingStopPolicy aktivasi/
  trail/step/ratchet/near-TP/mirror short/fallback-R/degenerate, classifier
  TrailingStop long+short). Frontend `tsc -b` bersih.

---

## 15. FREEZE sistem — periode observasi 1 bulan (2026-07-07 s/d ±2026-08-07)

Keputusan: setelah deploy batch 13-14, **tidak ada tuning/fitur/improvement baru
selama ±1 bulan**. Sistem dibiarkan berjalan untuk mengumpulkan 30-50 trade dengan
konfigurasi stabil (threshold 65, RiskPerTrade 1%, TargetMargin 3). Pengecualian:
bug fix operasional, perbaikan keamanan, dan canary check `openAlgoOrders` saat
ratchet trailing pertama.

Baseline lama: 6 trade closed (3W/3L, ~+2.95 USDT). Catatan "ratusan sampel
fallback" sudah tidak berlaku sejak batch correlation-safe learning; loop berulang
tidak lagi dihitung sebagai evidence independen.

Evaluasi ±7 Agustus 2026, berbasis data yang sudah tercatat otomatis:
1. Win rate / profit factor / expectancy (sampel ≥30 trade)
2. Bucket kalibrasi confidence → keputusan threshold
3. Exit TrailingStop vs StopLoss vs TakeProfit → efektivitas trailing guard
4. Stabilitas inversi faktor (bolak-balik = noise)
5. Validation-performance Claude (confirmed vs hesitant) → keputusan soft-veto
   & kandidat upgrade model (claude-sonnet-5)
6. Profit factor > ~1.5 → momen deposit; RiskPerTrade jadi tuas PnL

Prinsip: perbaiki meteran → kumpulkan data dengan meteran stabil → setel dari
pembacaan. Jangan setel dial di tengah eksperimen.

---

## 16. Correlation-safe factor learning (2026-07-21, batch 7)

Audit production: 28 closed positions, net +5.4881 USDT, win rate 53.6%; hanya
21 posisi matched ke opening decision. Database mempunyai 8.128 AiDecisionLogs
(8.079 evaluated), tetapi hanya sekitar 265 bucket symbol+regime+arah+jam yang
independen. Counter lama membuat FactorStats melaporkan hingga ~1.200 sampel per
faktor dan memicu auto-inversion dengan keyakinan palsu.

- `FactorStats` sekarang di-rebuild idempotent dari `AiDecisionLogs` + `Positions`.
- Setiap matched Position History dipertahankan sebagai evidence utama dengan bobot
  `clamp(1+|ROI|, 1, 3)`.
- Unmatched 60-minute fallback tetap berguna, tetapi maksimal 1 per
  symbol+regime+arah+jam, bobot 0.25×, dan dead band 0.15% tetap berlaku.
- Recency diterapkan langsung per evidence (half-life 30 hari; lookback 120 hari).
- Rebuild otomatis pada tick `AiLearningWorker`; tidak menghapus Position History,
  AiDecisionLogs, ExecutionStats, atau calibration data.
- Gate confidence production tetap setting-driven (audit saat implementasi: 62).

Testing: 165/165 unit test pass di Docker .NET 9; production image build sukses.

---

## 17. Entry quality gate + bounded Claude influence (2026-07-22)

Audit 30 closed positions menunjukkan bucket confidence 60-61.9 hanya menang
5/14 dan net -4.020 USDT. Entry sebelumnya dapat dibuka dari satu snapshot 30 detik,
tanpa cooldown setelah close, sedangkan defensive multiplier Claude tertimpa mode
Margin × Leverage.

- **Konfirmasi temporal**: sinyal pada threshold normal harus muncul dua kali searah
  dengan jarak minimal 5 menit; candidate kedaluwarsa setelah 15 menit. Sinyal
  `threshold + 7` boleh langsung lanjut. Pergantian arah me-reset candidate.
- **Risk-before-LLM**: daily loss/consecutive loss pause diperiksa sebelum Claude.
  Rule scan tetap dicatat ke `AiDecisionLogs`, sehingga learning tidak kehilangan
  observasi meskipun call Claude ditunda.
- **Bounded hurdle**: Claude confirmed memakai threshold normal; Claude hesitant
  memerlukan `threshold + 2`. Claude tidak boleh membalik arah engine. API Claude
  gagal/tidak tersedia mempertahankan hasil rule engine yang sudah terkonfirmasi.
- **Defensive target sizing**: pada mode Margin × Leverage, multiplier Claude
  diterapkan setelah target quantity dan dicap maksimum 1.0×. Target 7×20 dengan
  multiplier 0.25 berarti request quantity 25% dari target, sebelum normalisasi
  minimum quantity/notional Binance dan exposure cap.
- **Cooldown setelah close**: TakeProfit/TrailingStop/Manual/AutoClose = 30 menit;
  StopLoss/Unknown = 60 menit. Jika row Position History terbaru gagal tersimpan,
  worker memakai fallback konservatif 60 menit agar tidak langsung re-entry.
- **Tidak menyentuh position management**: Position Check tetap rule-based tanpa
  token Claude, dan trailing stop tetap dievaluasi tiap tick saat posisi terbuka.

Testing: 182/182 unit test pass via Docker .NET 9; production image build sukses.

---

## 18. Batch responsiveness v1.3 — gate longgar + analisis level klasik (2026-07-29)

Latar: audit 43 closed positions (WR 48.8%, PF 1.18, kolaps 68%→33% setelah 17 Jul)
menemukan production berjalan di branch `v1` TANPA batch 17 — dan itu SENGAJA:
konfirmasi 5-menit + hesitant hurdle (+2) membuat sistem hampir tidak pernah open
posisi. Keputusan owner: sistem harus tetap aktif membuka posisi; kualitas entry
dinaikkan lewat analisis yang lebih kaya, bukan gate yang makin ketat.

### a) Entry gate dilonggarkan (`AutoEntryPolicy`)
- `ConfirmationDelay` 5 → **2 menit** (persistence check ringan, bukan penghalang).
- `StrongSignalOffset` 7 → **4** (sinyal cukup kuat langsung jalan).
- `HesitantSignalOffset` 2 → **0**: Claude ragu TIDAK menaikkan bar entry — dia
  tetap mengecilkan size via multiplier (bounded 0.1–1.0×). Data 34 decision: Claude
  hesitant di 32/34 → hurdle +2 de-facto memblok hampir semua entry.
- **Cooldown pasca-close TETAP 30/60 menit** — data mendukung keras: 9 re-entry
  cepat pasca-close (23–27 Jul) mayoritas loss (revenge chop).

### b) Analisis level klasik baru (kategori `structure`)
File baru: `FibonacciAnalysis.cs`, `ChartPatternDetector.cs`,
`SupportResistanceLevels.cs` (semua pure & unit-tested, TF primer 1h).
- **Fibonacci retracement**: impulse terakhir dari dealing range 60 candle (syarat
  range ≥ 3×ATR); pullback ke zona 0.382/0.5/golden pocket 0.618-0.65/0.786 = vote
  searah impulse (golden pocket terbesar +12); retrace ≥ 0.9 = impulse gagal (vote
  balik). Ekstensi 1.272/1.618 tampil sebagai konteks target di summary.
- **Chart patterns**: trendline fit 3 swing high + 3 swing low (fractal swing SMC);
  klasifikasi ascending/descending/symmetrical triangle & rectangle. Di dalam
  pattern: bias textbook (asc +6 / desc −6). **Breakout close melewati proyeksi
  boundary ± 0.1×ATR = vote ±12, +5 bila volume > 1.3× SMA20**. Measured-move
  target (tinggi pattern) tampil sebagai konteks TP.
- **Horizontal S/R**: pivot swing di-cluster (toleransi 0.35×ATR, min 2 sentuhan).
  Harga bertahan ≤ 0.5×ATR di atas support teruji = +8 (+4 rejection wick);
  menekan resistance = mirror negatif; close menembus level teruji ± 0.2×ATR = ±10.
- Bobot dalam `structure` (bobot kategori 17% tidak berubah, learning key stabil):
  SmartMoney 30%, PriceAction 25%, Pattern 20%, Fibonacci 12.5%, S/R 12.5%.
- **TP snap ke S/R** setelah snap volume-profile: TP yang melewati wall teruji
  ditarik ke depan wall (buffer 0.25×ATR) bila reward tersisa ≥ 60%; kalau tidak,
  jadi caution "target may be optimistic". Caution baru: entry menabrak wall < 1×ATR.
- **Elliott Wave sengaja TIDAK diimplementasi**: subjektif/tidak testable; bagian
  actionable-nya (struktur impulse/koreksi) sudah tercakup BOS/CHoCH + swing fractal.

### c) News lebih responsif
- RSS 2 → **5 feed** (CoinDesk, Cointelegraph, + Decrypt, Bitcoin Magazine,
  CryptoSlate) — fetch **paralel** (latency = feed terlambat, bukan penjumlahan;
  feed gagal = subset kosong, tidak pernah memblok yang lain). Scoring (event
  patterns, recency 12h half-life, speculation guard) tidak berubah.

### Testing
- **206/206 unit test pass** via Docker .NET 9 (24 baru: fib zones/direction/
  invalidation, pattern forming/breakout/trending-null/bias matrix, S/R clustering/
  bounce/TP-snap guardrail, wiring komponen engine, gate 2-menit & hesitant-pass).
- Full solution build 0 warning 0 error.

### Deploy & evaluasi
- Branch **v1.3**. Deploy Jenkins `DEPLOY_BRANCH=v1.3` (user yang deploy).
- Ukur lewat meteran yang sudah ada: `/api/ai/performance` (kategori structure),
  `/api/ai/confidence-calibration`, ExecutionStats TP-hit-rate, dan bandingkan WR
  pra/pasca deploy. Threshold tetap setting-driven (server: 60).

---

## 19. Trading Style: Intraday vs Scalper (2026-07-29, batch 2)

Setting baru **Metode Trading** di dashboard (Settings → dropdown, 2 opsi), kolom
`TradingSettings.TradingStyle` (0 = Intraday default, 1 = Scalper; ALTER idempotent).
Dibaca per tick — ganti mode berlaku di scan berikutnya tanpa restart.

| | Intraday (default, perilaku lama) | Scalper |
|---|---|---|
| TF anchor (ATR/regime/level) | 1h | **15m** |
| TF SMC | 15m | **5m** |
| Bobot vote MTF | 5m 10 / 15m 20 / 1h 30 / 4h 25 / 1d 15 | **5m 30 / 15m 35 / 1h 25 / 4h 10** |
| SL/TP baseline | 2× / 4× ATR(1h) + learned tuning | **1.5× / 3× ATR(15m), tuning learned di-bypass** |
| Konfirmasi entry | 2 menit | **30 detik** (1 tick ekstra) |
| Cooldown TP/SL | 30 / 60 menit | **10 / 20 menit** |
| Target realistis | 1–2% | ~0.4–0.8% (RR tetap 2.0) |

File: `TradingStyleProfile.cs` (baru), `AutoEntryTiming` di `AutoEntryPolicy.cs`,
`AdvancedDecisionEngine.Evaluate(..., styleProfile)`, `AiDecisionService` (regime
dideteksi di TF anchor style), `AutoTradingWorker` (timing per tick).

Keputusan desain penting:
- **Scalper TIDAK memakai learned execution tuning** — multiplier itu dipelajari dari
  exit geometri 1h; menskalakan stop 15m dengannya salah kaprah. Leverage factor juga
  netral (1.0) di scalper.
- **TP floor de-facto ~3× fee**: SL 1.5×/TP 3× ATR(15m) dipilih supaya target minimal
  ~0.4% vs fee taker round-trip ~0.1%. Scalp lebih tipis dari itu kalah secara
  matematis sebelum mulai.
- **Cooldown tetap ada di scalper** (10/20 mnt) — re-entry instan pasca stop adalah
  pola terburuk di Position History, berlaku untuk kedua style.
- Trailing guard tidak perlu diubah: ratchet berbasis R otomatis mengetat karena R
  scalper lebih kecil.
- Semua analisis batch 18 (fib/pattern/S&R/volume profile) otomatis ikut pindah ke
  TF 15m di mode scalper karena mereka membaca TF anchor.
- ⚠️ Learning (FactorStats/ExecutionStats) TIDAK dipisah per style — bucket regime
  akan tercampur bila style sering diganti-ganti. Rekomendasi: pilih satu style dan
  konsisten selama pengumpulan data; pemisahan per-style jadi kandidat batch
  berikutnya bila scalper dipakai serius.

Testing: **213/213 unit test pass** via Docker .NET 9 (7 baru: scalper timing
30 detik/cooldown/resolusi style, engine anchor 15m, bypass learned tuning, default
intraday). Full solution build + frontend `tsc` bersih.

---

## 20. Hardening worker + Account Risk Guard + fix sizing (2026-07-30)

### a) FALSE ALARM yang perlu dicatat: "worker beku 24 jam" TIDAK PERNAH TERJADI
Saat audit, sempat disimpulkan kedua worker beku 24 jam karena decision terakhir
bertanggal "30 Jul 17:53" sementara tanggal lokal terbaca 31 Juli. **Salah baca
timezone**: server berjalan UTC, workstation WIB (UTC+7) — 17:53 UTC itu 16 menit
sebelumnya, bukan kemarin. Verifikasi: `date -u` di server + hitung decision per
jam di DB.

Yang tampak seperti "gap 23 jam" di `AiDecisionLogs` (29 Jul 12:00 → 30 Jul 11:00)
juga **perilaku normal by design**: posisi SHORT terbuka sejak 28 Jul 00:01 dan
worker mem-PAUSE analisa entry selama posisi terbuka (Bagian 2) — yang tercatat di
periode itu hanya `Open-position revalidation`, bukan `AI decision`. Begitu posisi
tertutup ~30 Jul 11:00, decision langsung mengalir lagi (~34 baris/jam).

> **Pelajaran operasional:** selalu bandingkan `date -u` server sebelum menyimpulkan
> outage dari timestamp log, dan ingat bahwa sepinya `AiDecisionLogs` = posisi
> terbuka, bukan sistem mati.

### b) Hardening worker (preventif, BUKAN perbaikan insiden)
Tidak ada insiden yang memicu ini, tetapi dua kelemahan nyata tetap ditutup:
1. **Console sink asinkron** — `WriteTo.Async(..., blockWhenFull: false)` (paket
   `Serilog.Sinks.Async`). Sink console sinkron dapat memblok thread penulis saat
   pembaca stdout melambat; buffer bounded yang men-DROP baris saat penuh membuat
   backpressure logging tidak pernah bisa menyentuh loop trading. Plus filter
   `System.Net.Http.HttpClient → Warning` — sebelumnya ~20 baris HTTP per tick
   membanjiri log dan menyulitkan audit.
2. **Watchdog per tick** di kedua worker: CTS linked + `CancelAfter` (trading 2
   menit vs cadence 30 detik, learning 4 menit vs cadence 5 menit). Await yang
   menggantung (socket tanpa timeout, provider mandek) dibatalkan, tick berikutnya
   lanjut, dan penyebabnya muncul sebagai error log — bukan diam.

### b) Account Risk Guard — checkbox on/off (permintaan owner)
Setting baru `AccountRiskGuardEnabled` (default TRUE, kolom idempotent, checkbox
di Risk Configuration). **TRUE** = perilaku lama: daily-loss pause, consecutive-
loss pause (3x), dan exposure clamp aktif. **FALSE** = semua rem akun dilepas —
status gate `guard-off`, statistik tetap dihitung & tampil (dashboard menunjukkan
apa yang SEHARUSNYA diblok), executor risk block & exposure clamp dilewati.
Fail-safe equity-unavailable TETAP memblok (itu masalah data, bukan risk appetite).
⚠️ Didokumentasikan di UI: nonaktif = satu hari buruk bisa menghabiskan saldo.

### c) Fix sizing Margin × Leverage (bug "set 6 kebukanya 3")
Bukti log production 30 Jul: `qty 0.007084 -> 0.000370 ... Claude defensive cap 20%`.
Dua penyebab bertumpuk:
1. **Claude defensive cap (0.20-0.25x hampir konstan) memotong target notional**
   120 USDT → ~24-30 USDT → di bawah minimum bursa → validator menaikkan balik ke
   0.001 BTC (margin ~3.2). Target owner diganti diam-diam oleh lantai bursa.
   → Multiplier Claude TIDAK lagi diterapkan di mode Margin × Leverage (owner
   sudah menetapkan notional; Claude tetap tak bisa veto; exposure cap tetap
   jadi safety net saat guard aktif).
2. **Step rounding FLOOR**: qty 0.00185 (margin 6) di-floor validator ke 0.001
   (margin 3.2). → `AutoPositionSizingPolicy` kini snap ke step TERDEKAT
   (0.001 BTC, konst worker): 0.00185 → 0.002 → margin realisasi ~6.5 ≈ target.
Catatan akun kecil: dengan equity ~$8 dan Max Exposure 50%, cap margin $4 <
target 6 — kalau guard aktif, exposure clamp masih memotong. Naikkan Max
Exposure, matikan guard, atau top-up.

### Testing
- **216/216 unit test pass** (3 baru sizing: nearest-step 2 arah + ignore Claude
  multiplier; 2 baru risk gate: guard-off melewati daily-loss & consecutive-loss).
- Full solution build + frontend `tsc` bersih.
