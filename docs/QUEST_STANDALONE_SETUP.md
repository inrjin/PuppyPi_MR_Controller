# Quest 3 단독 실행 설정

확인일: 2026-10-27. 현재 Mac에 저장된 프로젝트 기준이다.

## 이번 빌드의 연결 설정

- Unity: 6000.0.62f1
- 씬: `Assets/Scenes/SampleScene.unity`
- 로봇 IPv4: `192.168.100.153` (사용자가 제공한 주소)
- UDP 제어 포트: `5001` (현재 프로젝트의 v2 규격)
- `Console_BallGrip > PuppyPiV2Client > Network Enabled`: 켜짐
- 실행하면 통신을 시작한다. 유효한 중립 ACK를 받은 뒤 그립을 새로 잡아야 조종 입력이 활성화된다.
- Quest가 로봇에 직접 통신한다. Quest와 PuppyPi 사이에 통신 가능한 로컬 네트워크가 필요하다. Mac의 연결 상태만으로 Quest의 접근 가능 여부를 판단할 수 없다.
- 서버 ACK와 Quest 손 조작에 따른 실제 로봇 이동은 확인했다. 정지 지연과 통신 단절 시험은 별도 검증 항목이다.

## 2026-10-26 Quest 3 설치 확인

- 손 추적 지원을 `Controllers And Hands`로 설정하고 Unity 6000.0.62f1에서 APK 빌드를 완료했다.
- 최종 APK의 `INTERNET`, `HAND_TRACKING` 권한과 손 추적 기능 선언, Quest 3 지원을 확인했다.
- USB ADB에서 Quest 3를 확인했고, `adb install -r`가 `Success`로 완료됐다. 앱 ID는 `com.inrjin.puppypimr`다.
- 앱 시작 명령이 `Status: ok`로 완료됐고, 시작 로그에서 Quest 3/OpenXR 세션과 손 skeleton 초기화를 확인했다.
- Quest Wi-Fi 주소는 `192.168.100.151/24`였고, 설정된 로봇 주소 `192.168.100.153`까지 ping 1회가 응답했다(약 5.27ms). 이는 UDP v2 ACK나 실제 로봇 조종 성공을 뜻하지 않는다.
- 화면·콘솔 표시와 맨손 잡기 인식을 실제 Quest에서 확인했다. 아래 조종권 인계 후 사용자가 콘솔 조작에 따라 실제 로봇이 움직이는 것을 확인했다. 정지 지연·통신 단절·비상정지 실측 검증은 별도로 남아 있다.
- 로컬 빌드/실행 로그: `.local/quest/build-hands.log`, `.local/quest/quest-startup.log`.

## Mac 조종기에서 Quest로 조종권 넘기기

같은 로봇에 `python3 mac_controller/puppypi_mac_controller.py --robot auto`를 실행해 두면, 키를 누르지 않아도 Mac 조종기가 UDP 중립 명령을 20Hz로 보내 조종권을 유지한다. 브라우저 창만 닫아서는 해제되지 않는다.

1. Mac 조종기로 필요한 서기 동작을 완료한다.
2. 조종기를 실행한 터미널에서 **Ctrl+C**로 정상 종료한다. 확인한 클라이언트 구현은 중립과 `disconnect`를 보내며, 로봇 서버 종료나 앉기 명령은 보내지 않는다.
3. Quest 앱을 새로 실행하고 중립 ACK가 수락될 때까지 그립을 놓고 기다린다.
4. `idle / ok`, `accepted=True`를 확인한 뒤 새로 잡는다. 비상정지가 남아 있다면 소유권을 얻은 상태에서 기존 정지 해제 절차를 따른다.

`not_owner`는 현재 세션의 소유권 거부이며, `accepted=False`인 동안 조작은 차단된다. 기존 HUD의 `CONNECTED`는 ACK 수신만 의미해 거부 상태에서도 표시될 수 있었다. 수정 빌드는 소유권 거부·비상정지·로봇 오류를 별도 상태로 표시한다.

