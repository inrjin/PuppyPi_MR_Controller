#!/usr/bin/env python3
"""Print Unity v2 inputs and acknowledge them. No ROS or motor output."""
import argparse
import json
from pathlib import Path
import socket
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path.home() / 'Puppy-PI-Cpp')
    parser.add_argument('--bind', default='0.0.0.0')
    parser.add_argument('--port', type=int, default=5001)
    args = parser.parse_args()
    sys.path.insert(0, str(args.repo / 'puppypi_remote_camera' / 'robot'))
    try:
        from remote_control import V2Session
    except ImportError:
        parser.error('Cannot import remote_control.V2Session; check --repo and the robot checkout.')
    session = V2Session(5.0, 3.0, 0.20, 0.25, 1.0)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind((args.bind, args.port))
        sock.settimeout(0.05)
        print('INPUT CHECK ONLY - NO MOTORS / NO ROS', flush=True)
        print('Listening on {}:{}; Ctrl+C to stop.'.format(*sock.getsockname()), flush=True)
        while True:
            try:
                data, peer = sock.recvfrom(4096)
            except (socket.timeout, ConnectionResetError):
                data = None
            now = time.monotonic()
            expired = session.check_timeouts(now)
            if expired:
                print('TIMEOUT: state={} target={}'.format(session.state, session.target), flush=True)
            if data is None:
                continue
            result = session.handle_datagram(data, peer, now)
            ack = result.ack
            try:
                sock.sendto(json.dumps(ack).encode('utf-8'), peer)
            except OSError as error:
                print('ACK send failed: {}'.format(error), flush=True)
            prefix = 'RX {}:{} seq={} state={} accepted={}'.format(
                peer[0], peer[1], ack['sequence'], ack['state'], ack['accepted'])
            if ack['accepted']:
                packet = json.loads(data)
                if packet['type'] == 'command':
                    print('{} active={} F={:+.3f} T={:+.3f}'.format(prefix,
                          packet['control_active'], packet['forward'], packet['turn']), flush=True)
                else:
                    print('{} event={}'.format(prefix, packet['type']), flush=True)
            else:
                print('{} reason={}'.format(prefix, ack['reason']), flush=True)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print('\nInput check stopped.', flush=True)
