"""Reproducible trial summaries from versioned study JSONL records.

All intervals use identified monotonic clocks.  A client timestamp is mapped
to the recorder clock only when the log explicitly supplies a valid alignment
and its uncertainty.  Missing intervals stay empty in CSV, including the
completion time of an unsuccessful trial.
"""

from __future__ import annotations

import csv
import json
import math
import statistics
from collections import defaultdict
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping


INTERFACE_IDS = frozenset(("smartphone_touch", "mr_ballgrip"))
OUTCOMES = frozenset(("success", "timeout", "aborted"))


@dataclass(frozen=True)
class AnalysisConfig:
    """Pilot analysis parameters, independent of either input device's units."""

    version: str = "demo-analysis-v1"
    sample_period_s: float = 0.05
    deadzone: float = 0.05
    hysteresis: float = 0.02
    min_hold_s: float = 0.1
    max_sample_gap_s: float = 0.25
    max_clock_error_us: int = 50_000
    first_input_policy: str = "nonzero_engaged"
    command_source: str = "command_sent"

    def __post_init__(self) -> None:
        for name in ("sample_period_s", "deadzone", "hysteresis", "min_hold_s",
                     "max_sample_gap_s"):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value):
                raise ValueError(f"{name} must be finite")
        if not 0 < self.sample_period_s <= self.max_sample_gap_s:
            raise ValueError("sample_period_s must be positive and <= max_sample_gap_s")
        if not 0 <= self.hysteresis <= self.deadzone < 1:
            raise ValueError("require 0 <= hysteresis <= deadzone < 1")
        if self.deadzone + self.hysteresis >= 1 or self.min_hold_s < 0:
            raise ValueError("invalid direction hysteresis or hold duration")
        if isinstance(self.max_clock_error_us, bool) or not isinstance(self.max_clock_error_us, int) or self.max_clock_error_us < 0:
            raise ValueError("max_clock_error_us must be a nonnegative integer")
        if self.first_input_policy != "nonzero_engaged":
            raise ValueError("first_input_policy is fixed to the confirmed nonzero_engaged definition")
        if self.command_source not in ("command_sent", "robot_command_accepted"):
            raise ValueError("command_source must identify one common command stage")
        if not self.version:
            raise ValueError("analysis version is required")

    @classmethod
    def from_dict(cls, values: Mapping[str, Any] | None) -> "AnalysisConfig":
        return cls(**dict(values or {}))


@dataclass
class LogData:
    records: list[dict[str, Any]]
    quality_flags: list[str] = field(default_factory=list)
    invalid_lines: int = 0
    source_path: str | None = None


@dataclass(frozen=True)
class _Time:
    clock: str
    value: int
    uncertainty_us: float = 0


