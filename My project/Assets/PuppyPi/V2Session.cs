using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PuppyPi
{
    [Serializable]
    public sealed class V2Command
    {
        public int protocol = 2;
        public string type = "command";
        public string client_id;
        public long sequence;
        public bool control_active;
        public float forward;
        public float turn;
    }

    [Serializable]
    public sealed class V2Event
    {
        public int protocol = 2;
        public string type;
        public string client_id;
        public long sequence;
    }

    [Serializable]
    public sealed class V2Ack
    {
        public int protocol;
        public string type;
        public string client_id;
        public long sequence = -1;
        public bool accepted;
        public string reason;
        public bool emergency_stop;
        public string state;
    }

    // JsonUtility does not support the protocol's nested numeric arrays.
    public sealed class V2Path
    {
        public string ClientId { get; }
        public long Sequence { get; }
        private readonly double[][] _points;
        internal V2Path(string clientId, long sequence, double[][] points)
        { ClientId = clientId; Sequence = sequence; _points = points; }
        public string ToJson()
        {
            var json = new StringBuilder("{\"protocol\":2,\"type\":\"path\",\"client_id\":\"");
            json.Append(ClientId).Append("\",\"sequence\":").Append(Sequence.ToString(CultureInfo.InvariantCulture))
                .Append(",\"points\":[");
            for (int i = 0; i < _points.Length; i++)
            {
                if (i != 0) json.Append(',');
                json.Append('[').Append(_points[i][0].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(_points[i][1].ToString("R", CultureInfo.InvariantCulture)).Append(']');
            }
            return json.Append("]}").ToString();
        }
    }

    // No Unity or socket dependency: all time is supplied by the caller's monotonic clock.
    public sealed class V2Session
    {
        private struct Sent
        {
            public double at;
            public string kind;
            public bool neutral;
        }
        private readonly Dictionary<long, Sent> _sent = new Dictionary<long, Sent>();
        private readonly Queue<long> _order = new Queue<long>();
        public string ClientId { get; } = Guid.NewGuid().ToString();
        public long LastSequence { get; private set; }
        public long LastAckSequence { get; private set; } = -1;
        public double LastAckTime { get; private set; } = double.NegativeInfinity;
        public bool Connected { get; private set; }
        public bool NeutralConfirmed { get; private set; }
        public bool Armed { get; private set; }
        public bool Faulted { get; private set; }
        public bool Emergency { get; private set; }
        public bool LastAccepted { get; private set; }
        public string ServerState { get; private set; } = "no_ack";
        public string ServerReason { get; private set; } = "no_ack";
        public string LastAckKind { get; private set; }
        public bool Retired { get; private set; }
        private long _neutralAfter;
        private bool _grabbed;
        private bool _valid;
        private bool _released = true;

        public V2Session(bool emergency = false)
        {
            Emergency = emergency;
        }

        public void LatchEmergency()
        {
            Emergency = true;
            Block();
        }

        public void Block()
        {
            Armed = false;
            NeutralConfirmed = false;
            _neutralAfter = LastSequence;
            _released = !_grabbed;
        }

        public void ObserveInput(bool grabbed, bool valid)
        {
            if ((_valid && !valid) || (_grabbed && !grabbed)) Block();
            if (!grabbed) { _released = true; Armed = false; }
            if (grabbed && !_grabbed)
            {
                Armed = valid && Connected && NeutralConfirmed && _released && !Emergency && !Faulted && !Retired;
                _released = false;
            }
            _grabbed = grabbed;
            _valid = valid;
        }

        public bool CheckTimeout(double now, double timeout)
        {
            if (!Connected || now - LastAckTime <= timeout) return false;
            Connected = false;
            Block();
            return true;
        }

        public V2Command Command(double now, bool active, float forward, float turn)
        {
            bool enabled = active && Armed && Connected && NeutralConfirmed && !Emergency && !Faulted && !Retired;
            return new V2Command {
                client_id = ClientId, sequence = Register(now, "command", !enabled),
                control_active = enabled, forward = enabled ? Clamp(forward) : 0,
                turn = enabled ? Clamp(turn) : 0
            };
        }

        public V2Event Event(double now, string kind)
        {
            if (kind != "emergency_stop" && kind != "clear_emergency" && kind != "disconnect")
                throw new ArgumentException("Unknown v2 event", nameof(kind));
            Block();
            if (kind == "emergency_stop") Emergency = true;
            if (kind == "disconnect") Retired = true;
            return new V2Event { client_id = ClientId, sequence = Register(now, kind, false), type = kind };
        }

        public V2Path Path(double now, IReadOnlyList<double[]> points)
        {
            if (!Connected || !NeutralConfirmed || !LastAccepted || Emergency || Faulted || Retired)
                throw new InvalidOperationException("An accepted neutral session is required for a path");
            if (points == null || points.Count < 1 || points.Count > 20)
                throw new ArgumentException("A path requires 1 to 20 points", nameof(points));
            var copy = new double[points.Count][];
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p == null || p.Length != 2 || !Coordinate(p[0]) || !Coordinate(p[1]))
                    throw new ArgumentException("Path coordinates must be finite and within 100 metres", nameof(points));
                copy[i] = new[] { p[0], p[1] };
            }
            Armed = false;
            return new V2Path(ClientId, Register(now, "path", false), copy);
        }

        public V2Event PathCancel(double now)
        {
            Block();
            return new V2Event { client_id = ClientId, sequence = Register(now, "path_cancel", false), type = "path_cancel" };
        }

        private static bool Coordinate(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= 100;

        private long Register(double now, string kind, bool neutral)
        {
            if (LastSequence >= 9007199254740991L) throw new InvalidOperationException("v2 sequence exhausted");
            long sequence = ++LastSequence;
            _sent.Add(sequence, new Sent { at = now, kind = kind, neutral = neutral });
            _order.Enqueue(sequence);
            while (_order.Count > 128) _sent.Remove(_order.Dequeue());
            return sequence;
        }

        public void ForgetUnsent(long sequence) => _sent.Remove(sequence);

        public bool AcceptAck(V2Ack ack, double now, double maxAge)
        {
            if (!TryGetAckKind(ack, now, maxAge, out _) || ack.sequence <= LastAckSequence) return false;
            Sent sent = _sent[ack.sequence];
            LastAckSequence = ack.sequence;
            LastAckTime = now;
            Connected = true;
            LastAccepted = ack.accepted;
            ServerState = ack.state;
            ServerReason = ack.reason;
            LastAckKind = sent.kind;
            if (ack.state == "fault" || ack.reason == "fault") Faulted = true;
            if (ack.emergency_stop || ack.state == "emergency_stop") Emergency = true;
            if (sent.kind == "clear_emergency" && ack.accepted && !ack.emergency_stop) Emergency = false;
            if (!ack.accepted || ack.state == "timeout" || ack.state == "awaiting_neutral" ||
                Emergency || Faulted || (sent.kind != "command" && sent.kind != "path"))
                Block();
            else if (sent.neutral && ack.sequence > _neutralAfter && ack.state == "idle")
                NeutralConfirmed = true;
            return true;
        }

        // An operation receipt can arrive after a newer heartbeat ACK. Validate its
        // original send record without rolling back global session state/connectivity.
        public bool TryGetAckKind(V2Ack ack, double now, double maxAge, out string kind)
        {
            kind = null;
            if (ack == null || ack.protocol != 2 || ack.type != "ack" || ack.client_id != ClientId ||
                !_sent.TryGetValue(ack.sequence, out Sent sent) || now < sent.at || now - sent.at > maxAge ||
                !KnownState(ack.state) || !KnownReason(ack.reason)) return false;
            kind = sent.kind;
            return true;
        }

        private static float Clamp(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : Math.Max(-1, Math.Min(1, value));
        private static bool KnownState(string state) => state == "idle" || state == "active" || state == "timeout" ||
            state == "emergency_stop" || state == "awaiting_neutral" || state == "fault";
        private static bool KnownReason(string reason) => reason == "ok" || reason == "invalid_packet" ||
            reason == "unsupported_protocol" || reason == "out_of_order" || reason == "not_owner" ||
            reason == "emergency_stop" || reason == "neutral_required" || reason == "fault";
    }
}
