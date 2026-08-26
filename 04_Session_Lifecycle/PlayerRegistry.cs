using System;
using System.Collections.Generic;
using UnityEngine;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using NGPN.Core;
using FishNet.Managing;


#if !DISABLESTEAMWORKS
using Steamworks; // Steamworks.NET
#endif
#if USE_EOS || EOS_PLUGIN
using Epic.OnlineServices;            // EOS SDK가 프로젝트에 있을 때만
using Epic.OnlineServices.Platform;   // (플래그는 프로젝트에 맞게)
#endif

namespace NGPN.Gameplay
{
    public class PlayerRegistry : NetworkBehaviour
    {
        // Managers 쪽에서는 싱글톤 써도 OK (Actors/Core에서는 절대 접근 금지)
        public static PlayerRegistry Instance { get; private set; }

        // 서버에서만 관리하는 플레이어 리스트
        private readonly List<ICharacterActor> _players = new();

        private readonly SyncHashSet<int> _connectedOwners = new(); // 접속 중 ownerId
        private readonly SyncHashSet<int> _readyOwners = new(); // 레디 ownerId
        private readonly SyncDictionary<int, string> _ownerNames = new(); // 이름 디렉토리
        private readonly SyncDictionary<int, JobType> _ownerJobs = new(); // ownerId -> JobType

#if !DISABLESTEAMWORKS
        // 클라이언트의 SteamID를 미리 캐싱해두어 연결 종료 시 확실하게 P2P 세션을 닫기 위한 딕셔너리
        private readonly Dictionary<int, CSteamID> _clientSteamIds = new();
#endif

        private bool _allowNewConnectionsServer = true; // 서버에서 신규 연결 허용 여부. 게임 씬에서는 false로 바꿔서 중간 참가 방지.
        public bool AllowNewConnectionServer => _allowNewConnectionsServer;

        public int PlayerCount => _connectedOwners.Count;
        public int ReadyPlayerCount => _readyOwners.Count;

        // 모두 준비 완료 시 알림(서버)
        public event Action AllPlayersReadyServer;

        /// <summary>(준비 인원, 총 인원) 카운트 변동 이벤트</summary>
        public event Action<int, int, bool> CountsChanged; // 인자 : <ready, total, asServer>

        public event Action<int, string> OnPlayerNameChanged;

        // 외부 참조용
        public IReadOnlyList<ICharacterActor> Players => _players; // 플레이어 목록

        // 내부 변수
        private bool hooked = false; // 호스트에서 이벤트 구독이 2회 발생되는 것을 막기 위한 플래그

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        #region Server LifeCycle

        public override void OnStartServer()
        {
            base.OnStartServer();

            _players.Clear();
            _connectedOwners.Clear();
            _readyOwners.Clear();
            _ownerNames.Clear();
            _ownerJobs.Clear();

            // 네트워크 연결/해제 감시
            ServerManager.OnRemoteConnectionState += OnRemoteConnState_Server;

            BackfillExistingConnections_Server(); // 서버 시작 시점에

            // 이미 씬에 떠있는 액터/오너 스캔 (재호스트/씬 리로드 대비)
            foreach (NetworkObject no in InstanceFinder.ServerManager.Objects.Spawned.Values)
                if (no.TryGetComponent(out CharacterActor actor))
                {
                    Register(actor); // Register 내부에서 readyOwners 정리
                    actor.InitializeDisplayName_Server();
                }

            HookOnChange(); // 클라/서버 공통 OnChange 훅
            PublishCounts(true); // 초기 1회
        }

        public override void OnStopServer()
        {
            ServerManager.OnRemoteConnectionState -= OnRemoteConnState_Server;

            base.OnStopServer();
        }

        #endregion

        #region Client LifeCycle

        public override void OnStartClient()
        {
            base.OnStartClient();

            HookOnChange();

            PublishCounts(false); // 초기 1회
        }

        #endregion

        #region Hard Reset

        /// <summary>
        /// 씬 전환 시, 모든 플레이어 캐릭터를 즉시 죽이고(클린업)
        /// 즉시 리스폰된 상태로 강제 초기화한다.
        /// (IDeathCleanable / IRespawnable을 모두 태움)
        /// </summary>
        [Server]
        public static void KillAndRespawnAllPlayersForSceneChange_Server()
        {
            if (Instance == null || !Instance.IsServerInitialized) return;

            List<ICharacterActor> list = Instance._players;
            if (list == null || list.Count == 0) return;

            foreach (ICharacterActor actor in list)
            {
                if (actor == null) continue;

                // ICharacterActor → Component로 캐스팅해서 GameObject에 접근
                if (actor is Component comp)
                    if (comp.TryGetComponent(out CharacterHealth health))
                        health.KillAndRespawnImmediately_Server();
            }
        }

