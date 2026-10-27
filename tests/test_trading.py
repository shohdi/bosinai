import time
from decimal import Decimal
import pytest
from common import amount, catalog
from trading import PaperBook
from research_mcp import validate_url

def q(side='buy', value='10', output='0.05'):
    return dict(symbol='QCOM',side=side,input=value,output=output,timestamp=time.time())

@pytest.mark.parametrize('value',['0','-1','NaN','Infinity','abc'])
def test_reject_invalid_amount(value):
    with pytest.raises(ValueError): amount(value)

def test_fractional_fill_idempotence_and_conflict(tmp_path):
    b = PaperBook(tmp_path/'book.db')
    order = b.fill(q(),'one')
    assert b.fill(q(),'one')==order
    assert Decimal(b.positions()['balances']['USDT'])==Decimal('990')
    assert Decimal(b.positions()['balances']['QCOM'])==Decimal('0.04985')
    with pytest.raises(ValueError,match='another order'): b.fill(q(value='11'),'one')
    b.fill(q('sell','0.01','2'),'two')
    assert Decimal(b.positions()['balances']['USDT'])==Decimal('991.994')

def test_rejected_orders_leave_balances_unchanged(tmp_path):
    b = PaperBook(tmp_path/'book.db')
    before = b.positions()
    for order in [q(value='26'),q('sell','1','10'),dict(q(),timestamp=time.time()-61),dict(q(),output='NaN')]:
        with pytest.raises(ValueError): b.fill(order,'bad')
        assert b.positions()==before

def test_weekly_pause(tmp_path):
    b = PaperBook(tmp_path/'book.db')
    with b.db() as db: db.execute("UPDATE metadata SET value=? WHERE key='started'",(str(time.time()-8*86400),))
    with pytest.raises(ValueError,match='evaluation ended'): b.fill(q(),'week')

@pytest.mark.parametrize('url',['http://www.google.com','https://127.0.0.1','file:///C:/secret','https://www.google.com@127.0.0.1','https://evil.com','https://www.google.com:8443'])
def test_browser_rejects_unsafe_destinations(url):
    with pytest.raises(ValueError): validate_url(url)

def test_catalog_is_real_workbook():
    assets = catalog()
    assert len(assets)==200
    assert assets['QCOM']['address']=='0xfbd4d681c92ead6af0e49950c8b2e47eeacbb2db'

def test_concurrent_duplicate_has_one_fill(tmp_path):
    from concurrent.futures import ThreadPoolExecutor
    b = PaperBook(tmp_path/'book.db')
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(lambda _:b.fill(q(),'same'),range(4)))
    assert all(result==results[0] for result in results)
    assert b.positions()['balances']['USDT']=='990'

def test_unvalued_positions_never_report_profit(tmp_path,monkeypatch):
    import trading
    b = PaperBook(tmp_path/'book.db')
    b.fill(q(),'one')
    monkeypatch.setattr(trading,'PaperBook',lambda:b)
    def missing(*args,**kwargs): raise ValueError('Unavailable')
    monkeypatch.setattr(trading,'quote',missing)
    report = trading.performance()
    assert report['estimated_pnl_usdt'] is None
    assert report['unvalued_positions']==['QCOM']

def test_missing_api_fails_without_synthetic_quote(monkeypatch):
    import trading
    monkeypatch.setattr(trading.keyring,'get_password',lambda *a:None)
    with pytest.raises(ValueError,match='not configured'):
        trading.quote('QCOM','buy','10')

def test_browser_rejects_private_dns(monkeypatch):
    import research_mcp
    monkeypatch.setattr(research_mcp.socket,'getaddrinfo',lambda *a,**kw:[(2,1,6,'',('127.0.0.1',443))])
    with pytest.raises(ValueError,match='Private'):
        validate_url('https://www.google.com')

def test_fill_rounds_down_to_destination_precision(tmp_path):
    b = PaperBook(tmp_path/'book.db')
    result = b.fill(dict(q(output='0.123456'),dst_decimals=6),'precise')
    assert Decimal(result['credited'])==Decimal('0.123085')
    assert b.fill(q(value='10.0',output='0.123456'),'precise')==result
