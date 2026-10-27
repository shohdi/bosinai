import json
from pathlib import Path
from decimal import Decimal, InvalidOperation
import openpyxl

ROOT = Path(__file__).resolve().parents[1]
SETTINGS = json.loads((ROOT / 'config/settings.json').read_text())
DATA = ROOT / 'data'
DATA.mkdir(exist_ok=True)

def amount(value: str) -> Decimal:
    try:
        n = Decimal(value)
    except InvalidOperation:
        raise ValueError('Invalid decimal amount') from None
    if not n.is_finite() or n <= 0:
        raise ValueError('Amount must be finite and positive')
    return n

def catalog():
    wb = openpyxl.load_workbook(ROOT / 'stocks.xlsx', read_only=True, data_only=True)
    rows = list(wb.active.values)
    wb.close()
    result = {}
    addresses = set()
    for name, symbol, address, page, *rest in rows[1:]:
        if not symbol:
            continue
        symbol = str(symbol).strip().upper()
        if symbol in result:
            raise ValueError(f'Duplicate symbol: {symbol}')
        if not isinstance(address, str) or len(address) != 42 or not address.startswith('0x'):
            raise ValueError(f'Invalid address for {symbol}')
        int(address[2:], 16)
        if address.lower() in addresses:
            raise ValueError('Duplicate token address: aliases cannot bypass per-stock limits')
        addresses.add(address.lower())
        result[symbol] = dict(name=name, symbol=symbol, address=address, source=page,
                              issuer_verified=False)
    return result