2026-10-26에는 실행 중인 Mac 조종기를 정상 종료했고, 이후 로봇 상태에서 `engine=ready`, `estop=false`, `vr_permitted=true`를 확인했다. 중복된 루트 잡기 경로도 비활성화했다. 관련 검증은 EditMode 35개 통과/실제 Python ACK fixture 미존재 1개 제외, PlayMode 8개 통과다. 수정 빌드 로그는 `.local/quest/build-grip-fix.log`에 있다.

사용자가 인계 후 맨손 콘솔 조작에 따른 실제 로봇 이동을 확인했다. 상태 표시 수정 APK도 `adb install -r`로 업데이트했고 앱을 새로 실행했다.

## 설치 전에 정해야 하는 값

Android 앱에는 손가락 터치·손 ray/pinch·컨트롤러로 사용하는 VR 메뉴가 있다. `CONNECT` 탭에서 검색, IP/포트 직접 입력, 재연결·연결 해제를 할 수 있다. Quest에서 성공한 연결 기록과 속도는 기기에 저장된다. PC의 연결 기록은 Quest와 별도다.

| 항목 | 현재 값 | 변경 위치 |
| --- | --- | --- |
| 로봇 주소·포트·통신 사용 | 위 연결 설정 참고 | `Console_BallGrip > PuppyPiV2Client` |
| 이동 속도 비율 | 50%, 10–100% 조절 | VR 메뉴 `-10%` / `+10%` |
| ACK 타임아웃 | 0.3초 | `PuppyPiV2Client > Ack Timeout Seconds` |
| 허용 ACK 나이 | 0.2초 | `PuppyPiV2Client > Max Ack Age Seconds` |
| 그립 최대 각도 | 40도 | `Console_BallGrip > BallGripInput` |
| 중앙 무반응 범위 | 5도 | `BallGripInput > Deadzone Degrees` |
| 감도 | 1 | `BallGripInput > Sensitivity` |
| 전후·회전 반전 | 둘 다 꺼짐 | `BallGripInput > Invert Forward / Invert Turn` |
| 콕핏 소환 거리 | 시선 방향 0.4m, 월드 아래 방향 0.4m | `Cockpit_Manager > SummonCockpit` |
| 입력 방식 | Controllers And Hands | `Assets/Oculus/OculusProjectConfig.asset`의 Hand Tracking Support |

주소·연결·속도는 앱에서 바꿀 수 있다. ACK·그립 감도·각도 같은 Inspector 설정을 바꾸면 빌드를 갱신해야 한다.

앱은 기존 손 그랩 구성을 유지하며 Hand Tracking Support를 Controllers And Hands로 설정했다. 헤드셋에서도 손 추적을 활성화해야 한다. 비상정지·해제는 VR 메뉴와 기존 컨트롤러 버튼 모두 지원한다.

## VR 메뉴와 움직일 수 있는 카메라 화면

- `DASHBOARD`: 카메라 미리보기, `STAND / GO HOME`, `OPEN / RECENTER CAMERA`.
- `MAP`: 지도 영상과 미터 좌표. 빨간 로봇과 파란 이동 경로는 원본 영상에 이미 포함되어 있다.
- `POSES`: Mac 조종기와 같은 7개 자세를 고른 뒤 `APPLY SELECTED POSE`로 실행한다.
- `ACTIONS`: 같은 25개 동작을 페이지로 탐색하고 선택 후 실행한다. 선택·hover만으로 동작을 보내지 않는다.
- `CONNECT`: UDP 5002 기기 검색, 최근 주소, 숫자 키패드로 IP/포트 입력, 재연결·해제.
- 상단 `UDP STOP` / `CLEAR STOP`은 v2 정지·해제, `ROBOT STOP` / `RESET ROBOT`은 로봇 전체 정지·해제다. 전체 정지는 v2 정지도 함께 요청하므로 필요한 경우 두 정지를 각각 해제한다.

