"""Run tests and isolated MCP accounting scenarios. No secrets or real trades needed."""
import argparse
import asyncio
from contextlib import asynccontextmanager
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import httpx
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT=Path(__file__).resolve().parents[1]

@asynccontextmanager
async def connect(folder):
    params=StdioServerParameters(command=sys.executable,args=[str(ROOT/'tests/mcp_fixture_server.py'),str(folder)])
    async with stdio_client(params) as streams:
        async with ClientSession(*streams) as session:
            await session.initialize()
            yield session

async def call(session,name,args=None,error=False):
    result=await session.call_tool(name,args or {})
    assert bool(result.isError)==error, result
    raw='\n'.join(c.text for c in result.content if c.type=='text')
    return raw if error else json.loads(raw)

async def integration(folder):
    async with connect(folder) as session:
        tools={t.name for t in (await session.list_tools()).tools}
        assert {'get_profit_loss','get_trade_history','get_allocation_limits'}<=tools
        buy={'symbol':'QCOM','side':'buy','quantity':'200','request_id':'test-buy'}
        await call(session,'paper_order',buy)
        await call(session,'paper_order',buy)  # Retry must not duplicate the fill.
        assert '20%' in await call(session,'paper_order',dict(buy,quantity='1',request_id='blocked'),error=True)
        await call(session,'paper_order',{'symbol':'QCOM','side':'sell','quantity':'5','request_id':'test-sell'})
        report=await call(session,'get_profit_loss')
        assert report['estimated_pnl_usdt']=='0E-18' or float(report['estimated_pnl_usdt'])==0
        history=await call(session,'get_trade_history')
        assert history['total']==2
        assert float(history['trades'][1]['execution_price_usdt'])==10
    # A genuinely new MCP process reloads the same on-disk ledger.
    async with connect(folder) as session:
        history=await call(session,'get_trade_history')
        balances=await call(session,'get_positions')
        assert history['total']==2
        assert float(balances['balances']['QCOM'])==15
        assert float(balances['balances']['USDT'])==850
        print('PASS: real MCP calls, persistent buy/sell prices, retry protection, 20% rejection, P&L, process restart/reload.')

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--online',action='store_true',help='Also check live news MCP and local model tool calling; never submits orders.')
    args=p.parse_args()
    subprocess.run([sys.executable,'-m','pytest','-q'],cwd=ROOT,check=True)
    with tempfile.TemporaryDirectory(prefix='bosinai-test-') as folder:
        print('Isolated ledger:',folder,flush=True)
        asyncio.run(integration(folder))
    if args.online:
        subprocess.run([sys.executable,str(ROOT/'scripts/smoke.py')],cwd=ROOT,check=True)
        auth={'Authorization':'Bearer '+(ROOT/'data/model-api.key').read_text().strip()}
        tools=[{'type':'function','function':{'name':'test_echo','description':'Return the provided text','parameters':{'type':'object','properties':{'text':{'type':'string'}},'required':['text']}}}]
        r=httpx.post('http://127.0.0.1:8080/v1/chat/completions',headers=auth,json={'messages':[{'role':'user','content':'Call test_echo with text hello.'}],'tools':tools,'tool_choice':'required','max_tokens':128,'chat_template_kwargs':{'enable_thinking':True}},timeout=180)
        r.raise_for_status()
        calls=r.json()['choices'][0]['message'].get('tool_calls',[])
        assert calls and calls[0]['function']['name']=='test_echo',r.text
        assert json.loads(calls[0]['function']['arguments'])=={'text':'hello'},r.text
        print('PASS: local model structured tool calling and live news MCP.')
    print('PASS: test environment complete. Production ledger unchanged by fixture trades.')

if __name__=='__main__':
    main()
