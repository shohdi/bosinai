"""TEST ONLY: deterministic quote adapter, restricted to a temporary test directory."""
import sys
import tempfile
import time
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
import trading
import trading_mcp

folder=Path(sys.argv[1]).resolve()
if folder.parent!=Path(tempfile.gettempdir()).resolve() or not folder.name.startswith('bosinai-test-'):
    raise SystemExit('Fixture server requires a dedicated temporary test directory')
trading.DATA=folder
trading.SETTINGS['max_order_usdt']='1000'
trading.SETTINGS['paper_fee_bps']=0

def fixture_quote(symbol,side,quantity,enforce_cap=True):
    units=trading.amount(quantity)
    price=trading.Decimal('10')
    return {'symbol':symbol,'side':side,'input':str(units),'output':str(units/price if side=='buy' else units*price),'timestamp':time.time(),'dst_decimals':18,'source':'TEST FIXTURE — NOT A MARKET PRICE'}

trading.quote=fixture_quote
trading_mcp.quote=fixture_quote
trading_mcp.mcp.run(transport='stdio')
