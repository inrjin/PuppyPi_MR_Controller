# IP가 바뀌어도 Unity에서 PuppyPi 연결하기

2026-10-04. 같은 공유기/LAN에 연결된 **PC Unity Editor + Quest Link + Pi** 구성용이다.
연결 패널은 PC의 Unity **Game** 화면에 표시된다. Quest에는 연결 상태가 월드 HUD로 표시되며,
Quest 내부에서 검색 목록을 누르거나 주소를 입력하는 VR 패널은 이번 범위에 포함하지 않는다.

## 평소 사용

1. Pi에서 기존 **v2 조종 서버(UDP 5001)**를 실행한다. 이 저장소의 예전 `udp_joystick_receiver.py`(5005)가 아니다.
2. Unity `SampleScene`에서 Play하고 일시정지(Ⅱ)를 해제한다. Game 탭을 열어 둔다.
3. 왼쪽 **PuppyPi 연결 → 기기 검색**을 누른다. 약 3초 동안 검색한다.
4. 목록의 기기 이름과 주소를 보고 **연결**을 누른다. 최근 성공한 기기는 `최근 연결`로 표시된다.
5. **연결됨** 상태를 확인하고 그립을 놓았다가 새로 잡는다.

IP나 공유기가 바뀌면 새 네트워크에 PC와 Pi를 연결한 뒤 **기기 검색 → 연결**을 반복하면 된다.
Inspector 수정, 고정 IP 설정, Unity Play 재시작은 필요 없다. 앱 시작 시 마지막 주소를 표시하지만
자동으로 조종 서버에 접속하지 않는다. 오래된 주소로 자동 전송하지 않고 새 검색 결과를 선택한다.

검색이 제한된 네트워크에서는 IP나 호스트 이름을 직접 입력할 수 있다. `puppypi.local`은 Pi에
그 이름이 설정되어 있고 OS의 mDNS 이름 해석이 동작할 때만 사용 가능하다. 이름 해석은
Unity 화면을 멈추지 않도록 비동기로 실행하며 최대 5초 기다린다.

## Pi에서 한 번만 준비

### 현재 Pi IP를 모르는 경우

IP를 몰라도 Pi에 직접 접근할 수 있다면 설치할 수 있다. 설치 ZIP을 USB 메모리로 Pi에 옮기고
압축을 푼 뒤 Pi 터미널에서 아래 설치 명령을 실행한다. 이 경우 최초 설치에도 SSH는 필요 없다.
Pi와 PC를 같은 공유기에 연결하고 검색 서비스와 기존 v2 조종 서버를 실행하면 Unity에서 검색한다.

SSH로 설치하려면 다음 중 하나로 최초 주소를 확인한다.

1. Pi에 모니터/키보드를 연결해 터미널에서 `hostname -I` 실행.
2. 공유기 관리 화면의 **연결된 기기 / DHCP 할당 목록**에서 Pi 이름과 주소 확인.
3. Pi 호스트 이름과 mDNS가 설정되어 있다면 `이름.local`로 접속. 이름은 설치 시 설정에 따라 다르며
   `raspberrypi.local`이나 `puppypi.local`이 항상 존재하는 것은 아니다.

