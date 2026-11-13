# Study HTTP v1 and adapter contract

This opt-in channel is separate from v2 UDP control. Python does not create a
motion client, open a camera, connect to ROS, or change the existing UDP packet.
Only the MR client is implemented. Session metadata retains both canonical study
conditions, `smartphone_touch` and `mr_ballgrip`, so MR can occupy period 1 or 2.

## Service and operator commands

Run from the repository root. Default HTTP binding is loopback port 8091. Bind a
specific host or `0.0.0.0` explicitly when a Quest must reach the service. This
small service has no authentication/TLS and belongs on the controlled experiment
network. `--mock` means simulated readiness, not hardware validation.

```sh
python3 -m puppypi_study demo
python3 -m puppypi_study serve --mock --host 127.0.0.1
python3 -m puppypi_study status
python3 -m puppypi_study session --participant-id P001 --interface-order smartphone_touch,mr_ballgrip --assignment-seed 42
python3 -m puppypi_study start --difficulty demo-wide --practice
python3 -m puppypi_study abort --reason operator_abort
python3 -m puppypi_study next --difficulty demo-narrow
python3 -m puppypi_study next --retry --difficulty demo-wide
python3 -m puppypi_study export PATH/records.jsonl --csv PATH/summary.csv --manifest PATH/manifest.json --analysis-config experiments/configs/demo-analysis.json
python3 -m puppypi_study replay PATH/records.jsonl --manifest PATH/manifest.json
```

Operator commands accept `--endpoint http://HOST:8091`. Start selects the sole
ready registered v2 UUID unless `--client-id UUID` is supplied. MR period is
inferred from the actual session order. `--trial-id`, `--period-index`,
`--retry-of-trial-id` and `--request-id` allow explicit assignment/retry. A retry
uses a new trial ID and preserves its parent. `session --randomize-order
--assignment-seed 42` records a reproducible shuffle. Running trials cannot be
replaced by `next`. Release and neutral confirmation remain required.

## HTTP messages

JSON responses/requests use `schema_version: 1`; absent request version means v1
for the clock exchange's existing client DTO. Bodies are capped at 64 KiB,
batches at 128 events, connections at 8, and socket reads at 2 seconds. NaN,
duplicate JSON keys, chunked input and ambiguous content lengths are rejected.
Errors are `{schema_version:1,error:"code",message:"explanation"}` with HTTP 4xx
or 503. POST mutations require a stable `request_id` except heartbeat and clock.
Reusing an ID with different content returns 409. Retries return current state,
never an earlier permission to move. Requests from earlier trials are rejected.

| Endpoint | Request / purpose |
| --- | --- |
| `GET /v1/status?client_id=UUID` | Current state, identifiers, readiness reasons, registered clients, frozen settings, signal and motion permission. |
| `POST /v1/client` | `client_id` (actual v2 UUID), `interface_id`, `source_clock_id`, `source_t_mono_us`, boolean `engaged/input_valid/neutral_confirmed/control_connected/last_accepted/emergency`, `control_sequence`, `settings`. |
| `POST /v1/session` | `request_id`, anonymous `participant_id`, `interface_order`, optional `metadata` including assignment seed/method. |
| `POST /v1/trials/start` | `request_id`, `experiment_session_id`, new `trial_id`, `control_client_id`, `interface_id:"mr_ballgrip"`, `period_index`, `difficulty_id`, `practice`, optional `settings` and `retry_of_trial_id`. |
| `POST /v1/trials/abort` | Same session/trial/client identifiers, `request_id`, `reason`. Terminal outcome is immutable. |
| `POST /v1/clock` | `client_id`, `source_clock_id`, client-send `source_t_mono_us`; returns echoed source time and `clock_id/server_recv_us/server_send_us`. |
| `POST /v1/events` | Session/trial/client identifiers, `request_id`, `events:[{event_id,event_type,source_clock_id,source_t_mono_us,payload}]`. |

Client events include clock/signal, input, sent-command, ACK-observation,
settings, blocks, tracking, focus/pause and local logging events. They are logged
as `report_origin:client_claim`, never actual robot receipt. The server rejects
client submissions named `robot_command_received` or `robot_command_accepted`.

## Trial and time rules

Start checks client heartbeat, release/neutral, control acceptance, emergency,
log health and live adapter/camera/decoder readiness. It freezes a copy of
settings actually reported by the client; changed settings abort the trial.
The service enters countdown, then asks for `signal_id` presentation. A
`signal_shown` event after the due time and before the ACK deadline starts the
framing evaluator. A fresh grab after the due time is allowed while output stays
neutral until running permission. Pre-due engagement aborts.

