from mcp.server.fastmcp import FastMCP
from common import catalog, amount
from trading import PaperBook, wallet_positions, quote, receipt, performance, inspect_asset, save_proposal

mcp = FastMCP('bosinai-1inch-paper')

@mcp.tool()
def list_assets(query: str = '', limit: int = 20) -> dict:
    """Find allowed tokenized assets from stocks.xlsx. Addresses are not issuer-verified."""
    assets = [a for a in catalog().values() if query.upper() in (a['symbol']+' '+a['name']).upper()]
    return {'total':len(assets),'assets':assets[:max(1,min(limit,30))]}

@mcp.tool()
def get_positions(symbols: list[str] = [], live_wallet: bool = False) -> dict:
    """Read paper balances, or configured public wallet balances including USDT and BNB."""
    return wallet_positions(symbols[:20]) if live_wallet else PaperBook().positions()

@mcp.tool()
def get_swap_quote(symbol: str, side: str, quantity: str) -> dict:
    """1inch quote. Buy quantity is USDT spend; sell quantity is token units. Never executes."""
    return quote(symbol.upper(),side,quantity)

@mcp.tool()
def paper_order(symbol: str, side: str, quantity: str, request_id: str) -> dict:
    """Simulate a fractional buy/sell using a fresh 1inch quote. No real money moves."""
    book = PaperBook()
    previous = book.order(request_id)
    if previous.get('status')=='paper_filled':
        old = previous['quote']
        if (old['symbol'],old['side'],amount(old['input'])) != (symbol.upper(),side,amount(quantity)):
            raise ValueError('Idempotency key conflict')
        return previous
    return book.fill(quote(symbol.upper(),side,quantity),request_id)

@mcp.tool()
def get_order(order_id: str) -> dict:
    """Check a paper order by request ID."""
    return PaperBook().order(order_id)

@mcp.tool()
def get_transaction(tx_hash: str) -> dict:
    """Read the receipt and confirmation count of a BNB Chain transaction."""
    return receipt(tx_hash)

@mcp.tool()
def inspect_token(symbol: str) -> dict:
    """Check the allowlisted BNB Chain contract's symbol and decimal precision. Does not verify issuer or liquidity."""
    return inspect_asset(symbol.upper())

@mcp.tool()
def paper_performance() -> dict:
    """Estimate paper P&L from cash and liquidation quotes. Missing valuations produce unknown P&L."""
    return performance()

@mcp.tool()
def get_trade_history(symbol: str = '', limit: int = 50, offset: int = 0) -> dict:
    """Read durable paper buy/sell history, fee-inclusive execution prices, quantities and realized P&L. Newest first."""
    return PaperBook().history(symbol,limit,offset)

@mcp.tool()
def get_profit_loss() -> dict:
    """Current paper realized/unrealized P&L from persisted purchase costs and fresh liquidation quotes. Unknown marks remain unknown. Not live-wallet cost basis."""
    return PaperBook().profit_loss()

@mcp.tool()
def get_allocation_limits() -> dict:
    """20% cumulative per-stock allocation limit using cash plus remaining purchase costs. Repeated buys cannot bypass the limit."""
    return PaperBook().allocation()

@mcp.tool()
def propose_for_human_review(symbol: str, side: str, quantity: str) -> dict:
    """Save an indicative 1inch quote for human wallet review. Does not execute, approve, sign, or broadcast any transaction."""
    return save_proposal(symbol.upper(),side,quantity)

if __name__=='__main__':
    mcp.run(transport='stdio')