카메라 창은 앱 시작 시 시야 왼쪽에 별도로 생성된다. **테두리를 한 손으로 잡아 위치·방향을 바꾸고, 두 손으로 잡아 벌리거나 모아 크기를 조절**한다. 크기는 0.35–2배로 제한되며 원본 영상 비율과 전체 이미지가 유지된다. 놓으면 그 위치에 머무르고, 콕핏이나 조이스틱을 함께 움직이지 않는다. `OPEN / RECENTER CAMERA`로 현재 시점 가까이 가져올 수 있다. 대시보드도 네 변의 테두리를 한 손으로 잡아 이동·회전하고 두 손으로 균일하게 확대·축소한다(0.35–2배). 버튼 영역은 터치·ray 메뉴 조작에 사용한다. 메뉴의 `RECENTER`는 선택한 크기를 유지하며 대시보드를 현재 시점 앞으로 다시 배치한다.

자세·동작 실행 전에는 손잡이와 두 화면을 놓아야 한다. 카메라 창이나 대시보드를 잡고 배치하는 동안 주행 입력은 중립으로 전환된다. 명령 중에는 이동 입력이 중립으로 전환되고, 완료·복구 후 새로 잡아야 이동을 재개한다. 속도 비율은 로봇 측 최대 속도의 비율이며 두 입력 축에 한 번만 적용한다.

## 조종 손잡이가 달린 콘솔 본체 이동

콘솔 바닥 양옆의 **청록색 `MOVE CONSOLE` 손잡이**를 한 손으로 집으면 본체 전체의 위치·방향을 바꿀 수 있다. 손을 놓으면 새 위치에 고정된다. 본체 크기는 유지된다. 손 추적과 컨트롤러 잡기를 모두 지원한다.

본체를 배치하는 동안 주행 입력과 자세·동작 실행은 차단된다. 위치를 정한 뒤에는 이동용 손잡이를 놓고 중앙 조종 손잡이를 새로 잡아 주행한다. 본체와 내부 조종 손잡이의 Rigidbody를 같은 이동·회전량으로 옮겨 조인트 기준을 보존한다. 오른쪽 컨트롤러 A로 소환할 때도 잡기를 취소하고 같은 이동 경로를 사용한다.

이동 손잡이는 전용 Rigidbody와 충돌체를 사용한다. 기존 본체 잡기 설정을 다시 켜서 중앙 조종 손잡이와 충돌체를 공유하지 않는다.

본체 이동 추가 후 EditMode 85개와 PlayMode 20개가 통과했다(별도 Python ACK fixture 필요 1개 제외). 전용 충돌체 분리, SDK 한손 이동·회전, 동적 손잡이 상대 자세와 조인트 보존, 놓은 뒤 고정, 주행 중립, 잘못된 pose 거부를 검사했다. 보고서는 `TestResults/EditMode-console-placement.xml`, `TestResults/PlayMode-console-placement.xml`이다.

## PuppyPi Media v1 연결

이동 UDP 5001과 미디어는 별도 채널이다. 미디어 지연·재접속은 조종권이나 비상정지 상태를 변경하지 않는다.

| 채널 | 주소 |
| --- | --- |
| ROS 명령·상태 | `ws://<IP>:9090/` |
| 외장 USB 웹캠 영상 | `http://<IP>:8080/stream?topic=/usb_cam/image_raw&type=ros_compressed` |
| 지도 영상 | `http://<IP>:8080/stream?topic=/lidar_map/image&type=ros_compressed` |
| 미디어 주소 목록 | `http://<IP>:8090/api/media` |
| 지도 좌표 | `http://<IP>:8090/api/map` |
| 보기 전용 상태 | `http://<IP>:8090/api/status` |

