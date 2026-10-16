"""Run the real robotics repository tests and replay Unity's JSON over loopback UDP.

No ROS or motor APIs are imported by the UDP fixture. Supply a separate robotics
checkout; raspberry_pi/ is legacy and intentionally never used here.
"""
import argparse
import importlib
import json
from pathlib import Path
import socket
import sys
import time
import unittest

for stream in (sys.stdout, sys.stderr):
    if hasattr(stream, "reconfigure"):
        stream.reconfigure(encoding="utf-8")


def config(port=0):
    return dict(bind_address="127.0.0.1", control_port=port, protocol_version=2,
                command_timeout_seconds=0.25, client_lease_timeout_seconds=1.0,
                stop_repetitions=1, velocity_limits=dict(max_forward_cm_s=5.0,
                max_reverse_cm_s=3.0, max_yaw_rate_rad_s=0.2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--packets", type=Path, default=Path("TestResults/unity-v2-packets.jsonl"))
    parser.add_argument("--serve", action="store_true", help="Keep a motor-free loopback server on UDP 5001")
    parser.add_argument("--skip-tests", action="store_true")
    args = parser.parse_args()
    package = args.repo.resolve() / "puppypi_remote_camera"
    sys.path.insert(0, str(package / "robot"))
    remote = importlib.import_module("remote_control")
    if not args.skip_tests and not args.serve:
        result = unittest.TextTestRunner(verbosity=1).run(unittest.defaultTestLoader.discover(str(package / "tests")))
        if not result.wasSuccessful():
            return 1
    emitted = []

    def output(x, y, yaw):
        emitted.append((x, y, yaw))
        if args.serve:
            print(json.dumps(dict(test_linear_x_m_s=x / 100, test_linear_y_m_s=y / 100,
                                  test_angular_z_rad_s=yaw)), flush=True)

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(("127.0.0.1", 0))
        local_port = probe.getsockname()[1]
    server = remote.RemoteControlServer(config(5001 if args.serve else local_port), output)
    server.start()
    discovery = None
    try:
        if args.serve:
            sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "raspberry_pi"))
            from discovery_service import DiscoveryServer
            discovery = DiscoveryServer(name="Local test (no motors)", control_port=server.bound_port, bind="127.0.0.1")
            discovery.start()
            print("Motor-free loopback v2 server: 127.0.0.1:5001. Ctrl+C to stop.", flush=True)
            print("Discovery: 127.0.0.1:5002. In Unity, click Search then Connect.", flush=True)
            while True:
                time.sleep(0.1)
        packets = [line.encode("utf-8") for line in args.packets.read_text(encoding="utf-8-sig").splitlines() if line]
        expected_states = ["idle", "active", "emergency_stop", "awaiting_neutral", "idle", "awaiting_neutral"]
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.settimeout(1)
            client.bind(("127.0.0.1", 0))
            replies = []
            for packet, state in zip(packets, expected_states, strict=True):
                parsed = remote.parse_v2_packet(packet)
                client.sendto(packet, ("127.0.0.1", server.bound_port))
                data, address = client.recvfrom(2048)
                ack = json.loads(data)
                assert address == ("127.0.0.1", server.bound_port), address
                assert ack["sequence"] == parsed.sequence and ack["accepted"], ack
                assert ack["state"] == state, ack
                replies.append(ack)
            assert any(abs(x - 3) < 1e-5 and y == 0 and abs(yaw + 0.06) < 1e-6 for x, y, yaw in emitted), emitted
            assert emitted[-1] == remote.STOP, emitted
            args.packets.with_name("robot-v2-acks.json").write_text(json.dumps(replies, indent=2), encoding="utf-8")
            args.packets.with_name("robot-v2-acks.jsonl").write_text("\n".join(json.dumps(ack) for ack in replies), encoding="utf-8")
            print("PASS: 6 Unity-serialized packets -> strict real parser -> UDP ACK on same socket")
            print("PASS: 0.6/-0.3 -> 3 cm/s (0.03 m/s), -0.06 rad/s; stop/clear/disconnect")
    except KeyboardInterrupt:
        pass
    finally:
        if discovery is not None:
            discovery.shutdown()
        server.shutdown()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
