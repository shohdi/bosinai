"""Real MCP handshake, tool discovery, tool invocation; optional network/model checks."""
import asyncio
import json
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from common import ROOT

async def main():
    for module,tool,args in [('trading_mcp','list_assets',{'query':'QCOM'}),('research_mcp','stock_news',{'symbol':'QCOM','limit':2})]:
        async with stdio_client(StdioServerParameters(command=sys.executable,args=[str(ROOT/'src'/f'{module}.py')])) as streams:
            async with ClientSession(*streams) as session:
                await session.initialize()
                print(module,[t.name for t in (await session.list_tools()).tools])
                result = await session.call_tool(tool,args)
                print(result.model_dump_json()[:1800])
                assert not result.isError
    print('MCP smoke passed')

asyncio.run(main())
