import asyncio
from types import SimpleNamespace
from agent import fit_context, compact_tool_result
from context_budget import INPUT_TOKEN_BUDGET, TOOL_RESULT_CHARS

def test_context_trim_removes_complete_exchange():
    messages = [{'role':'system','content':'policy'},{'role':'user','content':'task'},
                {'role':'assistant','tool_calls':[{'id':'old'}]},
                {'role':'tool','tool_call_id':'old','content':'old result'},
                {'role':'assistant','tool_calls':[{'id':'new'}]},
                {'role':'tool','tool_call_id':'new','content':'new result'}]
    class Client:
        count = 0
        async def post(self,url,json):
            if url.endswith('/apply-template'):
                payload={'prompt':'rendered'}
            else:
                self.count += 1
                payload={'tokens':[0]*(INPUT_TOKEN_BUDGET+1 if self.count==1 else INPUT_TOKEN_BUDGET)}
            return SimpleNamespace(raise_for_status=lambda:None,json=lambda:payload)
    assert asyncio.run(fit_context(Client(),messages,[]))==1
    assert [m['role'] for m in messages]==['system','user','assistant','tool']
    assert messages[-1]['tool_call_id']=='new'

def test_tool_output_is_bounded_and_marked():
    result=SimpleNamespace(content=[SimpleNamespace(type='text',text='x'*(TOOL_RESULT_CHARS+1))])
    import json
    compact=json.loads(compact_tool_result(result))
    assert compact['truncated']
    assert len(compact['untrusted_excerpt'])>4200
    assert len(compact_tool_result(result))<=TOOL_RESULT_CHARS
