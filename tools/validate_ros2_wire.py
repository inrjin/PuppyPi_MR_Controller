#!/usr/bin/env python3
"""Replay Unity packets through the ROS 2 repository's motor-free UDP server.

Only protocol_v2.py and v2_server.py are imported. All sockets bind to localhost;
receiver_node, ROS and motor adapters are never imported.
"""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import socket
import sys
import types


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot-src", required=True, type=Path)
    parser.add_argument("--packets", type=Path, default=Path("TestResults/unity-v2-packets.jsonl"))
    parser.add_argument("--report", type=Path, default=Path("TestResults/robot-v2-wire.json"))
    args = parser.parse_args()
    source = args.robot_src.resolve()
    package = types.ModuleType("puppy_vr_bridge")
    package.__path__ = [str(source)]
    sys.modules[package.__name__] = package
    protocol = importlib.import_module("puppy_vr_bridge.protocol_v2")
    transport = importlib.import_module("puppy_vr_bridge.v2_server")
    outputs = []
    session = protocol.V2Session(5.0, 3.0, .2, .25, 1.0)
    server = transport.V2UdpServer(session, lambda value, reason: outputs.append(value),
                                   bind_address="127.0.0.1", port=0)
    packets = args.packets.read_text(encoding="utf-8-sig").splitlines()
    expected = ["idle", "active", "emergency_stop", "awaiting_neutral", "idle", "awaiting_neutral"]
    if len(packets) != len(expected):
        raise AssertionError("Expected six Unity-generated protocol fixtures")
    acks = []
    server.start()
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.bind(("127.0.0.1", 0))
            client.settimeout(2)
            for packet, state in zip(packets, expected):
                parsed = protocol.parse_v2_packet(packet.encode())
                client.sendto(packet.encode(), ("127.0.0.1", server.bound_port))
                raw, peer = client.recvfrom(2048)
                ack = json.loads(raw)
                if (peer != ("127.0.0.1", server.bound_port) or not ack["accepted"] or
                        ack["sequence"] != parsed.sequence or ack["state"] != state):
                    raise AssertionError((peer, ack, state))
                acks.append(ack)
    finally:
        server.shutdown()
    if not any(abs(x - 3) < 1e-5 and y == 0 and abs(yaw + .06) < 1e-6 for x, y, yaw in outputs):
        raise AssertionError("Actual server did not decode the expected velocity")
    if outputs[-1] != protocol.STOP:
        raise AssertionError("Actual server did not finish with neutral output")
    ack_path = args.packets.with_name("robot-v2-acks.jsonl")
    ack_path.write_text("\n".join(json.dumps(ack) for ack in acks) + "\n")
    report = {
        "passed": len(acks), "real_loopback_udp": True, "motor_commands_sent": False,
        "states": [ack["state"] for ack in acks], "ack_file": str(ack_path),
        "source_sha256": {name: hashlib.sha256((source / name).read_bytes()).hexdigest()
                          for name in ["protocol_v2.py", "v2_server.py"]},
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    print("PASS: six Unity packets -> actual ROS 2 UDP parser/server -> same-socket ACKs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
