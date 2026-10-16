using UnityEngine;

namespace PuppyPi
{
    public sealed class BallGripHud : MonoBehaviour
    {
        public BallGripInput input;
        public PuppyPiV2Client client;
        public bool showQuestHud = true;
        private TextMesh _text;
        private PuppyPiConnection _connection;
        private readonly PuppyPiConnectionPanel _panel = new PuppyPiConnectionPanel();

        private void Start()
        {
            _connection = GetComponent<PuppyPiConnection>();
            if (_connection == null) _connection = gameObject.AddComponent<PuppyPiConnection>();
            _connection.client = client;
            if (!showQuestHud) return;
            GameObject panel = new GameObject("BallGrip status (Quest)");
            panel.transform.SetParent(transform, false);
            _text = panel.AddComponent<TextMesh>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.GetComponent<MeshRenderer>().sharedMaterial = _text.font.material;
            _text.fontSize = 48;
            _text.characterSize = 0.003f;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.color = Color.cyan;
        }

        private void Update()
        {
            // Quest Touch: B = latched stop; Y = explicit clear (grip released).
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) client.EmergencyStop();
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) client.ClearEmergency();
        }

        private void LateUpdate()
        {
            if (_text == null) return;
            _text.text = Status();
            _text.transform.position = transform.TransformPoint(new Vector3(0, -0.12f, 0.35f));
            if (Camera.main != null) _text.transform.rotation = Camera.main.transform.rotation;
        }

        private string Status()
        {
            if (input == null || client == null) return "BallGrip references missing";
            V2Session state = client.Session;
            // Keep the headset HUD ASCII: the built-in runtime font has no guaranteed Korean glyphs on Quest.
            return $"PUPPYPI {(client.networkEnabled ? state.Connected ? "CONNECTED" : "WAITING FOR ACK" : "DISCONNECTED")}\n" +
                $"{client.robotAddress}:{client.port}\n" +
                $"BALL GRIP  F {input.Axes.x:F2}  T {input.Axes.y:F2}\n" +
                $"Grab {input.Grabbed}  Tracking {input.TrackingValid}\n" +
                $"TX {(client.LastCommand != null && client.LastCommand.control_active ? "ACTIVE" : "NEUTRAL")}  ACK {client.AckAge:F2}s\n" +
                $"{state.ServerState} / {state.ServerReason}  accepted={state.LastAccepted}\n" +
                $"{client.TransportStatus}\nB: EMERGENCY STOP   Y: CLEAR (release first)";
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        private void OnGUI()
        {
            _panel.Draw(_connection, input);
        }
#endif
    }
}
