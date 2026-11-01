using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PuppyPi
{
    [Serializable]
    public sealed class RobotPosePreset
    {
        public string name;
        public float roll;
        public float pitch;
        public float yaw;
        public float height;
        public float x_shift;
        public float stance_x;
        public float stance_y;
    }

    // Matches the user's mac_controller/puppypi_mac_controller.py, not the older
    // public Noetic server's API. These lists whitelist every published argument.
    public static class RobotCommandCatalog
    {
        public static readonly string[] PoseNames = {
            "Stand", "LieDown", "LookDown", "LookDown_10deg", "LookDown_20deg", "LookDown_30deg", "StandLow"
        };
        public static readonly string[] ActionNames = {
            "2_legs_stand", "bow", "boxing", "boxing2", "jump", "kick_ball_left", "kick_ball_left_bak", "kick_ball_right",
            "lie_down", "moonwalk", "nod", "pee", "push-up", "shake_hands", "shake_head", "sit", "spacewalk", "stand",
            "stretch", "temp", "up_stairs_2cm", "up_stairs_3.5cm", "up_stairs_3.5cm0", "up_stairs_3.5cm1", "wave"
        };
        private static readonly HashSet<string> Actions = new HashSet<string>(ActionNames, StringComparer.Ordinal);

        public static bool IsAction(string name) => name != null && Actions.Contains(name);

        public static bool TryGetPose(string name, out RobotPosePreset preset)
        {
            preset = new RobotPosePreset { name = name, height = -10f, x_shift = -0.5f };
            switch (name)
            {
                case "Stand": return true;
                case "LieDown": preset.height = -5f; preset.x_shift = 2f; return true;
                case "LookDown": preset.pitch = -15f * Mathf.Deg2Rad; return true;
                case "LookDown_10deg": preset.pitch = -10f * Mathf.Deg2Rad; preset.height = -9f; preset.x_shift = -0.1f; return true;
                case "LookDown_20deg": preset.pitch = -20f * Mathf.Deg2Rad; preset.height = -9f; preset.x_shift = -0.1f; return true;
                case "LookDown_30deg":
                    preset.pitch = -30f * Mathf.Deg2Rad; preset.height = -9.6f; preset.x_shift = -1.4f; preset.stance_x = 1f; return true;
                case "StandLow": preset.height = -7f; return true;
                default: preset = null; return false;
            }
        }
    }

    public static class RobotRosProtocol
    {
        public const string PoseTopic = "/puppy_control/pose";
        public const string StatusTopic = "/puppy_control/status";
        public const string ArbiterTopic = "/puppy_control/arbiter_state";
        public const string StandService = "/puppy_control/go_home";
        public const string ActionService = "/puppy_control/runActionGroup";
        public const string StopService = "/puppy_control/emergency_stop";
        public const string ResetService = "/puppy_control/reset_estop";

        [Serializable]
        private sealed class PoseMessage
        {
            public float roll, pitch, yaw, height, x_shift, stance_x, stance_y;
            public int run_time = 500;
        }

        public static string AdvertisePose() => "{\"op\":\"advertise\",\"topic\":\"" + PoseTopic + "\",\"type\":\"puppy_control/Pose\"}";

        public static string Subscribe(string topic)
        {
            if (topic != StatusTopic && topic != ArbiterTopic) throw new ArgumentException("Unsupported robot status topic.", nameof(topic));
            return "{\"op\":\"subscribe\",\"topic\":" + Quote(topic) + ",\"type\":\"std_msgs/String\",\"queue_length\":1}";
        }

        public static string Pose(string name)
        {
            if (!RobotCommandCatalog.TryGetPose(name, out RobotPosePreset preset)) throw new ArgumentException("Unknown pose.", nameof(name));
            var message = new PoseMessage {
                roll = preset.roll, pitch = preset.pitch, yaw = preset.yaw, height = preset.height,
                x_shift = preset.x_shift, stance_x = preset.stance_x, stance_y = preset.stance_y
            };
            return "{\"op\":\"publish\",\"topic\":" + Quote(PoseTopic) + ",\"msg\":" + JsonUtility.ToJson(message) + "}";
        }

        public static string Service(string service, string id, string action = null)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 100) throw new ArgumentException("A bounded request id is required.", nameof(id));
            string type;
            string args = "{}";
            switch (service)
            {
                case StandService: type = "std_srvs/Empty"; break;
                case StopService:
                case ResetService: type = "std_srvs/Trigger"; break;
                case ActionService:
                    if (!RobotCommandCatalog.IsAction(action)) throw new ArgumentException("Unknown action group.", nameof(action));
                    type = "puppy_control/SetRunActionName";
                    args = "{\"name\":" + Quote(action + ".d6ac") + ",\"wait\":false}";
                    break;
                default: throw new ArgumentException("Unsupported robot service.", nameof(service));
            }
            return "{\"op\":\"call_service\",\"service\":" + Quote(service) + ",\"type\":" + Quote(type) +
                ",\"args\":" + args + ",\"id\":" + Quote(id) + "}";
        }

        public static string Quote(string value)
        {
            if (value == null) return "null";
            var result = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
                        else result.Append(c);
                        break;
                }
            }
            return result.Append('"').ToString();
        }
    }
}