        /// <summary>
        /// (서버) 현재 등록된 모든 플레이어의 스킬 쿨타임을 즉시 초기화.
        /// SceneTransitionManager에서 씬 전환 직전에 호출한다.
        /// </summary>
        [Server]
        public static void ForceAllPlayerSkillsReady_Server()
        {
            if (Instance == null || !Instance.IsServerInitialized) return;

            List<ICharacterActor> list = Instance._players;
            if (list == null || list.Count == 0) return;

            foreach (ICharacterActor actor in list)
            {
                if (actor == null) continue;

                if (actor is Component comp && comp.TryGetComponent(out CharacterActor ca))
                    ca.ForceAllSkillsReady_Server();
            }
        }

        #endregion

        private void HookOnChange()
        {
            if (hooked) return;
            hooked = true;


            // 컬렉션 변경 시마다 UI에 알림(클라 컨텍스트)
            _connectedOwners.OnChange += (op, value, asServer) => OnCountsChanged(asServer);
            _readyOwners.OnChange += (op, value, asServer) => OnCountsChanged(asServer);
            _ownerNames.OnChange += (op, key, value, asServer) =>
            {
                // 이름이 실제로 생기거나 바뀐 경우에만 알린다
                if (op == SyncDictionaryOperation.Add ||
                    op == SyncDictionaryOperation.Set)
                    OnPlayerNameChanged?.Invoke(key, value);
                // Remove일 때는 네임플레이트를 비워버리면 안 되니까 무시
            };
        }

        // 준비 인원이나 총 인원 수에 변동이 생기면 불리는 콜백 함수
        private void OnCountsChanged(bool asServer)
        {
            PublishCounts(asServer);

            // 서버 전용 로직: 전원 레디 신호
            if (asServer && PlayerCount > 0 && ReadyPlayerCount == PlayerCount)
                AllPlayersReadyServer?.Invoke();
        }

        /// <summary>
        /// 다른 스크립트에게 준비 인원이나 총 인원 수에 변동이 생겼다고 알림
        /// </summary>
        /// <param name="asServer">해당 인자를 통해 서버용 함수, 클라용 함수를 구분하도록 함</param>
        private void PublishCounts(bool asServer)
        {
            CountsChanged?.Invoke(ReadyPlayerCount, PlayerCount, asServer);
        }

        /// <summary>이름 매핑하기 위한 임시 코드</summary>
        [Server]
        private void BackfillExistingConnections_Server()
        {
            // FishNet: 서버에 인지된 모든 연결을 순회
            foreach (KeyValuePair<int, NetworkConnection> kvp in InstanceFinder.ServerManager.Clients)
            {
                NetworkConnection conn = kvp.Value;
                if (conn == null) continue;

                // 총원/레디 상태 스냅샷 반영
                _connectedOwners.Add(conn.ClientId);
                _readyOwners.Remove(conn.ClientId);

                // ✅ (수정) 기존 연결들 Backfill 시에도 서버가 플랫폼 닉네임을 추측하지 않는다.
                if (!_ownerNames.ContainsKey(conn.ClientId))
                {
                    string name = ResolvePlatformDisplayName_Server(conn);

                    if (!string.IsNullOrWhiteSpace(name))
                        _ownerNames[conn.ClientId] = name;
                }
            }
        }

        #region Server API

        /// <summary>서버: 신규 연결 허용 여부 설정</summary>
        [Server]
        public void SetAllowNewConnections_Server(bool allow)
        {
            if (!IsServerInitialized) return;
            _allowNewConnectionsServer = allow;
        }

        /// <summary>서버: 로그인/플랫폼에서 받은 닉네임을 등록</summary>
        [Server]
        // public void SetPlayerName(int connId, string displayName)
        // {
        //     if (!IsServerInitialized) return;
        //     if (string.IsNullOrWhiteSpace(displayName)) return;
        //     if (_ownerNames.TryGetValue(connId, out string cur) && cur == displayName)
        //         return;

        //     _ownerNames[connId] = displayName;
        //     OnPlayerNameChanged?.Invoke(connId, displayName);
        // }
        // PlayerRegistry.cs
        public void SetPlayerName(int connId, string displayName)
        {
            if (!IsServerInitialized) return;

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = $"Conn-{connId}";

            // 서버/클라 모두 동일 경로로 갱신되게 RPC로 통일
            SyncPlayerName_ObserversRpc(connId, displayName);
        }

