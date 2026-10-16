#!/usr/bin/env python3
"""Temporarily share only the Pi discovery setup files with one LAN peer."""
import argparse
import hashlib
import ipaddress
import secrets
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", required=True, help="PC's LAN IPv4 address")
    parser.add_argument("--peer", required=True, help="Pi's current IPv4 address")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--minutes", type=int, default=30)
    args = parser.parse_args()
    bind = str(ipaddress.IPv4Address(args.bind))
    peer = str(ipaddress.IPv4Address(args.peer))
    if ipaddress.IPv4Address(bind).is_unspecified:
        parser.error("--bind must be a specific LAN address")
    if not 1 <= args.port <= 65535 or not 1 <= args.minutes <= 60:
        parser.error("port must be 1..65535; minutes must be 1..60")

    source = Path(__file__).resolve().parents[1] / "raspberry_pi"
    token = secrets.token_hex(4)
    payloads = {
        "/{}/{}".format(token, name): (source / name).read_bytes()
        for name in ("discovery_service.py", "puppypi-discovery.service")
    }

    class Handler(BaseHTTPRequestHandler):
        def setup(self):
            super().setup()
            self.connection.settimeout(5)

        def do_GET(self):
            if self.client_address[0] not in (peer, bind):
                self.send_error(403)
                return
            payload = payloads.get(self.path)
            if payload is None:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, fmt, *values):
            print("{} {}".format(self.client_address[0], fmt % values), flush=True)

    with HTTPServer((bind, args.port), Handler) as server:
        server.timeout = 0.5
        for path, payload in payloads.items():
            print("http://{}:{}{} sha256={}".format(
                bind, args.port, path, hashlib.sha256(payload).hexdigest()), flush=True)
        print("Only Pi {} and local checks allowed; expires in {} minutes.".format(
            peer, args.minutes), flush=True)
        deadline = time.monotonic() + args.minutes * 60
        try:
            while time.monotonic() < deadline:
                server.handle_request()
        except KeyboardInterrupt:
            pass
    print("Transfer server stopped.", flush=True)


if __name__ == "__main__":
    main()
