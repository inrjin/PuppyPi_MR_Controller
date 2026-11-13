"""Motor-free demonstration, HTTP service and reproducible exports.

Real ingress accepts one JSON object per stdin line: robot_status, observation,
or adapter_error. It never opens a camera device or a locomotion socket.
"""

import argparse
import hashlib
import json
from pathlib import Path
import random
import subprocess
import sys
import threading
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from .config import demo_config, load_config
from .journal import AsyncJournal
from .runtime import RuntimePolicy, StudyRuntime


def source_identity():
    root = Path(__file__).resolve().parents[1]
    result = {"git_commit": None, "git_tracked_dirty": None,
              "source_sha256": {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in sorted((root / "puppypi_study").glob("*.py"))}}
    try:
        result["git_commit"] = subprocess.run(["git", "rev-parse", "HEAD"], cwd=root,
            check=True, capture_output=True, text=True, timeout=2).stdout.strip()
        result["git_tracked_dirty"] = bool(subprocess.run(
            ["git", "status", "--porcelain", "--untracked-files=no"], cwd=root,
            check=True, capture_output=True, text=True, timeout=2).stdout.strip())
    except (OSError, subprocess.SubprocessError):
        pass
    return result


def _runtime(args, *, mock_mode=False, clock=None):
    config = load_config(args.config) if getattr(args, "config", None) else demo_config()
    run_id = "run_" + time.strftime("%Y%m%dT%H%M%S") + "_" + uuid.uuid4().hex[:8]
    output = Path(args.output) / run_id
    output.mkdir(parents=True, exist_ok=False)
    journal = AsyncJournal(output / "records.jsonl")
    runtime = StudyRuntime(config, journal, mock_mode=mock_mode, clock=clock,
                           policy=RuntimePolicy(countdown_seconds=0 if mock_mode else 3))
    manifest = {"schema_version": 1, "mock_mode": mock_mode,
        "code_version": "puppypi_study/0.1.0", "clock_id": runtime.clock_id,
        "config": config.to_dict(), "config_hash": config.sha256,
        "adapter": "mock" if mock_mode else "stdin_jsonl_unconnected_until_ingress",
        "capture_timestamps_available": False, "physical_stop_measured": False}
    manifest.update(source_identity())
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return runtime, output


def _stdin_ingress(runtime):
    while True:
        line = sys.stdin.readline(65537)
        if not line:
            runtime.adapter_failed("stdin_closed")
            return
        if len(line.encode("utf-8")) > 65536 or not line.endswith("\n"):
            runtime.adapter_failed("ingress_line_exceeds_bound")
            return
        try:
            item = json.loads(line)
            if item.get("kind") == "robot_status":
                runtime.ingest_robot_status(item["status"])
            elif item.get("kind") == "observation":
                runtime.ingest_observation(item["observation"], item.get("neutral_axes"))
            elif item.get("kind") == "adapter_error":
                runtime.adapter_failed(item.get("reason", "adapter_error"))
            elif item.get("kind") == "control_event":
                runtime.ingest_control_event(item["event_type"], item["payload"])
            else:
                runtime.adapter_failed("unknown_ingress_kind")
        except (ValueError, TypeError, KeyError) as error:
            runtime.adapter_failed("invalid_ingress:" + type(error).__name__)


def serve(args):
    from .server import StudyHTTPServer
    runtime, output = _runtime(args, mock_mode=args.mock)
    server = None
    try:
        server = StudyHTTPServer((args.host, args.port), runtime)
        if args.stdin_ingress:
            threading.Thread(target=_stdin_ingress, args=(runtime,), daemon=True).start()
        print(json.dumps({"endpoint": "http://%s:%d" % server.server_address,
                          "clock_id": runtime.clock_id,
                          "mock_mode": args.mock, "output": str(output)}, ensure_ascii=False), flush=True)
        server.serve_forever(poll_interval=.1)
    except KeyboardInterrupt:
        pass
    finally:
        if server is not None:
            server.server_close()
        runtime.close()


