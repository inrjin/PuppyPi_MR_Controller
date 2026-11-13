#!/usr/bin/env python3
"""Motor-free HTTP + UDP bench for the Unity MR study client.

This is a deliberately small mock ACK implementation for integration regression.
It is not the robot server, a protocol-conformance validator, or a QR image test.
Only IPv4 loopback listeners are permitted. No robot, ROS or camera is opened.
"""

from __future__ import annotations

import argparse
import ipaddress
import json
import math
from pathlib import Path
import socket
import sys
import threading
import time
import uuid


REPO_ROOT = Path(__file__).resolve().parents[1]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from puppypi_study.config import demo_config
from puppypi_study.journal import AsyncJournal
from puppypi_study.runtime import RuntimePolicy, StudyRuntime
from puppypi_study.server import StudyHTTPServer


COMMAND_FIELDS = frozenset({"protocol", "type", "client_id", "sequence", "control_active", "forward", "turn"})
EVENT_FIELDS = frozenset({"protocol", "type", "client_id", "sequence"})
EVENT_TYPES = frozenset({"emergency_stop", "clear_emergency", "disconnect"})
MAX_SEQUENCE = 9_007_199_254_740_991


def loopback_host(value):
    try:
        address = ipaddress.ip_address(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("an IPv4 loopback address is required") from error
    if address.version != 4 or not address.is_loopback:
        raise argparse.ArgumentTypeError("this mock bench refuses every non-loopback bind")
    return str(address)


def port(value):
    try:
        number = int(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("port must be an integer") from error
    if not 0 <= number <= 65535:
        raise argparse.ArgumentTypeError("port must be 0..65535; 0 selects an ephemeral listener")
    return number


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate key")
        result[key] = value
    return result


def _finite_axis(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and -1 <= value <= 1


class MockV2State:
    """Simplified owner/neutral/sequence/watchdog policy; never actuates anything."""

    def __init__(self, watchdog_seconds=0.3, lease_seconds=1.0):
        self.watchdog_seconds = watchdog_seconds
        self.lease_seconds = lease_seconds
        self.owner = None
        self.owner_peer = None
        self.last_owner_message = None
        self.last_command_at = None
        self.last_sequences = {}
        self.retired_clients = set()
        self.neutral_confirmed = False
        self.emergency = False
        self.active = False
        self.axes = (0.0, 0.0)
        self.timed_out = False

    def expire(self, now):
        if self.last_command_at is not None and now - self.last_command_at > self.watchdog_seconds:
            self.active = False
            self.axes = (0.0, 0.0)
            self.neutral_confirmed = False
            self.timed_out = True
        if self.owner is not None and now - self.last_owner_message > self.lease_seconds:
            self.owner = self.owner_peer = None
            self.neutral_confirmed = False
            self.active = False
            self.axes = (0.0, 0.0)

    @property
    def state(self):
        if self.emergency:
            return "emergency_stop"
        if self.timed_out:
            return "timeout"
        if not self.neutral_confirmed:
            return "awaiting_neutral"
        return "active" if self.active else "idle"

    def _ack(self, packet, accepted, reason):
        return {"protocol": 2, "type": "ack", "client_id": packet["client_id"],
                "sequence": packet["sequence"], "accepted": accepted, "reason": reason,
                "emergency_stop": self.emergency, "state": self.state}

    def receive(self, raw, peer, now):
        self.expire(now)
        try:
            packet = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_object,
                                parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
            if not isinstance(packet, dict):
                raise ValueError("expected object")
            client_id = packet.get("client_id")
            if not isinstance(client_id, str) or str(uuid.UUID(client_id)) != client_id:
                raise ValueError("canonical UUID required")
            sequence = packet.get("sequence")
            if type(sequence) is not int or not 0 <= sequence <= MAX_SEQUENCE:
                raise ValueError("safe integer sequence required")
        except (UnicodeDecodeError, ValueError, TypeError, AttributeError):
            return None, None, "malformed_packet"
        if type(packet.get("protocol")) is not int or packet["protocol"] != 2:
            return packet, self._ack(packet, False, "unsupported_protocol"), "unsupported_protocol"
        kind = packet.get("type")
        if kind == "command":
            valid = (set(packet) == COMMAND_FIELDS and type(packet.get("control_active")) is bool and
                     _finite_axis(packet.get("forward")) and _finite_axis(packet.get("turn")))
            if valid and not packet["control_active"] and (packet["forward"] != 0 or packet["turn"] != 0):
                valid = False
        else:
            valid = kind in EVENT_TYPES and set(packet) == EVENT_FIELDS
        if not valid:
            return packet, self._ack(packet, False, "invalid_packet"), "invalid_packet"
        if sequence <= self.last_sequences.get(client_id, -1):
            return packet, self._ack(packet, False, "out_of_order"), "out_of_order"
        if client_id in self.retired_clients:
            return packet, self._ack(packet, False, "not_owner"), "retired_mock_client"
        # The mock binds a UUID to one UDP endpoint for its current lease.
        if self.owner is not None and (client_id != self.owner or peer != self.owner_peer):
            return packet, self._ack(packet, False, "not_owner"), "not_owner"
        if self.owner is None:
            if kind == "command" and not packet["control_active"] or kind == "emergency_stop":
                self.owner, self.owner_peer = client_id, peer
            else:
                return packet, self._ack(packet, False, "neutral_required"), "neutral_required"
        self.last_sequences[client_id] = sequence
        if kind == "command":
            if packet["control_active"] and self.emergency:
                return packet, self._ack(packet, False, "emergency_stop"), "emergency_stop"
            if packet["control_active"] and not self.neutral_confirmed:
                return packet, self._ack(packet, False, "neutral_required"), "neutral_required"
            self.last_owner_message = self.last_command_at = now
            self.timed_out = False
            self.active = packet["control_active"]
            self.axes = (float(packet["forward"]), float(packet["turn"])) if self.active else (0.0, 0.0)
            if not self.active:
                self.neutral_confirmed = True
        elif kind == "emergency_stop":
            self.last_owner_message = now
            self.emergency = True
            self.active = False
            self.axes = (0.0, 0.0)
            self.neutral_confirmed = False
        elif kind == "clear_emergency":
            self.last_owner_message = now
            self.emergency = False
            self.active = False
            self.axes = (0.0, 0.0)
            self.neutral_confirmed = False
            self.timed_out = False
        elif kind == "disconnect":
            self.retired_clients.add(client_id)
            self.owner = self.owner_peer = None
            self.neutral_confirmed = False
            self.active = False
            self.axes = (0.0, 0.0)
        return packet, self._ack(packet, True, "ok"), "accepted"


class LoopbackV2Server:
    def __init__(self, host, udp_port, runtime, journal):
        if loopback_host(host) != host:
            raise ValueError("loopback host required")
        self.runtime, self.journal = runtime, journal
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind((host, udp_port))
        self.socket.settimeout(.05)
        self.address = self.socket.getsockname()
        self.stop_event = threading.Event()
        self.lock = threading.Lock()
        self.state = MockV2State()
        self.thread = threading.Thread(target=self._run, name="study-loopback-v2", daemon=True)
        self.packet_count = self.accepted_count = self.invalid_count = 0
        self.thread.start()

    def _log(self, event_type, packet, ack=None, reason=None):
        status = self.runtime.status()
        record = {"schema_version": 1, "event_id": str(uuid.uuid4()),
            "experiment_session_id": status.get("experiment_session_id"),
            "trial_id": status.get("trial_id"), "participant_id": status.get("participant_id"),
            "interface_id": "mr_ballgrip", "period_index": status.get("period_index"),
            "difficulty_id": status.get("difficulty_id"), "source": "loopback_udp_mock",
            "clock_id": self.runtime.clock_id, "t_mono_us": self.runtime.clock(),
            "record_type": "event", "event_type": event_type,
            "payload": {"mock_mode": True, "physical_motion": False,
                        "packet": packet, "ack": ack, "reason": reason}}
        self.journal.emit(record)

    def _run(self):
        while not self.stop_event.is_set():
            try:
                raw, peer = self.socket.recvfrom(4097)
            except socket.timeout:
                with self.lock:
                    self.state.expire(time.monotonic())
                continue
            except OSError:
                return
            if not ipaddress.ip_address(peer[0]).is_loopback:
                continue
            with self.lock:
                self.packet_count += 1
                if len(raw) > 4096:
                    self.invalid_count += 1
                    continue
                packet, ack, reason = self.state.receive(raw, peer, time.monotonic())
                if ack and ack["accepted"]:
                    self.accepted_count += 1
                else:
                    self.invalid_count += 1
            self._log("mock_v2_received", packet, ack, reason)
            if ack is not None:
                try:
                    self.socket.sendto(json.dumps(ack, allow_nan=False, separators=(",", ":")).encode(), peer)
                except OSError:
                    pass

    def neutral_axes(self):
        with self.lock:
            self.state.expire(time.monotonic())
            return tuple(self.state.axes)

    def close(self):
        self.stop_event.set()
        self.socket.close()
        self.thread.join(timeout=1)


def mock_camera(runtime, udp, stop_event):
    frame_id = 0
    while not stop_event.wait(.05):
        state = runtime.status()["state"]
        frame_id += 1
        now = runtime.clock()
        running = state == "running"
        runtime.ingest_observation({"schema_version": 1, "frame_id": frame_id,
            "clock_id": runtime.clock_id, "received_mono_us": now, "decoded_mono_us": now,
            "captured_mono_us": None, "capture_clock_id": None,
            "image_width": 640, "image_height": 480,
            "marker_text": runtime.config.framing.target_marker if running else None,
            "corners": [[270, 190], [370, 190], [370, 290], [270, 290]] if running else None,
            "data_valid": True, "invalid_reason": None, "decoder": "mock_fixture",
            "processing_duration_us": 0}, neutral_axes=udp.neutral_axes())


def self_test_mock():
    """Exercise only this bench's documented simplified policy without sockets."""
    state = MockV2State()
    first, other = str(uuid.uuid4()), str(uuid.uuid4())
    peer = ("127.0.0.1", 31001)
    checks = 0

    def send(sequence, *, kind="command", client=first, active=False, forward=0, turn=0,
             at=0.0, extra=None, from_peer=peer):
        packet = {"protocol": 2, "type": kind, "client_id": client, "sequence": sequence}
        if kind == "command":
            packet.update(control_active=active, forward=forward, turn=turn)
        if extra:
            packet.update(extra)
        return state.receive(json.dumps(packet, allow_nan=False).encode(), from_peer, at)[1]

    def expect(condition, label):
        nonlocal checks
        if not condition:
            raise AssertionError("mock regression failed: " + label)
        checks += 1

    expect(send(1, active=True, forward=.1)["reason"] == "neutral_required", "first command needs neutral")
    expect(send(2)["state"] == "idle", "neutral ACK confirms idle")
    expect(send(3, active=True, forward=.1, turn=-.2, at=.05)["state"] == "active", "two-axis command")
    expect(send(3, active=True, forward=.1, at=.06)["reason"] == "out_of_order", "duplicate sequence")
    expect(send(4, extra={"trial_id": "forbidden"}, at=.07)["reason"] == "invalid_packet", "strict v2 fields")
    expect(send(4, active=False, forward=.1, at=.08)["reason"] == "invalid_packet", "inactive command must be neutral")
    expect(send(1, client=other, from_peer=("127.0.0.1", 31002), at=.09)["reason"] == "not_owner", "owner isolation")
    expect(send(4, from_peer=("127.0.0.1", 31003), at=.1)["reason"] == "not_owner", "endpoint isolation")
    expect(send(4, kind="emergency_stop", at=.11)["emergency_stop"], "emergency latches")
    expect(send(5, at=.12)["emergency_stop"], "neutral never clears emergency")
    expect(send(6, active=True, forward=.1, at=.13)["reason"] == "emergency_stop", "latched movement rejected")
    clear = send(7, kind="clear_emergency", at=.14)
    expect(clear["accepted"] and not clear["emergency_stop"] and clear["state"] == "awaiting_neutral", "explicit clear needs fresh neutral")
    expect(send(8, active=True, forward=.1, at=.15)["reason"] == "neutral_required", "clear does not rearm")
    expect(send(9, at=.16)["state"] == "idle", "fresh neutral after clear")
    expect(send(10, active=True, forward=.1, at=.5)["reason"] == "neutral_required", "watchdog requires new neutral")
    expect(send(11, at=.51)["state"] == "idle", "watchdog recovery")
    expect(send(12, kind="disconnect", at=.52)["accepted"], "disconnect accepted")
    expect(send(13, at=.53)["reason"] == "not_owner", "retired UUID cannot reconnect")
    expect(send(2, client=other, from_peer=("127.0.0.1", 31002), at=.54)["accepted"], "new owner after disconnect")
    malformed = b'{"protocol":2,"protocol":2}'
    expect(state.receive(malformed, peer, .55)[1] is None, "duplicate JSON keys rejected")
    try:
        loopback_host("0.0.0.0")
        raise AssertionError("non-loopback host accepted")
    except argparse.ArgumentTypeError:
        checks += 1
    return checks


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mock", action="store_true", help="required acknowledgement that all data are simulated")
    parser.add_argument("--host", type=loopback_host, default="127.0.0.1")
    parser.add_argument("--http-port", type=port, default=18091)
    parser.add_argument("--udp-port", type=port, default=15001)
    parser.add_argument("--output", default=".local/experiments/loopback")
    parser.add_argument("--self-test", action="store_true", help="exercise this mock policy and exit without opening sockets")
    parser.add_argument("--duration", type=float, default=0,
                        help="optional automatic shutdown after this many seconds; 0 waits for Ctrl+C")
    args = parser.parse_args(argv)
    if not args.mock:
        parser.error("--mock is required; this tool never operates a physical robot")
    if not math.isfinite(args.duration) or args.duration < 0:
        parser.error("duration must be finite and nonnegative")
    if args.self_test:
        print(json.dumps({"mock_mode": True, "self_test_checks": self_test_mock(),
                          "scope": "loopback_mock_only_no_robot_protocol_conformance"}))
        return 0
    output = Path(args.output) / ("run_" + time.strftime("%Y%m%dT%H%M%S") + "_" + uuid.uuid4().hex[:8])
    output.mkdir(parents=True, exist_ok=False)
    journal = AsyncJournal(output / "records.jsonl")
    runtime = StudyRuntime(demo_config(), journal, mock_mode=True,
        policy=RuntimePolicy(countdown_seconds=.3, signal_ack_timeout_seconds=5,
                             client_timeout_seconds=2, robot_timeout_seconds=2))
    udp = http = None
    stop_event = threading.Event()
    camera = None
    try:
        udp = LoopbackV2Server(args.host, args.udp_port, runtime, journal)
        http = StudyHTTPServer((args.host, args.http_port), runtime)
        http_thread = threading.Thread(target=http.serve_forever, kwargs={"poll_interval": .05},
                                       name="study-loopback-http", daemon=True)
        http_thread.start()
        camera = threading.Thread(target=mock_camera, args=(runtime, udp, stop_event),
                                  name="study-loopback-camera", daemon=True)
        camera.start()
        manifest = {"schema_version": 1, "mock_mode": True,
            "code_version": "tools/study_loopback_bench.py", "config": runtime.config.to_dict(),
            "config_hash": runtime.config.sha256, "clock_id": runtime.clock_id,
            "http_endpoint": "http://%s:%d" % http.server_address,
            "udp_endpoint": "%s:%d" % udp.address,
            "camera_source": "20Hz_generated_detection_results_no_real_image",
            "qr_decoder_verified": False, "physical_motion": False,
            "v2_scope": "simplified_mock_not_robot_protocol_conformance",
            "mock_watchdog_seconds": udp.state.watchdog_seconds,
            "mock_lease_seconds": udp.state.lease_seconds}
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(dict(manifest, ready=True, output=str(output),
                              http_port=http.server_address[1], udp_port=udp.address[1]), ensure_ascii=False), flush=True)
        if args.duration:
            stop_event.wait(args.duration)
        else:
            while not stop_event.wait(.25):
                pass
    except KeyboardInterrupt:
        pass
    finally:
        stop_event.set()
        if camera is not None:
            camera.join(timeout=1)
        if http is not None:
            http.shutdown()
            http.server_close()
        if udp is not None:
            udp.close()
        runtime.close()
        print(json.dumps({"closed": True, "output": str(output), "mock_mode": True,
                          "udp_packets": udp.packet_count if udp else 0,
                          "udp_accepted": udp.accepted_count if udp else 0,
                          "journal": journal.status()}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