def _integer(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _finite(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _reject_constant(value: str) -> None:
    raise ValueError(f"non-finite JSON constant {value}")


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key {key}")
        result[key] = value
    return result


def load_records(path: str | Path) -> LogData:
    """Read usable records while reporting malformed or truncated JSONL lines."""
    raw = Path(path).read_bytes()
    records: list[dict[str, Any]] = []
    flags: set[str] = set()
    invalid = 0
    lines = raw.splitlines(keepends=True)
    for index, line in enumerate(lines):
        if not line.strip():
            continue
        try:
            item = json.loads(line.decode("utf-8"), parse_constant=_reject_constant,
                              object_pairs_hook=_unique_object)
            if not isinstance(item, dict):
                raise ValueError("record is not an object")
        except (UnicodeDecodeError, ValueError):
            invalid += 1
            flags.add("invalid_jsonl_record")
            if index == len(lines) - 1 and not line.endswith((b"\n", b"\r")):
                flags.add("truncated_final_record")
            continue
        records.append(item)
    if raw and not raw.endswith((b"\n", b"\r")):
        flags.add("missing_final_newline")
    return LogData(records, sorted(flags), invalid, str(path))


def _payload(record: Mapping[str, Any]) -> Mapping[str, Any]:
    value = record.get("payload", {})
    return value if isinstance(value, dict) else {}


def _time(record: Mapping[str, Any], config: AnalysisConfig, flags: set[str]) -> _Time | None:
    payload = _payload(record)
    source_value = payload.get("source_t_mono_us")
    source_clock = payload.get("source_clock_id")
    # Unity's typed payload also has a source_clock_id field for clock-sync
    # metadata. Only recorder-enriched payloads contain source_t_mono_us; local
    # Unity records already identify their actual timestamp in the header.
    if "source_t_mono_us" in payload:
        if not _integer(source_value) or source_value < 0 or not isinstance(source_clock, str) or not source_clock:
            flags.add("invalid_source_timestamp")
            return None
        aligned = payload.get("aligned_t_mono_us")
        error = payload.get("clock_alignment_error_us")
        aligned_clock = payload.get("aligned_clock_id", record.get("clock_id"))
        if payload.get("clock_alignment_valid") is True and _integer(aligned) and aligned >= 0 and _finite(error) and 0 <= error <= config.max_clock_error_us and isinstance(aligned_clock, str) and aligned_clock:
            return _Time(aligned_clock, aligned, error)
        flags.add("client_clock_unaligned")
        return _Time(source_clock, source_value)
    value, clock = record.get("t_mono_us"), record.get("clock_id")
    if not _integer(value) or value < 0 or not isinstance(clock, str) or not clock:
        flags.add("invalid_timestamp")
        return None
    return _Time(clock, value)


def _duration(start: _Time | None, end: _Time | None, flags: set[str], metric: str) -> float | None:
    if start is None or end is None:
        return None
    if start.clock != end.clock:
        flags.add(f"clock_mismatch:{metric}")
        return None
    if end.value < start.value:
        flags.add(f"time_reversal:{metric}")
        return None
    return (end.value - start.value) / 1_000_000


def _union(intervals: Iterable[tuple[int, int]]) -> list[tuple[int, int]]:
    result: list[tuple[int, int]] = []
    for start, end in sorted((a, b) for a, b in intervals if b > a):
        if result and start <= result[-1][1]:
            result[-1] = (result[-1][0], max(end, result[-1][1]))
        else:
            result.append((start, end))
    return result


def _intersect(first: Iterable[tuple[int, int]], second: Iterable[tuple[int, int]]) -> list[tuple[int, int]]:
    left, right = _union(first), _union(second)
    result = []
    i = j = 0
    while i < len(left) and j < len(right):
        start, end = max(left[i][0], right[j][0]), min(left[i][1], right[j][1])
        if end > start:
            result.append((start, end))
        if left[i][1] < right[j][1]:
            i += 1
        else:
            j += 1
    return result


def _seconds(intervals: Iterable[tuple[int, int]]) -> float:
    return sum(b - a for a, b in _union(intervals)) / 1_000_000


def _percentile(values: list[float], quantile: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    low = math.floor(position)
    high = math.ceil(position)
    return ordered[low] + (ordered[high] - ordered[low]) * (position - low)


def _axes(payload: Mapping[str, Any], normalized: bool = False) -> tuple[float, float] | None:
    prefix = "normalized_" if normalized else ""
    forward, turn = payload.get(prefix + "forward"), payload.get(prefix + "turn")
    if not _finite(forward) or not _finite(turn) or abs(forward) > 1 or abs(turn) > 1:
        return None
    return float(forward), float(turn)


def _valid_input(payload: Mapping[str, Any], config: AnalysisConfig, nonzero: bool) -> bool:
    axes = _axes(payload, normalized=True)
    if payload.get("engaged") is not True or payload.get("input_valid") is not True or axes is None:
        return False
    return not nonzero or max(abs(axes[0]), abs(axes[1])) > config.deadzone


def count_direction_reversals(samples: Iterable[tuple[int, float]], config: AnalysisConfig | None = None) -> int:
    """Count sustained sign changes after common zero-order 20 Hz resampling.

    Values are shared normalized *commands*, not touch events or render frames.
    A gap longer than max_sample_gap_s breaks the episode. A neutral interval
    longer than min_hold_s clears its previous direction, so ordinary release
    and a later new engagement are not counted as a reversal.
    """
    config = config or AnalysisConfig()
    raw = list(samples)
    if not raw:
        return 0
    period = max(1, round(config.sample_period_s * 1_000_000))
    gap = round(config.max_sample_gap_s * 1_000_000)
    hold = round(config.min_hold_s * 1_000_000)
    if any(not _integer(t) or not _finite(v) or abs(v) > 1 for t, v in raw):
        raise ValueError("direction samples require integer monotonic microseconds and normalized commands")
    raw.sort()
    # Duplicate timestamps with contradictory commands cannot be ordered reliably.
    if any(raw[i][0] == raw[i - 1][0] and raw[i][1] != raw[i - 1][1] for i in range(1, len(raw))):
        raise ValueError("conflicting command timestamps")
    segments: list[list[tuple[int, float]]] = []
    for sample in raw:
        if not segments or sample[0] - segments[-1][-1][0] > gap:
            segments.append([])
        segments[-1].append(sample)
    result = 0
    for segment in segments:
        grid_start = ((segment[0][0] + period - 1) // period) * period
        index = 0
        stable = candidate = 0
        candidate_since = neutral_since = None
        for t in range(grid_start, segment[-1][0] + 1, period):
            while index + 1 < len(segment) and segment[index + 1][0] <= t:
                index += 1
            _, value = segment[index]
            enter, exit_limit = config.deadzone + config.hysteresis, config.deadzone - config.hysteresis
            if value > enter:
                sign = 1
            elif value < -enter:
                sign = -1
            elif abs(value) <= exit_limit:
                sign = 0
            else:
                sign = candidate if candidate else stable
            if sign == 0:
                candidate = 0
                candidate_since = None
                if neutral_since is None:
                    neutral_since = t
                if t - neutral_since >= hold:
                    stable = 0
                continue
            neutral_since = None
            if sign != candidate:
                candidate, candidate_since = sign, t
            if candidate_since is not None and t - candidate_since >= hold:
                if stable and candidate != stable:
                    result += 1
                stable = candidate
    return result


def _blocking(records: list[dict[str, Any]], start: _Time | None, end: _Time | None,
              config: AnalysisConfig, flags: set[str]) -> dict[str, Any]:
    empty = {"control_demand_s": None, "blocked_demand_s": None,
             "blocked_by_reason_json": "{}", "reentry_count": 0,
             "reentry_total_s": None, "reentry_mean_s": None, "reentry_unfinished_count": 0}
    if start is None or end is None or start.clock != end.clock:
        flags.add("blocking_interval_unavailable")
        return empty
    stop = end.value
    samples = []
    explicit: dict[str, list[tuple[int, int]]] = defaultdict(list)
    open_reasons: dict[str, int] = {}
    timed = []
    for record in records:
        kind, payload = record.get("event_type"), _payload(record)
        if kind not in ("input_sample", "block_started", "block_ended"):
            continue
        point = _time(record, config, flags)
        if point is None or point.clock != start.clock:
            flags.add("unaligned_block_or_input_sample")
            continue
        if point.value < start.value or point.value > stop or payload.get("late_after_terminal") is True:
            continue
        timed.append((point.value, record))
    for t, record in sorted(timed, key=lambda item: item[0]):
        kind, payload = record.get("event_type"), _payload(record)
        if kind == "input_sample":
            samples.append((t, payload))
        else:
            reason = str(payload.get("reason", "unspecified"))
            if kind == "block_started":
                if reason in open_reasons:
                    flags.add("duplicate_block_start")
                else:
                    open_reasons[reason] = t
            elif reason in open_reasons:
                explicit[reason].append((open_reasons.pop(reason), t))
            else:
                flags.add("unmatched_block_end")
    for reason, opened in open_reasons.items():
        explicit[reason].append((opened, stop))
    samples.sort(key=lambda pair: pair[0])
    if not samples:
        flags.add("missing_input_samples")
        return empty
    demand = []
    valid_inputs = []
    sampled_blocks: dict[str, list[tuple[int, int]]] = defaultdict(list)
    maximum_gap = round(config.max_sample_gap_s * 1_000_000)
    for index, (t, payload) in enumerate(samples):
        next_t = samples[index + 1][0] if index + 1 < len(samples) else stop
        interval_end = min(next_t, t + maximum_gap, stop)
        if next_t - t > maximum_gap:
            flags.add("input_sample_gap")
        demanded = payload.get("demanded")
        if not isinstance(demanded, bool):
            demanded = payload.get("engaged") is True
            flags.add("demand_inferred_from_engagement")
        if demanded:
            demand.append((t, interval_end))
        if _valid_input(payload, config, True) and payload.get("blocked") is not True:
            valid_inputs.append(t)
        reasons = payload.get("block_reasons", [])
        if payload.get("blocked") is True:
            if not isinstance(reasons, list) or not reasons:
                reasons = ["unspecified"]
            for reason in reasons:
                sampled_blocks[str(reason)].append((t, interval_end))
    reasons = set(explicit) | set(sampled_blocks)
    raw_blocks = {reason: explicit.get(reason, []) + sampled_blocks.get(reason, []) for reason in reasons}
    by_reason = {reason: _intersect(raw_blocks[reason], demand)
                 for reason in reasons}
    blocked = _union(interval for intervals in by_reason.values() for interval in intervals)
    system_episodes = _union(interval for intervals in raw_blocks.values() for interval in intervals)
    reentries = []
    unfinished = 0
    for episode in system_episodes:
        if not _intersect([episode], demand):
            continue
        released = episode[1]
        if released >= stop:
            unfinished += 1
            continue
        candidates = [t for t in valid_inputs if t >= released and not any(a <= t < b for a, b in system_episodes)]
        if candidates:
            reentries.append((min(candidates) - released) / 1_000_000)
        else:
            unfinished += 1
    return {"control_demand_s": _seconds(demand), "blocked_demand_s": _seconds(blocked),
            "blocked_by_reason_json": json.dumps({reason: _seconds(intervals) for reason, intervals in sorted(by_reason.items())}, sort_keys=True),
            "reentry_count": len(reentries), "reentry_total_s": sum(reentries) if reentries else None,
            "reentry_mean_s": statistics.mean(reentries) if reentries else None,
            "reentry_unfinished_count": unfinished}


def _frame_metrics(records: list[dict[str, Any]], flags: set[str]) -> dict[str, Any]:
    observations = []
    for record in records:
        if record.get("event_type") != "detection_sample":
            continue
        payload = _payload(record)
        observation = payload.get("observation", payload)
        if isinstance(observation, dict):
            observations.append(observation)
    seen: set[tuple[str, int]] = set()
    ids_by_clock: dict[str, list[int]] = defaultdict(list)
    times_by_clock: dict[str, list[int]] = defaultdict(list)
    delay_ms = []
    processing_ms = []
    duplicate = invalid = 0
    capture_known = 0
    for observation in observations:
        clock, frame_id = observation.get("clock_id"), observation.get("frame_id")
        received, decoded = observation.get("received_mono_us"), observation.get("decoded_mono_us")
        if not isinstance(clock, str) or not _integer(frame_id) or frame_id < 0 or not _integer(received) or not _integer(decoded) or decoded < received:
            flags.add("invalid_frame_timestamps")
            invalid += 1
            continue
        key = (clock, frame_id)
        if key in seen:
            duplicate += 1
        else:
            seen.add(key)
            ids_by_clock[clock].append(frame_id)
            times_by_clock[clock].append(received)
        delay_ms.append((decoded - received) / 1000)
        processing = observation.get("processing_duration_us")
        if _finite(processing) and processing >= 0:
            processing_ms.append(processing / 1000)
        if observation.get("data_valid") is False:
            invalid += 1
        capture = observation.get("captured_mono_us")
        if capture is None:
            flags.add("capture_timestamp_missing")
        elif observation.get("capture_clock_id") == clock and _integer(capture) and capture <= received:
            capture_known += 1
        else:
            flags.add("capture_clock_unaligned")
    intervals_ms = []
    frame_gaps = 0
    for clock, ids in ids_by_clock.items():
        for left, right in zip(ids, ids[1:]):
            if right <= left:
                flags.add("frame_id_reversal")
            else:
                frame_gaps += max(0, right - left - 1)
        times = times_by_clock[clock]
        for left, right in zip(times, times[1:]):
            if right <= left:
                flags.add("frame_time_reversal")
            else:
                intervals_ms.append((right - left) / 1000)
    return {"detection_samples": len(observations), "unique_detection_frames": len(seen),
            "duplicate_detection_frames": duplicate, "invalid_detection_frames": invalid,
            "frame_id_gap_count": frame_gaps, "capture_timestamp_known_frames": capture_known,
            "frame_update_interval_mean_ms": statistics.mean(intervals_ms) if intervals_ms else None,
            "decode_latency_mean_ms": statistics.mean(delay_ms) if delay_ms else None,
            "decode_latency_p95_ms": _percentile(delay_ms, 0.95),
            "processing_duration_mean_ms": statistics.mean(processing_ms) if processing_ms else None,
            "processing_duration_p95_ms": _percentile(processing_ms, 0.95)}


def summarize_records(records: Iterable[dict[str, Any]] | LogData,
                      manifest: Mapping[str, Any] | None = None,
                      analysis_config: AnalysisConfig | Mapping[str, Any] | None = None) -> list[dict[str, Any]]:
    """Produce one row per session/trial, preserving failed and incomplete trials."""
    manifest = manifest or {}
    config = analysis_config if isinstance(analysis_config, AnalysisConfig) else AnalysisConfig.from_dict(analysis_config or manifest.get("analysis"))
    base_flags = set(records.quality_flags if isinstance(records, LogData) else ())
    invalid_lines = records.invalid_lines if isinstance(records, LogData) else 0
    raw_records = list(records.records if isinstance(records, LogData) else records)
    unique = []
    seen_ids: dict[str, dict[str, Any]] = {}
    for record in raw_records:
        if not isinstance(record, dict) or isinstance(record.get("schema_version"), bool) or record.get("schema_version") != 1:
            base_flags.add("unsupported_record_schema")
            continue
        event_id = record.get("event_id")
        if not isinstance(event_id, str) or not event_id:
            base_flags.add("missing_event_id")
        elif event_id in seen_ids:
            if record != seen_ids[event_id]:
                base_flags.add("conflicting_event_id")
            continue
        else:
            seen_ids[event_id] = record
        unique.append(record)
    logging_started = any(r.get("event_type") == "logging_started" for r in unique)
    logging_closed = any(r.get("event_type") == "logging_closed" for r in unique)
    if not logging_started:
        base_flags.add("logging_start_missing")
    if not logging_closed:
        base_flags.add("logging_close_missing")
    dropped = 0
    dropped_total = 0
    for record in unique:
        payload = _payload(record)
        if record.get("event_type") == "logging_overflow":
            count = payload.get("dropped_since_last", payload.get("dropped_records", payload.get("dropped_count", payload.get("count", 0))))
            if _integer(count):
                dropped += max(0, count)
            base_flags.add("logging_overflow")
        total = payload.get("dropped_total", 0)
        if _integer(total):
            dropped_total = max(dropped_total, total)
        if record.get("event_type") == "logging_error":
            base_flags.add("logging_error")
        if record.get("event_type") == "logging_closed" and payload.get("complete") is False:
            base_flags.add("logging_incomplete")
    dropped = max(dropped, dropped_total)
    groups: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for record in unique:
        trial_id = record.get("trial_id")
        if isinstance(trial_id, str) and trial_id:
            groups[(str(record.get("experiment_session_id", "")), trial_id)].append(record)
    rows = []
    for (session_id, trial_id), trial in groups.items():
        flags = set(base_flags)
        first = trial[0]
        prepared = next((r for r in trial if r.get("event_type") == "trial_prepared"), first)
        started = next((r for r in trial if r.get("event_type") == "trial_started"), None)
        terminal = [r for r in trial if r.get("event_type") == "trial_ended"]
        if not terminal:
            terminal = [r for r in trial if r.get("event_type") in ("trial_success", "trial_timeout", "trial_aborted")]
        ended = terminal[0] if terminal else None
        terminal_outcomes = {_payload(r).get("outcome", str(r.get("event_type", "")).removeprefix("trial_")) for r in terminal}
        if len(terminal_outcomes) > 1:
            flags.add("conflicting_trial_outcomes")
        outcome = str(_payload(ended).get("outcome", ended.get("event_type", "").removeprefix("trial_"))) if ended else ""
        if outcome not in OUTCOMES:
            flags.add("terminal_outcome_missing")
            outcome = ""
        info = dict(_payload(prepared))
        if started:
            info.update(_payload(started))
        interface = first.get("interface_id", info.get("interface_id", ""))
        if interface not in INTERFACE_IDS:
            flags.add("invalid_interface_id")
        for name in ("participant_id", "interface_id", "difficulty_id", "period_index"):
            values = {str(r.get(name)) for r in trial if r.get(name) is not None}
            if len(values) > 1:
                flags.add(f"inconsistent_identifier:{name}")
        order = info.get("interface_order", manifest.get("interface_order", []))
        period = first.get("period_index", info.get("period_index", ""))
        if not isinstance(order, list) or any(value not in INTERFACE_IDS for value in order):
            flags.add("invalid_interface_order")
            order = []
        if order and (not _integer(period) or not 1 <= period <= len(order) or order[period - 1] != interface):
            flags.add("period_order_mismatch")
        signal = next((r for r in trial if r.get("event_type") == "signal_shown"), None)
        signal_time = _time(signal, config, flags) if signal else None
        start_time = _time(started, config, flags) if started else None
        end_time = _time(ended, config, flags) if ended else None
        if not signal:
            flags.add("client_signal_missing")
        if not started:
            flags.add("trial_start_missing")
        def eligible_input(record):
            payload = _payload(record)
            if record.get("event_type") != "input_sample" or payload.get("late_after_terminal") is True or not _valid_input(payload, config, False):
                return False
            point = _time(record, config, flags)
            if point is None:
                return False
            beginning = signal_time or start_time
            if beginning and beginning.clock == point.clock and point.value < beginning.value:
                return False
            return not (end_time and end_time.clock == point.clock and point.value > end_time.value)
        valid_engaged = [r for r in trial if eligible_input(r)]
        valid_engaged.sort(key=lambda r: (_time(r, config, flags).value))
        nonzero = [r for r in valid_engaged if _valid_input(_payload(r), config, True)]
        first_engaged = _time(valid_engaged[0], config, flags) if valid_engaged else None
        first_nonzero = _time(nonzero[0], config, flags) if nonzero else None
        chosen_input = first_nonzero
        decode = next((r for r in trial if r.get("event_type") == "first_marker_decoded"), None)
        framing = next((r for r in trial if r.get("event_type") == "framing_entered"), None)
        decode_time = _time(decode, config, flags) if decode else None
        framing_time = _time(framing, config, flags) if framing else None
        snapshot = info.get("config_snapshot", manifest.get("config", {}))
        if not isinstance(snapshot, dict):
            snapshot = {}
            flags.add("config_snapshot_missing")
        success_time = end_time if outcome == "success" else None
        row = {"experiment_session_id": session_id, "trial_id": trial_id,
               "participant_id": first.get("participant_id", info.get("participant_id", "")),
               "interface_id": interface, "difficulty_id": first.get("difficulty_id", info.get("difficulty_id", "")),
               "period_index": period, "interface_order": json.dumps(order, ensure_ascii=False),
               "practice": info.get("practice", ""), "retry_of_trial_id": info.get("retry_of_trial_id", ""),
               "assignment_seed": info.get("assignment_seed", manifest.get("assignment_seed", "")),
               "assignment_method": info.get("assignment_method", manifest.get("assignment_method", "")),
               "control_client_id": info.get("control_client_id", ""),
               "code_version": manifest.get("code_version", ""),
               "config_version": info.get("config_version", snapshot.get("version", snapshot.get("config_version", ""))),
               "config_hash": info.get("config_hash", manifest.get("config_hash", "")),
               "outcome": outcome, "reason": _payload(ended).get("reason", "") if ended else "",
               "success": outcome == "success", "first_input_policy": config.first_input_policy,
               "signal_to_first_valid_input_s": _duration(signal_time, chosen_input, flags, "signal_to_first_valid_input"),
               "signal_to_first_engaged_valid_s": _duration(signal_time, first_engaged, flags, "signal_to_first_engaged_valid"),
               "signal_to_first_nonzero_input_s": _duration(signal_time, first_nonzero, flags, "signal_to_first_nonzero_input"),
               "signal_to_success_s": _duration(signal_time, success_time, flags, "signal_to_success"),
               "first_valid_decode_to_success_s": _duration(decode_time, success_time, flags, "first_valid_decode_to_success"),
               "first_framing_entry_to_success_s": _duration(framing_time, success_time, flags, "first_framing_entry_to_success"),
               "observed_duration_s": _duration(start_time, end_time, flags, "observed_duration"),
               "signal_to_end_s": _duration(signal_time, end_time, flags, "signal_to_end"),
               "framing_reexits": sum(r.get("event_type") == "framing_exited" for r in trial),
               "hold_resets": sum(r.get("event_type") == "framing_hold_reset" for r in trial),
               "first_decode_frame_id": _payload(decode).get("frame_id", "") if decode else "",
               "first_framing_frame_id": _payload(framing).get("frame_id", "") if framing else "",
               "terminal_frame_id": _payload(ended).get("frame_id", "") if ended else "",
               "analysis_version": config.version,
               "analysis_config_json": json.dumps(asdict(config), sort_keys=True),
               "invalid_jsonl_lines": invalid_lines, "dropped_log_records": dropped,
               "logging_closed": logging_closed}
        row.update(_blocking(trial, signal_time or start_time, end_time, config, flags))
        commands = [r for r in trial if r.get("event_type") == config.command_source]
        command_clock = (start_time or signal_time).clock if start_time or signal_time else None
        command_samples = []
        sequence_ids = set()
        for command in commands:
            payload = _payload(command)
            if payload.get("late_after_terminal") is True:
                continue
            axes = _axes(payload)
            point = _time(command, config, flags)
            sequence = payload.get("sequence")
            identity = (payload.get("control_client_id", payload.get("client_id")), sequence)
            if sequence is not None and identity in sequence_ids:
                flags.add("duplicate_command_sequence")
                continue
            sequence_ids.add(identity)
            if axes is None or point is None or point.clock != command_clock:
                flags.add("command_series_unavailable")
                continue
            if start_time and point.value < start_time.value or end_time and point.value > end_time.value:
                continue
            command_samples.append((point.value, axes))
        row["command_source"] = config.command_source
        row["command_samples"] = len(command_samples)
        for axis, index in (("forward", 0), ("turn", 1)):
            try:
                row[f"direction_reversals_{axis}"] = count_direction_reversals(((t, axes[index]) for t, axes in command_samples), config) if command_samples else None
            except ValueError:
                row[f"direction_reversals_{axis}"] = None
                flags.add("conflicting_command_timestamps")
        row.update(_frame_metrics(trial, flags))
        row["signal_alignment_error_us"] = signal_time.uncertainty_us if signal_time else None
        row["clock_alignment_valid"] = not any(flag.startswith(("clock_mismatch:", "client_clock_unaligned", "invalid_source_timestamp", "invalid_timestamp", "unaligned_")) for flag in flags)
        row["quality_status"] = "complete" if not flags else "limited"
        row["quality_flags"] = json.dumps(sorted(flags), ensure_ascii=False)
        rows.append(row)
    return rows


def export_summary(log_path: str | Path, csv_path: str | Path,
                   manifest_path: str | Path | None = None,
                   analysis_config: AnalysisConfig | Mapping[str, Any] | None = None) -> list[dict[str, Any]]:
    """Write all trial outcomes; no significance tests or success-only filtering."""
    manifest = json.loads(Path(manifest_path).read_text(encoding="utf-8")) if manifest_path else None
    rows = summarize_records(load_records(log_path), manifest, analysis_config)
    destination = Path(csv_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8", newline="") as stream:
        fields = list(rows[0]) if rows else ["experiment_session_id", "trial_id", "outcome", "quality_flags"]
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    return rows


def verify_framing_replay(records: Iterable[dict[str, Any]] | LogData,
                          manifest: Mapping[str, Any] | None = None) -> dict[str, Any]:
    """Re-evaluate saved observations; mock detections remain labelled as mock.

    Replaying detector output verifies judgement reproducibility, not whether a
    real QR decoder correctly read the image. Image replay belongs to vision.py.
    """
    from .config import FramingConfig, StudyConfig
    from .framing import FrameObservation, FramingEvaluator

    manifest = manifest or {}
    raw = list(records.records if isinstance(records, LogData) else records)
    quality = set(records.quality_flags if isinstance(records, LogData) else ())
    unique = []
    seen = {}
    for record in raw:
        event_id = record.get("event_id")
        if event_id and event_id in seen:
            if seen[event_id] != record:
                quality.add("conflicting_event_id")
            continue
        if event_id:
            seen[event_id] = record
        unique.append(record)
    trials: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for record in unique:
        if record.get("trial_id"):
            trials[(str(record.get("experiment_session_id", "")), record["trial_id"])].append(record)
    results = []
    for (session_id, trial_id), trial in trials.items():
        start = next((r for r in trial if r.get("event_type") == "trial_started"), None)
        item: dict[str, Any] = {"experiment_session_id": session_id, "trial_id": trial_id,
                                "status": "unverifiable", "reason": "trial_start_missing"}
        if start is None:
            results.append(item)
            continue
        info = _payload(start)
        snapshot = info.get("config_snapshot", manifest.get("config"))
        try:
            if not isinstance(snapshot, dict):
                raise ValueError("config_snapshot_missing")
            if "framing" in snapshot:
                config = StudyConfig.from_dict(snapshot).profile(str(start.get("difficulty_id", info.get("difficulty_id", ""))))
            else:
                config = FramingConfig.from_dict(snapshot)
            clock_id = str(start["clock_id"])
            evaluator = FramingEvaluator(config, clock_id=clock_id)
            evaluator.start(trial_id, info.get("evaluator_start_mono_us", start["t_mono_us"]),
                            last_frame_id=info.get("last_frame_id"))
            expected = [r for r in trial if r.get("event_type") in ("trial_success", "trial_timeout")]
            replay_events = []
            decoder_modes = set()
            observation_count = 0
            for record in trial:
                payload = _payload(record)
                if record.get("event_type") == "detection_sample":
                    observation_json = payload.get("observation", payload)
                    observation = FrameObservation.from_dict(observation_json)
                    decoder_modes.add(observation_json.get("decoder", "unknown"))
                    replay_events.extend(evaluator.observe(observation,
                        now_mono_us=payload.get("evaluation_now_mono_us", record["t_mono_us"]),
                        neutral_axes=payload.get("neutral_axes")))
                    observation_count += 1
                elif record.get("event_type") == "framing_tick":
                    replay_events.extend(evaluator.tick(payload.get("evaluation_now_mono_us", record["t_mono_us"])))
            replay_terminal = [event for event in replay_events if event.event_type in ("trial_success", "trial_timeout")]
            expected_signature = [(r["event_type"], r["t_mono_us"]) for r in expected]
            actual_signature = [(event.event_type, event.t_mono_us) for event in replay_terminal]
            transitions = {"first_marker_decoded", "framing_entered", "framing_exited",
                           "framing_hold_started", "framing_hold_reset"}
            expected_transitions = [(r["event_type"], r["t_mono_us"], _payload(r).get("frame_id"), _payload(r).get("reason"))
                                    for r in trial if r.get("event_type") in transitions]
            actual_transitions = [(event.event_type, event.t_mono_us, event.payload.get("frame_id"), event.payload.get("reason"))
                                  for event in replay_events if event.event_type in transitions]
            status = "match" if expected_signature == actual_signature and expected_transitions == actual_transitions else "mismatch"
            reason = ""
            if any(r.get("event_type") == "trial_aborted" for r in trial):
                status, reason = "unverifiable", "aborted_outcome_is_not_a_framing_decision"
            if not observation_count:
                status, reason = "unverifiable", "no_observations"
            item.update(status=status, reason=reason,
                        expected=expected_signature, replayed=actual_signature,
                        transitions_match=expected_transitions == actual_transitions,
                        observations=observation_count, decoder_modes=sorted(decoder_modes))
        except (ValueError, TypeError, KeyError, AttributeError) as error:
            item.update(reason=str(error))
        results.append(item)
    matched = bool(results) and not quality and all(item["status"] == "match" for item in results)
    return {"analysis_version": "framing-replay-v1", "trials": results, "matched": matched,
            "quality_flags": sorted(quality),
            "matches": sum(item["status"] == "match" for item in results),
            "mismatches": sum(item["status"] == "mismatch" for item in results),
            "unverifiable": sum(item["status"] == "unverifiable" for item in results)}