def demo(args):
    now = [1_000_000]
    runtime, output = _runtime(args, mock_mode=True, clock=lambda: now[0])
    client_id = str(uuid.uuid4())
    session = runtime.create_session({"request_id": "demo-session", "participant_id": "DEMO_ONLY",
                                      "interface_order": ["mr_ballgrip"]})
    heartbeat = {"schema_version": 1, "client_id": client_id, "interface_id": "mr_ballgrip",
        "source_clock_id": "demo-client", "source_t_mono_us": now[0], "engaged": False,
        "input_valid": True, "neutral_confirmed": True, "control_connected": True,
        "last_accepted": True, "emergency": False, "settings": {"demo_only": True}}
    runtime.register_client(heartbeat)
    base = {"experiment_session_id": session["experiment_session_id"], "trial_id": "demo-trial",
            "control_client_id": client_id}
    started = runtime.start_trial(dict(base, request_id="demo-start", interface_id="mr_ballgrip",
                                      period_index=1, difficulty_id="demo-wide", practice=True))
    runtime.client_events(dict(base, request_id="demo-clock", events=[{
        "event_id": "demo-clock", "event_type": "clock_sync", "source_clock_id": "demo-client",
        "source_t_mono_us": now[0], "payload": {"offset_us": 0, "uncertainty_us": 0,
            "valid": True, "reference_clock_id": runtime.clock_id, "source_clock_id": "demo-client",
            "mock_alignment": True}}]))
    runtime.client_events(dict(base, request_id="demo-signal", events=[{
        "event_id": "demo-shown", "event_type": "signal_shown", "source_clock_id": "demo-client",
        "source_t_mono_us": now[0], "payload": {"signal_id": started["signal_id"]}}]))
    runtime.client_events(dict(base, request_id="demo-input", events=[{
        "event_id": "demo-input", "event_type": "input_sample", "source_clock_id": "demo-client",
        "source_t_mono_us": now[0], "payload": {"normalized_forward": .1, "normalized_turn": 0,
            "engaged": True, "input_valid": True, "blocked": False, "block_reasons": []}}]))
    for index in range(1, 20):
        now[0] += 100000
        heartbeat["source_t_mono_us"] = now[0]
        runtime.register_client(heartbeat)
        runtime.client_events(dict(base, request_id="demo-input-" + str(index), events=[{
            "event_id": "demo-input-" + str(index), "event_type": "input_sample",
            "source_clock_id": "demo-client", "source_t_mono_us": now[0],
            "payload": {"normalized_forward": .1, "normalized_turn": 0,
                "engaged": True, "input_valid": True, "blocked": False, "block_reasons": []}}]))
        runtime.ingest_observation({"schema_version": 1, "frame_id": index,
            "clock_id": runtime.clock_id, "received_mono_us": now[0], "decoded_mono_us": now[0],
            "captured_mono_us": None, "capture_clock_id": None, "image_width": 640,
            "image_height": 480, "marker_text": runtime.config.framing.target_marker,
            "corners": [[270,190],[370,190],[370,290],[270,290]], "data_valid": True,
            "invalid_reason": None, "decoder": "mock_fixture"}, neutral_axes=(0, 0))
        if runtime.status()["state"] in {"success", "timeout", "aborted"}:
            break
    result = runtime.status()
    complete = runtime.close()
    print(json.dumps({"mock_mode": True, "outcome": result["outcome"], "output": str(output),
                      "logging_complete": complete}, indent=2))
    return 0 if result["outcome"] == "success" and complete else 1


def api_request(endpoint, route, body=None):
    if not endpoint.startswith("http://") or len(endpoint) > 512:
        raise ValueError("use an explicit http:// study endpoint")
    encoded = None if body is None else json.dumps(body, allow_nan=False).encode("utf-8")
    request = Request(endpoint.rstrip("/") + route, data=encoded,
                      headers={"Content-Type": "application/json"},
                      method="GET" if body is None else "POST")
    try:
        with urlopen(request, timeout=3) as response:
            raw = response.read(262145)
            if len(raw) > 262144:
                raise ValueError("response exceeds maximum size")
            return json.loads(raw)
    except HTTPError as error:
        raise ValueError("study HTTP %s: %s" % (error.code, error.read(1024).decode("utf-8", errors="replace")))
    except URLError as error:
        raise ValueError("study service unavailable: " + str(error.reason))


