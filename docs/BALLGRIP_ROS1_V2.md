# BallGrip 2축 / ROS1 v2 실행·검증 안내

작성: 2026-10-03. 구현 대상은 `My project/Assets/Scenes/SampleScene.unity`다.

2026-10-04 업데이트: IP 변경에 대응하는 **Game 화면 연결 UI**와 Pi 검색 서비스를 추가했다.
평소 연결은 [CONNECTION_UI.md](CONNECTION_UI.md)의 **기기 검색 → 연결** 절차를 사용한다.

## 기준과 범위

- 작업 시작 시 사용자 변경 없음. 적용할 `AGENTS.md` 없음.
- Unity 로컬 HEAD와 GitHub HEAD: `035bfb9421d52e78135d3536264df7c7a98988f7`.
- 로봇 GitHub HEAD와 로컬 검증 복제본: `6648399fb612db1cfee4b35349fc12afaeb770a5`.
- 로봇 기준: https://github.com/Ksyear/Puppy-PI-Cpp 의 `puppypi_remote_camera/`.
- Unity `6000.0.62f1`, Meta Core `85.0.0`, Interaction OVR `201.0.0`의 설치된 SDK로 컴파일했다.
- `raspberry_pi/`의 이전 5005 조종 구현은 보존한다. 새 `discovery_service.py`만 v2 기기 검색에 사용한다.
- 1·2단계 코드와 로컬 검증을 진행했다. Pi 접속·ROS1 실행·Quest 실제 손 추적·보행 시험은 아직 수행하지 않았다. 영상 구현은 이번 변경에 없다.

## 1단계: 네트워크 없이 확인

1. Unity Hub에서 `My project`를 열고 `SampleScene`을 연다.
2. `Console_BallGrip`의 `PuppyPiV2Client.Network Enabled`는 **꺼진 상태**가 기본이다. `Robot Address`는 빈 문자열이다.
3. Editor Play에서 `BallGripInput.Editor Preview`를 켠다. `Preview Grabbed`를 켜고 `Preview Tilt`를 조절한다. X는 전후 각도, Y는 회전 입력 각도다. 예: `(26, -15.5)`는 감도 1·deadzone 5° 기준 약 `(0.6, -0.3)`이다.
4. `Preview Grabbed`를 끄면 즉시 입력이 0이 된다. 관절이 돌아오는 동안에도 0이어야 한다. `Preview Tracking`을 끄고 다시 켜도 놓기/새 잡기 전에는 입력이 살아나지 않아야 한다.
5. Quest 손/컨트롤러 시험에서는 Editor Preview가 사용되지 않는다. 실제 선택 소스의 추적 유효성을 확인한다. Quest에서 전후·좌우 부호와 소환 후 조작감을 직접 확인한다.

기존 HingeJoint는 로컬 X축 하나만 허용했다. 이제 ConfigurableJoint의 위치 X/Y/Z와 각도 Z를 잠그고 각도 X/Y를 각각 ±40° 허용한다. BallGrip 받침대는 X=-90°이므로 받침대 로컬 +Z가 위, -Y가 전방, -X가 왼쪽이다. 입력은 중립 상대 자세의 방향 벡터에서 다음처럼 계산한다.

```text
forward_angle = atan2(-direction.y, direction.z)
turn_angle    = atan2(-direction.x, direction.z)
value = sign(angle) × clamp01((abs(angle)-deadzone)/(maxTilt-deadzone) × sensitivity)
```

앞은 forward 양수, 왼쪽은 turn 양수다. 후진해도 turn 부호를 뒤집지 않는다. 대각선 두 축을 함께 사용하며 로봇 y 이동은 없다. 최대 각도·중앙 구간·감도·축 반전은 Inspector에서 조절한다.

`BallGripJointDriver`는 Meta `ITransformer`로 연결된다. 손의 위치 변화로 관절 목표 각도를 구동하며, 손잡이 비틀림은 입력으로 사용하지 않는다. 별도의 고정 관절을 추가하지 않는다. 놓기/취소/입력 무효화 후에는 목표 중립과 스프링으로 복귀한다. Grabbable의 kinematic 전환·던지기는 꺼져 있다. `SummonCockpit`은 BallGrip에 연결하고, 받침대의 원래 -90° 자세와 자식 Rigidbody 상대 자세를 유지한다. 소환 시에는 중립 요청과 새 잡기를 요구한다.

StickGrip 루트와 장면 안 기존 5005 송신기 3개, 기존 X/Z HUD를 비활성화했다. 자산은 남겨두었다. 새 v2 송수신기는 1개다.

## 2단계: localhost 시험

별도 로봇 체크아웃과 Python/PyYAML이 필요하다. 이 작업의 임시 체크아웃은 Git에서 제외된 `.local/Puppy-PI-Cpp`에 있다. 다른 PC에서는 직접 복제한다.

