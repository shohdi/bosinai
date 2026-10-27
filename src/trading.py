"""Read-only chain adapter and an explicitly simulated, transactional paper ledger."""
import json
import sqlite3
import time
import uuid
from decimal import Decimal, ROUND_DOWN
import httpx
import keyring
from web3 import Web3
from common import SETTINGS, DATA, amount, catalog

ABI = [
    {'type':'function','name':'balanceOf','stateMutability':'view','inputs':[{'name':'owner','type':'address'}],'outputs':[{'type':'uint256'}]},
    {'type':'function','name':'decimals','stateMutability':'view','inputs':[],'outputs':[{'type':'uint8'}]},
    {'type':'function','name':'symbol','stateMutability':'view','inputs':[],'outputs':[{'type':'string'}]},
]

def chain():
    w = Web3(Web3.HTTPProvider(SETTINGS['rpc_url'], request_kwargs={'timeout':20}))
    if w.eth.chain_id != 56:
        raise ValueError('RPC is not BNB Chain')
    return w

def token(w, address):
    address = Web3.to_checksum_address(address)
    if not w.eth.get_code(address):
        raise ValueError('No token contract at this address')
    return w.eth.contract(address=address, abi=ABI)

def wallet_positions(symbols: list[str]):
    wallet = keyring.get_password('bosinai', 'wallet_address')
    if not wallet:
        return {'configured':False, 'message':'Configure a public wallet address locally.'}
    w = chain()
    wallet = Web3.to_checksum_address(wallet)
    assets = catalog()
    selected = [('USDT', SETTINGS['usdt'])] + [(s,assets[s]['address']) for s in symbols]
    out = []
    for symbol, address in selected:
        c = token(w,address)
        decimals = c.functions.decimals().call()
        out.append({'symbol':symbol,'address':address,'balance':str(Decimal(c.functions.balanceOf(wallet).call()) / 10**decimals), 'decimals':decimals})
    return {'chain_id':56,'wallet':wallet,'block':w.eth.block_number,'bnb':str(w.from_wei(w.eth.get_balance(wallet),'ether')),'positions':out}

def quote(symbol: str, side: str, quantity: str, enforce_cap=True):
    """Buy quantity is USDT spend; sell quantity is token units."""
    if side not in ('buy','sell'):
        raise ValueError('Side must be buy or sell')
    q = amount(quantity)
    asset = catalog()[symbol]
    api = keyring.get_password('bosinai', 'oneinch_api_key')
    if not api:
        raise ValueError('1inch API key is not configured; no executable quote available')
    src,dst = (SETTINGS['usdt'],asset['address']) if side == 'buy' else (asset['address'],SETTINGS['usdt'])
    w = chain()
    sd = token(w,src).functions.decimals().call()
    dd = token(w,dst).functions.decimals().call()
    raw = q * 10**sd
    if raw != raw.to_integral_value():
        raise ValueError('Amount exceeds token precision')
    r = httpx.get(SETTINGS['oneinch_url']+'/quote', params={'src':src,'dst':dst,'amount':str(int(raw))}, headers={'Authorization':'Bearer '+api},timeout=30)
    if r.status_code != 200:
        raise ValueError(f'1inch quote unavailable (HTTP {r.status_code})')
    output = Decimal(r.json()['dstAmount']) / 10**dd
    if output <= 0:
        raise ValueError('No output liquidity')
    if enforce_cap and (q if side == 'buy' else output) > Decimal(SETTINGS['max_order_usdt']):
        raise ValueError('Order exceeds configured USDT cap')
    return {'symbol':symbol,'side':side,'input':str(q),'output':str(output),'src':src,'dst':dst,'src_decimals':sd,'dst_decimals':dd,'timestamp':time.time(),'chain_id':56,'executable':False,'note':'Indicative quote only; gas, allowance, issuer eligibility and live approval are not validated.'}

def performance():
    return PaperBook().profit_loss()

def mark_performance(book):
    with book.db() as db:
        db.execute('BEGIN')
        p = book.positions(db)
        accounting = book.accounting(db)
    cash = Decimal(p['balances']['USDT'])
    value, marks, missing = cash, [], []
    for symbol,quantity in p['balances'].items():
        if symbol=='USDT' or Decimal(quantity)==0:
            continue
        try:
            q = quote(symbol,'sell',quantity,enforce_cap=False)
            mark = Decimal(q['output'])*(1-Decimal(SETTINGS['paper_fee_bps'])/10000)
            value += mark
            cost = accounting['positions'][symbol]['cost_usdt']
            marks.append({'symbol':symbol,'quantity':quantity,'cost_basis_usdt':str(cost),'average_buy_price_usdt':str(cost/Decimal(quantity)),'estimated_net_usdt':str(mark),'unrealized_pnl_usdt':str(mark-cost),'timestamp':q['timestamp']})
        except Exception:
            missing.append(symbol)
    realized = accounting['realized_pnl_usdt']
    unrealized = sum((Decimal(m['unrealized_pnl_usdt']) for m in marks),Decimal(0))
    return {'mode':'PAPER','cash_usdt':str(cash),'initial_usdt':p['initial_usdt'],'realized_pnl_usdt':str(realized),'unrealized_pnl_usdt':None if missing else str(unrealized),'estimated_liquidation_usdt':None if missing else str(value),'estimated_pnl_usdt':None if missing else str(realized+unrealized),'unvalued_positions':missing,'marks':marks,'gas_included':False,'note':'Weighted-average historical cost; realized P&L uses recorded simulated fills. Unrealized P&L uses liquidation quotes less simulated fees. Gas excluded; missing marks mean total P&L unknown.'}

