"""Exercise web conversations without launching a model or browser MCP."""
import asyncio
import copy
import json
import threading
import time

import pytest
from starlette.testclient import TestClient

from chat_session import ChatSession, ContextFull
from chat_web import MAX_MESSAGE_CHARS, create_app


REPLY = "Here is Python:\n\n```python\nprint('violet')\n```\n\n**Done.**"


class FakeClient:
    def __init__(self, output=print):
        self.output = output

    async def __aenter__(self):
        return self

    async def __aexit__(self, *args):
        return False

    async def ask(self, session, text):
        self.output("[Browser MCP: browse_page]")
        await asyncio.sleep(0)
        session.messages.extend([
            {"role": "user", "content": text},
            {"role": "assistant", "content": REPLY},
        ])
        session.save()
        return REPLY


def client_for(directory, client_factory=FakeClient):
    return TestClient(create_app(directory=directory, client_factory=client_factory,
                                 allowed_hosts=["testserver"]))


def new_session(client, **values):
    response = client.post("/api/sessions", json=values)
    assert response.status_code == 201, response.text
    return response.json()["session"]


def send(client, session_id, content):
    response = client.post(f"/api/sessions/{session_id}/messages", json={"content": content})
    assert response.status_code == 202, response.text
    return response.json()["job"]


def finished_job(client, job_id):
    deadline = time.monotonic() + 4
    while time.monotonic() < deadline:
        response = client.get(f"/api/jobs/{job_id}")
        assert response.status_code == 200, response.text
        job = response.json()["job"]
        if job["status"] not in ("pending", "running"):
            return job
        time.sleep(0.01)
    pytest.fail("Generation did not finish within four seconds")


def test_session_crud_survives_app_restart(tmp_path):
    with client_for(tmp_path) as client:
        assert client.get("/api/sessions").json() == {"sessions": []}
        session = new_session(client, title="  Project notes  ")
        session_id = session["id"]
        assert session["title"] == "Project notes"
        assert session["messages"] == []
        assert session["message_count"] == 0
        assert session["active_job"] is None
        assert session["created_at"] and session["updated_at"]
        renamed = client.patch(f"/api/sessions/{session_id}", json={"title": "Renamed chat"})
        assert renamed.status_code == 200
        assert renamed.json()["session"]["title"] == "Renamed chat"

    with client_for(tmp_path) as client:
        loaded = client.get(f"/api/sessions/{session_id}")
        assert loaded.status_code == 200
        assert loaded.json()["session"]["title"] == "Renamed chat"
        assert client.get("/api/sessions").json()["sessions"][0]["id"] == session_id
        assert client.delete(f"/api/sessions/{session_id}").status_code == 200
        assert client.get(f"/api/sessions/{session_id}").status_code == 404
        assert client.get("/api/sessions").json() == {"sessions": []}
        assert not (tmp_path / f"{session_id}.json").exists()


def test_cli_sessions_resume_with_full_history_and_markdown(tmp_path):
    cli = ChatSession("terminal_chat", directory=tmp_path)
    cli.messages.extend([
        {"role": "user", "content": "My word is violet."},
        {"role": "assistant", "content": "Remembered."},
        {"role": "assistant", "content": "", "tool_calls": [{"id": "a"}]},
        {"role": "tool", "tool_call_id": "a", "content": "private tool details"},
    ])
    cli.save()
    cli.close()
    captured = []

    class RecordingClient(FakeClient):
        async def ask(self, session, text):
            captured.append(copy.deepcopy(session.messages))
            return await super().ask(session, text)

    with client_for(tmp_path, RecordingClient) as client:
        visible = client.get("/api/sessions/terminal_chat").json()["session"]
        assert visible["title"] == "My word is violet."
        assert visible["message_count"] == 2
        assert [message["role"] for message in visible["messages"]] == ["user", "assistant"]
        job = finished_job(client, send(client, "terminal_chat", "Write it as code")["id"])
        assert job["status"] == "completed"
        assert job["error"] is None
        assert job["session"]["messages"][-1]["content"] == REPLY
        assert any(message["role"] == "tool" for message in captured[0])
        assert captured[0][1]["content"] == "My word is violet."
        assert client.get("/api/sessions").json()["sessions"][0]["message_count"] == 4

    with client_for(tmp_path) as client:
        resumed = client.get("/api/sessions/terminal_chat").json()["session"]
        assert resumed["messages"][-2:] == [
            {"role": "user", "content": "Write it as code"},
            {"role": "assistant", "content": REPLY},
        ]
        assert resumed["active_job"] is None
    saved = json.loads((tmp_path / "terminal_chat.json").read_text(encoding="utf-8"))
    assert any(message["role"] == "tool" for message in saved["messages"])


