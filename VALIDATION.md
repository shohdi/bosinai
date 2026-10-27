# Validation on 2026-09-20 (Asia/Riyadh)

## Passed

- Requested model: SHA-256 `53107f530aa52eb00912263ab1ee29bd199261c87cd7b4ad4ca1318c1fe33ee3`, 5,946,648,928 bytes; matches Hugging Face LFS metadata.
- Both CUDA 12.4 release archives matched publisher SHA-256 digests recorded in `config/artifacts.json`.
- Model server loaded and answered health checks on the RTX 4060 Laptop GPU (8,188 MiB total), using an 8,192-token context and one slot. Observed total GPU use about 7,050 MiB under inference; observed decode about 26–27 tokens/second. These are short local measurements, not a sustained benchmark.
- Unauthenticated `/v1/models` requests were rejected with HTTP 401 after authentication was enabled.
- 22 pytest cases passed. Covered fractional buy/sell accounting, destination token precision, invalid amounts, insufficient balances, quote expiry, order caps, idempotency conflicts, concurrent retries, weekly pause, unknown P&L with missing marks, missing credentials, blocked browser destinations/private DNS, bounded tool output and complete-exchange context trimming.
- Python source compilation and `pip check` passed.
- Both MCP servers initialized, listed their tools, and returned real tool results through stdio MCP sessions. The final trading server exposes nine tools; research exposes three.
- First model/MCP cycle: paper balances → workbook QCOM lookup → live Google News RSS → sourced summary.
- Second authenticated model/MCP cycle: paper balances/performance → on-chain QCOM inspection → missing-key quote attempt → correct abstention and configuration explanation.
- The initial unrestricted research cycle exceeded the model context limit and stopped. The loop was corrected to render/token-count prompts before each inference, trim older complete tool exchanges, limit tool-result size, preserve an audit transcript, and require a final response at the turn budget.
- A subsequent cycle completed but hallucinated balances and headlines after older evidence was trimmed. The ledger itself remained unchanged at 1,000 USDT. The loop now refreshes authoritative paper balances every turn, retains selected original news evidence across trimming, and writes separate authoritative ledger/evidence fields alongside unverified model analysis. This finding is a reason not to treat model prose as accounting truth.
- After that correction, the default background AMD research cycle completed successfully, reported the correct 1,000 USDT balance and no held tokens, and abstained because the quote key was missing. It scheduled the next cycle with `configured interval 300s`, confirming the requested five-minute cadence was loaded. This validates one corrected cycle, not general factual reliability.
- BNB Chain RPC reported chain ID 56. Workbook QCOM address contains a deployed contract returning `QCOMon`, 18 decimals.
- Read-only Playwright navigation to CNBC Markets returned HTTP 200 and page text.

## Unavailable or unverified

- No public wallet address, 1inch key or Brave Search key was supplied. No private key is stored or needed for this implementation.
- Authenticated 1inch quotes and real route availability have not been tested. The paper ledger is tested with deterministic quote fixtures; no production paper fill is claimed.
- Browser-based social search hit Google's unusual-traffic/CAPTCHA page. API-backed Brave search is implemented but not tested with a real key. Full X coverage is not guaranteed.
- QCOM and MARA contract inspections succeeded; issuer identity, transfer eligibility, redemption terms and liquidity across the 200-asset catalog remain unverified.
- No real-money order, allowance, signature or transaction was submitted. Live execution uses a manual handoff to the human's wallet; the application has no signer.
- No week-long soak test, profitable strategy result, live fill comparison or restart-after-Windows-reboot test has occurred. Laptop sleep/logoff can interrupt operation.

The model and research/MCP path are operational. Quote-based paper trading needs local API configuration; unattended real-money investment execution is deliberately absent.

## Persistent accounting and 20% allocation update

- `scripts/test-environment.py --online` passed: 29 pytest cases, isolated MCP fixture buys/sells, retry protection, cumulative 20% rejection, P&L, and persistence across a fresh MCP process. Online news MCP and the real model's structured tool calling also passed.
- Added `get_trade_history`, `get_profit_loss`, and `get_allocation_limits` (12 trading tools total).
- Restart tests cover weighted-average cost on partial sales, realized/unrealized P&L, legacy record compatibility and initial capital surviving configuration changes.
- Allocation tests cover five different 200-USDT positions on 1,000 USDT capital, repeated buys, and simultaneous requests that would exceed the cap. The tests raise the separate order cap only inside the isolated test process; production retains its 25-USDT order cap.
- All fixture execution prices are deterministic test data. They do not validate authenticated 1inch quotes or real execution prices. Live-wallet purchase history remains separate and unknown because this implementation does not execute live trades.

## Interactive chat tester

- 32 automated tests passed, including disk-backed session resume, fresh-session isolation, exclusive session locking, conversation history forwarding, MCP tool-result persistence and context-overflow refusal without losing old history.
- Live model test: saved `Violet Otter` as a project codename and recalled it correctly in a follow-up message.
- A new tester process resumed that saved session, called `browse_page` for CNBC Markets, returned a headline from the retrieved page, and correctly recalled the same codename.
- Interactive `/new`, `/history`, `/resume`, `/tools` and `/quit` command routing was exercised without submitting trades. Chat attaches only research/browser MCP, now exposing four tools including general web search.
- Internet browsing still follows the configured domain allowlist. Brave-backed search requires a configured key; Google fallback can be blocked. Conversation memory is bounded by the loaded model context; the tester refuses oversized prompts instead of silently discarding prior messages.


## 32,768-token context consistency fix

- Shared input budget: 30,456 tokens, with the existing 1,800-token reply limit and 512 tokens of safety margin.
- Agent and chat tool excerpts use the same 24,576-character cap, including JSON escaping and metadata. Browser page text uses that cap before metadata.
- 37 automated tests passed, including exact-budget acceptance, overflow handling, complete-exchange trimming, and escaped/Unicode tool excerpts.
- Restarted the stale 50,000-token server using the restored launcher settings; authenticated /props reported 32,768 tokens. GPU layers remain 99, with original cache defaults and sampling settings. No new reasoning-quality benchmark was performed.
- Earlier 8,192-token measurements above are historical, not measurements of this configuration.
