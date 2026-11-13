"""Bounded HTTP/1 study side channel. No UDP or ROS control client is created."""

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import threading
from urllib.parse import parse_qs, urlsplit

from .runtime import StudyError, bounded_object

MAX_BODY_BYTES = 65536


def _object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON key")
        value[key] = item
    return value


class StudyHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True
    request_queue_size = 8

    def __init__(self, address, runtime, max_connections=8):
        self.runtime = runtime
        self._slots = threading.BoundedSemaphore(max_connections)
        self._stop_tick = threading.Event()
        self._ticker = None
        super().__init__(address, StudyHandler)
        self._ticker = threading.Thread(target=self._tick, name="study-clock", daemon=True)
        self._ticker.start()

    def _tick(self):
        while not self._stop_tick.wait(.02):
            try:
                self.runtime.tick()
            except Exception as error:
                self.runtime.adapter_failed("runtime_tick:" + type(error).__name__)

    def process_request(self, request, client_address):
        if not self._slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self._slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._slots.release()

    def server_close(self):
        self._stop_tick.set()
        super().server_close()
        if self._ticker is not None:
            self._ticker.join(timeout=1.0)


class StudyHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def setup(self):
        super().setup()
        self.connection.settimeout(2.0)

    def log_message(self, format, *args):
        # Payloads and participant identifiers never go into access logs.
        pass

    def _reply(self, status, body):
        encoded = json.dumps(body, allow_nan=False, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        try:
            self.wfile.write(encoded)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _error(self, error):
        self._reply(error.http_status, {"schema_version": 1, "error": error.code,
                                      "message": str(error)[:512]})

    def do_GET(self):
        target = urlsplit(self.path)
        if target.path != "/v1/status":
            self._error(StudyError("not_found", "unknown study endpoint", 404))
            return
        query = parse_qs(target.query)
        try:
            self._reply(200, self.server.runtime.status(query.get("client_id", [None])[0]))
        except StudyError as error:
            self._error(error)

    def do_POST(self):
        routes = {"/v1/session": "create_session", "/v1/client": "register_client",
                  "/v1/trials/start": "start_trial", "/v1/trials/abort": "abort_trial",
                  "/v1/events": "client_events", "/v1/clock": "clock_exchange"}
        route = urlsplit(self.path).path
        if route not in routes:
            self._error(StudyError("not_found", "unknown study endpoint", 404))
            return
        try:
            lengths = self.headers.get_all("Content-Length", [])
            if len(lengths) != 1 or self.headers.get("Transfer-Encoding"):
                raise StudyError("invalid_length", "one Content-Length is required", 411)
            try:
                length = int(lengths[0])
            except ValueError:
                raise StudyError("invalid_length", "invalid Content-Length", 400)
            if not 0 < length <= MAX_BODY_BYTES:
                raise StudyError("payload_too_large", "request body is outside bounds", 413)
            if self.headers.get_content_type() != "application/json":
                raise StudyError("unsupported_media", "application/json required", 415)
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise StudyError("incomplete_body", "request ended early")
            try:
                body = json.loads(raw.decode("utf-8"), object_pairs_hook=_object,
                                  parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
            except (UnicodeDecodeError, ValueError):
                raise StudyError("invalid_json", "malformed or non-finite JSON")
            bounded_object(body, MAX_BODY_BYTES)
            if body.get("schema_version", 1) != 1 or isinstance(body.get("schema_version"), bool):
                raise StudyError("unsupported_version", "schema_version must be 1")
            result = getattr(self.server.runtime, routes[route])(body)
            self._reply(200, result)
        except StudyError as error:
            self._error(error)
        except (TimeoutError, OSError):
            self._error(StudyError("request_timeout", "request read timed out", 408))
        except (ValueError, TypeError, KeyError) as error:
            self._error(StudyError("invalid_request", str(error)))
