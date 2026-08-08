# 다음에 할 일

지금 매칭 기능(검색/호스트/재모집/킥/밴/설정)은 완료된 상태. 아래는 매칭 기능과는 별개로,
나중에 "멀티 상태 불일치 해소 혹은 빠른 재모집" 기능을 만들 때 참고할 조사 결과 메모.

## 멀티 상태 불일치(StateDivergence) 튕김 대응

### 배경
플레이 중 "멀티상태 불일치로 튕기는" 케이스가 잦다는 문제 제기. 매칭 정확도(핑/지역)와는
무관한 별개 문제로 확인됨 - 매칭 기능에 끼워 넣지 않고 독립 기능/모드로 다룰 것.

### 조사 결과 (sts2.dll 디컴파일 기반, ilspycmd 사용)

- **원인은 핑이 아니라 결정론적 시뮬레이션 검증 실패.** `ChecksumTracker`(`MegaCrit.Sts2.Core.Multiplayer.Game`)가
  전투/이벤트/휴식처 이후마다 호스트-클라이언트 풀스테이트 체크섬을 비교하고, 안 맞으면 호스트가
  `NetHostGameService.DisconnectClient(senderId, NetError.StateDivergence)`로 그 클라이언트만 끊음.
- `NErrorPopup.cs`의 `LocStringFromNetError`를 보면 MegaCrit도 `StateDivergence`를
  `InternalError`/`UnknownNetworkError`와 같은 "버그" 등급으로 취급함 (언모드 상태에서 버그 신고 버튼 자동 노출).
  즉 게임 자체 넷코드의 결정론적 동기화 버그이고, 모드 범위 밖 - **원인 자체를 고칠 수는 없음.**
- 모드 버전 불일치가 원인일 가능성은 낮음 - `ModManager.GetGameplayRelevantModNameList()`가
  `id + "-" + version` 형식이라 우리 `MatchTags.ComputeGameplayModHash()`가 이미 버전 차이까지 걸러냄.
- **StateDivergence는 어긋난 클라이언트 한 명만 끊는 것** - 호스트와 나머지 참가자는 계속 진행됨.
  방/런 자체가 깨지는 게 아니라 그 한 사람만 떨어져 나가는 구조.

### "그 자리로 바로 복귀"는 안 됨 (확인 완료, 결론 확정)

- 게임 프로토콜 자체는 재접속을 지원함: `ClientRejoinRequestMessage`/`ClientRejoinResponseMessage`,
  `JoinFlow.AttemptRejoin()`, `RunLobby.OnConnectedToClientAsHost`가 `_playerCollection`(=원래 이
  런 멤버였는지)을 확인해서 맞으면 `RunSessionState.Running` + 풀스냅샷(`serializableRun` + `combatState`)을
  돌려주는 로직이 존재함.
- **근데 이걸 실제로 소비하는 클라이언트 코드가 게임 어디에도 없음.** `JoinFlow.Begin()`을 호출하는
  두 곳(`NJoinFriendScreen.JoinGameAsync`, 디버그용 `NMultiplayerTest`) 전부 `RunSessionState.Running`을
  받으면 `rejoinResponse`를 버리고 `NetError.RunInProgress`로 무조건 끊어버림.
- 즉 서버 쪽 스캐폴딩만 있고 클라이언트 쪽 "복귀" 로직은 미구현/미사용 상태 (프로덕션에서 한 번도
  끝까지 실행된 적 없는 경로일 가능성 높음) - 우리가 직접 구현하려면 검증 안 된 경로를 억지로 타는
  셈이라 리스크가 큼 (세이브 손상 등). **미드전투 정밀 복귀 방향은 폐기.**

### 남은 가능성 (조사만 하고 미착수)

- **체크포인트 복귀**: `combatState`(미드전투 정밀 상태)는 포기하고, `serializableRun`만 우리가 이미
  아는 "재모집(rehost)" 시스템의 `InLoadedLobby`/`InitializeAsClient` 경로에 태워서 마지막 체크포인트
  (맵/휴식처 등)로라도 복귀시키는 방향. `NMultiplayerLoadGameScreen.InitializeAsClient`, `LoadRunLobby.cs`
  내부 구조와 `ClientRejoinResponseMessage`의 데이터 호환 여부를 더 파봐야 실현 가능성을 알 수 있음.
  미확정 - 조사 더 필요.
- **빠른 재모집 유도(더 현실적, 낮은 리스크)**: 복귀 자체를 포기하고, `NErrorPopup.Create(NetErrorInfo)`
  또는 `RunManager.LocalPlayerDisconnected`를 Harmony로 후킹해서 `StateDivergence`(및 필요시 다른
  일시적 net error들)로 튕겼을 때 자동으로 "재모집 대기를 시작할까요?" 팝업을 띄우는 것.
  단, 이 케이스는 **호스트가 안 떠난 상태**라 기존 "재모집(rehost)" 태깅 시스템(호스트가 세이브를 다시
  열 때 붙는 태그)이 자연스럽게 걸리지 않음 - 호스트가 다시 열 때까지 기다리는 게 아니라, 튕기기 직전
  로비 ID/호스트 SteamID를 우리 쪽에서 기억해뒀다가 바로 재접속을 시도하는 별도 경로가 필요함.

### 참고용 핵심 클래스/파일 (다음에 다시 팔 때 시작점)

- `MegaCrit.Sts2.Core.Multiplayer.Game.ChecksumTracker` - 체크섬 비교/StateDivergence 발생 지점
- `MegaCrit.Sts2.Core.Runs.RunManager` - `LocalPlayerDisconnected`, `ReturnToMainMenuWithError`
- `MegaCrit.Sts2.Core.Nodes.CommonUi.NErrorPopup` - 에러 팝업 표시, `NetError` → 문구 매핑
- `MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow` - `Begin`, `AttemptRejoin`
- `MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.RunLobby` - `OnConnectedToClientAsHost`,
  `HandleClientRejoinRequestMessage`
- `MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NJoinFriendScreen` - `JoinGameAsync` (Running 케이스 처리 지점)
- `MegaCrit.Sts2.Core.Entities.Multiplayer.NetError` - 전체 에러 사유 enum
- `MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.ClientRejoinRequestMessage` / `ClientRejoinResponseMessage`

디컴파일은 `ilspycmd`(dotnet tool, 이미 설치돼 있음)로 `data_sts2_windows_x86_64\sts2.dll` 전체를
프로젝트 모드로 뽑아서 grep하는 식으로 진행함 (`ilspycmd -p -o <출력폴더> sts2.dll`).