```powershell
git clone https://github.com/Ksyear/Puppy-PI-Cpp.git .local/Puppy-PI-Cpp
# 아래 별도 패치 설명에 따라 검토·적용 후:
python tools/validate_v2.py --repo .local/Puppy-PI-Cpp --serve
```

이 서버는 `127.0.0.1:5001`에서 실제 v2 파서·상태기를 사용한다. ROS·모터 API를 호출하지 않고 변환값만 출력한다. Unity에서 `Robot Address=127.0.0.1`, `Port=5001`, `Network Enabled=true`로 설정한다. 먼저 그립을 놓고 `idle`, 중립 ACK를 확인한 다음 새로 잡는다. Editor Preview는 loopback 주소만 허용한다.

게임 화면 HUD에는 두 축, grab/tracking, 마지막 송신 활성 여부, ACK 나이, 서버 state/reason/accepted가 나온다. Editor HUD에는 주소·UUID·sequence도 나온다. `accepted=true`는 명령 수락이며 실제 보행 성공이 아니다.

- Quest Touch **오른쪽 B**: 비상정지 요청. **왼쪽 Y**: 그립을 놓은 상태에서 명시적 해제. **오른쪽 A**: 콕핏 소환.
- Quest에는 월드 공간 상태 텍스트도 표시한다. 실제 기기의 텍스트 위치·가독성·버튼 동작은 확인이 필요하다.
- Editor에는 비상정지/해제 버튼이 있다. 이것을 Quest 손 추적 UI로 간주하지 않는다. **컨트롤러 없는 손 추적 전용 비상정지 UI는 미구현**이다. 기기 시험은 정지 버튼에 접근 가능한 Touch 컨트롤러 구성으로 준비한다.

### 통신·재조작 정책

- 동일 UDP 소켓에서 command와 이벤트 송신 및 ACK 수신. 일반 command는 평균 20Hz, 놓기·추적 상실·pause·focus 상실은 즉시 중립 요청.
- ACK는 대상 IP/포트, 엄격한 8필드 형식, 현재 UUID, 실제 송신한 sequence, 증가 순서, 송신 이후 허용 나이를 모두 확인한다.
- ACK timeout **0.30초**, ACK 최대 나이 **0.20초**는 Inspector에서 바꿀 수 있는 임시 시험값이다. 공동 합의값이 아니다.
- 시작·ACK 상실·서버 timeout·비상정지 해제 뒤에는 새로운 중립 ACK와 놓기/새 잡기가 필요하다. 복구 중 계속 잡고 있으면 재출발하지 않는다.
- `fault`는 같은 세션에서 유지한다. 서버를 복구/재시작한 뒤 연결 UI에서 명시적으로 다시 연결해 새 세션으로 시험한다.
- 한 실행/클라이언트 인스턴스 동안 UUID와 sequence를 유지한다. pause는 disconnect를 보내지 않는다. 소켓 오류 후에는 기존 주소의 1초 lease를 고려해 1.1초 후 동일 UUID로 재시도한다.
- 최종 종료 또는 `DisconnectSession()`은 중립+disconnect를 보내고 소켓을 닫는다. UDP 종료 패킷 전달은 보장되지 않으므로 서버 watchdog이 필요하다. 연결 UI의 명시적 **연결**은 새 UUID를 만들며 Play/앱 재시작은 필요 없다.
- 주소/포트 변경은 연결 UI에서 한다. 이전 목적지에 중립+disconnect 후 새 목적지로 연결한다. 자동 소켓 복구는 UUID를 유지하고, 재연결 후 중립 ACK와 놓기/새 잡기를 요구한다. 로컬 비상정지는 재연결로 해제하지 않는다.

## 로봇 watchdog 별도 패치

`patches/robot-watchdog-before-command.patch`는 **로봇 저장소용**이다. Unity 저장소의 `raspberry_pi/`에 적용하지 않는다. 로봇 기준 커밋 위에서 검증했으며 Pi에 배포하지 않았다.

```bash
# Puppy-PI-Cpp 저장소에서, PATCH를 전달받은 패치의 실제 경로로 설정
git status --short
git rev-parse HEAD
git apply --check "$PATCH"
git apply "$PATCH"
python3 -m unittest discover -s puppypi_remote_camera/tests -v
```

변경 내용:

