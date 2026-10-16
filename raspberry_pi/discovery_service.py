#!/usr/bin/env python3
"""PuppyPi LAN discovery sidecar (Python 3.8+, standard library only).

This service advertises a v2 control endpoint. It never imports ROS, opens a
control socket, changes ownership, or starts motors. Unity confirms the control
server separately using v2 ACKs after the user presses Connect.
"""
import argparse
import hashlib
import re
import socket
import threading
import time
from pathlib import Path
from urllib.parse import quote

DISCOVERY_PORT = 5002
REQUEST = b"PUPPYPI_DISCOVER/1|"
REPLY = "PUPPYPI_HERE/1"
NONCE = re.compile(rb"[0-9a-f]{32}\Z")


def device_id():
    """Keep identity when DHCP or Wi-Fi changes; never disclose the machine ID."""
    try:
        identity = Path("/etc/machine-id").read_text(encoding="ascii").strip()
    except OSError:
        identity = socket.gethostname()
    return hashlib.sha256(("puppypi-discovery-v1:" + identity).encode()).hexdigest()[:32]


def make_reply(packet, name, identity, control_port):
    if len(packet) != len(REQUEST) + 32 or not packet.startswith(REQUEST):
        return None
    nonce = packet[len(REQUEST):]
    if not NONCE.fullmatch(nonce):
        return None
    return ("{}|{}|{}|{}|{}|2".format(
        REPLY, nonce.decode("ascii"), identity, quote(name, safe=""), control_port
    )).encode("ascii")


class DiscoveryServer:
    def __init__(self, name=None, identity=None, control_port=5001,
                 bind="0.0.0.0", port=DISCOVERY_PORT):
        self.name = name if name is not None else socket.gethostname()[:32]
        self.identity = identity if identity is not None else device_id()
        if (not self.name.strip() or len(self.name) > 32 or
                len(self.name.encode("utf-8")) > 128 or
                any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in self.name)):
            raise ValueError("name must be 1–32 printable characters")
        if not re.fullmatch(r"[0-9a-f]{32}", self.identity):
            raise ValueError("identity must contain 32 lowercase hexadecimal characters")
        if not 1 <= control_port <= 65535:
            raise ValueError("control_port must be between 1 and 65535")
        self.control_port = control_port
        self.bind = bind
        self.port = port
        self._socket = None
        self._thread = None
        self._stop = threading.Event()

    @property
    def bound_port(self):
        return self._socket.getsockname()[1] if self._socket else None

    def start(self):
        if self._socket is not None:
            raise RuntimeError("discovery server is already running")
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            # No SO_REUSEADDR: an accidental second responder should fail visibly.
            sock.bind((self.bind, self.port))
            sock.settimeout(0.1)
        except Exception:
            sock.close()
            raise
        self._socket = sock
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="puppypi-discovery", daemon=True)
        self._thread.start()

    def _run(self):
        window_start = time.monotonic()
        replies = 0
        while not self._stop.is_set():
            try:
                packet, peer = self._socket.recvfrom(1024)
            except socket.timeout:
                continue
            except ConnectionResetError:
                continue  # Windows loopback can report ICMP from a closed client.
            except OSError:
                break
            reply = make_reply(packet, self.name, self.identity, self.control_port)
            if reply is None:
                continue
            now = time.monotonic()
            if now - window_start >= 1:
                window_start, replies = now, 0
            if replies >= 20:
                continue
            replies += 1
            try:
                self._socket.sendto(reply, peer)
            except OSError:
                pass  # A departing client/network must not stop discovery.

    def shutdown(self):
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=1)
            self._thread = None
        if self._socket is not None:
            self._socket.close()
            self._socket = None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", help="Display name (default: Pi hostname)")
    parser.add_argument("--id", help="Stable 32-hex identity override (normally not needed)")
    parser.add_argument("--control-port", type=int, default=5001)
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=DISCOVERY_PORT)
    args = parser.parse_args()
    server = DiscoveryServer(args.name, args.id, args.control_port, args.bind, args.port)
    server.start()
    print("PuppyPi discovery: {} on UDP {}; advertises v2 UDP {}. Control server runs separately.".format(
        server.name, server.bound_port, server.control_port), flush=True)
    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.shutdown()


if __name__ == "__main__":
    main()