def operate(args):
    if args.command == "session":
        order = args.interface_order.split(",")
        seed = args.assignment_seed
        if args.randomize_order:
            if seed is None:
                seed = uuid.uuid4().int & ((1 << 53) - 1)
            random.Random(seed).shuffle(order)
        return api_request(args.endpoint, "/v1/session", {"schema_version": 1,
            "request_id": args.request_id or str(uuid.uuid4()), "participant_id": args.participant_id,
            "interface_order": order, "metadata": {"assignment_seed": seed,
                "assignment_method": "seeded_shuffle" if args.randomize_order else "operator_selected"}})
    status = api_request(args.endpoint, "/v1/status")
    if args.command == "status":
        return status
    if args.command == "abort":
        if not status.get("trial_id"):
            raise ValueError("no trial is selected")
        return api_request(args.endpoint, "/v1/trials/abort", {"schema_version": 1,
            "request_id": args.request_id or str(uuid.uuid4()),
            "experiment_session_id": status["experiment_session_id"], "trial_id": status["trial_id"],
            "control_client_id": status["control_client_id"], "reason": args.reason})
    if not status.get("experiment_session_id"):
        raise ValueError("create a session before starting a trial")
    client = args.client_id
    if client is None:
        ready = [item["client_id"] for item in status.get("clients", []) if item.get("ready")]
        if len(ready) != 1:
            raise ValueError("specify --client-id; expected exactly one ready registered client")
        client = ready[0]
    if args.command == "next" and status.get("trial_id") and status["state"] not in {"success", "timeout", "aborted"}:
        raise ValueError("finish/abort the current trial before next")
    order = status.get("interface_order", ["mr_ballgrip"])
    period = args.period_index or order.index("mr_ballgrip") + 1
    body = {"schema_version": 1, "request_id": args.request_id or str(uuid.uuid4()),
        "experiment_session_id": status["experiment_session_id"],
        "trial_id": args.trial_id or "trial_" + uuid.uuid4().hex,
        "control_client_id": client, "interface_id": "mr_ballgrip", "period_index": period,
        "difficulty_id": args.difficulty, "practice": args.practice,
        "retry_of_trial_id": args.retry_of_trial_id}
    if getattr(args, "retry", False):
        if not status.get("trial_id"):
            raise ValueError("no earlier trial to retry")
        body["retry_of_trial_id"] = status["trial_id"]
    if args.settings:
        body["settings"] = json.loads(Path(args.settings).read_text(encoding="utf-8"))
    return api_request(args.endpoint, "/v1/trials/start", body)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Opt-in MR study service; no device control on import")
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("demo", "serve"):
        command = commands.add_parser(name)
        command.add_argument("--config")
        command.add_argument("--output", default=".local/experiments")
    serving = commands.choices["serve"]
    serving.add_argument("--host", default="127.0.0.1")
    serving.add_argument("--port", type=int, default=8091)
    serving.add_argument("--mock", action="store_true", help="explicit simulated readiness; never a robot validation")
    serving.add_argument("--stdin-ingress", action="store_true", help="read trusted robot-adapter JSONL from stdin")
    for name in ("status", "session", "start", "abort", "next"):
        operation = commands.add_parser(name)
        operation.add_argument("--endpoint", default="http://127.0.0.1:8091")
        operation.add_argument("--request-id", help="reuse for an identical retry after response loss")
        if name == "session":
            operation.add_argument("--participant-id", required=True)
            operation.add_argument("--interface-order", default="mr_ballgrip")
            operation.add_argument("--assignment-seed", type=int)
            operation.add_argument("--randomize-order", action="store_true")
        elif name in {"start", "next"}:
            operation.add_argument("--client-id", help="actual v2 UUID; defaults to the sole ready client")
            operation.add_argument("--trial-id")
            operation.add_argument("--period-index", type=int)
            operation.add_argument("--difficulty", default="demo-wide")
            operation.add_argument("--practice", action="store_true")
            operation.add_argument("--retry-of-trial-id")
            operation.add_argument("--settings", help="JSON snapshot already applied and reported by the MR client")
            if name == "next":
                operation.add_argument("--retry", action="store_true", help="preserve link to the previous trial")
        elif name == "abort":
            operation.add_argument("--reason", default="operator_abort")
    exporting = commands.add_parser("export")
    exporting.add_argument("log")
    exporting.add_argument("--csv", required=True)
    exporting.add_argument("--manifest")
    exporting.add_argument("--analysis-config")
    replaying = commands.add_parser("replay")
    replaying.add_argument("log")
    replaying.add_argument("--manifest")
    args = parser.parse_args(argv)
    if args.command in {"status", "session", "start", "abort", "next"}:
        try:
            print(json.dumps(operate(args), ensure_ascii=False, indent=2))
            return 0
        except (ValueError, OSError) as error:
            print(str(error), file=sys.stderr)
            return 2
    if args.command == "serve":
        serve(args)
        return 0
    if args.command == "demo":
        return demo(args)
    if args.command == "export":
        from .analysis import export_summary
        analysis_config = json.loads(Path(args.analysis_config).read_text()) if args.analysis_config else None
        rows = export_summary(args.log, args.csv, args.manifest, analysis_config=analysis_config)
        print(json.dumps({"trials": len(rows), "csv": args.csv}))
        return 0
    from .analysis import load_records, verify_framing_replay
    data = load_records(args.log)
    manifest = json.loads(Path(args.manifest).read_text()) if args.manifest else None
    report = verify_framing_replay(data, manifest)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report.get("matched", report.get("valid", False)) else 1


if __name__ == "__main__":
    raise SystemExit(main())
