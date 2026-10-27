"""Manual review handoff. Execution stays in the human's own wallet."""
import argparse
import json
import sys
import webbrowser
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
from common import DATA

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('proposal_id',nargs='?')
p.add_argument('--open-wallet-site',action='store_true')
a = p.parse_args()
folder = DATA/'proposals'
if not a.proposal_id:
    for f in folder.glob('*.json'):
        proposal = json.loads(f.read_text())
        print(proposal['id'],proposal['status'],proposal['quote']['symbol'],proposal['quote']['side'])
else:
    if len(a.proposal_id)!=32 or any(c not in '0123456789abcdef' for c in a.proposal_id):
        raise SystemExit('Invalid proposal ID')
    proposal = json.loads((folder/(a.proposal_id+'.json')).read_text())
    print(json.dumps(proposal,indent=2))
    print('Quote is indicative and may have expired. Manually refresh and review in your wallet. No transaction has been executed.')
    if a.open_wallet_site:
        webbrowser.open('https://1inch.com/swap/')
