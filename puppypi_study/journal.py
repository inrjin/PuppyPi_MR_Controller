"""Bounded, asynchronous JSONL storage with explicit loss/error accounting."""

import copy
import json
import os
from pathlib import Path
import queue
import threading
import time
import uuid


class AsyncJournal:
    def __init__(self, path, capacity=2048, max_record_bytes=65536):
        if capacity < 1 or max_record_bytes < 1024:
            raise ValueError("invalid journal bounds")
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._file = self.path.open("x", encoding="utf-8", newline="\n")
        self._queue = queue.Queue(maxsize=capacity)
        self._lock = threading.Lock()
        self._max_record_bytes = max_record_bytes
        self._dropped = 0
        self._reported_dropped = 0
        self._written = 0
        self._error = None
        self._closing = False
        self._closed = False
        self._clock_id = "journal_monotonic_" + uuid.uuid4().hex
        self._last_header = {}
        self._worker = threading.Thread(target=self._run, name="study-journal", daemon=True)
        self._worker.start()

    @property
    def healthy(self):
        state = self.status()
        return not state["error"] and not state["dropped"] and not state["closing"]

    def status(self):
        with self._lock:
            return {"path": str(self.path), "queued": self._queue.qsize(),
                    "written": self._written, "dropped": self._dropped,
                    "error": self._error, "closing": self._closing, "closed": self._closed}

    def emit(self, record):
        # Serialize on admission so callers cannot mutate queued records afterwards.
        try:
            line = json.dumps(record, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
            if len(line.encode("utf-8")) > self._max_record_bytes:
                raise ValueError("record exceeds maximum size")
        except (TypeError, ValueError):
            with self._lock:
                self._dropped += 1
            return False
        with self._lock:
            if self._closing or self._error:
                self._dropped += 1
                return False
            try:
                self._queue.put_nowait(("record", line))
            except queue.Full:
                self._dropped += 1
                return False
            self._last_header = {key: copy.deepcopy(value) for key, value in record.items()
                                 if key != "payload"}
            return True

    def flush(self, timeout=2.0):
        event = threading.Event()
        try:
            self._queue.put(("flush", event), timeout=max(0, timeout))
        except queue.Full:
            return False
        return event.wait(max(0, timeout)) and self._error is None

    def close(self, timeout=2.0):
        started = time.monotonic()
        with self._lock:
            if self._closed:
                return self._error is None
            first_close = not self._closing
            self._closing = True
        if first_close:
            try:
                self._queue.put(("close", None), timeout=max(0, timeout))
            except queue.Full:
                with self._lock:
                    self._error = "close_timeout: log may be partial"
                return False
        self._worker.join(max(0, timeout - (time.monotonic() - started)))
        return self._closed and self._error is None

    def _quality_record(self, event_type, payload):
        with self._lock:
            record = dict(self._last_header)
        record.update(schema_version=1, event_id=str(uuid.uuid4()), source="journal",
                      record_type="event", event_type=event_type,
                      clock_id=self._clock_id, t_mono_us=time.monotonic_ns() // 1000, payload=payload)
        return json.dumps(record, ensure_ascii=False, allow_nan=False) + "\n"

    def _write_loss(self):
        with self._lock:
            dropped = self._dropped
        if dropped != self._reported_dropped:
            self._file.write(self._quality_record("logging_overflow", {
                "dropped_total": dropped, "dropped_since_last": dropped - self._reported_dropped,
                "complete": False}))
            self._reported_dropped = dropped

    def _run(self):
        last_flush = time.monotonic()
        try:
            while True:
                try:
                    kind, value = self._queue.get(timeout=.25)
                except queue.Empty:
                    self._write_loss()
                    self._file.flush()
                    last_flush = time.monotonic()
                    continue
                try:
                    self._write_loss()
                    if kind == "record":
                        self._file.write(value + "\n")
                        if time.monotonic() - last_flush >= .25:
                            self._file.flush()
                            last_flush = time.monotonic()
                        with self._lock:
                            self._written += 1
                    elif kind == "flush":
                        self._file.flush()
                        value.set()
                    elif kind == "close":
                        self._file.write(self._quality_record("logging_closed", {
                            "complete": self._dropped == 0, "dropped_total": self._dropped,
                            "written": self._written}))
                        self._file.flush()
                        os.fsync(self._file.fileno())
                        break
                finally:
                    self._queue.task_done()
        except (OSError, ValueError) as error:
            with self._lock:
                self._error = type(error).__name__ + ": " + str(error)
        finally:
            try:
                self._file.close()
            finally:
                with self._lock:
                    self._closed = True


class MemoryJournal:
    """In-memory sink for deterministic, motor-free tests."""
    def __init__(self):
        self.records = []
        self.healthy = True

    def emit(self, record):
        self.records.append(copy.deepcopy(record))
        return self.healthy

    def status(self):
        return {"written": len(self.records), "dropped": 0, "error": None,
                "closing": False, "closed": False}

    def close(self, timeout=2.0):
        return True
