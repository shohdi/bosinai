# Bonsai local stock research and paper trading

This project runs the requested **Ternary-Bonsai-2-27B-PTQ1_0.gguf** on the local RTX 4060, with two real stdio MCP servers and a five-minute agent loop. It supports autonomous research and **simulated trading only**. It does **not** autonomously invest real funds, store a wallet private key, or sign/broadcast transactions. Live proposals require manual review and execution in your own wallet through 1inch.

## Installed here

- Project-local Python 3.12 `.venv`; existing Conda environments unchanged.
- Prism ML CUDA 12.4 runtime, release `prism-b10709-9a9394a`; original release archives and model hashes verified. See `config/artifacts.json`.
- Requested 5,946,648,928-byte model in `models/`.
- Playwright Chromium in its normal per-user browser cache.
- Local model API on `127.0.0.1:8080`, one inference slot, 32,768-token context, full GPU offload. Authentication key is generated in ignored `data/model-api.key` and is not put in model messages. Do not expose the port publicly.
- 200 nonempty assets imported from `stocks.xlsx` on each lookup. Workbook addresses are an allowlist, **not issuer verification**.

## Run and stop

From this folder in PowerShell:

```powershell
# Start model and five-minute paper/research agent in the background
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-all.ps1

# Configuration status (prints only credential presence, never values)
.\.venv\Scripts\python.exe scripts/status.py

# One interactive cycle instead of background operation
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-agent.ps1 -Once

# Stop future tool work and stop the owned model process
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/stop.ps1
```

`stop.ps1` creates `data/STOP`; an in-flight tool may finish before the agent exits. Remove that file deliberately to resume. The file lock prevents two agent loops from running together. The agent retries model startup when unavailable at the start of a cycle. This is a foreground/user-session installation, not a Windows service; sleep, shutdown, logout and connectivity affect availability. No machine startup task is installed.

Logs: `logs/model.stderr.log`, `logs/agent.stdout.log`, `logs/agent.stderr.log`. Completed cycle transcripts: `data/cycle-*.json`. Paper balances and orders: `data/paper.sqlite`. The experiment stops after seven days for review, regardless of profitability; no automatic reset or risk escalation.

Cycles target a **300-second start-to-start interval** without overlap. If a cycle exceeds five minutes, the next starts after it completes. `data/latest-report.json` separates the authoritative ledger and source evidence from the model's unverified analysis. The ledger is refreshed in each prompt and selected source evidence survives context trimming. Language-model prose can still contain errors; the ledger and actual tool results are authoritative.

## Configure credentials locally

```powershell
.\.venv\Scripts\python.exe scripts/configure.py
```

Enter a **public** BNB Chain wallet address, a 1inch API key, and optionally a Brave Search API key. Secrets are entered with hidden input and stored in **Windows Credential Manager** for the current Windows account. Do not paste secrets into chat or commit them. No private key is requested: the human wallet retains signing authority.

The trading server reads the 1inch key and public address directly. The research server reads the optional search key. The agent receives only tool schemas/results. No shell, filesystem, arbitrary JavaScript, credential-management, or wallet-signing tool is exposed to the model. This is an application boundary, not a separate Windows security principal: other trusted programs under your Windows account can access your credential store.

Without the 1inch key the system can research, read configured public wallet balances and inspect contracts, but cannot obtain authenticated quotes or fill quote-based paper orders. It reports that limitation instead of inventing prices. Quotes/route availability for the full workbook have **not** been verified.

## Tools

`src/trading_mcp.py` provides asset lookup, on-chain token inspection, paper or live wallet balances including USDT/BNB, 1inch quotes, simulated fractional orders, order lookup, paper performance, human-review proposals, and read-only transaction receipts.