Heartbeat `event_delivery_ready:false` blocks starting the next trial until
previous event batches/local logging are settled. During countdown/running,
pending telemetry is normal and this flag alone never aborts or blocks motion.
Older clients without the field use compatibility default true.

Before signal acknowledgement, a `clock_sync` event must contain
`source_clock_id/reference_clock_id/offset_us/uncertainty_us/valid`. Offset maps
client time into server time. Reference clock must match, the uncertainty must
be bounded, and alignment must be fresh. Client signal time is canvas submission,
not measured photon onset. Server receipt starts the evaluator; aligned client
signal time is preserved separately for analysis. The evaluator cannot use an
earlier trial's frame or hold. Clock changes during a trial abort it.

Default **demo runtime policies**: countdown 3 s (CLI mock/demo 0 s), signal ACK
deadline 5 s, client/robot channel loss deadline 2 s, clock uncertainty at most
100 ms and synchronization age 30 s. These are configurable `RuntimePolicy`
values, not confirmed experiment thresholds. Ready camera freshness uses the
framing profile's `max_frame_age` (demo 0.3 s). A frame gap resets hold according
to the profile; a complete channel loss aborts at the separate runtime deadline.
QR absent with `data_valid:true` is healthy; decoder/invalid-image failures with
`data_valid:false` immediately invalidate decoder readiness and abort an active
trial. Bad marker geometry alone resets the framing hold.

Success/timeout/abort request neutral and require release, neutral ACK and fresh
engagement for the next trial. A terminal callback must enqueue into the existing
robot authority, preserve emergency latches and return promptly. Status also tells
the MR client to hold neutral. Neither callback dispatch nor ACK is a measured
physical robot stop.

## Trusted robot adapter boundary — not installed on a robot

No verified installed robot checkout was available. No robot source was changed,
connected to, deployed, or restarted. These interfaces are preparation for its
maintainer; a live service remains unready without robot observations.

Embed `StudyRuntime(config,journal,neutral_callback=enqueue_neutral)` in the
verified robot-side integration, or use `serve --stdin-ingress` for read-only
ingress. `neutral_callback(control_client_id,reason)` must be wired separately;
stdin alone does not deliver terminal-neutral requests to a robot. Ingress is
trusted operator/maintainer input and has no public HTTP endpoint.

```json
{"kind":"robot_status","status":{"control_client_id":"ACTUAL-V2-UUID","controller_active":true,"neutral_confirmed":true,"emergency_stop":false}}
{"kind":"control_event","event_type":"robot_command_received","payload":{"control_client_id":"ACTUAL-V2-UUID","sequence":42,"forward":0.1,"turn":0.0}}
{"kind":"observation","observation":{"schema_version":1,"frame_id":42,"clock_id":"CURRENT-SERVICE-CLOCK-ID","captured_mono_us":null,"capture_clock_id":null,"received_mono_us":1234000,"decoded_mono_us":1240000,"image_width":640,"image_height":480,"marker_text":null,"corners":null,"data_valid":true,"invalid_reason":null,"decoder":"opencv"},"neutral_axes":[0,0]}
{"kind":"adapter_error","reason":"camera_unavailable"}
```

`ingest_robot_status`, `ingest_observation`, `ingest_control_event` and
`adapter_failed` are also public in-process methods. Trusted control event types
are `robot_command_received`, `robot_command_accepted`, `robot_ack_sent` and
`robot_pose`. All retain `report_origin:adapter_report`; actual instrumentation
must be installed before claiming these observations came from a real robot.
Frames must use the runtime's identified monotonic clock, maintain increasing
original IDs, and preserve unknown capture times as null. Decoder input must
branch from the existing camera pipeline; this service never opens a second
camera. The helper cannot infer real neutral axes from a client claim.

## Records and validation

JSONL uses the common header in the handoff document. Original client time stays
in payload beside alignment metadata. `trial_started` includes config/hash,
settings, assignment, practice, evaluator start and prior frame cutoff.
`detection_sample` stores the whole observation, evaluation time and neutral
axes; `framing_tick` records time-dependent evaluator events for exact replay.
Input samples retain normalized axes, engagement/validity/block reasons.

The bounded writer reports queue loss and closes with `logging_closed.complete`.
Log failure aborts rather than hiding missing data. Shutdown waits at most the
configured close timeout. Run manifests contain Git commit/dirty state and each
Python module's SHA256. Generated logs go under ignored `.local/experiments/`.

```sh
python3 -m unittest discover -s tests/study -v
```

HTTP tests bind IPv4 loopback only and require a host environment that permits
local listener sockets. Demo/replay verify synthetic detections, not camera QR
performance, Quest hand tracking or robot movement.
