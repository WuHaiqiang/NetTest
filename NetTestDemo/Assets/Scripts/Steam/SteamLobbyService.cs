// -----------------------------------------------------------------------------
// SteamLobbyService
//
// 负责：
//   * 创建 Steam 大厅并在创建成功后以 Host 身份启动 Mirror
//   * 通过 Steam 好友 / 大厅邀请加入别人的房间（GameLobbyJoinRequested）
//   * 通过 Steam 富文本“加入游戏”加入房间（GameRichPresenceJoinRequested）
//   * 直接输入 SteamID64 加入（不依赖大厅）
//
// Steam 大厅只用于“匹配 + 好友邀请”，真正的游戏数据走 SteamP2PTransport。
// -----------------------------------------------------------------------------
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using Mirror;
using UnityEngine;

#if !DISABLESTEAMWORKS
using Steamworks;
#endif

namespace NetTest
{
    public enum RoomRole { Idle, Host, Client }

    [DisallowMultipleComponent]
    [AddComponentMenu("NetTest/Steam Lobby Service")]
    public class SteamLobbyService : MonoBehaviour
    {
        public const string LobbyDataKeyName = "NetTestRoom";
        public const string LobbyDataVersion = "1";

        public static SteamLobbyService Instance { get; private set; }

        [Header("Room")]
        [Range(1, 4)]
        [Tooltip("房间最大人数（含主机）。本作要求 1~4 人。")]
        public int maxPlayers = 4;

        /// <summary>供 UI 显示的状态文本</summary>
        public event Action<string> OnStatus;

        public RoomRole Role { get; private set; } = RoomRole.Idle;
#if !DISABLESTEAMWORKS
        public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;
#endif
        public bool IsBusy { get; private set; }

        // 简单防抖：避免重复点击造成重复创建大厅 / 重复 StartClient
        float lastActionTime = -10f;
        const float ActionCooldown = 1f;

        bool ActionLocked
        {
            get
            {
                if (IsBusy) return true;
                return Time.realtimeSinceStartup - lastActionTime < ActionCooldown;
            }
        }

        void MarkAction()
        {
            lastActionTime = Time.realtimeSinceStartup;
        }

        string status = string.Empty;
        public string Status => status;

        RoomNetworkManager Manager => RoomNetworkManager.singleton as RoomNetworkManager;
        SteamP2PTransport Transport => SteamP2PTransport.active as SteamP2PTransport;

#if !DISABLESTEAMWORKS
        Callback<GameLobbyJoinRequested_t> gameLobbyJoinRequested;
        Callback<GameRichPresenceJoinRequested_t> richPresenceJoinRequested;
        Callback<LobbyChatUpdate_t> lobbyChatUpdate;
        CallResult<LobbyCreated_t> lobbyCreated;
        Callback<LobbyEnter_t> lobbyEntered;
        bool callbacksRegistered;
#endif

        // ---------------------------------------------------------------------
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start() => SetStatus(SteamReady ? "就绪：Steam 已初始化。" : "警告：Steam 未初始化（请通过 Steam 运行游戏）。");

        void LateUpdate() => IsBusy = false;

        void OnEnable()
        {
#if !DISABLESTEAMWORKS
            if (!SteamManager.Initialized) return;
            if (callbacksRegistered) return;

            gameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            richPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
            lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            callbacksRegistered = true;
#endif
        }

        void OnDisable()
        {
#if !DISABLESTEAMWORKS
            UnregisterAll();
#endif
        }

        public static bool SteamReady
        {
            get
            {
#if !DISABLESTEAMWORKS
                return SteamManager.Initialized;
#else
                return false;
#endif
            }
        }

        public ulong LocalSteamId
        {
            get
            {
#if !DISABLESTEAMWORKS
                return SteamReady ? SteamUser.GetSteamID().m_SteamID : 0;
#else
                return 0;
#endif
            }
        }