영상은 최신 JPEG 한 장만 유지한다. 새 프레임이 2초 동안 없으면 이전 화면을 어둡게 하고 `NO SIGNAL`로 표시하며, 연결이 끊기면 2초 뒤 재시도한다. 카메라 원본은 1280×720·약 30fps이며 Quest 디코딩은 기본 12fps로 제한한다. 지도 크기는 API 값을 사용하며 실제 수신 예시는 321×232다. 화면 비율은 매 프레임 해상도에 맞춰 유지한다.

지도 좌표는 로봇 시작 위치 원점, +x 시작 시 전방, +y 왼쪽, 단위 m다. 픽셀은 `u = (x - left_m) * px_per_m`, `v = height_px - (y - bottom_m) * px_per_m`으로 변환한다. 서버의 `age_s`와 로컬 경과 시간을 함께 사용하고, 과거 세션의 좌표를 새 연결에 재사용하지 않는다.

## 2026-10-27 영상 연결 검증

외장 USB 웹캠은 `NV50-HD220S` (`05a3:9331`)이며 Pi의 `/dev/video2`로 인식된다. USB 타임아웃으로 영상이 없던 상태는 웹캠 케이블을 분리하고 3초 뒤 다시 연결한 후 복구됐다. 실제 카메라 JPEG 1280×720과 지도 JPEG 수신을 확인했고, 카메라 프레임은 앱의 JPEG 파서에도 그대로 통과했다.

이 영상 서버는 topic의 `/`를 `%2F`로 변환하면 HTTP 200과 multipart 경계만 반환하고 연결을 닫는다. 앱은 검증된 ROS topic의 `/`를 그대로 보내도록 수정했다. HTTP 200만으로 영상 정상 여부를 판단하지 말고 실제 JPEG와 최근 프레임 시간을 확인한다. Mac 조종기 실행은 영상 수신에 필요하지 않다. 수정 APK를 Quest에서 실행한 뒤 카메라·지도 모두 `LIVE`, ROS 상태 갱신, UDP 중립 ACK 수락을 로그로 확인했다.

## 이동·확대 대시보드 검증

대시보드 변경 후 Unity EditMode 85개와 PlayMode 17개가 통과했다. 별도 로봇 저장소의 Python ACK fixture가 필요한 시험 1개는 제외됐다. 실제 Meta SDK 변환기로 한손 이동·회전, 두손 균일 확대·축소 한계, 놓은 뒤 정지, 메뉴 표면의 ray·poke 판정, 영상 비율, 재중앙화 시 크기 유지, 창을 잡는 동안 주행 중립 전환과 일반 버튼 클릭 차단을 검사했다. 긴급정지 버튼은 창을 잡은 상태에서도 사용할 수 있다.

보고서: `TestResults/EditMode-movable-dashboard.xml`, `TestResults/PlayMode-movable-dashboard.xml`.

## SSH 로그아웃 후 ROS 데이터가 멈추는 경우

2026-10-27 최종 대시보드 APK는 설치·시작에 성공했다. 이후 Pi에서 카메라·지도·상태 데이터가 함께 정체된 것을 확인했다. SSH 세션 종료(UTC 2026-10-26 15:57:39) 10초 뒤 모든 주요 ROS 노드의 FastDDS 공유메모리가 unlink됐고, 프로세스는 삭제된 파일을 계속 열고 있었다. `/dev/shm` 용량 부족은 아니었다. `RemoveIPC=yes`, 사용자 종료 유예 10초, `Linger=no`였던 설정과 일치한다.

재발방지로 **`sudo loginctl enable-linger pi`를 적용**했다. `loginctl show-user pi -p Linger`의 `Linger=yes`와 `user@1000.service`의 active 상태를 확인했고, 기존 제어 서비스 PID는 바뀌지 않았다. 이는 로그인 세션이 없어도 pi 사용자 환경을 유지한다. 원래 값으로 되돌리는 명령은 `sudo loginctl disable-linger pi`지만, 로그아웃 시 같은 문제가 재발할 수 있다.

