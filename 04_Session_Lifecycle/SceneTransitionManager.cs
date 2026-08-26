using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Cysharp.Threading.Tasks;
using NGPN.Core;
using NGPN.Gameplay.UI;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;
using UnityScene = UnityEngine.SceneManagement.Scene;
using FishNet.Transporting;

namespace NGPN.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SceneTransitionManager : NetworkBehaviour
    {
        public static SceneTransitionManager Instance { get; private set; }

        [Header("Scene Names")]
        [SerializeField] private string lobbySceneName = "Game Lobby";
        [SerializeField] private string gameSceneName = "MergeTestScene";
        [SerializeField] private string victorySceneName = "VictoryScene";
        [SerializeField] private string mainmenuSceneName = "Main Menu";

        [Header("Replace Option")]
        [Tooltip("true면 ReplaceOption.All, false면 ReplaceOption.OnlineOnly")]
        [SerializeField] private bool replaceAllScenes = true;

        [Header("Lobby Cinematic")]
        [SerializeField, Min(0f)] private float lobbyCinematicDuration = 5f;

        private bool _lobbyToGameTransitionStarted;

        [Header("Victory -> Lobby Cinematic")]
        [SerializeField] private bool useVictoryToLobbyCinematic = true;

        // 전원 동의 직후 잠깐 블랙(“잠시 Fade out”)
        [SerializeField][Min(0f)] private float victoryToLobbyPreBlackSec = 2.0f;
        [SerializeField][Min(0f)] private float victoryReturnCinematicDuration = 5.5f;

        // Iris close(+hold) 이후 로딩 호출까지 여유
        [SerializeField][Min(0f)] private float victoryToLobbyIrisBaseDelaySec = 0.2f;

        private bool _victoryToLobbyTransitionStarted;

        // --- Return-to-Lobby Vote (server authoritative) ---
        [Header("Return To Lobby Vote")]
        [SerializeField, Min(1f)] private float returnToLobbyTimeoutSec = 45f;

        private readonly SyncVar<int> _returnVoteReady = new();
        private readonly SyncVar<int> _returnVoteTotal = new();
        private readonly SyncVar<double> _returnVoteEndTs = new();
        private readonly SyncVar<bool> _returnVoteActive = new();

        private readonly HashSet<int> _returnVotedClientIds = new();
        private bool _returnVoteExecuted;

        private bool _returnVoteDelayStarted; // 1초 딜레이 중복 실행 방지

        private readonly SyncVar<GameOverResult> _endMatchDestination = new();

        private InteractionLockHub _myInteractionLockHub;

        private EscPanelController _escPanelCached;

        // 클라가 UI 켤 때 “현재 스냅샷”을 읽을 수 있게 공개 Getter
        public bool Client_IsReturnVoteActive => _returnVoteActive.Value;
        public int Client_ReturnVoteReady => _returnVoteReady.Value;
        public int Client_ReturnVoteTotal => _returnVoteTotal.Value;
        public GameOverResult Client_VoteDestination => _endMatchDestination.Value;

        /// <summary>기본 mouse cursor 풀지</summary>
        [Header("Cursor Default Policy")]
        [SerializeField] private bool unlockCursorInMainMenu = true;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            UnitySceneManager.activeSceneChanged += HandleActiveSceneChanged;
        }

        private void OnDisable()
        {
            UnitySceneManager.activeSceneChanged -= HandleActiveSceneChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            ServerManager.OnRemoteConnectionState += OnRemoteConnectionState_Server;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (ServerManager != null)
                ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState_Server;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ApplyDefaultCursorForScene_Client(UnitySceneManager.GetActiveScene().name);
        }

        [Server]
        private void OnRemoteConnectionState_Server(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            // 누군가 나갔고, 현재 투표가 진행 중이라면
            if (args.ConnectionState == RemoteConnectionState.Stopped && _returnVoteActive.Value)
            {
                int disconnectedClientId = conn.ClientId;

                // 1) 투표를 했던 유저라면 목록에서 제거하고 레디 카운트 감소
                if (_returnVotedClientIds.Remove(disconnectedClientId))
                    _returnVoteReady.Value = Mathf.Max(0, _returnVoteReady.Value - 1);

                // 2) 전체 인원 다시 계산 (방금 나간 유저를 명시적으로 제외)
                int currentTotal = 0;
                foreach (NetworkConnection c in ServerManager.Clients.Values)
                {
                    // 아직 연결이 살아있고 && 이번에 나간 클라이언트가 아닌 경우만 카운트
                    if (c != null && c.IsActive && c.ClientId != disconnectedClientId)
                        currentTotal++;
                }

                _returnVoteTotal.Value = Mathf.Max(1, currentTotal);

                // 3) UI 즉시 갱신 (모든 클라이언트에게 새로운 비율 알림)
                ReturnToLobbyVoteProgress_ObserversRpc(_returnVoteReady.Value, _returnVoteTotal.Value);

                Debug.Log($"[EndMatchVote] Player Left! Recalculated Vote: {_returnVoteReady.Value}/{_returnVoteTotal.Value}");

                // 4) 만약 남은 인원이 모두 투표를 완료한 상태라면 즉시 씬 전환 실행
                if (!_returnVoteExecuted && _returnVoteReady.Value >= _returnVoteTotal.Value)
                    ExecuteReturnVoteAfterDelay_Server().Forget();
            }
        }

        private void HandleActiveSceneChanged(UnityScene oldScene, UnityScene newScene)
        {
            // 클라에서만 커서 상태 의미 있음
            if (!InstanceFinder.IsClientStarted) return;

            // 씬 전환 시 UI 참조는 무효화(각 씬마다 EscPanel이 새로 존재)
            _escPanelCached = null;

            ApplyDefaultCursorForScene_Client(newScene.name);
        }

        [Client]
        private void ApplyDefaultCursorForScene_Client(string sceneName)
        {
            // Main Menu만 커서 자유, 그 외(로비/게임/승리/패배)는 기본 잠금
            bool free =
                unlockCursorInMainMenu &&
                !string.IsNullOrEmpty(mainmenuSceneName) &&
                string.Equals(sceneName, mainmenuSceneName, StringComparison.Ordinal);

            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
        }

        #region Public API

        private bool TryBeginLobbyToGameTransition()
        {
            if (_lobbyToGameTransitionStarted) return false;

            _lobbyToGameTransitionStarted = true;
            return true;
        }

        /// <summary>Lobby → Game 전환 (LobbyGameManager에서 호출)</summary>
        [Server]
        public async void StartGameFromLobby_Server()
        {
            // 투표 상태 리셋
            ResetReturnVoteState_Server("StartGameFromLobby_Server");

            // 1) 클라들한테 "이제 페이드아웃 시작해" 신호
            BeginSceneTransition_ObserversRpc();
            // 2) 클라이언트들이 페이드아웃 할 시간을 조금 준다 (연출 길이랑 맞춰서)
            await UniTask.Delay(TimeSpan.FromSeconds(1f));

            // 3) 실제 게임 씬 전환
            ClearAllInteractionLocksForAllPlayers_Server();
            PlayerRegistry.KillAndRespawnAllPlayersForSceneChange_Server();
            PlayerRegistry.ForceAllPlayerSkillsReady_Server();

            // 게임 씬 진입 1회 궁극기 초기화
            PlayerRegistry.ResetAllUltimateGaugesForSceneEntry_Server();

            LoadSceneWithMovedPlayers(gameSceneName);
        }

        [Server]
        public async void StartGameFromLobby_WithCinematic_Server()
        {
            if (!TryBeginLobbyToGameTransition()) return;

            ResetReturnVoteState_Server("StartGameFromLobby_WithCinematic_Server");

            // 1) 우선 화면을 한번 검게
            BeginSceneTransition_ObserversRpc();
            // 2) 페이드아웃 시간
            await UniTask.Delay(TimeSpan.FromSeconds(2f));

            // 3) 다시 보여주고
            EndSceneTransition_ObserversRpc();

            // 4) 시네마틱 시작
            JobType[] jobIds = BuildLobbyJobTypesInStableOrder_Server();
            StartLobbyCinematic_ObserversRpc(jobIds);

            // 5) 연출 재생 시간
            await UniTask.Delay(TimeSpan.FromSeconds(lobbyCinematicDuration));

            // 아이리스 준비 먼저
            PrepareLobbyToGameIris_ObserversRpc();

            // 6) 여기서 바로 페이드아웃
            BeginSceneTransition_ObserversRpc();

            PlayerSpawner spawner = FindAnyObjectByType<PlayerSpawner>(FindObjectsInactive.Exclude);
            spawner.ResolveRandomPlayersToRealJobs_Server();

            const float baseDelay = 1.0f; // 기존 페이드 준비 여유
            float irisDelay = SceneTransitionFx.Instance.IrisCloseDuration +
                              SceneTransitionFx.Instance.IrisHoldDuration;
            await UniTask.Delay(TimeSpan.FromSeconds(baseDelay + irisDelay));

            // 7) 검은 화면에서 시네마틱 정리 (카메라 OFF / 더미 제거)
            StopLobbyCinematic_ObserversRpc();

            ClearAllInteractionLocksForAllPlayers_Server();
            PlayerRegistry.KillAndRespawnAllPlayersForSceneChange_Server();
            PlayerRegistry.ForceAllPlayerSkillsReady_Server();

            // 로비 씬 진입 1회 궁극기 초기화
            PlayerRegistry.ResetAllUltimateGaugesForSceneEntry_Server();
            _lobbyToGameTransitionStarted = false;
            LoadSceneWithMovedPlayers(gameSceneName);
        }

        [ObserversRpc(BufferLast = false)]
        private void StartLobbyCinematic_ObserversRpc(JobType[] jobType)
        {
            LobbyCinematicClientController c =
                FindFirstObjectByType<LobbyCinematicClientController>(FindObjectsInactive.Include);

            if (c == null)
            {
                Debug.LogWarning("[SceneTransitionManager] LobbyCinematicClientController not found.");
                return;
            }


            c.StartCinematic(jobType);
        }

        [ObserversRpc(BufferLast = false)]
        private void StopLobbyCinematic_ObserversRpc()
        {
            LobbyCinematicClientController c =
                FindFirstObjectByType<LobbyCinematicClientController>(FindObjectsInactive.Include);

            if (c == null) return;

            c.StopCinematic();
        }

        [ObserversRpc(BufferLast = false)]
        private void PrepareLobbyToGameIris_ObserversRpc()
        {
            // 로비 컷씬 전환에서만 쓰는 1회성 세팅
            SceneTransitionFx fx = SceneTransitionFx.Instance;
            if (fx == null) return;

            // 포커스(여신상) 트랜스폼은 "로비 씬에 있는 여신상"을 찾아야 함
            // 가장 안정적인 방법: LobbyCinematicClientController가 가지고 있는 statueRoot를 가져오기
            LobbyCinematicClientController c =
                FindFirstObjectByType<LobbyCinematicClientController>(FindObjectsInactive.Include);

            if (c == null) return;

            Transform statue = c.StatueRoot;
            if (statue == null) return;

            fx.SetIrisFocus(statue);
            fx.UseIrisOnNextFadeOut(true); // 다음 FadeOut 1회만 아이리스
        }


        [Server]
        private JobType[] BuildLobbyJobTypesInStableOrder_Server()
        {
            // "모든 클라이언트가 같은 순서로 슬롯에 서야" 각 클라에서 더미가 동일하게 배치됨
            // -> ClientId(=Key)를 정렬해서 안정적인 순서 확보
            NetworkConnection[] ordered = InstanceFinder.ServerManager.Clients
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToArray();

            JobType[] jobTypes = new JobType[ordered.Length];

            for (int i = 0; i < ordered.Length; i++)
            {
                NetworkConnection conn = ordered[i];
                jobTypes[i] = GetJobTypeFromConnection_Server(conn);
            }

            return jobTypes;
        }

        [Server]
        private JobType GetJobTypeFromConnection_Server(NetworkConnection conn)
        {
            if (conn == null || conn.FirstObject == null)
                return 0;

            CharacterActor actor = conn.FirstObject.GetComponentInChildren<CharacterActor>(true);
            if (actor == null)
                return 0;

            return actor.JobStatsDef.jobType;
        }


        /// <summary>Game → Victory 전환 (DefenseGameManager에서 호출)</summary>
        [Server]
        public async void LoadVictorySceneFromGame_Server()
        {
            ResetReturnVoteState_Server("LoadVictorySceneFromGame_Server");

            BeginSceneTransition_ObserversRpc();
            await UniTask.Delay(TimeSpan.FromSeconds(1f));

            ClearAllInteractionLocksForAllPlayers_Server();
            PlayerRegistry.KillAndRespawnAllPlayersForSceneChange_Server();
            PlayerRegistry.ForceAllPlayerSkillsReady_Server();

            // 여기서는 굳이 궁극기 초기화 안 함
            LoadSceneWithMovedPlayers(victorySceneName);
        }


        /// <summary>Game/Victory/Defeat → Lobby 전환 (DefenseGameManager에서 호출)</summary>
        [Server]
        public async void LoadLobbySceneFromVictoryOrResult_Server()
        {
            ResetReturnVoteState_Server("LoadLobbySceneFromVictoryOrResult_Server");

            BeginSceneTransition_ObserversRpc();
            await UniTask.Delay(TimeSpan.FromSeconds(1f));

            ClearAllInteractionLocksForAllPlayers_Server();
            PlayerRegistry.KillAndRespawnAllPlayersForSceneChange_Server();
            PlayerRegistry.ForceAllPlayerSkillsReady_Server();
            PlayerRegistry.ResetAllUltimateGaugesForSceneEntry_Server();

            LoadSceneWithMovedPlayers(lobbySceneName);
        }

        /// <summary>전원 로비로 이동 요청 (클라는 ServerRpc, 서버는 즉시 실행)</summary>
        public static void RequestGotoLobbyFromResultOrGame()
        {
            if (Instance == null) return;

            // 호스트/서버면 즉시 실행
            if (Instance.IsServerInitialized)
                Instance.GotoLobby_Internal_Server();
            else
                Instance.GotoLobby_ServerRpc();
        }

        /// <summary>클라의 로비 이동 요청을 서버로 전달(소유권 무관)</summary>
        [ServerRpc(RequireOwnership = false)]
        private void GotoLobby_ServerRpc(NetworkConnection conn = null)
        {
            if (conn == null) return;
            GotoLobby_Internal_Server();
        }

        /// <summary>서버에서 로비 이동 전 전역 락 해제, 로비 씬 전환 시작</summary>
        [Server]
        private void GotoLobby_Internal_Server()
        {
            // 전환 전에 게임오버 락 해제
            DefenseGameManager.Instance?.ReleaseGlobalFreezeBeforeSceneTransition_Server();
            LoadLobbySceneFromVictoryOrResult_Server();
        }

        #endregion

        #region 승리씬 로비 복귀 투표

        /// <summary>
        /// Victory 씬 ESC의 ReturnToLobby 버튼에서 호출:
        /// - 투표가 아직 시작되지 않았으면 (dest=Lobby) 투표 시작
        /// - 그리고 본인 투표 제출
        /// </summary>
        public static void RequestReturnToLobbyVote_FromVictoryEsc()
        {
            if (Instance == null) return;

            if (Instance.IsServerInitialized)
            {
                int myId = InstanceFinder.ClientManager.Connection.ClientId;
                Instance.BeginReturnToLobbyVoteIfNeeded_Server();
                Instance.SubmitReturnVote_Internal_Server(myId);
            }
            else
            {
                Instance.BeginReturnToLobbyVoteAndSubmit_ServerRpc();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void BeginReturnToLobbyVoteAndSubmit_ServerRpc(NetworkConnection conn = null)
        {
            if (conn == null) return;

            BeginReturnToLobbyVoteIfNeeded_Server();
            SubmitReturnVote_Internal_Server(conn.ClientId);
        }

        [Server]
        private void BeginReturnToLobbyVoteIfNeeded_Server()
        {
            // 이미 진행 중이면 재시작 금지
            if (_returnVoteActive.Value)
                return;

            // 1. 투표 상태 자체 초기화 (ProceedVote 로직 인라인화)
            _returnVoteExecuted = false;
            _returnVoteDelayStarted = false;
            _returnVotedClientIds.Clear();

            // 2. 현재 접속 중인 전체 인원수 파악
            int total = 0;
            foreach (KeyValuePair<int, NetworkConnection> kv in InstanceFinder.ServerManager.Clients)
                if (kv.Value != null)
                    total++;

            _returnVoteTotal.Value = Mathf.Max(1, total);
            _returnVoteReady.Value = 0;

            // 3. 타이머 및 활성화 상태 설정
            _returnVoteEndTs.Value = InstanceFinder.TimeManager.ServerUptime + returnToLobbyTimeoutSec;
            _returnVoteActive.Value = true;


            /* NOTE :
             * 여기서 게임씬 -> 로비 때 쓰는 함수와 승리씬 -> 로비 때 쓰는 함수에 차이가 없음에도 기능이 분리되어 있지 않고 같은 호출 흐름을 타기에
             * 출시까지 얼마 남지 않은 점, 이미 구조가 많이 복잡한 점 등을 고려해 승리씬 -> 로비 과정을 하드코딩으로 처리함
             */

            // 4. 목적지 설정: 씬에 따라 분기
            string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            // Game 씬이면: 로비 복귀는 "패배 케이스"로 태워서 ExecuteReturnVote_Server에서 LoadLobbySceneFromVictoryOrResult_Server()로 가게
            if (string.Equals(active, gameSceneName, StringComparison.Ordinal))
            {
                _endMatchDestination.Value = GameOverResult.Defeat;
            }
            // Victory 씬이면: 승리 리턴 시네마틱 케이스로 태움
            else if (string.Equals(active, victorySceneName, StringComparison.Ordinal))
            {
                _endMatchDestination.Value = GameOverResult.VictoryReturnLobby;
            }
            else
            {
                // 예상 외(혹은 Defeat/로비 등)일 때 안전장치: 그냥 일반 로비 복귀(시네마틱 없음)로 처리하고 싶으면 Defeat,
                // 시네마틱을 강제로 타고 싶으면 VictoryReturnLobby 같은 값으로.
                Debug.LogWarning($"[ReturnVote] Unexpected active scene '{active}'. Fallback to Defeat->Lobby flow.");
                _endMatchDestination.Value = GameOverResult.Defeat;
            }

            // 5. 클라이언트들에게 투표 시작 알림 (UI_ResultsPanel은 null 체크로 걸러짐)
            EndMatchVoteState_ObserversRpc(true, 0, _returnVoteTotal.Value, returnToLobbyTimeoutSec,
                GameOverResult.Defeat);
        }

        /// <summary>투표 취소 로직 (ESC 패널 닫을 때 호출)</summary>
        public static void RequestCancelReturnToLobbyVote()
        {
            if (Instance == null) return;

            // 호스트면 내부 로직 바로 호출, 아니면 RPC
            if (Instance.IsServerInitialized)
                Instance.CancelReturnVote_Internal_Server(InstanceFinder.ClientManager.Connection.ClientId);
            else
                Instance.CancelReturnToLobbyVoteAndRetract_ServerRpc();
        }

        // 2. ServerRPC 추가
        [ServerRpc(RequireOwnership = false)]
        private void CancelReturnToLobbyVoteAndRetract_ServerRpc(NetworkConnection conn = null)
        {
            if (conn == null) return;
            CancelReturnVote_Internal_Server(conn.ClientId);
        }

        // 3. 서버 내부 로직 추가
        [Server]
        private void CancelReturnVote_Internal_Server(int clientId)
        {
            // 이미 결과가 나서 실행 중이면 취소 불가
            if (!_returnVoteActive.Value || _returnVoteExecuted) return;

            // 투표 목록에서 제거 성공 시 카운트 감소 및 UI 동기화
            if (_returnVotedClientIds.Remove(clientId))
            {
                _returnVoteReady.Value = Mathf.Max(0, _returnVoteReady.Value - 1);
                ReturnToLobbyVoteProgress_ObserversRpc(_returnVoteReady.Value, _returnVoteTotal.Value);
                Debug.Log(
                    $"[VictoryVote] Client {clientId} canceled vote. ({_returnVoteReady.Value}/{_returnVoteTotal.Value})");
            }
        }

        #endregion

        #region 게임씬 종료 투표

        [Server]
        public void BeginEndMatchProceedVote_Server(GameOverResult dest, float? timeoutOverride = null)
        {
            if (_returnVoteActive.Value) return;

            _returnVoteExecuted = false;
            _returnVoteDelayStarted = false;
            _returnVotedClientIds.Clear();

            int total = 0;
            foreach (KeyValuePair<int, NetworkConnection> kv in InstanceFinder.ServerManager.Clients)
                if (kv.Value != null)
                    total++;

            _returnVoteTotal.Value = Mathf.Max(1, total);
            _returnVoteReady.Value = 0;

            double now = InstanceFinder.TimeManager.ServerUptime;
            double timeout = timeoutOverride ?? returnToLobbyTimeoutSec;

            _returnVoteEndTs.Value = now + timeout;
            _returnVoteActive.Value = true;

            _endMatchDestination.Value = dest;

            EndMatchVoteState_ObserversRpc(true, _returnVoteReady.Value, _returnVoteTotal.Value, (float)timeout, dest);
        }

        public static void RequestEndMatchProceedVote()
        {
            if (Instance == null) return;

            if (Instance.IsServerInitialized)
                Instance.SubmitReturnVote_Internal_Server(InstanceFinder.ClientManager.Connection.ClientId);
            else
                Instance.SubmitEndMatchProceedVote_ServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void SubmitEndMatchProceedVote_ServerRpc(NetworkConnection conn = null)
        {
            if (conn == null) return;
            SubmitReturnVote_Internal_Server(conn.ClientId);
        }

        [Server]
        private void SubmitReturnVote_Internal_Server(int clientId)
        {
            if (!_returnVoteActive.Value) return;
            if (_returnVoteExecuted) return;

            if (_returnVotedClientIds.Add(clientId))
            {
                _returnVoteReady.Value = Mathf.Min(_returnVoteTotal.Value, _returnVoteReady.Value + 1);

                ReturnToLobbyVoteProgress_ObserversRpc(_returnVoteReady.Value, _returnVoteTotal.Value);

                if (_returnVoteReady.Value >= _returnVoteTotal.Value)
                    ExecuteReturnVoteAfterDelay_Server().Forget();
            }
        }

        /// <summary>전원 투표 완료 시 1초 대기 후, 전환 로직 실행</summary>
        [Server]
        private async UniTaskVoid ExecuteReturnVoteAfterDelay_Server()
        {
            if (_returnVoteDelayStarted) return; // 중복 방지
            _returnVoteDelayStarted = true;

            // 더 이상 추가 투표/중복 실행 방지
            _returnVoteExecuted = true;

            // 투표 UI는 그대로 유지 (ready/total 표시 가능)
            await UniTask.Delay(1500);

            _returnVoteActive.Value = false;

            ExecuteReturnVote_Server();
        }

        [Server]
        private void ExecuteReturnVote_Server()
        {
            // 여기서 패널 닫기(1초 후)
            EndMatchVoteState_ObserversRpc(false, _returnVoteReady.Value, _returnVoteTotal.Value, 0f,
                _endMatchDestination.Value);

            DefenseGameManager.Instance?.ReleaseGlobalFreezeBeforeSceneTransition_Server();

            // 여기서 목적지에 따라 이동
            switch (_endMatchDestination.Value)
            {
                case GameOverResult.Victory:
                    LoadVictorySceneFromGame_Server();
                    break;
                case GameOverResult.Defeat:
                    LoadLobbySceneFromVictoryOrResult_Server();
                    break;
                case GameOverResult.VictoryReturnLobby:
                    LoadLobbySceneFromVictory_WithCinematic_Server();
                    break;
                default:
                    LoadLobbySceneFromVictoryOrResult_Server();
                    break;
            }
        }

        [ObserversRpc(BufferLast = false, RunLocally = true)]
        private void EndMatchVoteState_ObserversRpc(bool active, int ready, int total, float timeoutSec,
            GameOverResult dest)
        {
            if (active)
                Debug.Log($"[EndMatchVote] Started: {ready}/{total}, timeout {timeoutSec:0}s, dest={dest}");

            // 패배 후 ResultPanel의 VotePanel 갱신
            if (UI_ResultsPanel.Instance != null)
            {
                if (active)
                    UI_ResultsPanel.Instance.UpdateProceedVoteUI(ready, total);
                else
                    UI_ResultsPanel.Instance.OnProceedVoteFinished();
            }

            // 승리씬 VotePanel 갱신
            EscPanelController escPanel = GetEscPanelCached_Client();
            if (escPanel != null)
            {
                if (active)
                {
                    escPanel.UpdateReturnToLobbyVoteUI(ready, total);
                }
                else
                {
                    escPanel.UpdateReturnToLobbyVoteUI(ready, total);
                    escPanel.OnReturnToLobbyVoteFinished();
                }
            }
        }

        [ObserversRpc(BufferLast = false, RunLocally = true)]
        private void ReturnToLobbyVoteProgress_ObserversRpc(int ready, int total)
        {
            Debug.Log($"[EndMatchVote] Progress: {ready}/{total}");

            if (UI_ResultsPanel.Instance != null)
                UI_ResultsPanel.Instance.UpdateProceedVoteUI(ready, total);

            EscPanelController escPanel = GetEscPanelCached_Client();
            if (escPanel) escPanel.UpdateReturnToLobbyVoteUI(ready, total);
        }

        #endregion

        #region 투표 상태 리셋

        /// <summary>씬 전환 경계에서 투표 상태 강제 리셋</summary>
        [Server]
        private void ResetReturnVoteState_Server(string reason)
        {
            _returnVoteExecuted = false;
            _returnVoteDelayStarted = false;
            _returnVotedClientIds.Clear();

            _returnVoteReady.Value = 0;
            _returnVoteTotal.Value = 0;
            _returnVoteEndTs.Value = 0;
            _returnVoteActive.Value = false;

            // 목적지는 의미 없게 초기화
            _endMatchDestination.Value = GameOverResult.Defeat;

            Debug.Log($"[EndMatchVote] Reset vote state. reason={reason}");
        }

        #endregion

        #region 승리씬 -> 로비 시네마틱

        private bool TryBeginVictoryToLobbyTransition()
        {
            if (_victoryToLobbyTransitionStarted) return false;

            _victoryToLobbyTransitionStarted = true;
            return true;
        }

        [Server]
        public async void LoadLobbySceneFromVictory_WithCinematic_Server()
        {
            if (!TryBeginVictoryToLobbyTransition()) return;

            ResetReturnVoteState_Server("LoadLobbySceneFromVictory_WithCinematic_Server");

            // 1) 잠깐 블랙
            BeginSceneTransition_ObserversRpc();
            await UniTask.Delay(TimeSpan.FromSeconds(victoryToLobbyPreBlackSec));

            // 2) 컷씬 보여주기 위해 다시 오픈
            EndSceneTransition_ObserversRpc();

            // 3) 컷씬 시작(클라 로컬)
            JobType[] jobTypes = BuildLobbyJobTypesInStableOrder_Server();
            StartVictoryReturnCinematic_ObserversRpc(jobTypes);

            // 4) 낙하 추적까지 진행될 시간
            await UniTask.Delay(TimeSpan.FromSeconds(victoryReturnCinematicDuration));

            // 5) Iris 포커스(낙하 타겟) 지정 + 다음 FadeOut에 Iris 사용
            PrepareVictoryToLobbyIris_ObserversRpc();

            // 6) Iris out + 블랙 홀드
            BeginSceneTransition_ObserversRpc();

            float irisDelay = GetIrisTotalDuration_Server();
            await UniTask.Delay(TimeSpan.FromSeconds(victoryToLobbyIrisBaseDelaySec + irisDelay));

            // 7) 검은 화면에서 컷씬 정리
            StopVictoryReturnCinematic_ObserversRpc();

            // 8) 실제 로비 로드
            ClearAllInteractionLocksForAllPlayers_Server();
            PlayerRegistry.KillAndRespawnAllPlayersForSceneChange_Server();
            PlayerRegistry.ForceAllPlayerSkillsReady_Server();
            PlayerRegistry.ResetAllUltimateGaugesForSceneEntry_Server();

            _victoryToLobbyTransitionStarted = false;
            LoadSceneWithMovedPlayers(lobbySceneName);
        }

        [Server]
        private float GetIrisTotalDuration_Server()
        {
            // 전용 서버(헤드리스)면 SceneTransitionFx가 없을 수 있으니 fallback
            SceneTransitionFx fx = SceneTransitionFx.Instance;
            if (fx != null)
                return fx.IrisCloseDuration + fx.IrisHoldDuration;

            return 0.9f; // 대략값(0.35 + 0.5)
        }

        [ObserversRpc(BufferLast = false)]
        private void StartVictoryReturnCinematic_ObserversRpc(JobType[] jobTypes)
        {
            VictoryReturnCinematicClientController c =
                FindFirstObjectByType<VictoryReturnCinematicClientController>(FindObjectsInactive.Include);

            if (c == null)
            {
                Debug.LogWarning("[SceneTransitionManager] VictoryReturnCinematicClientController not found.");
                return;
            }

            c.StartCinematic(jobTypes);
        }

        [ObserversRpc(BufferLast = false)]
        private void StopVictoryReturnCinematic_ObserversRpc()
        {
            VictoryReturnCinematicClientController c =
                FindFirstObjectByType<VictoryReturnCinematicClientController>(FindObjectsInactive.Include);

            if (c == null) return;

            c.StopCinematic();
        }

        [ObserversRpc(BufferLast = false)]
        private void PrepareVictoryToLobbyIris_ObserversRpc()
        {
            VictoryReturnCinematicClientController c =
                FindFirstObjectByType<VictoryReturnCinematicClientController>(FindObjectsInactive.Include);

            if (c == null) return;
            c.PrepareIrisClose();
        }

        #endregion

        private void ResolveLocalRefsIfNeeded()
        {
            if (_myInteractionLockHub == null)
            {
                InteractionLockHub[] hubs =
                    FindObjectsByType<InteractionLockHub>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (InteractionLockHub h in hubs)
                    if (h != null && h.IsOwner)
                    {
                        _myInteractionLockHub = h;
                        break;
                    }
            }
        }

        #region private loader

        /// <summary>
        /// 현재 접속 중인 모든 클라이언트의 FirstObject를 그대로 들고
        /// 지정한 씬으로 이동시킨다.
        /// </summary>
        [Server]
        private void LoadSceneWithMovedPlayers(string sceneName)
        {
            // 1) 로비에서만 신규 접속 허용
            bool allowJoins = string.Equals(sceneName, lobbySceneName, StringComparison.Ordinal);
            if (PlayerRegistry.Instance != null && PlayerRegistry.Instance.IsServerInitialized)
                PlayerRegistry.Instance.SetAllowNewConnections_Server(allowJoins);

            // 2) Steam 로비 Joinable도 로비에서만 true (호스트만)
            SteamLobby.TrySetLobbyJoinable_HostOnly(allowJoins);

            if (string.IsNullOrWhiteSpace(sceneName))
            {
                Debug.LogError($"[SceneTransitionManager] sceneName is null or empty.");
                return;
            }

            SceneLoadData sld = new(sceneName)
            {
                ReplaceScenes = replaceAllScenes ? ReplaceOption.All : ReplaceOption.OnlineOnly
            };

            List<NetworkObject> moved = new();

            foreach (KeyValuePair<int, NetworkConnection> kv in InstanceFinder.ServerManager.Clients)
            {
                NetworkConnection conn = kv.Value;
                if (conn != null && conn.FirstObject != null)
                    if (conn.FirstObject.GetComponent<CharacterActor>())
                        moved.Add(conn.FirstObject.GetComponentInParent<CharacterActor>()
                            .GetComponent<NetworkObject>());
            }

            if (moved.Count > 0)
                sld.MovedNetworkObjects = moved.ToArray();

            InstanceFinder.SceneManager.LoadGlobalScenes(sld);

            Debug.Log($"[SceneTransitionManager] Load '{sceneName}' (moved {moved.Count} players).");
        }

        #endregion

        [ObserversRpc(BufferLast = false)]
        private void BeginSceneTransition_ObserversRpc()
        {
            SceneTransitionFx fx = SceneTransitionFx.Instance;
            fx?.FadeOutAndHoldAsync().Forget();
        }

        [ObserversRpc(BufferLast = false)]
        private void EndSceneTransition_ObserversRpc()
        {
            SceneTransitionFx fx = SceneTransitionFx.Instance;
            fx?.FadeInAsync().Forget();
        }

        #region Interaction Locks Reset

        /// <summary>
        /// 현재 접속 중인 모든 플레이어의 InteractionLockHub에 걸린
        /// Move / Camera / Attack / Interaction 락을 전부 해제한다.
        /// (씬 전환 전에 호출)
        /// </summary>
        [Server]
        private void ClearAllInteractionLocksForAllPlayers_Server()
        {
            // 1) FirstObject 기준으로 각 플레이어의 InteractionLockHub 찾기
            foreach (KeyValuePair<int, NetworkConnection> kv in InstanceFinder.ServerManager.Clients)
            {
                NetworkConnection conn = kv.Value;
                if (conn == null) continue;

                NetworkObject first = conn.FirstObject;
                if (first == null) continue;

                // 허브가 루트에 있을 수도, 자식에 있을 수도 있으니 GetComponentInChildren 사용
                InteractionLockHub hub = first.GetComponentInChildren<InteractionLockHub>(true);
                if (hub != null && hub.IsServerInitialized)
                    // 모든 도메인 락 해제 (서버 권위)
                    hub.ClearAllLocks();
            }
        }

        #endregion

        #region Helper

        [Client]
        public void RegisterEscPanel_Client(EscPanelController esc)
        {
            if (esc == null) return;
            _escPanelCached = esc;
        }

        [Client]
        public void UnregisterEscPanel_Client(EscPanelController esc)
        {
            if (_escPanelCached == esc)
                _escPanelCached = null;
        }

        [Client]
        private EscPanelController GetEscPanelCached_Client()
        {
            if (_escPanelCached != null)
                return _escPanelCached;

            // fallback: 한 번만 찾고 캐시
            _escPanelCached = FindFirstObjectByType<EscPanelController>(FindObjectsInactive.Include);
            return _escPanelCached;
        }

        #endregion
    }
}
