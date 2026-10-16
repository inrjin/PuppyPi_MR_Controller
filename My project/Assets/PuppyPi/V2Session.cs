using System;
using System.Collections.Generic;

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
            if (ack == null || ack.protocol != 2 || ack.type != "ack" || ack.client_id != ClientId ||
                ack.sequence <= LastAckSequence || !_sent.TryGetValue(ack.sequence, out Sent sent) ||
                now < sent.at || now - sent.at > maxAge || !KnownState(ack.state) || !KnownReason(ack.reason)) return false;
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
                Emergency || Faulted || sent.kind != "command")
                Block();
            else if (sent.neutral && ack.sequence > _neutralAfter && ack.state == "idle")
                NeutralConfirmed = true;
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