        public string LocalPersonaName
        {
            get
            {
#if !DISABLESTEAMWORKS
                return SteamReady ? SteamFriends.GetPersonaName() : "<unknown>";
#else
                return "<no steam>";
#endif
            }
        }

        public void SetStatus(string message)
        {
            status = message;
            if (!string.IsNullOrEmpty(message)) Debug.Log($"[SteamLobby] {message}");
            OnStatus?.Invoke(message);
        }

        // host ////////////////////////////////////////////////////////////////
        /// <summary>创建大厅并作为主机启动（等大厅创建成功后再 StartHost）。</summary>
        public void CreateRoom()
        {
            if (!SteamReady)
            {
                SetStatus("无法创建房间：Steam 未初始化。");
                return;
            }
            if (ActionLocked) return;
            IsBusy = true;
            MarkAction();
            SetStatus("正在创建 Steam 大厅…");
#if !DISABLESTEAMWORKS
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            lobbyCreated.Set(call);
#endif
        }

        /// <summary>打开 Steam 好友邀请面板（需要已经在房间里）。</summary>
        public void InviteFriends()
        {
#if !DISABLESTEAMWORKS
            if (!SteamReady) { SetStatus("Steam 未初始化，无法邀请。"); return; }
            if (!CurrentLobby.IsValid()) { SetStatus("需要先创建房间才能邀请好友。"); return; }
            SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
#endif
        }

        // join ////////////////////////////////////////////////////////////////
        /// <summary>直接连接指定 SteamID64 的主机（不需要大厅）。</summary>
        public void JoinSteamId(ulong hostSteamId)
        {
            if (!SteamReady)
            {
                SetStatus("无法加入：Steam 未初始化。");
                return;
            }
            if (hostSteamId == 0 || hostSteamId == LocalSteamId)
            {
                SetStatus("无效的 SteamID。");
                return;
            }
            if (ActionLocked) return;
            MarkAction();

            RoomNetworkManager manager = Manager;
            if (manager == null) { SetStatus("场景中缺少 RoomNetworkManager。"); return; }

            SetStatus($"正在连接 {hostSteamId} …");
            Role = RoomRole.Client;
            manager.JoinViaSteam(hostSteamId);
        }

        /// <summary>通过 Steam 大厅 ID 加入（好友接受邀请后走这里）。</summary>
        public void JoinLobby(ulong lobbySteamId)
        {
            if (!SteamReady) { SetStatus("Steam 未初始化。"); return; }
#if !DISABLESTEAMWORKS
            if (CurrentLobby.IsValid())
            {
                SetStatus("已经在一个房间里了。");
                return;
            }
            if (ActionLocked) return;
            MarkAction();
            SetStatus("正在加入 Steam 大厅…");
            CSteamID lobby = new CSteamID(lobbySteamId);
            SteamMatchmaking.JoinLobby(lobby);
#endif
        }

        // leave ///////////////////////////////////////////////////////////////
        public void LeaveNetwork()
        {
            RoomNetworkManager manager = Manager;
            if (manager != null)
            {
                if (NetworkServer.active && NetworkClient.active) manager.StopHost();
                else if (NetworkClient.active) manager.StopClient();
                else if (NetworkServer.active) manager.StopServer();
            }

#if !DISABLESTEAMWORKS
            if (SteamReady && CurrentLobby.IsValid())
            {
                SteamMatchmaking.LeaveLobby(CurrentLobby);
                SetStatus("已离开大厅。");
            }
            CurrentLobby = CSteamID.Nil;
            if (SteamReady)
            {
                SteamFriends.SetRichPresence("connect", string.Empty);
                SteamFriends.SetRichPresence("status", string.Empty);
            }
#endif
            Role = RoomRole.Idle;
        }

