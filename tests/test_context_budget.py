import json
from types import SimpleNamespace
import pytest
from agent import compact_tool_result
from chat_session import tool_text
from context_budget import CONTEXT_TOKENS, INPUT_TOKEN_BUDGET, MAX_OUTPUT_TOKENS, TOOL_RESULT_CHARS


def test_reply_has_reserved_space():
    assert CONTEXT_TOKENS == 8192 * 4
    assert INPUT_TOKEN_BUDGET == 30456
    assert INPUT_TOKEN_BUDGET + MAX_OUTPUT_TOKENS + 512 == CONTEXT_TOKENS


@pytest.mark.parametrize('unit', ['x', '"\\\n', '\u0623'], ids=['plain', 'escaped', 'unicode'])
def test_both_clients_bound_serialized_excerpts(unit):
    raw = unit * TOOL_RESULT_CHARS + 'overflow'
    result = SimpleNamespace(content=[SimpleNamespace(type='text', text=raw)])
    output = compact_tool_result(result)
    assert output == tool_text(result)
    assert len(output) <= TOOL_RESULT_CHARS
    excerpt = json.loads(output)
    assert excerpt['truncated'] is True
    assert raw.startswith(excerpt['untrusted_excerpt'])


def test_tool_result_at_limit_is_preserved():
    raw = 'x' * TOOL_RESULT_CHARS
    result = SimpleNamespace(content=[SimpleNamespace(type='text', text=raw)])
    assert compact_tool_result(result) == tool_text(result) == raw
