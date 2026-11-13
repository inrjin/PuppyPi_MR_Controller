# MR Obstacle Avoidance: References and Implementation Scope

검토일: 2026-11-10. Nav2 소스 기준은 [`3b7ec435`](https://github.com/ros-navigation/navigation2/commit/3b7ec4355be91ab32da850e9a77382300c1ca560)이다. 이 문서는 공개 구현의 설계 원칙과 현재 MR 구현을 비교한다. Nav2를 Quest에 이식했거나 같은 수준의 실기 검증을 마쳤다는 의미는 아니다.

## 권장하는 구성

현재 요구에는 **경로 계획 → 정지 확인 → 제자리 회전 → 직선 전진 → 남은 경로 재검사** 구성이 맞는다. 새 장애물을 발견하면 기존 경로를 취소하고 정지를 확인한 뒤에만 새 경로를 보낸다. 후진은 사용하지 않는다. MR은 경로를 만들고, 기존 로봇 follower가 회전과 전진을 실행한다.

| 참고 구현 | 가져올 원칙 | 현재 적용 상태 |
| --- | --- | --- |
| [Nav2 Theta*](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_theta_star_planner/README.md) | 격자 탐색과 시선 검사로 불필요한 지그재그를 줄이고 직선 구간을 만든다. 장애물 근접 비용도 경로 품질에 반영한다. | MR은 방향 상태를 포함한 A*, 장애물 여유 거리 비용, 회전 비용을 구현했다. 직선 단축도 충돌·여유 거리·비용을 재검사한다. Theta* 원본을 이식한 것은 아니다. |
| [Nav2 Smac](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_smac_planner/README.md) | 로봇 형상과 가능한 동작을 탐색에 포함한다. 비원형 로봇은 점이나 단일 원으로만 판단하지 않는다. | MR은 직선의 사각형 이동 영역과 회전 지점의 원형 여유 영역을 별도로 검사한다. 보행 중 발의 전체 궤적과 동역학 모델은 포함하지 않는다. |
| [Nav2 경로 유효성 기반 재계획 BT](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_bt_navigator/behavior_trees/nav_to_pose_with_consistent_replanning_and_if_path_becomes_invalid.xml) | 유효한 기존 경로를 유지하고, 남은 경로가 무효일 때 재계획한다. 복구 횟수를 제한한다. | MR은 남은 경로만 검사하고 최대 세 번 우회를 시도한다. UDP 취소 ACK 후 새 HTTP 정지 상태 및 neutral ACK를 확인한 뒤 교체한다. 지도 초기화·소유권 상실·STOP은 자동 재시작을 허용하지 않는다. |
| [SimpleProgressChecker](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_controller/plugins/simple_progress_checker.cpp), [PoseProgressChecker](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_controller/plugins/pose_progress_checker.cpp) | 통신이 정상이어도 실제 위치·각도가 진행하지 않으면 실패 처리한다. 회전을 이동 정지로 오판하지 않는다. | MR에 목표까지 거리/회전 오차의 개선을 보는 별도 진행 감시기를 연결했다. 아래의 프로젝트 설정은 Nav2 기본값과 다르다. |
| [Nav2 Collision Monitor](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_collision_monitor/README.md), [무효 센서 정지 처리](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_collision_monitor/src/collision_monitor_node.cpp#L449) | 경로 계획과 독립된 로봇 측 센서 검사로 최종 속도 출력을 제한한다. 감지 영역과 실제 유발점을 시각화한다. | MR의 셀 표시·출처·좌표는 추가됐지만 독립된 로봇 측 Collision Monitor는 설치하지 않았다. 기존 로봇의 전방 정지 기능을 사용한다. |
| [DWB OscillationCritic](https://github.com/ros-navigation/navigation2/blob/3b7ec4355be91ab32da850e9a77382300c1ca560/nav2_dwb_controller/dwb_critics/src/oscillation.cpp) | 진행 없이 방향만 번갈아 바꾸는 동작을 기록하고 제한한다. | MR의 동일 경로 거부와 세 번 제한은 반복을 제한하지만, DWB의 속도 부호 이력 감시를 구현한 것은 아니다. |

## 프로젝트에서 확인한 값과 진행 감시

별도 로봇 저장소의 [`path_follower.py`](https://github.com/Ksyear/PuppyPI_Cpp_ROS2/blob/main/ros2_ws/src/puppy_vr_bridge/puppy_vr_bridge/path_follower.py)는 기본 전진 속도 `12.5 cm/s`, 제자리 회전 속도 `0.14 rad/s`, 도착 반경 `0.10 m`를 사용한다. 이 값은 소스 기본값이며, 실제 설치 설정을 직접 측정한 값은 아니다.

- 진행 감시의 프로젝트 초깃값은 **직진 8초 동안 목표 거리 0.05m 이상 감소**, **회전 8초 동안 목표 각도 오차 0.05rad 이상 감소**다. 멈춤·새 경로·목표 waypoint 변경 시 기준을 초기화한다. 단순한 위치 흔들림이나 잘못된 방향 이동은 진행으로 인정하지 않는다.
- 위 속도로 8초 동안 정상 명령이 유지되면 명목상 전진 1m 또는 회전 1.12rad에 해당한다. 따라서 이 기준은 느린 실제 보행을 허용하는 보수적인 초깃값이다. 실기 데이터에 맞춘 튜닝이 필요하다.
- 진행 실패는 경로 취소와 정지로 처리한다. 실패 원인이 미끄러짐·걸림·위치 추정 오류일 수 있으므로, 진행 실패만으로 새로운 방향으로 자동 출발하지 않는다.
- [Nav2 SimpleProgressChecker 문서](https://docs.nav2.org/rolling/configuration_and_development/configuration_guide/core_servers/controller_server/controller_server_plugins/simple_progress_checker/)의 기본 설정은 0.5m/10초다. 이 수치를 소형 PuppyPi에 그대로 복사하지 않았다.

## 남아 있는 중요한 차이

1. **회전 중 로봇 자체 보호:** 검토한 follower는 `rotate` 반환이 scan timeout/전방 장애물 검사보다 앞에 있다. 따라서 기존 `0.6s` scan timeout을 모든 회전에도 적용되는 충돌 보호라고 설명하면 안 된다. MR의 회전 여유 검사는 네트워크와 지도 갱신에 의존한다. 로봇 담당자는 최종 모터 출력 직전에 신선한 센서·회전 주변 영역을 검사하는 구조를 검토해야 한다.
2. **경로 품질 튜닝:** MR 탐색에도 이동 길이 외에 장애물 여유 거리 비용과 회전 비용을 추가했다. 여유 거리 선호 0.80m, 거리 비용 가중치 5, 회전당 기본 비용 0.12m와 각도당 0.15m/rad는 프로젝트 설정이다. 통과 금지 영역은 별도 정확한 형상 검사가 결정하며 이 비용으로 장애물·미관측 셀을 통과시키지 않는다. Nav2 전체 costmap과 동일한 구현은 아니며 실제 지도와 보행에서 가중치를 검증해야 한다.
3. **움직이는 물체:** 현재 지도 프레임에 대한 재검사와 정지 후 재계획이다. 물체 속도 추정이나 시간축 충돌 예측은 구현하지 않았다. 사람 사이를 연속적으로 통과하는 동적 주행을 검증한 것으로 취급하지 않는다.
4. **반복 복구:** 세 번 제한은 무한 반복을 막지만 좌우 우회안의 장기 이력을 관리하지 않는다. 같은 장소에서 재차 실패하면 자동 재시도를 중단한다. 이후 확장 시에는 최근 실패 경로/진행량과 재시도 이유를 기록해야 한다.
5. **최종 실행의 차이:** 지도상 직선 구간이 안전해도 실제 follower의 10cm 도착 반경, 위치 오차, 작은 주행 중 yaw 보정으로 물리 궤적이 달라진다. 생성 경로와 실제 궤적을 함께 기록해 통과 여유를 검증해야 한다.

## 확인해야 할 실행 사례

직선·L·Z 경로, 양쪽 우회 가능/한쪽만 가능/양쪽 막힘, 로봇 뒤쪽만 열린 상황, 가는 중 새 장애물, 회전 중 새 장애물, 센서 중단, UDP 취소 ACK 손실, 두 번째·세 번째 실행, 로봇이 실제로 진행하지 않는 상황을 각각 확인한다. 경로 계획 시간·재계획 횟수·정지 확인 시간·최소 장애물 거리·실제 이동 궤적을 남긴다. 테스트 결과는 `MR_MAP_PATH_EDITOR.md`의 사용법과 별도로 기록한다.