        [ObserversRpc(BufferLast = true, RunLocally = true)]
        private void SyncPlayerName_ObserversRpc(int connId, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = $"Conn-{connId}";

            _ownerNames[connId] = displayName;

            // 이제 "클라에서도" 이벤트가 발생하므로 HUD/Nameplate가 즉시 갱신됨
            OnPlayerNameChanged?.Invoke(connId, displayName);
        }

        [ObserversRpc(BufferLast = false, RunLocally = true)]
        private void RemovePlayerName_ObserversRpc(int connId)
        {
            _ownerNames.Remove(connId);
            OnPlayerNameChanged?.Invoke(connId, $"Conn-{connId}");
        }

        /// <summary>서버: 등록 닉네임 조회</summary>
        [Server]
        public bool TryGetPlayerName(int connId, out string displayName)
        {
            return _ownerNames.TryGetValue(connId, out displayName);
        }

        [Server]
        public void Register(ICharacterActor actor)
        {
            if (!IsServerInitialized || actor == null) return;
            if (_players.Contains(actor)) return;

            _players.Add(actor);

            int ownerId = -1;
            if (TryGetOwnerId(actor, out ownerId))
            {
                _connectedOwners.Add(ownerId);
                _readyOwners.Remove(ownerId);
            }

            if (actor is CharacterActor ca)
            {
                ca.ResetRunStats_Server();

                PlayerStatics ps = ca.GetComponentInParent<PlayerStatics>();
                if (ps != null) ps.ResetForNewRun_Server();

                if (ca.health != null) ca.health.RestoreFullHP_Server();

                if (ownerId >= 0)
                {
                    JobType jt = ca.JobStatsDef != null ? ca.JobStatsDef.jobType : JobType.None;
                    _ownerJobs[ownerId] = jt;
                }
            }

            PublishCounts(true);

            Debug.Log($"Registered actor: {actor}");
        }

        [Server]
        public void Unregister(ICharacterActor actor)
        {
            if (!IsServerInitialized || actor == null) return;

            _players.Remove(actor);

            if (TryGetOwnerId(actor, out int ownerId))
            {
                // 소유자 연결이 살아있을 수 있으니, 여기선 ready만 정리
                _readyOwners.Remove(ownerId);
                _ownerJobs.Remove(ownerId);
            }


            PublishCounts(true);
        }

        /// <summary>
        /// (모든 클라) 특정 플레이어의 음성 상태/아바타 매핑을 정리한다.
        /// </summary>
        [ObserversRpc(BufferLast = false, RunLocally = true)]
        private void OnPlayerLeft_ClearVoice_ObserversRpc(string vivoxPlayerId)
        {
            if (string.IsNullOrWhiteSpace(vivoxPlayerId))
                return;

            VoiceChatController vc = GameManager.Instance?.VoiceChat;
            if (vc == null)
                return;

            vc.UnregisterAvatar(vivoxPlayerId);
        }

        [Server]
        public void SetReady(NetworkConnection conn, bool ready)
        {
            if (!IsServerInitialized || conn == null) return;

            if (ready) _readyOwners.Add(conn.ClientId);
            else _readyOwners.Remove(conn.ClientId);
        }

        [Server]
        public void ResetReady()
        {
            _readyOwners.Clear();
            PublishCounts(true);
        }

        /// <summary>
        /// (서버) 현재 등록된 모든 플레이어의 런 스탯/쿨타임을 즉시 초기화하고,
        /// 체력을 완전 회복시키며, 통계도 리셋한다.
        /// 로비 씬 진입 시점 1회 호출용.
        /// </summary>
        [Server]
        public void ResetAllPlayersForLobbyEntry_Server()
        {
            if (!IsServerInitialized) return;

            // 레디 초기화 + 카운트 갱신
            ResetReady();
            RepublishCounts_Server();

            // 등록된 플레이어들 런 스탯/쿨 초기화 + 체력 회복 + 통계 리셋(있으면)
            foreach (ICharacterActor p in _players)
            {
                if (p is not CharacterActor ca) continue;

                // 스탯, 쿨 초기화
                ca.ResetRunStats_Server();

                // 통계 리셋
                PlayerStatics ps = ca.GetComponentInParent<PlayerStatics>();
                if (ps != null) ps.ResetForNewRun_Server();

                // 체력 완전 회복
                if (ca.health != null) ca.health.RestoreFullHP_Server();
            }
        }


