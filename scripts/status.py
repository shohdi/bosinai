import json
import sys
from pathlib import Path
import httpx
import keyring
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
from common import ROOT,DATA,SETTINGS,catalog
from trading import PaperBook

try:
    model = httpx.get(SETTINGS['model_url']+'/health',timeout=3).json()
except Exception:
    model = {'status':'offline'}
report_path = DATA/'latest-report.json'
last_report = json.loads(report_path.read_text())['timestamp'] if report_path.exists() else None
print(json.dumps({'model':model,'assets':len(catalog()),'interval_seconds':SETTINGS['interval_seconds'],'last_completed_report':last_report,'stop_flag':(DATA/'STOP').exists(),'configured':{name:bool(keyring.get_password('bosinai',name)) for name in ('wallet_address','oneinch_api_key','brave_api_key')},'paper':PaperBook().positions(),'live_execution':'Human wallet review only; no signer'},indent=2))