아직 검색 서비스를 설치하지 않은 Pi는 새 Unity 검색에 응답하지 않는다. PC의 Wi-Fi 연결은
필수는 아니며, 유선 PC와 Wi-Fi Pi가 같은 LAN에 있어도 된다.
참고: [Raspberry Pi 공식 IP 확인 안내](https://www.raspberrypi.com/documentation/computers/remote-access.html).

### 설치 파일과 명령

필요 파일은 이 저장소의 다음 두 개다. Pi의 동일한 임시 폴더에 복사한다.

- `raspberry_pi/discovery_service.py`
- `raspberry_pi/puppypi-discovery.service`

먼저 Pi에서 파일을 복사한 폴더로 이동해 임시로 실행할 수 있다(Python 3.8 이상, 추가 pip 패키지 없음).

```bash
python3 discovery_service.py
```

Unity에서 기기가 검색되는지 확인한 뒤 Ctrl+C로 종료한다. 다음 명령은 **Pi에서**, 두 파일이
있는 폴더에서 실행한다. 서비스는 모터나 ROS 설정을 바꾸지 않으며 검색 요청에만 응답한다.

```bash
sudo install -d -m 755 /opt/puppypi-discovery
sudo install -m 644 discovery_service.py /opt/puppypi-discovery/discovery_service.py
sudo install -m 644 puppypi-discovery.service /etc/systemd/system/puppypi-discovery.service
sudo systemctl daemon-reload
sudo systemctl enable --now puppypi-discovery.service
systemctl status puppypi-discovery.service --no-pager
```

이후 Pi가 재부팅되거나 DHCP 주소가 바뀌어도 검색 서비스는 모든 IPv4 인터페이스에서 수신한다.
검색 이름의 기본값은 Pi 호스트 이름이다. 이름이나 조종 포트를 바꾸려면 서비스의 `ExecStart`에
`--name "PuppyPi" --control-port 5001` 옵션을 넣고 daemon-reload/restart한다.
검색 포트는 Unity와 동일하게 **5002**를 유지한다.

**기존 v2 조종 서버는 별도로 실행되어 있어야 한다.** 검색 서비스 설치가 조종 서버 설치나
모터 활성화를 대신하지 않는다. 현재 로봇 저장소 기준의 영상 없는 실행은 다음과 같다.

```bash
cd /실제/Puppy-PI-Cpp/경로
bash puppypi_remote_camera/robot/start_quest_v2.sh use_camera:=false
```

기존 `enable_motor_control`, 비상정지, watchdog 설정은 그대로 사용한다.
조종 서버 시작 방법은 [기존 v2 안내](BALLGRIP_ROS1_V2.md)를 따른다.

## 연결 상태의 의미

| 화면 | 의미 / 다음 작업 |
|---|---|
| 기기가 검색 목록에 나타남 | Pi의 검색 서비스 응답을 받음. 조종 서버 상태는 아직 확인 전 |
| 연결 중 | 현재 목적지와 새 세션으로 중립 명령을 보내고 유효한 v2 ACK를 기다리는 중 |
| 연결됨 · 그립을 놓고 다시 잡으세요 | Pi가 명령을 수락함. 새 잡기 전까지 중립 유지 |
| 다른 조종기가 사용 중 | ACK의 `not_owner` 응답. 다른 조종기를 종료하거나 연결 해제 |
| 응답 끊김 · 그립 중립 | ACK timeout. 현재 세션을 차단하고 중립 전송. IP가 바뀌었다면 다시 검색 |
| 응답 없음 안내 | 첫 연결에서 5초 안에 ACK가 없음. 연결 시도를 종료함. 주소/포트/조종 서버 확인 |
| 비상정지 / 로봇 오류 | 해당 상태를 해소하기 전에는 조작하지 않음 |

화면의 응답 시간은 마지막 유효 ACK를 받은 후 지난 시간이며 ping 왕복 지연이 아니다.
`연결됨`은 프로토콜 연결 확인이지 로봇이 실제로 걸었다는 확인은 아니다.
연결 해제·목적지 변경 시 기존 목적지에 중립+disconnect를 보내고 소켓을 닫는다. 새 **연결**
동작은 새 UUID를 사용한다. 자동 소켓 복구는 기존 UUID와 lease 규칙을 유지한다.
종료 UDP가 유실되면 서버 watchdog/lease 만료를 기다리며 소유권을 강제로 가져오지 않는다.
명시적으로 재연결해도 로컬 비상정지 상태는 유지하고, 해제는 기존 버튼/입력으로만 한다.

## 검색이 안 될 때

- Quest Link의 Play 모드는 **PC ↔ Pi** 통신이다. Quest 자체 Wi-Fi가 달라도 PC와 Pi가 같은 LAN이면 된다.
- Pi에서 `systemctl status puppypi-discovery`와 `journalctl -u puppypi-discovery -n 30 --no-pager` 확인.
- 공유기의 게스트 Wi-Fi / AP isolation / VLAN 분리가 있으면 같은 SSID여도 기기 간 통신이 차단될 수 있다.
- 방화벽은 필요한 로컬 네트워크 통신만 허용한다: Pi 수신 UDP 5002(검색), UDP 5001(v2 제어),
  PC의 Unity 프로세스에 대한 해당 요청의 UDP 응답. 전체 방화벽을 끌 필요는 없다.
- 검색은 되는데 연결이 안 되면 조종 서버의 프로토콜 2, 포트 5001, bind 주소를 확인한다.
- `Editor Preview`는 기존대로 loopback 시험만 허용한다. 실제 Pi 연결 시에는 끈다.

검색 응답은 최근 검색 nonce와 발신 포트를 확인하고 10초 후 만료한다. 발신 IP를 사용하므로
Pi가 바뀐 IP를 설정 파일에 다시 적지 않아도 된다. 검색은 같은 신뢰할 수 있는 LAN을 위한
기능이며 서로 다른 인터넷을 연결하거나 암호화된 로봇 인증을 제공하는 기능은 아니다.

## Pi 없이 로컬 검증

```powershell
python -m unittest discover -s raspberry_pi/tests -v
python tools/validate_v2.py --repo .local/Puppy-PI-Cpp --serve
```

두 번째 명령은 모터 없는 실제 v2 상태기와 loopback 검색 서비스(5002)를 띄운다.
Unity Game에서 **기기 검색 → Local test (no motors) → 연결**하면 된다.
끝나면 Python 터미널에서 Ctrl+C. 다른 PC/Pi에는 이 테스트 서버를 노출하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_unity_tests.ps1 -Mode EditMode
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_unity_tests.ps1 -Mode PlayMode
```

참고: [.NET UDP broadcast](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.udpclient.enablebroadcast),
[systemd의 동적 네트워크 서비스 시작](https://systemd.io/NETWORK_ONLINE/).