        // 접속 해제시 자동 정리(서버)
        [Server]
        private void OnRemoteConnState_Server(NetworkConnection conn, RemoteConnectionStateArgs state)
        {
            // ✅ (수정) Started에서는 이름을 확정하지 않는다.
            // 이름은 각 클라이언트가 CharacterActor.SubmitDisplayName_ServerRpc로 제출한 값으로 확정된다.
            if (state.ConnectionState == RemoteConnectionState.Started)
            {
                if (!_allowNewConnectionsServer && conn != null)
                {
                    // 호스트(리슨서버)의 로컬 커넥션은 차단하지 않는다.
                    bool isHostLocalConn = false;
                    try
                    {
                        NetworkManager nm = InstanceFinder.NetworkManager;
                        if (nm != null && nm.ClientManager != null && nm.ClientManager.Connection == conn)
                            isHostLocalConn = true;
                    }
                    catch { }

                    if (!isHostLocalConn)
                    {
                        Debug.LogWarning($"[PlayerRegistry] Rejecting new connection (ClientId={conn.ClientId}) because joins are disabled for the current scene.");
                        conn.Disconnect(true);
                        return;
                    }
                }

                if (conn != null && conn.ClientId >= 0)
                {
                    _connectedOwners.Add(conn.ClientId);
                    _readyOwners.Remove(conn.ClientId);
                }

#if !DISABLESTEAMWORKS
                try
                {
                    // 클라이언트가 접속할 때 즉시 SteamID를 캐싱
                    string address = InstanceFinder.TransportManager.Transport.GetConnectionAddress(conn.ClientId);
                    if (!string.IsNullOrEmpty(address) && ulong.TryParse(address, out ulong steamIdUlong))
                    {
                        _clientSteamIds[conn.ClientId] = new CSteamID(steamIdUlong);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PlayerRegistry] 접속한 클라이언트({conn.ClientId})의 SteamID 캐싱 실패: {e}");
                }
#endif

                // ❌ 여기서 ResolvePlatformDisplayName_Server(conn) → SetPlayerName(...) 호출 금지
                PublishCounts(true);
                return;
            }

            if (state.ConnectionState == RemoteConnectionState.Stopped)
            {
                // vivoxPlayerId 확보
                string vivoxId = null;

                // conn.FirstObject가 살아있으면 가장 정확
                if (conn != null && conn.FirstObject != null)
                {
                    PlayerVoiceLink pvl = conn.FirstObject.GetComponentInChildren<PlayerVoiceLink>(true);
                    if (pvl != null)
                        vivoxId = pvl.VivoxPlayerId;
                }

                // conn.FirstObject가 이미 없을 때 대비:
                // 서버에 남아있는 spawned 오브젝트에서 Owner==conn인 PVL을 찾아본다
                if (string.IsNullOrEmpty(vivoxId))
                    foreach (NetworkObject no in InstanceFinder.ServerManager.Objects.Spawned.Values)
                    {
                        if (no == null || no.Owner != conn) continue;
                        PlayerVoiceLink pvl = no.GetComponentInChildren<PlayerVoiceLink>(true);
                        if (pvl != null && !string.IsNullOrWhiteSpace(pvl.VivoxPlayerId))
                        {
                            vivoxId = pvl.VivoxPlayerId;
                            break;
                        }
                    }

                //  모든 클라에게 “이 사람 음성 정리” 브로드캐스트
                if (!string.IsNullOrWhiteSpace(vivoxId))
                    OnPlayerLeft_ClearVoice_ObserversRpc(vivoxId);

                // --- Steam P2P 세션 찌꺼기 강제 정리 ---
#if !DISABLESTEAMWORKS
                try
                {
                    // 캐싱된 SteamID를 사용하여 확실하게 P2P 세션을 닫음
                    if (conn != null && _clientSteamIds.TryGetValue(conn.ClientId, out CSteamID targetSteamId))
                    {
                        SteamNetworking.CloseP2PSessionWithUser(targetSteamId);
                        _clientSteamIds.Remove(conn.ClientId);
                        Debug.Log($"[PlayerRegistry] 강제 종료된 클라이언트({conn.ClientId}, SteamID: {targetSteamId})의 Steam P2P 세션을 안전하게 닫았습니다.");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PlayerRegistry] Steam P2P 세션 종료 중 에러 발생: {e}");
                }
#endif

                // 해당 소유자의 액터 제거
                for (int i = _players.Count - 1; i >= 0; --i)
                    if (TryGetOwner(_players[i], out NetworkObject nob) && nob.Owner == conn)
                        _players.RemoveAt(i);

                _connectedOwners.Remove(conn.ClientId);
                _readyOwners.Remove(conn.ClientId);
                _ownerNames.Remove(conn.ClientId);
                _ownerJobs.Remove(conn.ClientId);

                RemovePlayerName_ObserversRpc(conn.ClientId);

                PublishCounts(true);
            }
        }

