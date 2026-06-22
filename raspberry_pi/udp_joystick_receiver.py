#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PuppyPi UDP 조이스틱 수신기
===========================
Meta Quest(Unity)의 UDP_Joystick_Sender.cs 가 보내는 조이스틱 기울기 각도를
라즈베리파이에서 받아 파싱하고, PuppyPi 로봇 이동 명령으로 변환합니다.

수신 패킷 형식 (Unity 송신과 동일):
    "X:12.3,Z:-5.0"
      X = 좌우 기울기(축에 따라 좌/우 회전에 사용)
      Z = 앞뒤 기울기(전진/후진에 사용)
    값 범위: 대략 -180.0 ~ 180.0

사용법:
    # 1) 우선 "수신이 잘 되는지"만 확인 (로봇 제어 없이 출력만)
    python3 udp_joystick_receiver.py --debug

    # 2) 실제 로봇까지 움직이기 (RobotDriver 부분을 실제 SDK로 채운 뒤)
    python3 udp_joystick_receiver.py

방화벽: UDP 5005 포트가 열려 있어야 합니다.
"""

import argparse
import socket
<<<<<<< HEAD
import time

=======
import sys
import time

# Windows 콘솔(cp949)에서도 한글 출력이 깨지거나 죽지 않도록 UTF-8로 강제
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

>>>>>>> 6201a6059921fe2318b74fb30483305acb5bd96e

# ───────────────────────────── 설정 (필요하면 조정) ─────────────────────────────
DEFAULT_HOST = "0.0.0.0"   # 모든 인터페이스에서 수신 (Quest가 어느 IP로 보내든 받음)
DEFAULT_PORT = 5005

DEADZONE = 5.0     # 이 각도(±) 이내는 "중립"으로 보고 무시 (손떨림/노이즈 방지)
MAX_ANGLE = 45.0   # 이 각도에서 최대 속도. 그 이상 기울여도 100%로 고정.
RECV_TIMEOUT = 1.0     # 이 시간(초) 동안 패킷이 없으면 안전상 로봇 정지
PRINT_HZ = 10          # 디버그 출력 빈도 제한 (초당 N회)


def parse_packet(text):
    """
    'X:12.3,Z:-5.0' 형태의 문자열을 (x, z) float 튜플로 파싱.
    형식이 깨졌으면 None 을 돌려준다(예외로 죽지 않음).
    """
    values = {}
    for part in text.strip().split(","):
        if ":" not in part:
            continue
        key, _, val = part.partition(":")
        key = key.strip().upper()
        try:
            values[key] = float(val.strip())
        except ValueError:
            return None
    if "X" not in values or "Z" not in values:
        return None
    return values["X"], values["Z"]


def to_velocity(x_angle, z_angle):
    """
    기울기 각도 → 이동 명령으로 변환.
      forward : 전/후진 (-1.0 ~ 1.0), z를 앞으로 기울이면 전진
      turn    : 좌/우 회전 (-1.0 ~ 1.0), x를 기울이면 회전
    데드존 안이면 0, MAX_ANGLE에서 ±1.0으로 정규화한다.
    """
    def norm(angle):
        if abs(angle) < DEADZONE:
            return 0.0
        v = angle / MAX_ANGLE
        return max(-1.0, min(1.0, v))

    forward = norm(z_angle)
    turn = norm(x_angle)
    return forward, turn


class RobotDriver:
    """
    실제 PuppyPi 제어를 담당하는 부분.
    ★ 여기 drive()/stop() 안을 본인의 PuppyPi SDK 호출로 채우세요. ★

    아래는 흔한 두 가지 방식의 예시입니다(주석). 환경에 맞는 것 하나를 살려 쓰세요.
    지금 상태(주석 그대로)에서는 로봇을 움직이지 않고 출력만 합니다.
    """

    def __init__(self, enabled=True):
        self.enabled = enabled
        # 예시 A) ROS(예: PuppyPi ROS) - cmd_vel 토픽 퍼블리시
        # import rospy
        # from geometry_msgs.msg import Twist
        # rospy.init_node("quest_udp_teleop", anonymous=True)
        # self.pub = rospy.Publisher("/cmd_vel", Twist, queue_size=1)

        # 예시 B) Hiwonder 보행 제어 라이브러리(버전마다 다름)
        # from puppy_control_sdk import Puppy   # ← 실제 모듈명으로 교체
        # self.puppy = Puppy()

    def drive(self, forward, turn):
        if not self.enabled:
            return
        # ── 예시 A) ROS cmd_vel ─────────────────────────────
        # twist = Twist()
        # twist.linear.x = forward * 0.15     # m/s (로봇 최대 속도에 맞게 스케일)
        # twist.angular.z = -turn * 1.0       # rad/s
        # self.pub.publish(twist)

        # ── 예시 B) Hiwonder SDK ────────────────────────────
        # self.puppy.move(x=forward, yaw=turn)
        pass

    def stop(self):
        if not self.enabled:
            return
        # twist = Twist(); self.pub.publish(twist)   # ROS
        # self.puppy.stop()                          # SDK
        pass


def main():
    ap = argparse.ArgumentParser(description="PuppyPi UDP 조이스틱 수신기")
    ap.add_argument("--host", default=DEFAULT_HOST, help="바인드할 주소 (기본 0.0.0.0)")
    ap.add_argument("--port", type=int, default=DEFAULT_PORT, help="UDP 포트 (기본 5005)")
    ap.add_argument("--debug", action="store_true", help="받은 값/변환 결과를 콘솔 출력")
    ap.add_argument("--no-robot", action="store_true", help="로봇 제어 끄고 수신/파싱만 테스트")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((args.host, args.port))
    sock.settimeout(RECV_TIMEOUT)

    robot = RobotDriver(enabled=not args.no_robot)

    print(f"[수신 대기] {args.host}:{args.port}  (Ctrl+C 로 종료)")
    print(f"  데드존=±{DEADZONE}°  최대각={MAX_ANGLE}°  무신호정지={RECV_TIMEOUT}s")

    last_print = 0.0
    stopped = False  # 신호 끊겼을 때 정지 명령 중복 방지

    try:
        while True:
            try:
                data, addr = sock.recvfrom(1024)
            except socket.timeout:
                # 일정 시간 패킷이 없으면 안전상 정지
                if not stopped:
                    robot.stop()
                    stopped = True
                    if args.debug:
                        print("[무신호] 안전 정지")
                continue

            stopped = False
            text = data.decode("utf-8", errors="replace")
            parsed = parse_packet(text)
            if parsed is None:
                if args.debug:
                    print(f"[형식오류] {addr[0]} -> {text!r}")
                continue

            x_angle, z_angle = parsed
            forward, turn = to_velocity(x_angle, z_angle)
            robot.drive(forward, turn)

            if args.debug:
                now = time.monotonic()
                if now - last_print >= 1.0 / PRINT_HZ:
                    last_print = now
                    print(f"[수신] {addr[0]:>15}  X={x_angle:6.1f} Z={z_angle:6.1f}"
                          f"  ->  전진={forward:+.2f} 회전={turn:+.2f}")
    except KeyboardInterrupt:
        print("\n[종료] 정지 명령 후 소켓 닫음")
    finally:
        robot.stop()
        sock.close()


if __name__ == "__main__":
    main()
