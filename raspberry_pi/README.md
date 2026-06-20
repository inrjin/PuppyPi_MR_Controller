# PuppyPi UDP 조이스틱 수신기 (라즈베리파이)

Meta Quest(Unity)에서 보내는 조이스틱 기울기 각도를 라즈베리파이가 받아서
PuppyPi 로봇 이동 명령으로 바꿔주는 스크립트입니다.

```
[Quest/Unity] --UDP "X:12.3,Z:-5.0"--> [라즈베리파이 udp_joystick_receiver.py] --> PuppyPi 이동
```

## 0. 라즈베리파이도 Unity도 없이 — 로컬 PC에서만 테스트

가장 빠른 확인. 한 명령으로 송신+수신을 동시에 돌려 값이 흐르는지 봅니다:

```bash
cd raspberry_pi
python3 local_test.py
```

→ 흔들리는 `X/Z` 값이 흐르고 마지막에 `[통과] ... ✅` 가 뜨면 수신 로직 정상.

**두 터미널로 나눠서** 더 실제처럼 보고 싶다면:

```bash
# 터미널 A — 수신기 (실제로 쓸 그 스크립트)
python3 udp_joystick_receiver.py --debug --no-robot

# 터미널 B — Unity 대신 가짜 조이스틱이 흔들리는 값을 쏨
python3 mock_joystick_sender.py
```

터미널 A에 `[수신] ... X=.. Z=..` 이 흐르면 성공. (`mock_joystick_sender.py --host <IP>` 로
다른 PC/라즈베리파이를 향해 쏠 수도 있습니다.)

## 1. 먼저 "수신이 잘 되는지" 확인 (로봇 없이)

라즈베리파이에서:

```bash
cd raspberry_pi
python3 udp_joystick_receiver.py --debug --no-robot
```

그 상태로 Quest 앱을 실행하고 조이스틱을 기울이면, 아래처럼 값이 찍히면 성공입니다:

```
[수신]    192.168.0.50  X=  15.5 Z= -30.2  ->  전진=-0.67 회전=+0.34
```

값이 안 보인다면 → 아래 **체크리스트** 참고.

## 2. 실제 로봇까지 움직이기

`udp_joystick_receiver.py` 안의 `RobotDriver` 클래스 `drive()` / `stop()` 에
본인 PuppyPi SDK 호출을 채워 넣으세요(파일에 ROS `cmd_vel` 예시와 SDK 예시 주석 있음). 그 다음:

```bash
python3 udp_joystick_receiver.py        # --debug 붙이면 값도 같이 출력
```

## 3. 동작 파라미터 (파일 상단 상수)

| 상수 | 뜻 | 기본값 |
|---|---|---|
| `DEADZONE` | 이 각도(±) 이내는 중립 처리(노이즈 무시) | 5.0° |
| `MAX_ANGLE` | 이 각도에서 최대 속도(±1.0) | 45.0° |
| `RECV_TIMEOUT` | 이 시간 동안 패킷 없으면 안전 정지 | 1.0초 |

## 체크리스트 (수신이 안 될 때)

1. **IP 일치**: Unity의 `UDP_Joystick_Sender.raspberryPi_IP` 가 라즈베리파이 실제 IP인지 (`hostname -I` 로 확인).
2. **포트 일치**: 양쪽 다 `5005`.
3. **같은 네트워크**: Quest와 라즈베리파이가 동일 Wi‑Fi/공유기.
4. **방화벽**: 포트가 막혀 있으면 열기 → `sudo ufw allow 5005/udp`
5. **빠른 확인**: PC에서 아래로 임의 패킷을 쏴서 수신기에 찍히는지 테스트.
   ```bash
   python3 -c "import socket;socket.socket(socket.AF_INET,socket.SOCK_DGRAM).sendto(b'X:10,Z:-10',('라즈베리파이IP',5005))"
   ```

## 자동 실행(선택) — 부팅 시 자동 시작

```bash
# 예: systemd 서비스로 등록하고 싶다면 (경로/사용자 맞게 수정)
# /etc/systemd/system/puppypi-teleop.service 참고용
```
