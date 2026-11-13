using System;

namespace PuppyPi
{
    [Serializable]
    public sealed class StudySettings
    {
        public string profile = "pilot";
        public int speed_percent;
        public float max_tilt_degrees;
        public float deadzone_degrees;
        public float sensitivity;
        public bool invert_forward;
        public bool invert_turn;
        public bool camera_visible;
        public bool map_visible;
        public bool layout_locked = true;
        public int command_hz = 20;
        public string output_filter = "none";
        public string raw_units = "degrees";
        public string input_modality = "hand_tracking";
        public float normalized_demand_threshold = 0.05f;
        public string app_version;
        public string app_build_guid;
        public string source_revision;
        public string source_fingerprint;
        public string source_tree_status;
        public string build_utc;
        public string unity_version;
        public string device_model;
        public string console_pose;
        public string camera_pose;
        public string dashboard_pose;
    }

    [Serializable]
    public sealed class StudyBuildInfo
    {
        public string source_revision;
        public string source_fingerprint;
        public string source_tree_status;
        public string build_utc;
    }

    [Serializable]
    public sealed class StudyStatus
    {
        public int schema_version;
        public string clock_id;
        public long server_t_mono_us;
        public string experiment_session_id;
        public string trial_id;
        public string participant_id;
        public string control_client_id;
        public string interface_id;
        public int period_index;
        public string difficulty_id;
        public string state;
        public string signal_id;
        public bool show_start_signal;
        public bool motion_permitted;
        public bool neutral_requested;
        public string outcome;
        public string reason;
        public bool mock_mode;
        public bool ready;
        public string[] readiness_reasons;
        public bool settings_present;
        public StudySettings settings;
    }

    [Serializable]
    public sealed class StudyPayload
    {
        public float raw_forward;
        public float raw_turn;
        public float normalized_forward;
        public float normalized_turn;
        public float forward;
        public float turn;
        public bool engaged;
        public bool input_valid;
        public bool focused;
        public bool paused;
        public bool demanded;
        public bool control_active;
        public bool accepted;
        public bool applied;
        public bool emergency_stop;
        public bool neutral_confirmed;
        public bool blocked;
        public string[] block_reasons;
        public long sequence;
        public string reason;
        public string state;
        public string raw_units;
        public string input_modality;
        public string signal_id;
        public string timestamp_basis;
        public string control_client_id;
        public string source_clock_id;
        public string reference_clock_id;
        public long offset_us;
        public long uncertainty_us;
        public bool valid;
        public long dropped_count;
        public StudySettings settings;
    }

    [Serializable]
    public sealed class StudyEvent
    {
        public string event_id = Guid.NewGuid().ToString("N");
        public string event_type;
        public string source_clock_id;
        public long source_t_mono_us;
        public StudyPayload payload;
    }

    // Offset maps source(client) monotonic time into reference(server) time.
    // The bound includes the entire round-trip asymmetry; it is not one-way latency.
    public sealed class StudyClockAlignment
    {
        public string ReferenceClockId { get; private set; }
        public long OffsetUs { get; private set; }
        public long UncertaintyUs { get; private set; } = long.MaxValue;
        public long MeasuredAtUs { get; private set; }
        public bool Valid { get; private set; }

        public bool Observe(string clockId, long clientSendUs, long serverReceiveUs, long serverSendUs,
            long clientReceiveUs, long maximumUncertaintyUs)
        {
            if (string.IsNullOrWhiteSpace(clockId) || clientSendUs < 0 || serverReceiveUs < 0 ||
                serverSendUs < serverReceiveUs || clientReceiveUs < clientSendUs || maximumUncertaintyUs < 0)
            { Valid = false; return false; }
            long elapsed = clientReceiveUs - clientSendUs;
            long processing = serverSendUs - serverReceiveUs;
            if (processing > elapsed) { Valid = false; return false; }
            long uncertainty = (elapsed - processing + 1) / 2;
            if (ReferenceClockId != clockId || clientReceiveUs - MeasuredAtUs > 10000000 || uncertainty <= UncertaintyUs)
            {
                ReferenceClockId = clockId;
                OffsetUs = (serverReceiveUs - clientSendUs) / 2 + (serverSendUs - clientReceiveUs) / 2;
                UncertaintyUs = uncertainty;
                MeasuredAtUs = clientReceiveUs;
            }
            Valid = UncertaintyUs <= maximumUncertaintyUs;
            return Valid;
        }

        public bool IsFresh(long nowUs, long maxAgeUs) => Valid && nowUs >= MeasuredAtUs && nowUs - MeasuredAtUs <= maxAgeUs;
        public void Invalidate() { Valid = false; }
        public void Reset()
        {
            Valid = false;
            ReferenceClockId = null;
            OffsetUs = MeasuredAtUs = 0;
            UncertaintyUs = long.MaxValue;
        }
    }

    public static class StudyMotionGate
    {
        public static bool CanObserveStartGrab(StudyStatus status, string reason, string presentedSignal) =>
            status != null && status.state == "countdown" && status.show_start_signal && reason == "study_countdown" &&
            !string.IsNullOrEmpty(presentedSignal) && status.signal_id == presentedSignal;

        public static bool ValidSettings(StudySettings settings) => settings != null &&
            settings.speed_percent >= 10 && settings.speed_percent <= 100 && settings.speed_percent % 10 == 0 &&
            settings.max_tilt_degrees >= 5 && settings.max_tilt_degrees <= 60 &&
            settings.deadzone_degrees >= 0 && settings.deadzone_degrees <= 20 && settings.deadzone_degrees < settings.max_tilt_degrees &&
            settings.sensitivity >= 0.1f && settings.sensitivity <= 3 &&
            settings.normalized_demand_threshold >= 0 && settings.normalized_demand_threshold < 1 &&
            settings.command_hz == 20 && settings.output_filter == "none";

        public static string BlockReason(StudyStatus status, string clientId, string sessionId, bool fresh,
            bool journalHealthy, bool clockFresh, string presentedSignal, bool mockIsolated)
        {
            if (!fresh) return "study_channel_unavailable";
            if (!journalHealthy) return "client_log_unavailable";
            if (!clockFresh) return "clock_alignment_unavailable";
            if (status == null || status.schema_version != 1) return "study_protocol_mismatch";
            if (status.mock_mode && !mockIsolated) return "mock_cannot_control_physical_robot";
            if (string.IsNullOrEmpty(status.experiment_session_id) || status.experiment_session_id != sessionId)
                return "study_session_mismatch";
            if (status.control_client_id != clientId || status.interface_id != "mr_ballgrip") return "study_client_mismatch";
            if (status.state != "running" || !status.motion_permitted || status.neutral_requested) return "study_" + status.state;
            if (string.IsNullOrEmpty(status.signal_id) || status.signal_id != presentedSignal) return "start_signal_not_presented";
            return null;
        }
    }
}
