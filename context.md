# Context — Penggunaan & Biaya API (Anthropic + LunarCrush)

> Update terakhir: scoring engine 10-kategori (institusional), confidence
> BUY/SELL/HOLD simetris, gate berbasis conviction, + sumber data gratis baru
> (macro Yahoo Finance, on-chain mempool.space). Lihat Bagian 6.
> Sebelumnya: model dropdown, confidence configurable, persistence settings ke DB,
> usage tracking, LunarCrush, decision caching.

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
- Claude balas JSON: `{confirmed, adjusted_confidence, narrative, risks}`.
- Hasil dipakai: CONFIRM/VETO (veto → `shouldTrade=false`), blend confidence
  (rata-rata rule-based + Claude), narasi + risiko tampil di panel AI Decision.
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
6. **Gate buka order** (`shouldTrade` true hanya bila SEMUA lolos): conviction ≥ Min Confidence,
   R:R ≥ 2.0, trend higher-timeframe selaras, funding tidak ekstrem, spread tidak lebar,
   (bila lolos) Claude CONFIRM, Auto mode ON, belum ada posisi. Confidence ≠ pemicu tunggal.

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
  bisa diturunkan via dashboard atau ubah default.
- Frontend: `AiDecision` type + panel sudah menampilkan baris BUY/HOLD/SELL confidence.