def test_generation_keeps_sessions_separate_and_uses_whole_conversation(tmp_path):
    histories = []

    class RecordingClient(FakeClient):
        async def ask(self, session, text):
            histories.append(copy.deepcopy(session.messages))
            return await super().ask(session, text)

    with client_for(tmp_path, RecordingClient) as client:
        first = new_session(client)["id"]
        second = new_session(client)["id"]
        assert first != second
        assert finished_job(client, send(client, first, "Remember violet.")["id"])["status"] == "completed"
        assert finished_job(client, send(client, first, "Repeat my word.")["id"])["status"] == "completed"
        assert histories[1][-2]["content"] == "Remember violet."
        assert histories[1][-1]["content"] == REPLY
        assert client.get(f"/api/sessions/{first}").json()["session"]["message_count"] == 4
        assert client.get(f"/api/sessions/{second}").json()["session"]["messages"] == []


@pytest.mark.parametrize("failure", [ValueError("Broken response"),
                                     ContextFull("Too much context. Use /new to continue.")],
                         ids=["invalid-response", "full-context"])
def test_failed_generation_preserves_completed_messages(tmp_path, failure):
    class FailingClient(FakeClient):
        async def ask(self, session, text):
            raise failure

    existing = ChatSession("completed", directory=tmp_path)
    existing.messages.extend([
        {"role": "user", "content": "Saved request"},
        {"role": "assistant", "content": "Saved answer"},
    ])
    existing.save()
    original = copy.deepcopy(existing.messages)
    existing.close()

    with client_for(tmp_path, FailingClient) as client:
        job = finished_job(client, send(client, "completed", "This request fails")["id"])
        assert job["status"] == "failed"
        assert job["error"]
        assert "/new" not in job["error"]
        detail = client.get("/api/sessions/completed").json()["session"]
        assert detail["messages"] == original[1:]
        assert detail["active_job"] is None
        assert client.patch("/api/sessions/completed", json={"title": "Still usable"}).status_code == 200
    saved = json.loads((tmp_path / "completed.json").read_text(encoding="utf-8"))
    assert saved["messages"] == original


def test_busy_session_rejects_mutation_and_cancel_releases_lock(tmp_path):
    started = threading.Event()

    class BlockingClient(FakeClient):
        async def ask(self, session, text):
            self.output("[Browser MCP: browse_page]")
            started.set()
            await asyncio.Event().wait()

    with client_for(tmp_path, BlockingClient) as client:
        session_id = new_session(client)["id"]
        job = send(client, session_id, "Wait for cancellation")
        assert started.wait(timeout=3)
        detail = client.get(f"/api/sessions/{session_id}").json()["session"]
        assert detail["active_job"]["id"] == job["id"]
        assert "browse page" in detail["active_job"]["progress"]
        assert client.post(f"/api/sessions/{session_id}/messages", json={"content": "Duplicate"}).status_code == 409
        assert client.patch(f"/api/sessions/{session_id}", json={"title": "Busy"}).status_code == 409
        assert client.delete(f"/api/sessions/{session_id}").status_code == 409
        cancelled = client.post(f"/api/jobs/{job['id']}/cancel")
        assert cancelled.status_code == 200
        assert cancelled.json()["job"]["status"] == "cancelled"
        assert finished_job(client, job["id"])["status"] == "cancelled"
        assert client.post(f"/api/jobs/{job['id']}/cancel").json()["job"]["status"] == "cancelled"
        detail = client.get(f"/api/sessions/{session_id}").json()["session"]
        assert detail["messages"] == []
        assert detail["active_job"] is None
        assert client.patch(f"/api/sessions/{session_id}", json={"title": "Stopped"}).status_code == 200
        assert client.delete(f"/api/sessions/{session_id}").status_code == 200


def test_session_open_in_cli_is_protected_from_web_writes(tmp_path):
    cli = ChatSession("locked", directory=tmp_path)
    try:
        with client_for(tmp_path) as client:
            assert client.patch("/api/sessions/locked", json={"title": "Overwrite"}).status_code == 409
            assert client.delete("/api/sessions/locked").status_code == 409
            job = finished_job(client, send(client, "locked", "Locked request")["id"])
            assert job["status"] == "failed"
            assert "already open" in job["error"]
            assert client.get("/api/sessions/locked").json()["session"]["messages"] == []
    finally:
        cli.close()


@pytest.mark.parametrize("content", ["", "  \n\t", None, 13, [], "x" * (MAX_MESSAGE_CHARS + 1)],
                         ids=["empty", "whitespace", "null", "number", "array", "too-long"])
def test_invalid_message_is_rejected_without_history_changes(tmp_path, content):
    with client_for(tmp_path) as client:
        session_id = new_session(client)["id"]
        response = client.post(f"/api/sessions/{session_id}/messages", json={"content": content})
        assert response.status_code == 400
        assert client.get(f"/api/sessions/{session_id}").json()["session"]["messages"] == []