        // ✅ (수정) 서버에서 SteamFriends/EOS API로 "원격 클라" 이름을 가져오려 하지 않는다.
        // listen-server에선 서버 로컬 API는 항상 "호스트 로컬 닉네임"만 주기 때문에 오염됨.
        // 대신, conn의 FirstObject에 반영된 DisplayName(클라가 제출한 값)만 읽는다.
        [Server]
        private string ResolvePlatformDisplayName_Server(NetworkConnection conn)
        {
            if (conn?.FirstObject == null)
                return null;

            // 1) IHasDisplayName 우선
            IHasDisplayName disp = conn.FirstObject.GetComponentInChildren<IHasDisplayName>();
            if (disp != null && !string.IsNullOrWhiteSpace(disp.DisplayName))
                return disp.DisplayName;

            // 2) CharacterActor 보조
            CharacterActor ca = conn.FirstObject.GetComponentInChildren<CharacterActor>();
            if (ca != null && !string.IsNullOrWhiteSpace(ca.DisplayName))
                return ca.DisplayName;

            return null;
        }

        #endregion

        /// <summary>
        /// (서버) 현재 등록된 모든 플레이어의 궁극기 게이지/상태를 즉시 초기화.
        /// 씬 진입 시점(로비/게임) 1회 호출용.
        /// </summary>
        [Server]
        public static void ResetAllUltimateGaugesForSceneEntry_Server()
        {
            if (Instance == null || !Instance.IsServerInitialized) return;

            List<ICharacterActor> list = Instance._players;
            if (list == null || list.Count == 0) return;

            foreach (ICharacterActor actor in list)
            {
                if (actor == null) continue;

                if (actor is Component comp && comp.TryGetComponent(out UltimateCharge uc))
                    uc.ResetForSceneEntry_Server();
            }
        }

        #region Utils

        private static bool TryGetOwnerId(ICharacterActor actor, out int ownerId)
        {
            ownerId = -1;
            if (!TryGetOwner(actor, out NetworkObject nob) || nob.Owner == null) return false;
            ownerId = nob.Owner.ClientId;
            return true;
        }

        private static bool TryGetOwner(ICharacterActor actor, out NetworkObject nob)
        {
            nob = (actor as Component)?.GetComponent<NetworkObject>();
            return nob != null;
        }

        public bool IsOwnerReady(int ownerId)
        {
            return _readyOwners.Contains(ownerId);
        }

        #endregion

        /// <summary>서버: 현재 준비/총원 카운트를 강제로 한 번 더 알린다.</summary>
        [Server]
        public void RepublishCounts_Server()
        {
            // 내부 공용 브로드캐스트(서버 경로로 발행)
            PublishCounts(true);
        }

        public bool TryGetName(int connId, out string displayName)
        {
            return _ownerNames.TryGetValue(connId, out displayName); // SyncDictionary: 클라에도 동기화됨
        }

        public string GetDisplayName(int connId)
        {
            return _ownerNames.TryGetValue(connId, out string s) && !string.IsNullOrWhiteSpace(s)
                ? s
                : $"Conn-{connId}";
        }

        public string GetLocalDisplayName()
        {
            int myId = -1;
            if (InstanceFinder.ClientManager?.Connection != null)
                myId = InstanceFinder.ClientManager.Connection.ClientId;

            if (_ownerNames.TryGetValue(myId, out string s) && !string.IsNullOrWhiteSpace(s))
                return s;

            return $"Conn-{myId}";
        }

        public bool TryGetJob(int ownerId, out JobType jobType)
        {
            return _ownerJobs.TryGetValue(ownerId, out jobType);
        }

        public bool TryGetConnectedOwnerIds(out List<int> ownerIds)
        {
            ownerIds = null;

            // _connectedOwners는 SyncHashSet이라 클라에서도 값이 존재함
            ownerIds = new List<int>(_connectedOwners.Count);
            foreach (int id in _connectedOwners)
                ownerIds.Add(id);

            return ownerIds.Count > 0;
        }
    }
}
