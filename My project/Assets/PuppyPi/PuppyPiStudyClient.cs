using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace PuppyPi
{
    public enum StudyPresentationPolicy { Unspecified, CameraOnly, CameraAndMap, NoMedia }
    // Opt-in side channel. It never adds fields to a v2 motor command.
    [DefaultExecutionOrder(150)]
    [DisallowMultipleComponent]
    public sealed class PuppyPiStudyClient : MonoBehaviour
    {
        public PuppyPiRobotController controller;
        public string serviceAddress = "";
        public int servicePort = 8091;
        [Min(0.5f)] public float channelTimeoutSeconds = 2;
        public bool lockLayout = true;
        public StudyPresentationPolicy presentationPolicy;
        [Tooltip("Pilot analysis threshold in normalized axes, not physical motion or a finalized study value.")]
        [Range(0, 0.99f)] public float normalizedDemandThreshold = 0.05f;
        public bool Active { get; private set; }
        public StudyStatus Status { get; private set; }
        public string Notice { get; private set; } = "Study mode is off.";
        public string BlockReason { get; private set; }
        public string LocalLogPath => _journal != null ? _journal.Path : "";
        public string ControlClientId => _clientId ?? (controller != null && controller.DriveClient != null ? controller.DriveClient.Session.ClientId : "");
        public bool ConfigurationLocked => Active && Status != null &&
            (Status.state == "countdown" || Status.state == "running");
        public bool LayoutLocked => ConfigurationLocked && (Status.settings_present && Status.settings != null ? Status.settings.layout_locked : lockLayout);
        public bool HasPendingSignal => Active && Status != null && Status.show_start_signal &&
            !string.IsNullOrEmpty(Status.signal_id) && _presentedSignal != Status.signal_id;
        public string DisplayText => !Active ? Notice : Status != null && Status.show_start_signal ? "GO — BEGIN INSPECTION" :
            Status == null ? Notice : (Status.state ?? "setup").ToUpperInvariant() + " | " + (BlockReason ?? Status.reason ?? "") +
            (Status.mock_mode ? " | MOTOR-FREE DEMO" : "");

        private readonly Queue<StudyEvent> _events = new Queue<StudyEvent>();
        private readonly StudyClockAlignment _clock = new StudyClockAlignment();
        private readonly string _clockId = "unity-monotonic-" + Guid.NewGuid().ToString("N");
        private StudyJournal _journal;
        private PuppyPiV2Client _drive;
        private string _baseUrl;
        private string _clientId;
        private string _sessionId;
        private string _trialId;
        private string _presentedSignal;
        private string _appliedSettingsTrial;
        private double _lastStatusAt = double.NegativeInfinity;
        private double _nextSample;
        private long _droppedEvents;
        private string _lastBlockReason;
        private bool _focused = true;
        private bool _paused;
        private StudySettings _originalSettings;
        private bool _exiting;
        private bool _abortPending;
        private string _protocolFault;
        private bool _lastEngaged;
        private bool _lastInputValid;
        private int _pendingBatchCount;
        private double _pendingSince = double.PositiveInfinity;
        private GameObject _cueRoot;
        private Text _cueText;
        private Camera _cueCamera;
        private StudyBuildInfo _buildInfo;
        private readonly HashSet<string> _controlBlocks = new HashSet<string>();
        private const int QueueCapacity = 512;
        private static double Now => Time.realtimeSinceStartupAsDouble;
        private static long NowUs => (long)(Time.realtimeSinceStartupAsDouble * 1000000);

        [Serializable] private sealed class ClientHeartbeat
        {
            public int schema_version = 1;
            public string client_id;
            public string interface_id = "mr_ballgrip";
            public string source_clock_id;
            public long source_t_mono_us;
            public bool engaged, input_valid, neutral_confirmed, control_connected, last_accepted, emergency;
            public bool event_delivery_ready;
            public long control_sequence;
            public StudySettings settings;
        }
        [Serializable] private sealed class ClockRequest { public string client_id, source_clock_id; public long source_t_mono_us; }
        [Serializable] private sealed class ClockReply { public string clock_id; public long server_recv_us, server_send_us, source_t_mono_us; }
        [Serializable] private sealed class EventBatch
        {
            public int schema_version = 1;
            public string request_id, experiment_session_id, trial_id, control_client_id;
            public StudyEvent[] events;
        }
        [Serializable] private sealed class AbortRequest
        {
            public int schema_version = 1;
            public string request_id, experiment_session_id, trial_id, control_client_id, reason;
        }
        [Serializable] private sealed class LocalRecord
        {
            public int schema_version = 1;
            public string event_id, experiment_session_id, trial_id, participant_id;
            public string interface_id = "mr_ballgrip";
            public int period_index;
            public string difficulty_id;
            public string source = "mr_client";
            public string clock_id;
            public long t_mono_us;
            public string record_type = "event";
            public string event_type;
            public StudyPayload payload;
        }

        private void Awake()
        {
            if (controller == null) controller = GetComponent<PuppyPiRobotController>();
            var resource = Resources.Load<TextAsset>("PuppyPiStudyBuild");
            _buildInfo = resource != null ? JsonUtility.FromJson<StudyBuildInfo>(resource.text) : new StudyBuildInfo {
                source_revision = "unavailable_editor", source_fingerprint = "unavailable_editor", source_tree_status = "unavailable_editor"
            };
        }

        public bool EnterStudy(string host, out string error)
        {
            error = null;
            if (Active) { error = "Exit the current study connection first."; return false; }
            if (presentationPolicy == StudyPresentationPolicy.Unspecified)
            { error = "Choose the shared camera/map policy before entering study mode."; return false; }
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224 ||
                servicePort < 1 || servicePort > 65535)
            { error = "Enter the experiment service IPv4 address and port."; return false; }
            _drive = controller != null ? controller.DriveClient : null;
            if (_drive == null || _drive.DriveInput == null) { error = "The MR drive input is not configured."; return false; }
            try
            {
                string root = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "../../.local/experiments/client")) :
                    Path.Combine(Application.persistentDataPath, "experiments");
                _journal = new StudyJournal(Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl"));
            }
            catch (Exception e) { error = "Cannot open study log: " + e.Message; return false; }
            serviceAddress = address.ToString();
            _baseUrl = "http://" + serviceAddress + ":" + servicePort;
            _clientId = _drive.Session.ClientId;
            _sessionId = _trialId = _presentedSignal = _appliedSettingsTrial = null;
            _events.Clear();
            _clock.Reset();
            Status = null;
            _lastStatusAt = double.NegativeInfinity;
            _droppedEvents = 0;
            _exiting = false;
            _abortPending = false;
            _protocolFault = null;
            _controlBlocks.Clear();
            _lastBlockReason = null;
            _lastEngaged = _lastInputValid = false;
            _pendingBatchCount = 0;
            _pendingSince = double.PositiveInfinity;
            _originalSettings = SnapshotSettings();
            Active = true;
            var studySettings = JsonUtility.FromJson<StudySettings>(JsonUtility.ToJson(_originalSettings));
            studySettings.camera_visible = presentationPolicy != StudyPresentationPolicy.NoMedia;
            studySettings.map_visible = presentationPolicy == StudyPresentationPolicy.CameraAndMap;
            ApplyFixedSettings(studySettings);
            _drive.CommandSent += OnCommandSent;
            _drive.AckObserved += OnAck;
            _drive.ControlEventSent += OnControlEvent;
            _drive.SetStudyCommandHold(false);
            _drive.DriveInput.Invalidate();
            _drive.SetStudyMotionBlocked(true);
            Notice = "Connecting to study service. Release the grip.";
            Emit("logging_started", new StudyPayload { settings = _originalSettings, control_client_id = _clientId });
            StartCoroutine(PollLoop());
            StartCoroutine(ClockLoop());
            StartCoroutine(EventLoop());
            return true;
        }

        private void Update()
        {
            if (!Active || _drive == null) return;
            bool isolated = !_drive.networkEnabled || (IPAddress.TryParse(_drive.robotAddress, out var ip) && IPAddress.IsLoopback(ip));
            string reason = _protocolFault ?? (_abortPending ? "abort_pending" :
                Now - _pendingSince > channelTimeoutSeconds ? "event_delivery_unavailable" :
                _drive.DriveInput != null && _drive.DriveInput.Engaged &&
                    _drive.DriveInput.InputModality != "hand_tracking" && !_drive.DriveInput.IsSimulation ? "hand_tracking_required" :
                _clientId != _drive.Session.ClientId ? "control_session_changed" :
                !_focused || _paused ? "app_inactive" : StudyMotionGate.BlockReason(Status, _clientId, _sessionId,
                    Now - _lastStatusAt <= channelTimeoutSeconds, _journal != null && _journal.Healthy && _droppedEvents == 0,
                    _clock.IsFresh(NowUs, 15000000), _presentedSignal, isolated));
            BlockReason = reason;
            if (_lastBlockReason != reason)
            {
                if (_lastBlockReason != null) Emit("block_ended", new StudyPayload { reason = _lastBlockReason });
                if (reason != null) Emit("block_started", new StudyPayload { reason = reason });
                _lastBlockReason = reason;
            }
            bool startGrabWindow = StudyMotionGate.CanObserveStartGrab(Status, reason, _presentedSignal);
            _drive.SetStudyCommandHold(startGrabWindow);
            _drive.SetStudyMotionBlocked(reason != null && !startGrabWindow);
            ObserveControlBlock("start_confirmation_pending", startGrabWindow);
            if (!_abortPending && Status != null && Status.state == "running" && reason != null)
            {
                Emit("client_error", new StudyPayload { reason = reason, dropped_count = _droppedEvents });
                AbortTrial(reason);
            }
            ApplyLayoutLock();
            if (ConfigurationLocked && Status.settings_present && Status.settings != null) ApplyFixedSettings(Status.settings);
            if (Now < _nextSample) return;
            _nextSample = Now + 0.05;
            var input = _drive.DriveInput;
            if (input == null) return;
            if (_lastEngaged != input.Engaged)
            {
                Emit(input.Engaged ? "grab" : "release", new StudyPayload { engaged = input.Engaged, input_valid = input.InputValid });
                _lastEngaged = input.Engaged;
            }
            if (_lastInputValid != input.InputValid)
            {
                Emit(input.InputValid ? "tracking_recovered" : "tracking_lost", new StudyPayload { engaged = input.Engaged, input_valid = input.InputValid });
                _lastInputValid = input.InputValid;
            }
            ObserveControlBlock("control_ack_unavailable", !_drive.Session.Connected);
            ObserveControlBlock("control_rejected", _drive.Session.Connected && !_drive.Session.LastAccepted);
            ObserveControlBlock("emergency_or_fault", _drive.Session.Emergency || _drive.Session.Faulted);
            ObserveControlBlock("external_motion_blocked", _drive.ExternalMotionBlocked);
            ObserveControlBlock("awaiting_neutral_ack", !_drive.Session.NeutralConfirmed);
            ObserveControlBlock("fresh_grab_required", input.Engaged && !_drive.Session.Armed);
            ObserveControlBlock("input_invalid", input.Engaged && !input.InputValid);
            Emit("input_sample", new StudyPayload {
                raw_forward = input.RawAxes.x, raw_turn = input.RawAxes.y, raw_units = input.RawUnits,
                input_modality = input.InputModality,
                normalized_forward = input.RequestedAxes.x, normalized_turn = input.RequestedAxes.y, engaged = input.Engaged,
                input_valid = input.InputValid && input.ControlActive, focused = input.Focused && _focused,
                paused = input.Paused || _paused, demanded = input.Engaged &&
                    (Mathf.Abs(input.RequestedAxes.x) > normalizedDemandThreshold || Mathf.Abs(input.RequestedAxes.y) > normalizedDemandThreshold),
                blocked = _drive.StudyMotionBlocked || _drive.StudyCommandHeld || _drive.ExternalMotionBlocked || !_drive.Session.Armed,
                block_reasons = CurrentBlockReasons(),
                neutral_confirmed = _drive.Session.NeutralConfirmed, control_client_id = _clientId
            });
        }

        private IEnumerator PollLoop()
        {
            while (Active)
            {
                var input = _drive.DriveInput;
                var heartbeat = new ClientHeartbeat {
                    client_id = _clientId, source_clock_id = _clockId, source_t_mono_us = NowUs,
                    engaged = input != null && input.Engaged, input_valid = input != null && input.InputValid,
                    neutral_confirmed = _drive.Session.NeutralConfirmed,
                    control_connected = _drive.networkEnabled && _drive.Session.Connected,
                    last_accepted = _drive.Session.LastAccepted, emergency = _drive.Session.Emergency || _drive.Session.Faulted,
                    event_delivery_ready = _events.Count == 0 && _pendingBatchCount == 0 && _droppedEvents == 0 && _journal != null && _journal.Healthy,
                    control_sequence = _drive.LastSentSequence, settings = SnapshotSettings()
                };
                yield return Request("POST", "/v1/client", JsonUtility.ToJson(heartbeat), null);
                yield return Request("GET", "/v1/status?client_id=" + _clientId, null, json => {
                    StudyStatus next;
                    try { next = JsonUtility.FromJson<StudyStatus>(json); }
                    catch (Exception) { Notice = "Invalid study status."; return; }
                    AcceptStatus(next);
                });
                yield return new WaitForSecondsRealtime(0.15f);
            }
        }

        private void AcceptStatus(StudyStatus next)
        {
            if (!Active) return;
            if (next == null || next.schema_version != 1) { _protocolFault = "study_protocol_mismatch"; return; }
            if (!string.IsNullOrEmpty(next.control_client_id) && next.control_client_id != _clientId)
            { _protocolFault = "study_client_mismatch"; Notice = "Study belongs to a different control client."; _drive.SetStudyMotionBlocked(true); return; }
            if (!string.IsNullOrEmpty(_sessionId) && next.experiment_session_id != _sessionId)
            { _protocolFault = "study_session_mismatch"; Notice = "Study session changed; reconnect explicitly."; _drive.SetStudyMotionBlocked(true); return; }
            if (_clock.ReferenceClockId != null && next.clock_id != _clock.ReferenceClockId)
            { _protocolFault = "study_clock_changed"; _clock.Invalidate(); _drive.SetStudyMotionBlocked(true); Notice = "Study clock changed."; return; }
            _lastStatusAt = Now;
            _sessionId = next.experiment_session_id;
            if (_trialId != next.trial_id)
            {
                if (_events.Count > 0)
                {
                    _droppedEvents += _events.Count;
                    Emit("client_error", new StudyPayload { reason = "previous_trial_delivery_incomplete", dropped_count = _events.Count });
                }
                _events.Clear(); // Pre-trial data remains in the local journal, never relabelled as trial data.
                _trialId = next.trial_id;
                Status = next;
                _presentedSignal = null;
                _appliedSettingsTrial = null;
                _abortPending = false;
                _lastBlockReason = null;
                _controlBlocks.Clear();
                EmitClockAlignment();
            }
            Status = next;
            if (next.settings_present && !StudyMotionGate.ValidSettings(next.settings))
            { _protocolFault = "invalid_study_settings"; _drive.SetStudyMotionBlocked(true); return; }
            Notice = string.IsNullOrEmpty(next.reason) ? next.state : next.reason;
            if (!string.IsNullOrEmpty(_trialId) && _appliedSettingsTrial != _trialId)
            {
                _appliedSettingsTrial = _trialId;
                if (next.settings_present && next.settings != null) ApplyFixedSettings(next.settings);
                Emit("client_settings_snapshot", new StudyPayload { settings = SnapshotSettings() });
            }
            if (next.neutral_requested || next.state != "running") _drive.SetStudyMotionBlocked(true);
        }

        private IEnumerator ClockLoop()
        {
            while (Active)
            {
                long sent = NowUs;
                var body = new ClockRequest { client_id = _clientId, source_clock_id = _clockId, source_t_mono_us = sent };
                yield return Request("POST", "/v1/clock", JsonUtility.ToJson(body), json => {
                    try
                    {
                        var reply = JsonUtility.FromJson<ClockReply>(json);
                        if (reply == null || reply.source_t_mono_us != sent) return;
                        _clock.Observe(reply.clock_id, sent, reply.server_recv_us, reply.server_send_us, NowUs, 50000);
                        EmitClockAlignment();
                    }
                    catch (Exception) { _clock.Invalidate(); }
                });
                yield return new WaitForSecondsRealtime(3);
            }
        }

        private void EmitClockAlignment()
        {
            if (string.IsNullOrEmpty(_clock.ReferenceClockId)) return;
            Emit("clock_sync", new StudyPayload {
                source_clock_id = _clockId, reference_clock_id = _clock.ReferenceClockId,
                offset_us = _clock.OffsetUs, uncertainty_us = _clock.UncertaintyUs,
                valid = _clock.IsFresh(NowUs, 15000000)
            });
        }

        private void ObserveControlBlock(string reason, bool blocked)
        {
            if (blocked ? !_controlBlocks.Add(reason) : !_controlBlocks.Remove(reason)) return;
            Emit(blocked ? "block_started" : "block_ended", new StudyPayload { reason = reason });
        }

        private string[] CurrentBlockReasons()
        {
            var reasons = new List<string>(_controlBlocks);
            if (BlockReason != null && !reasons.Contains(BlockReason)) reasons.Add(BlockReason);
            reasons.Sort(StringComparer.Ordinal);
            return reasons.ToArray();
        }

        private IEnumerator EventLoop()
        {
            EventBatch pending = null;
            while (Active)
            {
                if (string.IsNullOrEmpty(_sessionId) || string.IsNullOrEmpty(_trialId))
                { yield return new WaitForSecondsRealtime(0.1f); continue; }
                if (pending != null && pending.trial_id != _trialId)
                {
                    _droppedEvents += pending.events.Length;
                    Emit("client_error", new StudyPayload { reason = "pending_trial_delivery_incomplete", dropped_count = pending.events.Length });
                    pending = null;
                    _pendingBatchCount = 0;
                    _pendingSince = double.PositiveInfinity;
                }
                if (pending == null && _events.Count > 0)
                {
                    int count = Math.Min(8, _events.Count);
                    var batch = new StudyEvent[count];
                    for (int i = 0; i < count; i++) batch[i] = _events.Dequeue();
                    pending = new EventBatch { request_id = Guid.NewGuid().ToString("N"), experiment_session_id = _sessionId,
                        trial_id = _trialId, control_client_id = _clientId, events = batch };
                    _pendingBatchCount = count;
                    _pendingSince = Now;
                }
                if (pending != null)
                {
                    bool accepted = false;
                    yield return Request("POST", "/v1/events", JsonUtility.ToJson(pending), _ => accepted = true);
                    if (accepted) { pending = null; _pendingBatchCount = 0; _pendingSince = double.PositiveInfinity; } // Same IDs after a lost response.
                }
                yield return new WaitForSecondsRealtime(0.02f);
            }
        }

        private IEnumerator Request(string method, string path, string body, Action<string> completed)
        {
            using (var request = new UnityWebRequest(_baseUrl + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.timeout = 2;
                yield return request.SendWebRequest();
                if (!Active) yield break;
                if (request.result != UnityWebRequest.Result.Success)
                { Notice = "Study service: " + request.responseCode + " " + request.error; yield break; }
                completed?.Invoke(request.downloadHandler.text);
            }
        }

        // Called by the dashboard only once its actual GO label participates in
        // Canvas submission. This is a client presentation event, not photon timing.
        public void NotifySignalPresented(string timestampBasis = "canvas_submission")
        {
            if (!HasPendingSignal || !_focused || _paused) return;
            if (timestampBasis != "canvas_submission" && !Status.mock_mode) return;
            if (!StudyMotionGate.CanObserveStartGrab(Status, BlockReason, Status.signal_id)) return;
            _presentedSignal = Status.signal_id;
            Emit("signal_shown", new StudyPayload { signal_id = _presentedSignal, timestamp_basis = timestampBasis });
            // Observe a genuine new grab in the cue frame, while keeping all
            // motor output neutral until this signal is confirmed by the server.
            if (StudyMotionGate.CanObserveStartGrab(Status, BlockReason, _presentedSignal))
            {
                _drive.SetStudyCommandHold(true);
                _drive.SetStudyMotionBlocked(false);
                ObserveControlBlock("start_confirmation_pending", true);
            }
        }

        public void AbortTrial(string reason = "operator_abort")
        {
            if (!Active) return;
            bool alreadyPending = _abortPending;
            _abortPending = true;
            _drive.SetStudyMotionBlocked(true);
            if (!alreadyPending && !string.IsNullOrEmpty(_sessionId) && !string.IsNullOrEmpty(_trialId)) StartCoroutine(AbortRequestLoop(reason));
        }

        private IEnumerator AbortRequestLoop(string reason)
        {
            var body = new AbortRequest { request_id = Guid.NewGuid().ToString("N"), experiment_session_id = _sessionId,
                trial_id = _trialId, control_client_id = _clientId, reason = reason };
            string json = JsonUtility.ToJson(body);
            while (Active && _trialId == body.trial_id)
            {
                bool confirmed = false;
                yield return Request("POST", "/v1/trials/abort", json, _ => confirmed = true);
                if (confirmed || Status == null || Status.state == "success" || Status.state == "timeout" || Status.state == "aborted") yield break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        public void ExitStudy()
        {
            if (!Active || _exiting) return;
            if (Status != null && (Status.state == "running" || Status.state == "countdown"))
            { AbortTrial("operator_exit_requested"); Notice = "Aborting trial. Exit after terminal confirmation."; return; }
            _exiting = true;
            _drive.SetStudyMotionBlocked(true);
            StartCoroutine(FinishExit());
        }

        private IEnumerator FinishExit()
        {
            double until = Now + 1;
            while (!string.IsNullOrEmpty(_trialId) && (_events.Count > 0 || _pendingBatchCount > 0) && Now < until)
                yield return null;
            if (_events.Count > 0 || _pendingBatchCount > 0)
            {
                _droppedEvents += _events.Count + _pendingBatchCount;
                Emit("logging_interrupted", new StudyPayload { reason = "exit_delivery_deadline", dropped_count = _droppedEvents });
            }
            Emit("logging_stopped", new StudyPayload { dropped_count = _droppedEvents + (_journal != null ? _journal.DroppedCount : 0) });
            Active = false;
            StopAllCoroutines();
            UnbindDrive();
            ApplyFixedSettings(_originalSettings);
            controller?.SetStudyLayoutLocked(false);
            if (_cueRoot != null) Destroy(_cueRoot);
            _drive.DriveInput?.Invalidate();
            _drive.SetStudyMotionBlocked(false); // The invalidated input still requires release and a fresh grab.
            _drive.SetStudyCommandHold(false);
            bool complete = _journal == null || _journal.Close();
            Notice = complete ? "Study mode off. Release and re-grab for normal control." : "Study mode off; local log incomplete.";
        }

        private void Emit(string kind, StudyPayload payload)
        {
            if (!Active) return;
            var value = new StudyEvent { event_type = kind, source_clock_id = _clockId, source_t_mono_us = NowUs, payload = payload };
            var record = new LocalRecord {
                event_id = value.event_id, experiment_session_id = _sessionId, trial_id = _trialId,
                participant_id = Status != null ? Status.participant_id : null, period_index = Status != null ? Status.period_index : 0,
                difficulty_id = Status != null ? Status.difficulty_id : null, clock_id = _clockId,
                t_mono_us = value.source_t_mono_us, record_type = kind == "input_sample" ? "sample" : "event",
                event_type = kind, payload = payload
            };
            _journal?.Enqueue(JsonUtility.ToJson(record));
            if (string.IsNullOrEmpty(_trialId)) return;
            if (_exiting && kind != "logging_stopped" && kind != "logging_interrupted") return;
            if (Status != null && (Status.state == "success" || Status.state == "timeout" || Status.state == "aborted") &&
                kind != "logging_stopped" && kind != "logging_interrupted" && kind != "client_error") return;
            if (_events.Count >= QueueCapacity) { _droppedEvents++; return; }
            _events.Enqueue(value);
        }

        private void OnCommandSent(V2Command command) => Emit("command_sent", new StudyPayload {
            sequence = command.sequence, forward = command.forward, turn = command.turn,
            control_active = command.control_active, control_client_id = command.client_id
        });
        private void OnAck(V2Ack ack, bool applied) => Emit("ack_observed", new StudyPayload {
            sequence = ack.sequence, accepted = ack.accepted, applied = applied, emergency_stop = ack.emergency_stop,
            reason = ack.reason, state = ack.state, control_client_id = ack.client_id
        });
        private void OnControlEvent(string kind, long sequence) => Emit("control_event_sent", new StudyPayload { reason = kind, sequence = sequence });

        private StudySettings SnapshotSettings()
        {
            var grip = controller != null ? controller.GripInput : null;
            return new StudySettings {
                speed_percent = controller != null ? controller.SpeedPercent : 0,
                max_tilt_degrees = grip != null ? grip.maxTiltDegrees : 0,
                deadzone_degrees = grip != null ? grip.deadzoneDegrees : 0,
                sensitivity = grip != null ? grip.sensitivity : 0,
                normalized_demand_threshold = normalizedDemandThreshold,
                invert_forward = grip != null && grip.invertForward, invert_turn = grip != null && grip.invertTurn,
                camera_visible = controller != null && controller.StudyCameraVisible,
                map_visible = controller != null && controller.StudyMapVisible, layout_locked = lockLayout,
                app_version = Application.version, unity_version = Application.unityVersion, device_model = SystemInfo.deviceModel,
                app_build_guid = Application.buildGUID,
                source_revision = _buildInfo != null ? _buildInfo.source_revision : "unavailable",
                source_fingerprint = _buildInfo != null ? _buildInfo.source_fingerprint : "unavailable",
                source_tree_status = _buildInfo != null ? _buildInfo.source_tree_status : "unavailable",
                build_utc = _buildInfo != null ? _buildInfo.build_utc : "unavailable",
                console_pose = controller != null ? PoseText(controller.cockpitPlacement != null ? controller.cockpitPlacement.cockpitRoot : controller.transform) : "",
                camera_pose = controller != null && controller.floatingScreen != null ? PoseText(controller.floatingScreen.ScreenRoot) : "",
                dashboard_pose = controller != null && controller.dashboard != null ? PoseText(controller.dashboard.PanelRoot) : ""
            };
        }

        private static string PoseText(Transform target) => target == null ? "unavailable" : JsonUtility.ToJson(new TransformSnapshot {
            position = target.position, rotation = target.rotation, scale = target.lossyScale
        });
        [Serializable] private sealed class TransformSnapshot { public Vector3 position, scale; public Quaternion rotation; }

        private void ApplyFixedSettings(StudySettings settings)
        {
            if (settings == null || controller == null) return;
            controller.ApplyStudySettings(settings);
        }

        private void ApplyLayoutLock() { controller?.SetStudyLayoutLocked(LayoutLocked); }
        private void LateUpdate()
        {
            if (!Active) return;
            if (_cueRoot == null)
            {
                _cueCamera = Camera.main;
                if (_cueCamera == null) return;
                _cueRoot = new GameObject("Study trial cue", typeof(RectTransform), typeof(Canvas));
                _cueRoot.transform.SetParent(_cueCamera.transform, false);
                _cueRoot.transform.localPosition = new Vector3(0, 0.15f, 0.8f);
                _cueRoot.transform.localScale = Vector3.one * 0.001f;
                ((RectTransform)_cueRoot.transform).sizeDelta = new Vector2(650, 100);
                var canvas = _cueRoot.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = _cueCamera;
                canvas.sortingOrder = 100;
                var label = new GameObject("Common trial signal", typeof(RectTransform));
                label.transform.SetParent(_cueRoot.transform, false);
                var rect = (RectTransform)label.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.sizeDelta = Vector2.zero;
                _cueText = label.AddComponent<Text>();
                _cueText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                _cueText.fontSize = 24;
                _cueText.alignment = TextAnchor.MiddleCenter;
                _cueText.raycastTarget = false;
                _cueText.color = Color.green;
            }
            _cueText.text = DisplayText;
        }

        private void OnEnable() { Canvas.willRenderCanvases += OnCueSubmitted; }
        private void OnCueSubmitted()
        {
            if (!HasPendingSignal || _cueText == null || !_cueText.isActiveAndEnabled ||
                _cueText.canvasRenderer.cull || _cueCamera == null || _cueText.text != DisplayText) return;
            Vector3 viewport = _cueCamera.WorldToViewportPoint(_cueText.transform.position);
            if (viewport.z > 0 && viewport.x > 0 && viewport.x < 1 && viewport.y > 0 && viewport.y < 1)
                NotifySignalPresented();
        }
        private void UnbindDrive()
        {
            if (_drive == null) return;
            _drive.CommandSent -= OnCommandSent;
            _drive.AckObserved -= OnAck;
            _drive.ControlEventSent -= OnControlEvent;
        }
        private void OnApplicationFocus(bool focused)
        {
            _focused = focused;
            Emit("focus_changed", new StudyPayload { focused = focused });
            if (!focused && Active) AbortTrial("app_focus_lost");
        }
        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            Emit("pause_changed", new StudyPayload { paused = paused });
            if (paused && Active) AbortTrial("app_paused");
        }
        private void OnDisable()
        {
            Canvas.willRenderCanvases -= OnCueSubmitted;
            if (_cueRoot != null) Destroy(_cueRoot);
            if (!Active) return;
            _drive?.SetStudyMotionBlocked(true);
            Emit("logging_interrupted", new StudyPayload { reason = "component_disabled", dropped_count = _droppedEvents });
            Active = false;
            UnbindDrive();
            _journal?.Close();
            // Fail closed. Server heartbeat timeout records the independent abort.
        }
    }
}
