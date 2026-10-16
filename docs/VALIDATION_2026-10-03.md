# 2026-10-03 로컬 구현 검증 결과

실행 환경: Windows, Unity 6000.0.62f1, 설치된 Meta SDK, 사용자 `Python314` 설치 경로의 Python. 실제 Pi·Quest·모터는 사용하지 않았다.

| 검증 | 최종 결과 | 근거 |
|---|---|---|
| Unity 스크립트 컴파일, Editor API를 통한 SampleScene 구성·저장 | 통과 | `TestResults/unity-configure.log`, 종료 코드 0 |
| Unity EditMode | 24/24 통과, skip 0 | `TestResults/EditMode.xml` |
| Unity PlayMode | 2/2 통과, skip 0 | `TestResults/PlayMode.xml` |
| 로봇 원본 시험 + 별도 패치의 회귀시험 | 74/74 통과 | `tools/validate_v2.py`, 종료 코드 0; 기존 65개 + 신규 9개 |
| Unity JsonUtility 출력 → 실제 로봇 엄격 파서 → UDP ACK | 6개 메시지 통과 | `TestResults/unity-v2-packets.jsonl`, `robot-v2-acks.json` |
| 실제 로봇 ACK → Unity 엄격 디코더 | 통과 | EditMode `RealPythonServerAcksDecodeWhenAvailable` |
| 로봇 패치 적용 가능성 | 통과 | 기준 커밋 파일에 `git apply --check`, 수정 복제본에 역방향 `--check --reverse` |

EditMode는 방향 부호·대각선·받침대 회전·비틀림 제외·연속 deadzone·감도, 실제 Meta Grabbable의 Select/Move/Unselect/Cancel 이벤트와 IController 추적 유효성 연결, 장면의 단일 송신기·관절 참조, 시작/ACK 상실/오래된 ACK/다른 UUID/미송신 sequence/서버 정지/fault/재잡기, 엄격한 ACK 형식을 검사했다. Meta 이벤트 테스트는 Editor에서 SDK 생명주기를 직접 초기화한 시험이며 실제 하드웨어 추적 품질 검사가 아니다.

PlayMode의 첫 시험은 Unity 물리 엔진에서 6개 방향·대각선, 위치 잠금, 놓기·추적 상실 즉시 중립, 추적 복구 중 재출발 차단, 스프링 복귀, 120° 받침대 회전 뒤 입력 부호를 확인했다. 두 번째는 실제 `PuppyPiV2Client`와 localhost UDP 응답기를 연결해 같은 소켓/UUID/증가 sequence, `(0.6,-0.3)` 출력, ACK 단절·복구, pause 즉시 중립, 재잡기, 비상정지·해제·disconnect를 확인했다.

로봇 호환성 시험은 기준 저장소의 실제 `remote_control.py`를 사용했다. 0.6/-0.3 입력은 내부 출력 `x=3cm/s, y=0, yaw=-0.06rad/s`였으며 SI 시험 토픽 환산은 0.03m/s다. 기존 robot_server 시험은 fake rospy로 토픽·단위·모터 비활성 경로를 검사한다. 테스트 중 출력되는 모터 활성화 경고와 의도적인 publisher 실패 traceback은 mock 경로 시험의 출력이며, 실제 모터를 활성화하지 않았다.

실행 중 발견하고 수정한 사항:

- Meta GrabInteractor의 Selector가 비공개여서 SDK 프리팹의 자식 ControllerSelector와 공개 Controller API로 연결했다.
- Unity 컴포넌트의 특수 null 때문에 장면 구성기의 `??` 사용을 Unity null 비교로 교체했다.
- watchdog 수신 대기 50ms에서 Windows 시험 정지 출력이 302ms였던 경우가 있어 20ms로 줄였다. 300ms 판정 기준은 유지했다.
- 기존 로봇 상태 callback 시험이 ACK 직후 callback 완료를 가정해 간헐적으로 실패했다. 조건 변수로 callback 완료를 기다리도록 시험을 수정했다. ACK/서버 동작 계약은 바꾸지 않았다.
- 새 테스트 픽스처의 SDK 초기화와 disconnect 직후 닫힌 소켓으로 ACK를 보내던 부분을 수정했다. 최종 결과에는 실패·skip이 없다.

남은 기기 검증:

- SampleScene 전체 XR 실행과 Quest에서의 실제 손/컨트롤러 잡기 감각, tracking loss·cancel, HUD 가독성, B/Y 버튼과 A 소환.
- Android APK 빌드·설치와 실제 Quest 실행.
- Pi IP·SSH 접근·체크아웃·ROS1 Noetic·서버 실행, 실제 ROS 시험 토픽, Wi-Fi 단절·복구와 정지 지연.
- 실제 모터/보행은 미실행. 가감속·동시 입력 제한·최저 출발 속도·지연 패킷 신선도와 ACK 시간 합의가 필요하다.
- 컨트롤러 없는 손 추적 전용 정지/해제 UI와 영상은 미구현이다.

코드는 커밋·푸시·Pi 배포하지 않았다. 네트워크 기본값은 비활성, Pi 주소는 공란이다. 실행 절차와 정책은 [BALLGRIP_ROS1_V2.md](BALLGRIP_ROS1_V2.md)를 참조한다.
