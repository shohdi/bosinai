"""Local web interface backed by the same durable sessions as the chat CLI."""
import asyncio
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
import json
import logging
from pathlib import Path
import re
from urllib.parse import urlsplit
import uuid

import httpx
from starlette.applications import Starlette
from starlette.exceptions import HTTPException
from starlette.middleware import Middleware
from starlette.responses import FileResponse, JSONResponse
from starlette.routing import Mount, Route
from starlette.staticfiles import StaticFiles

from chat_session import ChatClient, ChatSession, ContextFull
from common import DATA, ROOT, SETTINGS

LOG = logging.getLogger(__name__)
SESSION_ID = re.compile(r"[A-Za-z0-9_-]{1,80}")
MAX_BODY_BYTES = 256_000
MAX_MESSAGE_CHARS = 100_000
LOCAL_HOSTS = ("127.0.0.1", "localhost", "[::1]")


class LocalBrowserMiddleware:
    """Keep the unauthenticated local API on the local site's own origin."""

    def __init__(self, app, allowed_hosts):
        self.app = app
        self.allowed_hosts = {host.strip("[]") for host in allowed_hosts}

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        headers = dict(scope.get("headers", []))
        try:
            host = urlsplit("http://" + headers.get(b"host", b"").decode("ascii"))
            host.port  # Validate the port, including malformed/out-of-range values.
            valid_host = (host.hostname in self.allowed_hosts and host.username is None
                          and host.password is None and not host.path and not host.query
                          and not host.fragment)
        except (ValueError, UnicodeError):
            valid_host = False
        if not valid_host:
            await JSONResponse({"error": "Invalid host header."}, 400)(scope, receive, send)
            return
        origin = headers.get(b"origin")
        blocked = False
        if origin:
            try:
                parsed = urlsplit(origin.decode("ascii"))
                blocked = (
                    parsed.scheme != scope.get("scheme", "http")
                    or parsed.hostname not in self.allowed_hosts
                    or parsed.username is not None
                    or parsed.password is not None
                    or parsed.path not in ("", "/")
                    or bool(parsed.query or parsed.fragment)
                    or (parsed.port or (443 if parsed.scheme == "https" else 80))
                    != (host.port or (443 if scope.get("scheme") == "https" else 80))
                )
            except (ValueError, UnicodeError):
                blocked = True
        if headers.get(b"sec-fetch-site") == b"cross-site":
            blocked = True
        if blocked:
            await JSONResponse({"error": "Open this app directly from its local address."}, 403)(scope, receive, send)
            return

        async def secure_send(message):
            if message["type"] == "http.response.start":
                extra = [
                    (b"x-content-type-options", b"nosniff"),
                    (b"referrer-policy", b"no-referrer"),
                    (b"content-security-policy", b"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"),
                ]
                if scope["path"].startswith("/api/"):
                    extra.append((b"cache-control", b"no-store"))
                message["headers"] = list(message.get("headers", [])) + extra
            await send(message)

        await self.app(scope, receive, secure_send)


async def json_body(request):
    if request.headers.get("content-type", "").split(";")[0].strip() != "application/json":
        raise HTTPException(415, "Send an application/json request.")
    body = bytearray()
    async for chunk in request.stream():
        body.extend(chunk)
        if len(body) > MAX_BODY_BYTES:
            raise HTTPException(413, "Message is too large.")
    try:
        value = json.loads(body)
    except (ValueError, UnicodeError):
        raise HTTPException(400, "Invalid JSON request.") from None
    if not isinstance(value, dict):
        raise HTTPException(400, "Expected a JSON object.")
    return value


def valid_title(value):
    if not isinstance(value, str) or not value.strip() or len(value.strip()) > 120:
        raise HTTPException(400, "Title must contain 1–120 characters.")
    return value.strip()


@dataclass
class Generation:
    id: str
    session_id: str
    content: str
    status: str = "pending"
    progress: str = "Waiting for the local model…"
    error: str | None = None
    session: dict | None = None
    task: asyncio.Task | None = field(default=None, repr=False)

    def public(self):
        return {key: getattr(self, key) for key in (
            "id", "session_id", "content", "status", "progress", "error", "session"
        )}


