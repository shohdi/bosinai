from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal
import time
import pytest
import trading
from trading import PaperBook

@pytest.fixture
def book(tmp_path,monkeypatch):
    monkeypatch.setitem(trading.SETTINGS,'max_order_usdt','1000')
    monkeypatch.setitem(trading.SETTINGS,'paper_fee_bps',0)
    return PaperBook(tmp_path/'history.sqlite')

def order(symbol='QCOM',side='buy',value='100',output='10'):
    return dict(symbol=symbol,side=side,input=value,output=output,timestamp=time.time())

def test_restart_execution_price_partial_sell_and_pnl(book,monkeypatch):
    book.fill(order(),'buy')
    book.fill(order(side='sell',value='4',output='60'),'sell')
    reopened=PaperBook(book.path)
    history=reopened.history()['trades']
    assert Decimal(history[1]['execution_price_usdt'])==10
    assert Decimal(history[0]['execution_price_usdt'])==15
    assert Decimal(history[0]['realized_pnl_usdt'])==20
    monkeypatch.setattr(trading,'quote',lambda *a,**kw:dict(output='90',timestamp=time.time()))
    pnl=reopened.profit_loss()
    assert Decimal(pnl['realized_pnl_usdt'])==20
    assert Decimal(pnl['unrealized_pnl_usdt'])==30
    assert Decimal(pnl['estimated_pnl_usdt'])==50
    assert Decimal(pnl['marks'][0]['cost_basis_usdt'])==60
    assert Decimal(pnl['marks'][0]['average_buy_price_usdt'])==10

def test_repeated_buys_cap_and_five_distinct_stocks(book):
    book.fill(order(value='100'),'first')
    book.fill(order(value='100'),'second')
    with pytest.raises(ValueError,match='20%'):
        book.fill(order(value='1'),'excess')
    for symbol in ['MARA','AMD','LLY','CAT']:
        book.fill(order(symbol,value='200'),symbol)
    assert Decimal(book.positions()['balances']['USDT'])==0
    assert len(book.allocation()['positions'])==5
    assert Decimal(book.allocation()['per_stock_cap_usdt'])==200
    assert book.history()['total']==6

def test_concurrent_buys_cannot_bypass_allocation(book):
    def fill(i):
        try:
            book.fill(order(value='120'),str(i)); return True
        except ValueError:
            return False
    with ThreadPoolExecutor(max_workers=2) as pool:
        assert sum(pool.map(fill,range(2)))==1
    assert book.history()['total']==1

def test_initial_capital_survives_config_changes(book,monkeypatch):
    book.fill(order(),'first')
    monkeypatch.setitem(trading.SETTINGS,'paper_start_usdt','9999')
    reopened=PaperBook(book.path)
    assert reopened.positions()['initial_usdt']=='1000'
    assert reopened.allocation()['capital_usdt']=='1000'

def test_sell_releases_cost_and_realized_profit_changes_capital(book):
    book.fill(order(value='200',output='20'),'buy')
    book.fill(order(side='sell',value='10',output='120'),'sell')
    limits=book.allocation()
    assert Decimal(limits['capital_usdt'])==1020
    assert Decimal(limits['per_stock_cap_usdt'])==204
    assert Decimal(limits['positions']['QCOM']['remaining_buy_capacity_usdt'])==104

def test_history_pagination_and_replay_of_legacy_orders(book):
    import json
    book.fill(order(),'buy')
    with book.db() as db:
        raw=json.loads(db.execute('SELECT result FROM orders').fetchone()[0])
        for key in ('execution_price_usdt','token_quantity','net_cash_usdt'): raw.pop(key)
        db.execute('UPDATE orders SET result=?',(json.dumps(raw),))
        db.execute("DELETE FROM metadata WHERE key='initial_usdt'")
    reopened=PaperBook(book.path)
    assert reopened.positions()['initial_usdt']=='1000'
    assert Decimal(reopened.history('QCOM')['trades'][0]['execution_price_usdt'])==10
    assert reopened.history(offset=1)['trades']==[]
    assert reopened.history('AMD')['total']==0

def test_realized_pnl_remains_known_without_market_quotes(book,monkeypatch):
    book.fill(order(),'buy')
    book.fill(order(side='sell',value='4',output='60'),'sell')
    def unavailable(*a,**kw): raise ValueError('No API key')
    monkeypatch.setattr(trading,'quote',unavailable)
    p=book.profit_loss()
    assert Decimal(p['realized_pnl_usdt'])==20
    assert p['estimated_pnl_usdt'] is None
    assert p['unrealized_pnl_usdt'] is None