- Buy `quantity` = USDT to spend; sell `quantity` = token units.
- Default maximum order: 25 USDT. Default paper cash: 1,000 USDT. Estimated simulated fee: 30 basis points; gas is excluded. Configure in `config/settings.json` before starting a new experiment. Changing initial cash does not reset an existing ledger.
- Decimal arithmetic, token precision checks, positive finite amounts, 60-second quote expiry, transactional balance updates, insufficient-balance checks and idempotency keys protect the paper ledger.
- Paper P&L uses indicative liquidation quotes and modeled fees. Missing valuations produce unknown P&L. There is no claim that paper execution matches real execution, slippage, gas or issuer restrictions.
- Every paper fill is stored transactionally in `data/paper.sqlite` and reloaded after restart. `get_trade_history(symbol, limit, offset)` exposes fee-inclusive execution prices, quantities, cash amounts, timestamps and realized P&L. Old fill records are supported without resetting the ledger. Prices are simulated fill prices, not confirmed blockchain executions.
- `get_profit_loss()` reports realized P&L from recorded fills and unrealized P&L from current liquidation quotes. Partial sells release weighted-average purchase cost. Realized P&L stays available even when market quotes are unavailable; unrealized and total P&L then remain unknown. Live wallet balances are kept separate: without recorded actual fills, their purchase prices and P&L are unknown.
- `get_allocation_limits()` exposes the hard **20% cumulative allocation per stock**. Capital means USDT cash plus remaining historical purchase costs. A 1,000-USDT portfolio allows at most 200 USDT of unsold purchases per stock, including repeat buys. Fully allocating it requires at least five distinct stocks. Realized gains/losses change capital; unrealized gains do not increase buying capacity. The separate 25-USDT per-order cap remains. Sells release the sold portion of cost basis. Existing concentration is not forcibly liquidated, but additional buys above the limit are rejected. Duplicate contract aliases in the workbook are rejected.
- A receipt confirms network transaction status only; it does not prove that the transaction matches a proposed order. An unknown hash is reported as pending-or-unknown.

`src/research_mcp.py` provides dated Google News RSS headlines, allowlisted read-only browser pages, and publicly indexed X/Reddit/Stocktwits searches. Brave Search is used when configured; otherwise browser search is attempted. Google returned a CAPTCHA in this installation, so social search requires the API option for dependable access. Even with a search key, complete or real-time X coverage is not promised. Login/paywall/CAPTCHA restrictions are reported, not bypassed. Returned pages and social claims are untrusted evidence.

The optional MCP client template is `config/mcp.example.json`. Its relative paths assume the client starts from the repository root; copy it to the ignored `config/mcp.json` and adjust paths for your machine when using another local MCP client. The bundled agent connects to these two servers directly over stdio. It measures rendered prompt tokens and trims older complete tool exchanges before exceeding the model context; tool results supplied to the model are retained in cycle logs even after those exchanges leave its context.

The browser is fresh for each request, rejects local/private destinations, blocks downloads, service workers, POST requests and page scripts, and has no wallet session. Its allowlist is in `config/settings.json`. Add a publisher explicitly when needed; many news links point to publishers outside the default list.

## Human-reviewed live workflow

The model can save an indicative proposal with `propose_for_human_review`. List/review locally:

```powershell
.\.venv\Scripts\python.exe scripts/review.py
.\.venv\Scripts\python.exe scripts/review.py PROPOSAL_ID --open-wallet-site
```

The command displays token addresses, direction and quoted amounts, and opens the official 1inch swap page. **It does not submit a prefilled trade or sign anything.** You select BNB Chain and both exact token addresses, verify asset issuer/eligibility, refresh the quote, review amounts/minimum received/fees, and confirm in your own wallet. The model's browser cannot access this wallet flow. Afterwards a transaction hash can be checked with `get_transaction`.

This manual wallet handoff replaces the requested unattended private-key trading server. No live swap has been tested or executed.

## Reproduce and verify

```powershell
# If rebuilding the project environment, use an available Python 3.12
C:\ProgramData\anaconda3\python.exe -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.lock.txt
.\.venv\Scripts\python.exe -m playwright install chromium
.\.venv\Scripts\python.exe scripts/bootstrap.py
.\.venv\Scripts\python.exe -m pytest -q
.\.venv\Scripts\python.exe scripts/smoke.py
```