        // callbacks ///////////////////////////////////////////////////////////
#if !DISABLESTEAMWORKS
        void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            Debug.Log($"[SteamLobby] GameLobbyJoinRequested lobby={request.m_steamIDLobby.m_SteamID}");
            SetStatus("收到好友邀请，正在加入…");
            JoinLobby(request.m_steamIDLobby.m_SteamID);
        }

        void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
        {
            Debug.Log($"[SteamLobby] RichPresenceJoinRequested connect='{request.m_rgchConnect}'");
            if (ulong.TryParse(request.m_rgchConnect, out ulong hostSteamId))
                JoinSteamId(hostSteamId);
            else
                SetStatus($"无法解析 connect 字符串: '{request.m_rgchConnect}'");
        }

        void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                SetStatus($"创建大厅失败: {result.m_eResult}");
                return;
            }

            CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(CurrentLobby, LobbyDataKeyName, LobbyDataVersion);
            SteamMatchmaking.SetLobbyData(CurrentLobby, "version", Application.version);

            // 让好友可以从 Steam 好友列表直接“加入游戏”
            SteamFriends.SetRichPresence("connect", SteamUser.GetSteamID().m_SteamID.ToString());
            SteamFriends.SetRichPresence("status", $"Hosting ({maxPlayers} max)");

            RoomNetworkManager manager = Manager;
            if (manager == null)
            {
                SetStatus("场景中缺少 RoomNetworkManager，无法作为主机启动。");
                return;
            }

            Role = RoomRole.Host;
            manager.HostViaSteam();
            SetStatus($"房间已创建（SteamID: {SteamUser.GetSteamID().m_SteamID}），等待玩家加入。");
        }

        void OnLobbyEntered(LobbyEnter_t result)
        {
            CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);

            if (result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                SetStatus($"加入大厅失败: {(EChatRoomEnterResponse)result.m_EChatRoomEnterResponse}");
                CurrentLobby = CSteamID.Nil;
                return;
            }

            CSteamID owner = SteamMatchmaking.GetLobbyOwner(CurrentLobby);
            CSteamID self = SteamUser.GetSteamID();
            if (owner == self)
            {
                // 自己就是房主（正常由 CreateRoom 流程处理）
                SetStatus("已进入自己创建的大厅。");
                return;
            }

            SetStatus($"已进入大厅，正在连接房主 {owner.m_SteamID} …");
            Role = RoomRole.Client;

            RoomNetworkManager manager = Manager;
            if (manager == null) return;
            manager.JoinViaSteam(owner.m_SteamID);
        }

        void OnLobbyChatUpdate(LobbyChatUpdate_t result)
        {
            if (new CSteamID(result.m_ulSteamIDLobby) != CurrentLobby) return;

            EChatMemberStateChange change = (EChatMemberStateChange)result.m_rgfChatMemberStateChange;
            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                (change & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0)
            {
                CSteamID owner = SteamMatchmaking.GetLobbyOwner(CurrentLobby);
                CSteamID changed = new CSteamID(result.m_ulSteamIDUserChanged);
                Debug.Log($"[SteamLobby] 成员离开: {changed.m_SteamID}, owner={owner.m_SteamID}");
                if (changed == owner && Role == RoomRole.Client)
                    SetStatus("房主已离开，正在退出…");
            }
        }

        void UnregisterAll()
        {
            try { gameLobbyJoinRequested?.Unregister(); } catch { /* ignored */ }
            try { richPresenceJoinRequested?.Unregister(); } catch { /* ignored */ }
            try { lobbyChatUpdate?.Unregister(); } catch { /* ignored */ }
            try { lobbyEntered?.Unregister(); } catch { /* ignored */ }
            try { lobbyCreated?.Cancel(); } catch { /* ignored */ }
            gameLobbyJoinRequested = null;
            richPresenceJoinRequested = null;
            lobbyChatUpdate = null;
            lobbyEntered = null;
            lobbyCreated = null;
            callbacksRegistered = false;
        }
#endif
    }
}
