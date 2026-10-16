# 2026-10-04 연결 UI·LAN 검색 검증

## 구현

- 기존 `BallGripHud`가 런타임에 연결 컨트롤러를 구성한다. SampleScene을 재구성하거나 참조를 수동으로 연결할 필요 없다.
- PC Unity Game 화면에 검색, 발견 기기별 연결, IP/호스트 이름 입력, 연결 해제, 최근 기기 표시, ACK 상태를 추가했다.
- Pi 검색 서비스는 Python 표준 라이브러리만 사용하고 UDP 5002에서 동작한다. 제어 패킷을 전송하지 않는다.
- 새로운 연결 동작은 이전 목적지에 중립+disconnect를 전송하고 새 세션을 만든다. 자동 소켓 복구는 기존 UUID를 유지한다.
- 재연결 시 중립 ACK와 놓기/새 잡기가 필요하다. 비상정지는 연결 전·해제 후에도 보존한다.
- 검색 결과 만료 10초, 검색 3초, DNS/최초 ACK 대기 각각 최대 5초. 서로 다른 인터넷을 연결하는 기능은 포함하지 않는다.

## 결과

| 검증 | 결과 | 산출물 |
|---|---|---|
| Unity 최종 컴파일 + EditMode | 34/34 통과, skip 0 | `TestResults/EditMode.xml`, `EditMode.log` |
| Unity PlayMode | 7/7 통과, skip 0 | `TestResults/PlayMode.xml`, `PlayMode.log` |
| Python 검색 서비스 | 4/4 통과 | `python -m unittest discover -s raspberry_pi/tests -v` |
| 기존 로봇 테스트 / 실제 v2 파서 왕복 | 74/74 + 6개 메시지 통과 | `tools/validate_v2.py --repo .local/Puppy-PI-Cpp` |

새 테스트는 DHCP 주소가 달라도 같은 기기 ID를 유지하는지, 잘못된/오래된 nonce와 포트·버전·이름 형식을
거부하는지, 중복 응답을 합치고 검색 결과를 만료시키는지, 검색이 제어 포트를 건드리지 않는지 확인한다.
실제 loopback UDP 클라이언트로 목적지 변경, disconnect 이후 재연결, 잡은 상태의 재출발 차단,
오프라인 비상정지 보존, ACK 미수신 5초 종료, 다른 조종자 사용 중 표시와 늦은 DNS 결과 무효화를 검사했다.

첫 PlayMode 실행에서 새 테스트용 응답기가 이미 닫힌 클라이언트에 응답한 뒤 Windows의 UDP ICMP
ConnectionReset을 받는 두 실패가 있었다. 테스트 응답기에 해당 종료 조건 처리를 추가했고 재실행은
모두 통과했다. 기존 로봇 테스트의 모터 활성화 경고·publisher 실패 출력은 mock 시험이며 실제 장치를 사용하지 않았다.
마지막 검색 완료 문구·Quest ASCII HUD 수정 후 최종 EditMode로 컴파일을 다시 확인했다.

## 아직 실측하지 않은 범위

- 실제 Pi 설치, systemd 부팅 자동 실행, 공유기 교체·DHCP 재할당 후 LAN broadcast 검색.
- PC Game 패널의 실제 화면/한글 표시와 Quest HUD 가독성, 실제 로봇 조작.
- Pi SSH 접속 정보가 제공되지 않아 배포하지 않았다. 설치·사용 안내는 [CONNECTION_UI.md](CONNECTION_UI.md).

Unity 코드와 Python 파일을 준비했으며 커밋·푸시는 하지 않았다. 기존 사용자 변경은 작업 트리에 남아 있다.