- 순수 상태기에서 패킷 적용 전 watchdog·lease 만료를 검사한다. 잘못된 패킷·다른 조종자·중복 패킷도 만료 상태의 정지 출력을 숨기지 못한다.
- UDP 서버에서 같은 lock과 단조 시각으로 만료를 검사하고, 패킷 적용 **전에** STOP을 발행한다. 정지 발행 실패는 fault로 유지한다.
- 유휴 수신 대기를 최대 50ms에서 20ms로 줄여 300ms 정지 출력 시험의 스케줄링 여유를 확보했다. 실제 Pi의 정지 지연을 보장하는 실측 결과는 아니다.
- t=0.249/0.250/0.251, 잘못된 패킷, lease, 새 중립, 서버 STOP 발행 순서 회귀시험을 추가했다. 기존 lease 시험은 만료가 패킷 수신 중 먼저 관측되는 새 동작에 맞췄다.
- 기존 UDP 상태 알림 시험은 ACK 뒤 실행되는 callback의 완료를 조건 변수로 기다리게 수정했다. ACK 수신을 callback 완료로 오인하던 시험의 경쟁 조건을 제거했다.

상위 코드의 종료/교체 UUID 거부 목록은 최대 1,024개이며 재시작 시 사라진다. 지연됐지만 sequence가 증가한 패킷의 신선도 정책, 가감속·복합 입력 제한·최소 출발 속도·드리프트 보정은 여전히 미합의/미구현이다. 패치가 이를 해결했다고 간주하지 않는다.

## 3단계: Pi에서 영상 없이 ROS1 시험

실제 IP·SSH 계정/접속 방법·체크아웃 경로를 확인해야 한다. 주소를 추측해 전송하지 않는다. Pi에서 먼저 다음을 확인한다.

```bash
cd /실제/Puppy-PI-Cpp/경로
git rev-parse HEAD
git status --short
hostname -I
printenv ROS_DISTRO
pgrep -af 'vr_udp_teleop|robot_server.py|camera_udp_sender'
```

`start_quest_v2.sh`는 ROS1 Noetic 실행 경로다. 설정 파일 `puppypi_remote_camera/config/robot_config.yaml`에서 `enable_motor_control: false`, `protocol_version: 2`, `tcp_video_enabled: false`를 확인한다. 다른 5005/v1 조종 서버가 동시에 발행하지 않도록 해당 실행 터미널에서 종료한다.

```bash
bash puppypi_remote_camera/robot/start_quest_v2.sh use_camera:=false
# 별도 ROS1 터미널
rostopic echo /puppypi_remote_camera/test_velocity
rostopic echo /puppypi_remote_camera/control_state
```

Unity에서 실측 Pi IPv4 주소와 5001을 설정한다. Editor Preview는 끄고, 그립을 놓은 상태에서 연결해 중립 ACK 후 새로 잡는다. 시험 토픽은 TwistStamped의 m/s·rad/s다. `(forward=0.6, turn=-0.3)`이면 `linear.x=0.03`, `linear.y=0`, `angular.z=-0.06`이 기대값이다. 실제 모터 토픽의 x/y 단위는 cm/s이며 같은 x는 3.0이다.

전후/좌우/대각선, 놓기/손 추적 상실, pause/focus, B 비상정지/Y 해제, Wi-Fi 단절과 복구, 소환 후 부호를 차례로 확인한다. 복구 시 계속 잡고 있는 손으로 자동 출발하면 시험을 중단하고 원인을 수정한다. 이번 작업에서는 실제 모터 활성화나 보행 시험을 실행하지 않았다.

## 검증 재실행

최종 실행 결과는 [VALIDATION_2026-10-03.md](VALIDATION_2026-10-03.md)에 기록했다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_unity_tests.ps1 -Mode EditMode
python tools/validate_v2.py --repo .local/Puppy-PI-Cpp
# 위 Python 실행이 생성한 실제 ACK의 Unity 디코딩도 검사
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_unity_tests.ps1 -Mode EditMode
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_unity_tests.ps1 -Mode PlayMode
```

Unity가 다른 경로에 있으면 `-Unity`를 전달한다. Python에 `yaml`이 없으면 해당 환경에 PyYAML을 설치한다. 첫 EditMode 실행에서는 아직 실제 서버 ACK 산출물이 없을 때 해당 왕복 디코딩 시험 하나가 skip될 수 있다. Python 실행 후에는 통과해야 한다.

결과 XML·로그·Unity JSON·로봇 ACK는 `TestResults/`에 남는다(Git 제외). PlayMode는 별도 테스트 장면에서 물리 엔진과 실제 UDP 클라이언트를 검사하며, Quest 장치나 전체 XR 장면을 사람이 조작한 결과가 아니다. Unity의 `PuppyPi > Configure BallGrip scene` 메뉴는 장면 재구성용이며, SampleScene을 저장하고 네트워크를 비활성화하므로 사용자 장면 변경을 저장한 뒤 의도적으로 사용한다.

관절 속성의 공식 참고: [Unity 6 ConfigurableJoint](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ConfigurableJoint.html). Meta API는 프로젝트의 실제 PackageCache 소스와 컴파일 결과로 확인했다.
