#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
가짜 조이스틱 송신기 (Unity 대용)
================================
Unity의 UDP_Joystick_Sender.cs 와 똑같은 형식("X:12.3,Z:-5.0")으로
가짜 각도를 UDP로 쏩니다. 라즈베리파이도 Quest도 없이 수신기를 테스트할 때 사용하세요.

사용 예:
    # 같은 PC에서 수신기를 띄워두고(다른 터미널), 여기로 흔들리는 값을 쏘기
    python3 mock_joystick_sender.py

    # 특정 라즈베리파이로 쏘기
    python3 mock_joystick_sender.py --host 192.168.0.100

    # 고정 테스트 패킷 몇 개만 보내고 종료
    python3 mock_joystick_sender.py --once
"""

import argparse
import math
import socket
import sys
import time

# Windows 콘솔(cp949)에서도 한글 출력이 깨지거나 죽지 않도록 UTF-8로 강제
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


def main():
    ap = argparse.ArgumentParser(description="가짜 조이스틱 UDP 송신기")
    ap.add_argument("--host", default="127.0.0.1", help="수신기 주소 (기본 127.0.0.1=내 PC)")
    ap.add_argument("--port", type=int, default=5005, help="UDP 포트 (기본 5005)")
    ap.add_argument("--rate", type=float, default=20.0, help="초당 전송 횟수 Hz (기본 20)")
    ap.add_argument("--amp", type=float, default=40.0, help="흔들리는 각도 진폭 (기본 ±40°)")
    ap.add_argument("--once", action="store_true", help="고정 테스트 패킷 몇 개만 보내고 종료")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = (args.host, args.port)

    if args.once:
        samples = ["X:0.0,Z:0.0", "X:20.0,Z:-15.0", "X:-30.0,Z:40.0", "X:0.0,Z:0.0"]
        for msg in samples:
            sock.sendto(msg.encode(), target)
            print(f"[송신] {target[0]}:{target[1]}  {msg}")
            time.sleep(0.2)
        sock.close()
        return

    print(f"[송신 시작] {target[0]}:{target[1]}  {args.rate}Hz  진폭±{args.amp}°  (Ctrl+C 종료)")
    interval = 1.0 / args.rate if args.rate > 0 else 0.05
    start = time.monotonic()
    try:
        while True:
            t = time.monotonic() - start
            # X, Z 를 서로 다른 주기의 사인파로 흔들어 "조이스틱을 휘젓는" 느낌 재현
            x = args.amp * math.sin(t * 0.7)
            z = args.amp * math.sin(t * 1.1 + 1.0)
            msg = f"X:{x:.1f},Z:{z:.1f}"
            sock.sendto(msg.encode(), target)
            print(f"[송신] {msg}", end="\r", flush=True)
            time.sleep(interval)
    except KeyboardInterrupt:
        print("\n[종료]")
    finally:
        sock.close()


if __name__ == "__main__":
    main()
