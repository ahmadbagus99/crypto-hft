# Context — Penggunaan & Biaya Anthropic API

## 1. Anthropic API key dipakai untuk apa saja?

Hanya **2 tempat** di sistem:

### a) Validasi keputusan trading (fungsi utama)
File: `backend/src/CryptoHft.Infrastructure/Ai/ClaudeDecisionValidator.cs`

- Lapisan "Hybrid" — Claude jadi **second opinion** atas sinyal rule-based.
- Dipanggil hanya jika sinyal **kuat & actionable**: `confidence >= 80` DAN `action != NoTrade`
  (lihat `AiDecisionService` + `Ai__LlmConfidenceThreshold`, default 80).
- Dikirim ke Claude: skor 12+ faktor, market regime, entry/SL/TP, R:R, funding rate,
  open interest, long/short ratio, orderbook imbalance, Fear & Greed, headline berita.
- Claude balas JSON: `{confirmed, adjusted_confidence, narrative, risks}`.
- Hasil dipakai: CONFIRM/VETO (veto → `shouldTrade=false`), blend confidence
  (rata-rata rule-based + Claude), narasi + risiko tampil di panel AI Decision.

### b) Test Connection
File: `backend/src/CryptoHft.Infrastructure/Ai/ConnectionTester.cs` (`TestAnthropicAsync`)
- Kirim 1 request kecil (`MaxTokens=8`, "ping") buat verifikasi key valid. Cost ~nol.

### Yang TIDAK pakai Anthropic key
- Adaptive learning / Bayesian (`AdaptiveWeightService`) → murni matematika lokal.
- SMC (Order Block, FVG, liquidity sweep) → deteksi pola lokal.
- Data harga, funding, OI, berita, Fear & Greed → Binance public API + RSS gratis.
- **Tanpa key → engine tetap jalan full rule-based (passthrough, Llm.Used=false), $0.**

---

## 2. Estimasi biaya Anthropic per hari

### Parameter (dari kode)
| Faktor | Nilai |
|---|---|
| Loop auto-trade | tiap 30 detik → 2.880 cycle/hari (AutoTradingWorker) |
| Gate panggil Claude | confidence >= 80 DAN action != NoTrade |
| Model | `claude-opus-4-8` → input $5/1M, output $25/1M |
| Token per call | input ~600, output max 1024 (realistis ~400) |

### Biaya per 1 panggilan
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
1. Naikkan `Ai__LlmConfidenceThreshold` 80 → 88 (pengontrol paling efektif).
2. Ganti `Ai__Model: claude-haiku-4-5` (5x lebih murah → ~$0.5–1/hari).
3. Perlambat loop AutoTradingWorker 30s → 60s (potong call setengah).
4. Kosongkan Anthropic key → full rule-based, $0.

**Rekomendasi awal:** Haiku + threshold 85 (~$1/hari) sambil validasi apakah lapisan
LLM benar-benar memperbaiki win-rate via `/api/ai/performance`. Kalau worth, naik ke Opus.
