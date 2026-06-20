#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
로컬 자체 테스트 (라즈베리파이/Unity 없이 한 명령으로 확인)
=========================================================
송신기와 수신기를 한 프로세스에서 동시에 돌려서,
"각도 값이 UDP로 흘러서 수신·파싱되는지"를 즉시 눈으로 확인합니다.

실행:
    python3 local_test.py

성공하면 흔들리는 X/Z 값이 화면에 흐르고, 마지막에 [통과]가 뜹니다.
실제 udp_joystick_receiver.py 의 파싱/변환 함수를 그대로 사용하므로,
여기서 통과하면 수신 로직이 맞다는 뜻입니다.
"""

import importlib.util
import math
import os
import socket
import sys
import threading
import time

# Windows 콘솔(cp949)에서도 한글 출력이 깨지거나 죽지 않도록 UTF-8로 강제
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

PORT = 5005
DURATION = 3.0   # 테스트 시간(초)
RATE = 20.0      # 송신 Hz

# 같은 폴더의 실제 수신기에서 파싱/변환 함수 불러오기
_here = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "rx", os.path.join(_here, "udp_joystick_receiver.py"))
rx = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(rx)


def sender(stop_event):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    start = time.monotonic()
    while not stop_event.is_set():
        t = time.monotonic() - start
        x = 40.0 * math.sin(t * 0.7)
        z = 40.0 * math.sin(t * 1.1 + 1.0)
        sock.sendto(f"X:{x:.1f},Z:{z:.1f}".encode(), ("127.0.0.1", PORT))
        time.sleep(1.0 / RATE)
    sock.close()


def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", PORT))
    srv.settimeout(0.5)

    stop_event = threading.Event()
    th = threading.Thread(target=sender, args=(stop_event,), daemon=True)
    th.start()

    print(f"[로컬 테스트] 127.0.0.1:{PORT} 로 {DURATION}초간 송수신합니다...\n")
    received = 0
    parsed_ok = 0
    deadline = time.monotonic() + DURATION
    while time.monotonic() < deadline:
        try:
            data, _ = srv.recvfrom(1024)
        except socket.timeout:
            continue
        received += 1
        result = rx.parse_packet(data.decode())
        if result is not None:
            parsed_ok += 1
            x_angle, z_angle = result
            forward, turn = rx.to_velocity(x_angle, z_angle)
            print(f"  [수신 {received:3d}] X={x_angle:6.1f} Z={z_angle:6.1f}"
                  f"  ->  전진={forward:+.2f} 회전={turn:+.2f}", end="\r")

    stop_event.set()
    th.join(timeout=1.0)
    srv.close()

    print("\n")
    print(f"  받은 패킷: {received}개,  정상 파싱: {parsed_ok}개")
    if received > 0 and received == parsed_ok:
        print("  [통과] 송신 -> 수신 -> 파싱 전부 정상입니다. (OK)")
    else:
        print("  [실패] 수신/파싱에 문제가 있습니다. (FAIL)")


if __name__ == "__main__":
    main()
