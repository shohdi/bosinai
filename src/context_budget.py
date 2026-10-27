"""Shared conversation limits; these do not change model inference settings."""
import json
from common import SETTINGS

CONTEXT_TOKENS = SETTINGS['context_tokens']
MAX_OUTPUT_TOKENS = SETTINGS['max_output_tokens']
INPUT_TOKEN_BUDGET = CONTEXT_TOKENS - MAX_OUTPUT_TOKENS - SETTINGS['context_safety_margin_tokens']
TOOL_RESULT_CHARS = SETTINGS['tool_result_chars']


def bounded_tool_text(raw):
    if len(raw) <= TOOL_RESULT_CHARS:
        return raw

    def wrap(length):
        return json.dumps({'truncated': True, 'untrusted_excerpt': raw[:length],
                           'note': 'Tool result excerpt only; request a narrower result if needed.'},
                          ensure_ascii=False)

    # Include JSON escaping and metadata in the character limit.
    low, high = 0, min(len(raw), TOOL_RESULT_CHARS)
    while low < high:
        mid = (low + high + 1) // 2
        if len(wrap(mid)) <= TOOL_RESULT_CHARS:
            low = mid
        else:
            high = mid - 1
    return wrap(low)