Bootstrap downloads only pinned artifacts, verifies SHA-256, and never edits other environments. Model installation uses the custom Prism runtime because this PTQ1_0 model requires its kernels. PowerShell execution policy is bypassed per process only; machine policy is unchanged.

See `VALIDATION.md` for measured results and unverified dependencies. A successful short test is not a week-long stability or profitability claim.

## Full test environment and code locations

Run from `C:\myfiles\projects\bosinai` in PowerShell:

```powershell
# Offline accounting tests + real MCP buy/sell calls with deterministic test prices.
# Uses a temporary SQLite ledger, then restarts the MCP process to check reload.
.\.venv\Scripts\python.exe scripts/test-environment.py

# Full checks including the real local model and live news MCP (requires internet).
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-model.ps1
.\.venv\Scripts\python.exe scripts/test-environment.py --online

# Run the actual paper/research agent every five minutes.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-all.ps1
.\.venv\Scripts\python.exe scripts/status.py
```

The test fixture's prices are explicitly fake and cannot be enabled in the production server. No API keys are needed for these tests; authenticated 1inch quote checks still require local configuration via `scripts/configure.py`. Fixture trades never touch `data/paper.sqlite`. Online model checks may queue behind a running agent cycle.

- MCP server/tool definitions: `src/trading_mcp.py`
- Accounting, persistent SQLite history, allocation enforcement and chain/1inch adapter: `src/trading.py`
- Agent prompt: `config/system-prompt.txt`
- Stored running ledger: `data/paper.sqlite` (ignored by Git)
- Tests: `tests/test_accounting.py`, `tests/test_trading.py`, `scripts/test-environment.py`

## Local web chat

`chat_tester.py` now starts a ChatGPT-style website by default. It uses the existing local model and browser/research tools, and reads the same saved chats as the terminal tester. The website has a responsive conversation sidebar, new chats, automatic titles, rename/delete controls, and a stop button. Both your messages and model replies render Markdown, including fenced code blocks with syntax highlighting and copy buttons, lists, headings, links, and tables.

Run this from `C:\myfiles\projects\bosinai` in **PowerShell**:

```powershell
# Starts/reuses the model, runs the web server, and opens your browser.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-chat.ps1
```

Open **http://127.0.0.1:8000**. Keep the server terminal open; press **Ctrl+C** there to stop the website. If this app is already running, the launcher opens the existing website. The existing model process continues running. The website may show that the model is loading until its startup finishes.

To stop the website when it is running in the background:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/stop-chat.ps1
```

This checks that the port belongs to this project's chat server, then stops only that server. Completed messages remain saved.

To start the pieces individually using the existing virtual environment:

```powershell
cd C:\myfiles\projects\bosinai
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-model.ps1
.\.venv\Scripts\python.exe chat_tester.py --open