이미 삭제된 공유메모리는 이 설정만으로 복원되지 않는다. `sudo systemctl restart puppypi-ros2.service`로 영향을 받은 참여자를 다시 만들 수 있지만 보행 엔진도 다시 시작하므로 로봇 몸체를 안정적으로 받친 상태에서 진행한다. 사용자의 명시적 승인 후 이 서비스를 재시작했고 새 PID 5463의 active/running 상태를 확인했다. 이후 Pi의 실제 카메라 JPEG 1280×720과 지도 JPEG, API의 새 데이터(arbiter 약 0.10초, map 약 0.70초), Quest의 카메라·지도 LIVE와 새 ROS 상태(engine ready)를 확인했다. 점검용 SSH를 종료한 뒤 20초 후에도 arbiter 나이 0.19초, 지도 나이 1.0초, 실제 카메라·지도 JPEG 수신을 확인해 로그아웃 후 갱신 유지까지 검증했다. 이 단계에서는 자세·동작·주행 명령을 자동 실행하지 않았다.

근거: `.local/quest/camera-dds-unlink-timing.json`, `.local/quest/pi-ipc-recovery-plan.md`. 동작 설명은 [systemd 249 RemoveIPC](https://www.freedesktop.org/software/systemd/man/249/logind.conf.html#RemoveIPC=), [loginctl linger](https://www.freedesktop.org/software/systemd/man/249/loginctl.html), [systemd 사용자 정리 구현](https://raw.githubusercontent.com/systemd/systemd/v249/src/login/logind-user.c)을 참고한다.

## 설치 후 가능한 조작

- 오른쪽 A: 콕핏을 현재 시선 앞에 다시 배치. 소환 거리 자체를 조절하는 메뉴는 없다.
- 오른쪽 B: 비상정지 명령.
- 왼쪽 Y: 그립을 놓은 상태에서 정지 해제 요청.
- 그립을 놓음, 입력 추적 상실, 앱 일시정지/포커스 상실: 중립 요청. 네트워크가 끊긴 상황의 실제 정지는 로봇 측 watchdog도 필요하다.
- HUD: 연결/ACK, 그립·추적 상태, 두 축의 입력 값 표시.

Wi-Fi 연결, 헤드셋의 경계/재중앙화는 Quest 시스템에서 바꿀 수 있어 APK를 다시 만들 필요가 없다. 실제 로봇의 최고 속도와 모터 구동 허용 설정은 로봇 측 설정이다.

## Mac에서 다시 빌드

1. Unity Hub에서 `My project`를 6000.0.62f1로 연다.
2. `File > Build Profiles`에서 Android를 활성화한다.
3. 위 설정을 수정했다면 씬을 저장한다.
4. 메뉴가 없는 기존 씬을 갱신할 때는 `PuppyPi > Configure complete VR dashboard`를 한 번 실행한다.
5. `PuppyPi > Build Quest development APK`를 실행한다.
6. 저장소 루트의 `.local/quest/PuppyPiQuest.apk`가 생성된다.

이 빌드 메뉴는 Android XR 로더가 없으면 설치된 Oculus 로더를 켜고, ARM64/IL2CPP, 최소 API 32, 네트워크 권한, Development Build와 로컬 HTTP 미디어 접근을 설정한다. 기본 템플릿 앱 ID는 `com.inrjin.puppypimr`로 바꾼다. 로봇 주소나 그립 감도는 메뉴가 덮어쓰지 않는다.

Quest를 USB로 연결하고 개발자 모드 및 USB 디버깅을 허용해야 설치할 수 있다. 이후 반복 작업에는 Unity의 Development Build와 Patch And Run을 사용할 수 있지만 변경 종류에 따라 전체 빌드가 필요하다.

관련 문서: [Unity Android 빌드](https://docs.unity3d.com/6000.0/Documentation/Manual/android-BuildProcess.html), [Application patching](https://docs.unity3d.com/6000.0/Documentation/Manual/android-AppPatching.html).