class ChatWeb:
    def __init__(self, directory=None, client_factory=ChatClient):
        self.directory = Path(directory) if directory is not None else DATA / "chat-sessions"
        self.directory.mkdir(parents=True, exist_ok=True)
        self.client_factory = client_factory
        self.jobs: dict[str, Generation] = {}
        self.model_lock = asyncio.Lock()

    def path(self, session_id):
        if not isinstance(session_id, str) or not SESSION_ID.fullmatch(session_id):
            raise HTTPException(400, "Invalid session ID.")
        path = self.directory / (session_id + ".json")
        if path.resolve().parent != self.directory.resolve():
            raise HTTPException(400, "Invalid session path.")
        return path

    def read(self, session_id):
        path = self.path(session_id)
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            raise HTTPException(404, "Conversation not found.") from None
        except (ValueError, UnicodeError):
            raise HTTPException(422, "This conversation file could not be read.") from None
        if not isinstance(data, dict) or data.get("version") != 1 or not isinstance(data.get("messages"), list):
            raise HTTPException(422, "Unsupported conversation file.")
        return data

    def active_job(self, session_id):
        return next((job for job in self.jobs.values()
                     if job.session_id == session_id and job.status in ("pending", "running")), None)

    def require_idle(self, session_id):
        if self.active_job(session_id):
            raise HTTPException(409, "This conversation is generating a reply. Stop it or wait for it to finish.")

    @staticmethod
    def visible_messages(data):
        return [{"role": message["role"], "content": message["content"]}
                for message in data["messages"]
                if isinstance(message, dict) and message.get("role") in ("user", "assistant")
                and isinstance(message.get("content"), str) and message["content"]]

    def summary(self, session_id, data):
        messages = self.visible_messages(data)
        first_user = next((m["content"] for m in messages if m["role"] == "user"), "")
        title = data.get("title") or " ".join(first_user.split())[:60] or "New chat"
        return {
            "id": session_id,
            "title": title,
            "created_at": data.get("created_at", ""),
            "updated_at": data.get("updated_at", ""),
            "message_count": len(messages),
            "preview": " ".join(messages[-1]["content"].split())[:100] if messages else "",
        }

    def detail(self, session_id):
        data = self.read(session_id)
        job = self.active_job(session_id)
        return {**self.summary(session_id, data), "messages": self.visible_messages(data),
                "active_job": job.public() if job else None}

    def open_session(self, session_id):
        try:
            return ChatSession(session_id, self.directory, require_existing=True)
        except ValueError as error:
            raise HTTPException(409, str(error)) from None

    async def homepage(self, request):
        return FileResponse(ROOT / "web" / "index.html")

    async def status(self, request):
        online = False
        try:
            key = (DATA / "model-api.key").read_text().strip()
            async with httpx.AsyncClient(timeout=2, trust_env=False) as client:
                response = await client.get(SETTINGS["model_url"] + "/health",
                                            headers={"Authorization": "Bearer " + key})
                online = response.is_success
        except (OSError, httpx.HTTPError):
            pass
        return JSONResponse({"model": {"name": "Bonsai", "online": online},
                             "active_jobs": [job.public() for job in self.jobs.values()
                                             if job.status in ("pending", "running")]})

    async def sessions(self, request):
        if request.method == "GET":
            sessions = []
            for path in self.directory.glob("*.json"):
                try:
                    sessions.append(self.summary(path.stem, self.read(path.stem)))
                except HTTPException:
                    LOG.warning("Skipping an unreadable conversation: %s", path.name)
            sessions.sort(key=lambda s: s["updated_at"], reverse=True)
            return JSONResponse({"sessions": sessions})
        body = await json_body(request)
        title = valid_title(body["title"]) if "title" in body else None
        session = ChatSession(directory=self.directory)
        try:
            if title:
                session.data["title"] = title
                session.save()
            session_id = session.name
        finally:
            session.close()
        return JSONResponse({"session": self.detail(session_id)}, 201)

    async def session(self, request):
        session_id = request.path_params["session_id"]
        self.read(session_id)
        if request.method == "GET":
            return JSONResponse({"session": self.detail(session_id)})
        self.require_idle(session_id)
        title = None
        if request.method == "PATCH":
            title = valid_title((await json_body(request)).get("title"))
        session = self.open_session(session_id)
        try:
            if request.method == "DELETE":
                session.path.unlink()
            else:
                session.data["title"] = title
                session.save()
        finally:
            session.close()
        if request.method == "DELETE":
            # Keep the lock file: unlinking it would break locks held by other processes.
            return JSONResponse({"ok": True})
        return JSONResponse({"session": self.detail(session_id)})

    async def send_message(self, request):
        session_id = request.path_params["session_id"]
        self.read(session_id)
        self.require_idle(session_id)
        body = await json_body(request)
        content = body.get("content")
        if not isinstance(content, str) or not content.strip() or len(content) > MAX_MESSAGE_CHARS:
            raise HTTPException(400, f"Message must contain 1–{MAX_MESSAGE_CHARS:,} characters.")
        # Recheck after reading the body, since another request may have started a turn.
        self.require_idle(session_id)
        if sum(j.status in ("pending", "running") for j in self.jobs.values()) >= 8:
            raise HTTPException(429, "The model queue is full. Wait for a reply to finish.")
        # Retain recent outcomes for reconnection without growing memory indefinitely.
        completed = [key for key, job in self.jobs.items() if job.status not in ("pending", "running")]
        for key in completed[:-100]:
            del self.jobs[key]
        job = Generation(uuid.uuid4().hex, session_id, content)
        self.jobs[job.id] = job
        job.task = asyncio.create_task(self.generate(job))
        return JSONResponse({"job": job.public()}, 202)

    async def generate(self, job):
        session = None
        try:
            # The existing on-disk lock also protects this chat against CLI writers.
            session = self.open_session(job.session_id)
            async with self.model_lock:
                job.status = "running"
                job.progress = "Connecting to Bonsai…"

                def progress(message):
                    if "Browser MCP:" in str(message):
                        name = str(message).split("Browser MCP:", 1)[1].strip(" []")
                        job.progress = "Using " + name.replace("_", " ") + "…"
                    else:
                        job.progress = str(message)

                async with self.client_factory(output=progress) as client:
                    job.progress = "Thinking… The shared model may queue behind the research agent."
                    if not session.data.get("title"):
                        session.data["title"] = " ".join(job.content.split())[:60]
                    await client.ask(session, job.content)
                job.status = "completed"
                job.progress = "Reply saved."
                job.session = self.detail(job.session_id)
        except asyncio.CancelledError:
            job.status = "cancelled"
            job.progress = "Stopped. Your completed conversation is saved."
        except ContextFull as error:
            job.status = "failed"
            job.error = str(error).replace("Use /new", "Choose New chat")
            job.progress = "Conversation context is full."
        except (httpx.HTTPError, OSError) as error:
            LOG.warning("Model connection failed: %s", type(error).__name__)
            job.status = "failed"
            job.error = "Could not reach the local model. Start it with scripts/start-model.ps1 and retry. Completed messages are saved."
            job.progress = "Model unavailable."
        except Exception as error:
            LOG.exception("Chat generation failed")
            job.status = "failed"
            job.error = str(error.detail if isinstance(error, HTTPException) else error)[:600] if isinstance(error, (ValueError, HTTPException)) else "The reply failed. Retry your message; completed messages are saved."
            job.progress = "Reply failed."
        finally:
            if session is not None:
                session.close()

    def find_job(self, request):
        job = self.jobs.get(request.path_params["job_id"])
        if job is None:
            raise HTTPException(404, "Generation not found. Reload the conversation to see saved messages.")
        return job

    async def job(self, request):
        return JSONResponse({"job": self.find_job(request).public()})

    async def cancel(self, request):
        job = self.find_job(request)
        if job.status in ("pending", "running") and job.task is not None:
            job.task.cancel()
            try:
                await job.task
            except asyncio.CancelledError:
                # The request can arrive before the new task enters generate().
                job.status = "cancelled"
                job.progress = "Stopped. Your completed conversation is saved."
        return JSONResponse({"job": job.public()})

    async def close(self):
        tasks = [job.task for job in self.jobs.values() if job.task and not job.task.done()]
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)


def create_app(directory=None, client_factory=ChatClient, allowed_hosts=None):
    service = ChatWeb(directory, client_factory)
    hosts = list(allowed_hosts or LOCAL_HOSTS)

    @asynccontextmanager
    async def lifespan(app):
        yield
        await service.close()

    async def api_error(request, exception):
        return JSONResponse({"error": str(exception.detail)}, exception.status_code)

    app = Starlette(
        lifespan=lifespan,
        routes=[
            Route("/", service.homepage),
            Route("/api/status", service.status),
            Route("/api/sessions", service.sessions, methods=["GET", "POST"]),
            Route("/api/sessions/{session_id}", service.session, methods=["GET", "PATCH", "DELETE"]),
            Route("/api/sessions/{session_id}/messages", service.send_message, methods=["POST"]),
            Route("/api/jobs/{job_id}", service.job),
            Route("/api/jobs/{job_id}/cancel", service.cancel, methods=["POST"]),
            Mount("/static", app=StaticFiles(directory=ROOT / "web"), name="static"),
        ],
        middleware=[
            Middleware(LocalBrowserMiddleware, allowed_hosts=hosts),
        ],
        exception_handlers={HTTPException: api_error},
    )
    app.state.chat = service
    return app