# Use another port if 8000 is occupied.
.\.venv\Scripts\python.exe chat_tester.py --port 8001 --open
```

In **Command Prompt**, activation is also supported:

```bat
cd /d C:\myfiles\projects\bosinai
call .venv\Scripts\activate.bat
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-model.ps1
python chat_tester.py --open
```

History lives in `data/chat-sessions/*.json` and survives browser refreshes and server restarts. Your existing tester conversations appear automatically. The sidebar displays conversation titles; internal file names remain stable when you rename a chat. New chats have independent model context. Delete removes that conversation's saved JSON after confirmation.

Only completed turns are saved. A failed or stopped reply preserves earlier messages and puts your unsent message back in the composer. While generating, the interface shows the model/tool progress; the complete Markdown reply appears when ready. You can switch conversations during generation and return to see the result. Requests queue because the local model has one inference slot and is shared with the research agent. If a conversation fills the model context, choose **New chat**; no old turns are silently discarded.

The web server binds to `127.0.0.1` by default, keeps the model key on the server, and renders sanitized Markdown. Frontend libraries are bundled locally, so the interface has no CDN dependency. Internet access is still used when the model calls browser tools. Use a single server process: its generation queue and stop controls are kept in memory.

All dependencies are already in the project `.venv`. For a fresh environment:

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements.lock.txt
```

Code: `src/chat_web.py` provides the web API and generation queue; `web/` contains the interface. Pinned frontend assets and licenses are in `web/vendor/`; `scripts/vendor-chat-assets.py` downloads and verifies them again when needed.

## Terminal chat tester

`chat_tester.py` talks to the loaded local model and attaches only the browser/research MCP. It has a general conversation prompt for understanding tests, not the trading-agent prompt or trading tools.

From this project folder:

```powershell
# Start the model if needed (reuses the already running process).
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-model.ps1

# Create or resume a named chat, with automatic history saving.
.\.venv\Scripts\python.exe chat_tester.py --session understanding-test

# Terminal mode without --session creates a new empty session.
.\.venv\Scripts\python.exe chat_tester.py --cli
```

Inside the chat:

```text
/new                          Start a fresh session; keep the previous one on disk
/new reasoning-test           Start a fresh named session
/resume understanding-test    Reload that session and its full conversation history
/sessions                     List saved session names
/history                      Display this session's user/assistant messages
/tools                        List browser/news/search tools
/help                         Show commands
/quit                         Exit; completed turns are already saved
```

Example understanding test: say `My project codename is Violet Otter. Remember it.` Then ask `What is my project codename?` Quit, resume the same session, and ask again to test history across restarts. Example browser test: `Read https://www.cnbc.com/markets/ with the browser and summarize one current headline with its source URL.`

Session JSON files are saved in `data/chat-sessions/`, including user/assistant messages and browser tool calls/results. Completed turns are atomically saved; failed or interrupted turns do not damage earlier history. Two testers cannot write the same session simultaneously. New sessions do not share chat history.

All saved messages are passed to the model on each request. Both clients use a 30,456-token input budget within the model's 32,768-token context, reserving 1,800 tokens for replies and a 512-token safety margin. These limits and the 24,576-character tool-result cap are shared through `config/settings.json`; the model launcher reads the same context setting. Browser page text is capped at that character limit before metadata, and clients bound the complete tool result including metadata. When full, the request is refused with a `/new` instruction; no old turns are silently discarded. Large browser outputs are marked excerpts. Responses that hit the output limit are marked; ask `continue` to continue them.

Internet tools: `browse_page`, `search_web`, `stock_news`, `search_stock_posts`. The existing browser domain allowlist and public HTTPS restrictions still apply (`config/settings.json`). General search uses Brave when configured, otherwise attempts Google browser search; CAPTCHA/login restrictions may prevent access. Browser page access does not require a search key. Chat browser activity is read-only, has no wallet access, and cannot trade.

The loaded model is shared with the five-minute agent, so chat requests may wait for its current inference. The tester does not stop or change that agent. MCP diagnostic logs go to `logs/chat-mcp.log`.

For scripted tests, repeat `--message`:

```powershell
.\.venv\Scripts\python.exe chat_tester.py --session memory-test --message "Remember the word violet." --message "What word did I give you?"
.\.venv\Scripts\python.exe chat_tester.py --list
```

Code: `chat_tester.py` launches the website or optional CLI; `src/chat_session.py` implements sessions and the model/MCP loop; `src/research_mcp.py` implements browser access. The `--session`, `--message`, and `--list` options retain their original terminal behavior.

## Primary references

- [Requested model](https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf)
- [Prism runtime/tool-calling guide](https://github.com/PrismML-Eng/Bonsai-demo/blob/main/TOOLS.md)
- [Pinned runtime release](https://github.com/PrismML-Eng/llama.cpp/releases/tag/prism-b10709-9a9394a)
- [1inch Classic Swap](https://business.1inch.com/portal/documentation/apis/swap/classic-swap/introduction)
- [1inch wallet connection and manual approval](https://help.1inch.com/en/articles/4585122-how-to-connect-or-disconnect-your-wallet)
- [MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk)


