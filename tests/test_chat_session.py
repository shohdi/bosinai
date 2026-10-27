import asyncio
from types import SimpleNamespace
import pytest
from chat_session import ChatSession, ChatClient, ContextFull
from context_budget import INPUT_TOKEN_BUDGET, MAX_OUTPUT_TOKENS

def test_session_resume_new_and_lock(tmp_path):
    a=ChatSession('first',tmp_path)
    a.messages.append({'role':'user','content':'Remember violet.'})
    a.save()
    with pytest.raises(ValueError,match='already open'):
        ChatSession('first',tmp_path)
    a.close()
    b=ChatSession('first',tmp_path,require_existing=True)
    assert b.messages[-1]['content']=='Remember violet.'
    b.close()
    c=ChatSession('second',tmp_path)
    assert len(c.messages)==1
    c.close()
    with pytest.raises(ValueError): ChatSession('../escape',tmp_path)
    with pytest.raises(ValueError): ChatSession('missing',tmp_path,require_existing=True)

def test_full_history_sent_and_tool_round_trip_saved(tmp_path):
    session=ChatSession('roundtrip',tmp_path)
    session.messages.extend([{'role':'user','content':'My word is violet.'},{'role':'assistant','content':'Understood.'}])
    client=ChatClient(output=lambda *a:None)
    client.tools=[]
    client.names={'browse_page'}
    captured=[]
    class Http:
        async def post(self,url,json):
            import copy
            assert json['max_tokens']==MAX_OUTPUT_TOKENS
            captured.append(copy.deepcopy(json['messages']))
            if len(captured)==1:
                message={'role':'assistant','content':'','tool_calls':[{'id':'test','type':'function','function':{'name':'browse_page','arguments':'{"url":"https://www.cnbc.com/markets/"}'}}]}
            else:
                message={'role':'assistant','content':'Your word is violet; the page says test.'}
            return SimpleNamespace(raise_for_status=lambda:None,json=lambda:{'choices':[{'message':message,'finish_reason':'stop'}]})
    class MCP:
        async def call_tool(self,name,args):
            return SimpleNamespace(content=[SimpleNamespace(type='text',text='test')])
    async def count(messages): return INPUT_TOKEN_BUDGET
    client.count_tokens=count
    client.http=Http(); client.mcp=MCP()
    asyncio.run(client.ask(session,'Read that page, then repeat my word.'))
    assert captured[0][1]['content']=='My word is violet.'
    assert captured[1][-1]=={'role':'tool','tool_call_id':'test','content':'test'}
    session.close()
    reloaded=ChatSession('roundtrip',tmp_path)
    assert reloaded.messages[-1]['content'].startswith('Your word is violet')
    assert any(m['role']=='tool' for m in reloaded.messages)
    reloaded.close()

def test_context_overflow_keeps_existing_history(tmp_path):
    session=ChatSession('full',tmp_path)
    client=ChatClient()
    async def count(messages): return INPUT_TOKEN_BUDGET+1
    client.count_tokens=count
    with pytest.raises(ContextFull): asyncio.run(client.ask(session,'Too much text'))
    assert len(session.messages)==1
    session.close()
