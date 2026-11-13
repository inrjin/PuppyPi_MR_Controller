# KHCI 2027 log analysis contract

This implementation extracts trial metrics from version 1 JSONL. It does not
perform significance testing or remove failed trials. `demo-analysis-v1` values
are pilot examples, not confirmed experimental thresholds. Smartphone software
is outside the current implementation; its fixed identifier remains supported
by the log reader for later integration of externally collected data.

## Reproduce a summary

Run from the repository root, replacing `<run>` with a generated run directory:

```sh
python3 -m puppypi_study export <run>/records.jsonl --manifest <run>/manifest.json --csv <run>/summary.csv
python3 -m puppypi_study replay <run>/records.jsonl --manifest <run>/manifest.json
python3 -m unittest discover -s tests/study -p test_analysis.py -v
```

The Python API also accepts `analysis_config` in `export_summary` and
`summarize_records`. An example is `experiments/configs/demo-analysis.json`.
Every CSV row contains the full analysis configuration and its version.
Keep generated logs, summaries and replay reports under `.local/experiments/`;
test reports belong in `TestResults/`.

## Input records and time

Records use the handoff's common header. `event_id` identifies a record;
`experiment_session_id` and `trial_id` identify a trial together. Identical
retransmissions are deduplicated. Conflicting reused IDs are quality errors.
The original `interface_id` is retained independently of `interface_order` and
the 1-based `period_index`.

`signal_shown` represents actual client signal presentation. Neither the server
start request nor the later `trial_started` event substitutes for this signal.
Recorder-enriched client reports retain `source_t_mono_us` and `source_clock_id`. Their mapped
`aligned_t_mono_us` is used only with `clock_alignment_valid: true` and finite
`clock_alignment_error_us` within the configured budget. The mapping targets
`aligned_clock_id`, or the record's server `clock_id` if omitted. Without it,
same-client intervals remain calculable but client-to-robot intervals are empty.
UTC and timestamps on different monotonic clocks are never directly subtracted.
The uncertainty budget and each signal's alignment error are exported.
Local Unity records use their header clock directly; an unused
`payload.source_clock_id` field does not replace that header timestamp.

## Trial metrics

| CSV field | Definition |
| --- | --- |
| `outcome`, `reason` | Original success, timeout or aborted result. An incomplete log has an empty outcome and a quality flag. |
| `signal_to_first_valid_input_s` | Actual signal to the first engaged, valid input with either normalized axis exceeding the analysis deadzone. This confirmed definition is fixed as `nonzero_engaged`. |
| `signal_to_first_engaged_valid_s` | Signal to valid engagement, including neutral axes; a separate descriptive metric. |
| `signal_to_success_s` | Actual signal to success. Empty for timeout, aborted, missing signal, reversed time or incompatible clocks. |
| `first_valid_decode_to_success_s` | First fresh target-marker decode to success; not physical arrival time. |
| `first_framing_entry_to_success_s` | First valid framing entry to success. |
| `observed_duration_s` | Server trial start to terminal result, including unsuccessful trials. |
| `framing_reexits`, `hold_resets` | Logged framing exits and hold resets. |
| `blocked_demand_s` | Union of system-block intervals intersected with observed control-demand intervals, beginning at the aligned actual start signal when available. This includes any start-confirmation command hold. |
| `reentry_*` | Each merged blocked-demand episode's end to the next valid nonneutral unblocked input. An episode without resumed input is retained as unfinished. |

Input samples contain `normalized_forward`, `normalized_turn`, `engaged`,
`input_valid`, `demanded`, `blocked` and `block_reasons`. Without `demanded`,
engagement is an explicit, flagged proxy. Samples use zero-order hold only up to
`max_sample_gap_s`; longer unobserved time is not silently counted as demand.
`block_started` and `block_ended` carry `reason`. Per-reason durations can overlap;
`blocked_demand_s` counts each instant once. Late terminal reports and input
before the start signal do not become first-input events.

## Command reversals and frame quality

Direction reversals use one fixed shared command stage, selected by
`command_source` (`command_sent` by default), with `forward`, `turn`, `sequence`
and control client ID. They are not counts of touch, grab or render events.
Commands are zero-order resampled at a common period. Direction enters above
`deadzone + hysteresis`, returns to neutral below
`deadzone - hysteresis`, and must persist for `min_hold_s`. A sustained neutral
interval or missing-data gap resets the direction episode, so release followed
by a new engagement does not itself count as a reversal.

`detection_sample.payload.observation` preserves the original frame contract.
Receive-to-decode delay, decoder processing duration, unique frame IDs,
duplicate frames, ID gaps and update intervals are separate metrics. ID gaps
can include intentionally skipped decoder frames and are not a measured
network-loss rate. Missing capture timestamps remain missing; receive-to-decode
delay is not capture-to-display latency. Physically measured position or stop
distance is not derived from these fields.

Malformed JSONL, a truncated final line, missing logger closure, queue loss,
identifier conflicts, unavailable alignments and sample gaps are exported in
`quality_flags`. `quality_status=limited` preserves the trial rather than
discarding it. Empty timing fields must not be replaced with zero.

## Re-evaluate framing decisions

Replay reads `trial_started.payload.config_snapshot`, the selected difficulty,
`evaluator_start_mono_us` and `last_frame_id`. It replays `detection_sample`
observations with `evaluation_now_mono_us` and `neutral_axes`, plus recorded
`framing_tick` calls. It compares framing transitions and success/timeout event
times with the saved decisions. Aborted outcomes are reported as unverifiable
by this framing-only replay; they remain in the CSV.

`decoder_modes` is retained in the replay report. Replaying `mock_fixture`
detections validates the evaluator, not image recognition. Real QR decoding and
recorded-image replay are separately tested through the vision adapter.