def inspect_asset(symbol):
    a = catalog()[symbol]
    w = chain()
    c = token(w,a['address'])
    return dict(a,chain_id=56,onchain_symbol=c.functions.symbol().call(),decimals=c.functions.decimals().call(),block=w.eth.block_number,liquidity_verified=False)

def save_proposal(symbol,side,quantity):
    q = quote(symbol,side,quantity)
    proposal = {'id':uuid.uuid4().hex,'status':'awaiting_human_review','quote':q,'review_url':'https://1inch.com/swap/','instructions':'Human must verify issuer eligibility, network, both token addresses, amount, minimum received and fees in their own wallet. This proposal cannot sign or broadcast.'}
    folder = DATA/'proposals'
    folder.mkdir(exist_ok=True)
    (folder/(proposal['id']+'.json')).write_text(json.dumps(proposal,indent=2))
    return proposal

class PaperBook:
    def __init__(self, path=None):
        self.path = path or DATA/'paper.sqlite'
        with self.db() as db:
            db.execute('CREATE TABLE IF NOT EXISTS balances(symbol TEXT PRIMARY KEY, quantity TEXT NOT NULL)')
            db.execute('CREATE TABLE IF NOT EXISTS orders(id TEXT PRIMARY KEY, request TEXT NOT NULL, result TEXT NOT NULL)')
            db.execute('CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL)')
            db.execute('INSERT OR IGNORE INTO balances VALUES (?,?)',('USDT',SETTINGS['paper_start_usdt']))
            db.execute('INSERT OR IGNORE INTO metadata VALUES (?,?)',('started',str(time.time())))
            # Migrate existing ledgers without overwriting history or using changed config.
            cash = Decimal(db.execute("SELECT quantity FROM balances WHERE symbol='USDT'").fetchone()[0])
            for (raw,) in db.execute('SELECT result FROM orders ORDER BY rowid'):
                order = json.loads(raw)
                cash += Decimal(order['quote']['input']) if order['quote']['side']=='buy' else -Decimal(order['credited'])
            db.execute('INSERT OR IGNORE INTO metadata VALUES (?,?)',('initial_usdt',str(cash)))

    def db(self):
        return sqlite3.connect(self.path, timeout=10)

    def positions(self, db=None):
        if db is None:
            with self.db() as connection:
                return self.positions(connection)
        return {'mode':'PAPER','balances':dict(db.execute('SELECT * FROM balances')),'started':float(db.execute("SELECT value FROM metadata WHERE key='started'").fetchone()[0]),'initial_usdt':db.execute("SELECT value FROM metadata WHERE key='initial_usdt'").fetchone()[0]}

    def accounting(self, db=None):
        if db is None:
            with self.db() as connection:
                return self.accounting(connection)
        positions, trades, realized = {}, [], Decimal(0)
        for (raw,) in db.execute('SELECT result FROM orders ORDER BY rowid'):
            order = json.loads(raw)
            q = order['quote']
            symbol, side = q['symbol'], q['side']
            p = positions.setdefault(symbol,{'quantity':Decimal(0),'cost_usdt':Decimal(0)})
            units = Decimal(order['credited'] if side=='buy' else q['input'])
            cash = Decimal(q['input'] if side=='buy' else order['credited'])
            pnl = Decimal(0)
            if side=='buy':
                p['quantity'] += units
                p['cost_usdt'] += cash
                removed = Decimal(0)
            else:
                if units>p['quantity'] or p['quantity']<=0:
                    raise ValueError('History does not reconcile: sell exceeds recorded holdings')
                removed = p['cost_usdt'] if units==p['quantity'] else p['cost_usdt']*units/p['quantity']
                p['quantity'] -= units
                p['cost_usdt'] -= removed
                pnl = cash-removed
                realized += pnl
            trades.append({'id':order['id'],'timestamp':order['timestamp'],'mode':'PAPER','symbol':symbol,'side':side,'token_quantity':str(units),'net_cash_usdt':str(cash),'execution_price_usdt':str(cash/units),'cost_basis_removed_usdt':str(removed),'realized_pnl_usdt':str(pnl),'fee_bps':order['fee_bps'],'tx_hash':order['tx_hash']})
        return {'positions':positions,'trades':trades,'realized_pnl_usdt':realized}

    def history(self, symbol='', limit=50, offset=0):
        trades = list(reversed(self.accounting()['trades']))
        if symbol:
            trades = [t for t in trades if t['symbol']==symbol.upper()]
        offset = max(0,offset)
        return {'mode':'PAPER','total':len(trades),'trades':trades[offset:offset+max(1,min(limit,100))]}

    def allocation(self, db=None):
        if db is None:
            with self.db() as connection:
                return self.allocation(connection)
        a = self.accounting(db)
        balances = dict(db.execute('SELECT * FROM balances'))
        for symbol, quantity in balances.items():
            if symbol!='USDT' and Decimal(quantity)!=a['positions'].get(symbol,{}).get('quantity',Decimal(0)):
                raise ValueError('Balances and recorded trade history do not reconcile')
        capital = Decimal(balances['USDT'])+sum((p['cost_usdt'] for p in a['positions'].values()),Decimal(0))
        cap = capital/5
        return {'basis':'cash plus remaining historical purchase cost; not shrinking cash or unrealized market gains','capital_usdt':str(capital),'per_stock_cap_usdt':str(cap),'positions':{s:{'cost_usdt':str(p['cost_usdt']),'remaining_buy_capacity_usdt':str(max(Decimal(0),cap-p['cost_usdt']))} for s,p in a['positions'].items() if p['quantity']>0}}

    def profit_loss(self):
        return mark_performance(self)

    def fill(self, q, request_id):
        if not request_id or len(request_id)>100:
            raise ValueError('Provide an idempotency key of 1-100 characters')
        signature = json.dumps({'symbol':q['symbol'],'side':q['side'],'input':str(amount(q['input']).normalize())},sort_keys=True)
        with self.db() as db:
            db.execute('BEGIN IMMEDIATE')
            previous = db.execute('SELECT request,result FROM orders WHERE id=?',(request_id,)).fetchone()
            if previous:
                if previous[0] != signature:
                    raise ValueError('Idempotency key already used for another order')
                return json.loads(previous[1])
            if time.time()-q['timestamp']>60 or q['timestamp']>time.time()+5:
                raise ValueError('Quote expired')
            if q['side'] not in ('buy','sell') or q['symbol'] not in catalog():
                raise ValueError('Invalid order')
            src,dst = ('USDT',q['symbol']) if q['side']=='buy' else (q['symbol'],'USDT')
            debit, gross = amount(q['input']), amount(q['output'])
            if (debit if src=='USDT' else gross)>Decimal(SETTINGS['max_order_usdt']):
                raise ValueError('Order exceeds cap')
            credit = (gross*(1-Decimal(SETTINGS['paper_fee_bps'])/10000)).quantize(Decimal(10)**-q.get('dst_decimals',18),rounding=ROUND_DOWN)
            if credit <= 0:
                raise ValueError('Output rounds to zero at token precision')
            balances = dict(db.execute('SELECT * FROM balances'))
            if Decimal(balances.get(src,'0')) < debit:
                raise ValueError('Insufficient paper balance')
            if q['side']=='buy':
                allocation = self.allocation(db)
                invested = Decimal(allocation['positions'].get(q['symbol'],{}).get('cost_usdt','0'))
                if invested+debit>Decimal(allocation['per_stock_cap_usdt']):
                    raise ValueError('20% per-stock allocation limit exceeded (including previous buys)')
            started = float(db.execute("SELECT value FROM metadata WHERE key='started'").fetchone()[0])
            if time.time()-started >= 7*86400:
                raise ValueError('One-week evaluation ended; paper orders paused for review')
            for symbol,value in [(src,Decimal(balances.get(src,'0'))-debit),(dst,Decimal(balances.get(dst,'0'))+credit)]:
                db.execute('INSERT OR REPLACE INTO balances VALUES (?,?)',(symbol,str(value)))
            result = {'id':request_id,'status':'paper_filled','mode':'PAPER','quote':q,'credited':str(credit),'fee_bps':SETTINGS['paper_fee_bps'],'timestamp':time.time(),'tx_hash':None}
            units = credit if q['side']=='buy' else debit
            cash = debit if q['side']=='buy' else credit
            result.update(token_quantity=str(units),net_cash_usdt=str(cash),execution_price_usdt=str(cash/units))
            db.execute('INSERT INTO orders VALUES (?,?,?)',(request_id,signature,json.dumps(result)))
            return result

    def order(self, order_id):
        with self.db() as db:
            row = db.execute('SELECT result FROM orders WHERE id=?',(order_id,)).fetchone()
            return json.loads(row[0]) if row else {'status':'not_found'}

def receipt(tx_hash):
    if not isinstance(tx_hash,str) or len(tx_hash)!=66 or not tx_hash.startswith('0x'):
        raise ValueError('Invalid transaction hash')
    int(tx_hash[2:],16)
    w = chain()
    from web3.exceptions import TransactionNotFound
    try:
        r = w.eth.get_transaction_receipt(tx_hash)
    except TransactionNotFound:
        return {'status':'pending_or_unknown','hash':tx_hash}
    return {'hash':tx_hash,'status':'confirmed' if r.status==1 else 'reverted','block':r.blockNumber,'confirmations':w.eth.block_number-r.blockNumber+1,'gas_used':r.gasUsed}