@pytest.mark.parametrize("title", ["", "  ", None, 13, "x" * 121],
                         ids=["empty", "whitespace", "null", "number", "too-long"])
def test_invalid_titles_rejected_on_create_and_rename(tmp_path, title):
    with client_for(tmp_path) as client:
        assert client.post("/api/sessions", json={"title": title}).status_code == 400
        session_id = new_session(client, title="Original")["id"]
        assert client.patch(f"/api/sessions/{session_id}", json={"title": title}).status_code == 400
        assert client.get(f"/api/sessions/{session_id}").json()["session"]["title"] == "Original"


def test_malformed_requests_and_missing_resources(tmp_path):
    with client_for(tmp_path) as client:
        assert client.post("/api/sessions", content="{}").status_code == 415
        assert client.post("/api/sessions", content="{", headers={"content-type": "application/json"}).status_code == 400
        assert client.post("/api/sessions", json=[]).status_code == 400
        assert client.post("/api/sessions", json={"title": "x" * 256_001}).status_code == 413
        assert client.get("/api/sessions/missing").status_code == 404
        assert client.get("/api/jobs/missing").status_code == 404
        assert client.post("/api/jobs/missing/cancel").status_code == 404


@pytest.mark.parametrize("session_id", ["..%5Cescape", "..escape", "bad.name", "x" * 81])
def test_session_ids_cannot_escape_history_directory(tmp_path, session_id):
    outside = tmp_path.parent / "escape.json"
    outside.write_text("private", encoding="utf-8")
    with client_for(tmp_path) as client:
        assert client.get(f"/api/sessions/{session_id}").status_code == 400
        assert client.delete(f"/api/sessions/{session_id}").status_code == 400
    assert outside.read_text(encoding="utf-8") == "private"


def test_unreadable_session_does_not_break_history_listing(tmp_path):
    (tmp_path / "broken.json").write_text("not JSON", encoding="utf-8")
    (tmp_path / "unsupported.json").write_text('{"version":2,"messages":[]}', encoding="utf-8")
    with client_for(tmp_path) as client:
        good = new_session(client, title="Readable")
        assert [item["id"] for item in client.get("/api/sessions").json()["sessions"]] == [good["id"]]
        assert client.get("/api/sessions/broken").status_code == 422
        assert client.get("/api/sessions/unsupported").status_code == 422


def test_html_shell_and_offline_markdown_assets(tmp_path):
    with client_for(tmp_path) as client:
        response = client.get("/")
        assert response.status_code == 200
        assert "text/html" in response.headers["content-type"]
        assert "<!doctype html>" in response.text.lower()
        assert "/static/" in response.text
        assert "frame-ancestors 'none'" in response.headers["content-security-policy"]
        for asset in ("marked.min.js", "purify.min.js", "highlight.min.js", "highlight.css"):
            assert client.get(f"/static/vendor/{asset}").status_code == 200
        assert client.get("/api/sessions").headers["cache-control"] == "no-store"
        assert client.get("/static/..%5C..%5Cchat_tester.py").status_code == 404


@pytest.mark.parametrize("origin", ["https://evil.example", "null", "http://127.0.0.1:9000",
                                    "https://127.0.0.1:8000", "http://user@127.0.0.1:8000"])
def test_foreign_origins_cannot_change_local_history(tmp_path, origin):
    with TestClient(create_app(directory=tmp_path, client_factory=FakeClient),
                    base_url="http://127.0.0.1:8000") as client:
        response = client.post("/api/sessions", json={}, headers={"origin": origin})
        assert response.status_code == 403
        assert client.get("/api/sessions").json() == {"sessions": []}


@pytest.mark.parametrize("address", ["http://127.0.0.1:8000", "http://localhost:8000", "http://[::1]:8000"])
def test_loopback_hosts_accept_their_own_origin(tmp_path, address):
    # Starlette's test transport cannot parse an IPv6 base_url; provide the real
    # loopback authority in Host so the application still validates IPv6.
    with TestClient(create_app(directory=tmp_path, client_factory=FakeClient),
                    base_url="http://127.0.0.1:8000") as client:
        response = client.post("/api/sessions", json={},
                               headers={"host": address.split("://", 1)[1], "origin": address})
        assert response.status_code == 201, response.text


def test_untrusted_host_and_cross_site_fetch_rejected(tmp_path):
    app = create_app(directory=tmp_path, client_factory=FakeClient)
    with TestClient(app, base_url="http://attacker.example") as client:
        assert client.get("/api/sessions").status_code == 400
    with TestClient(app, base_url="http://127.0.0.1:8000") as client:
        assert client.post("/api/sessions", json={}, headers={"sec-fetch-site": "cross-site"}).status_code == 403
