// -----------------------------------------------------------------------------
// RoomNetworkManager
//
// Mirror 的 NetworkManager 派生类：
//   * 限制房间人数为 1~4（maxConnections = 3 个远端 + 房主自己）
//   * 负责在 Steam P2P 与本机/局域网 KCP 两套 Transport 之间切换
//   * 房主主动退出时先广播通知，再真正停机（“主机退出处理”）
//   * 客户端掉线时通知 UI
// -----------------------------------------------------------------------------
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace NetTest
{
    public enum RoomTransportMode
    {
        Local = 0,   // KCP：localhost / 局域网
        Steam = 1    // Steam P2P（SteamNetworkingMessages + Steam Relay）
    }

    [AddComponentMenu("NetTest/Room Network Manager")]
    public class RoomNetworkManager : NetworkManager
    {
        public static RoomNetworkManager Instance => singleton as RoomNetworkManager;

        [Header("Room")]
        [Tooltip("房间最大人数（含房主）：2~4")]
        [Range(2, 4)]
        public int maxPlayers = 4;

        [Tooltip("出生点，加入的玩家会随机落到其中一个位置")]
        public List<Transform> spawnPoints = new List<Transform>();

        [Tooltip("负责每 15 秒生成一个球的服务器对象（作为联网 object 在服务器启动时生成）")]
        public GameObject roomControllerPrefab;

        [Tooltip("运行时填充：由 roomControllerPrefab 生成的 BallSpawner")]
        public BallSpawner ballSpawner;

        [Header("Transports")]
        public SteamP2PTransport steamTransport;
        public Transport localTransport;

        public RoomTransportMode TransportMode { get; private set; } = RoomTransportMode.Local;

        /// <summary>当前房间内的玩家数量（房主视角权威数据）</summary>
        public int PlayerCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<uint, NetworkIdentity> entry in NetworkServer.spawned)
                    if (entry.Value != null && entry.Value.TryGetComponent(out NetFirstPersonPlayer _))
                        count++;
                return count;
            }
        }

        /// <summary>供 UI 显示的断开原因（非空表示需要弹提示）</summary>
        public string DisconnectNotice { get; set; } = string.Empty;

        public bool LocalDisconnectRequested { get; set; }

        // ---------------------------------------------------------------------
        public override void Awake()
        {
            base.Awake();
            // 默认用本机 KCP，切到 Steam 由 SelectSteamTransport() 完成
            SelectTransport(RoomTransportMode.Local, silent: true);
        }

        public override void OnValidate()
        {
            base.OnValidate();
            // Mirror 内部：connections 里也包含房主自己的本地连接，
            // 且判定条件是 connections.Count >= maxConnections，
            // 因此 maxConnections == 总人数，才能让「房主 + maxConnections-1」刚好坐满房间。
            maxConnections = Mathf.Clamp(maxPlayers, 1, 16);
        }

        // transport selection /////////////////////////////////////////////////
        public void SelectSteamTransport()
        {
            if (steamTransport == null)
            {
                Debug.LogError("[RoomNetworkManager] 场景里没有 SteamP2PTransport。");
                return;
            }
            if (!steamTransport.Available())
            {
                Debug.LogWarning("[RoomNetworkManager] Steam 不可用（未通过 Steam 启动？），回退到本机 KCP。");
                RoomHud.Instance?.SetStatus("Steam 不可用，已回退到本机 KCP 模式。");
                SelectTransport(RoomTransportMode.Local);
                return;
            }
            SelectTransport(RoomTransportMode.Steam);
        }

        public void SelectLocalTransport() => SelectTransport(RoomTransportMode.Local);

        void SelectTransport(RoomTransportMode mode, bool silent = false)
        {
            Transport target = mode == RoomTransportMode.Steam ? steamTransport : localTransport;
            if (target == null) return;

            transport = target;
            Transport.active = target;
            TransportMode = mode;
            if (!silent)
                Debug.Log($"[RoomNetworkManager] Transport => {target.GetType().Name}");
        }

        // start / stop ////////////////////////////////////////////////////////
        public void HostViaSteam()
        {
            SelectSteamTransport();
            HostNow();
        }

        public void HostViaLocal()
        {
            SelectLocalTransport();
            networkAddress = "localhost";
            HostNow();
        }

        public void JoinViaSteam(ulong hostSteamId)
        {
            DisconnectNotice = string.Empty;
            LocalDisconnectRequested = false;
            SelectSteamTransport();
            networkAddress = hostSteamId.ToString();
            StartClient();
        }

        public void JoinViaLocal(string address = "localhost")
        {
            DisconnectNotice = string.Empty;
            LocalDisconnectRequested = false;
            SelectLocalTransport();
            networkAddress = string.IsNullOrEmpty(address) ? "localhost" : address;
            StartClient();
        }

        void HostNow()
        {
            DisconnectNotice = string.Empty;
            LocalDisconnectRequested = false;
            StartHost();
        }

        /// <summary>房主主动退出：先告诉所有客户端，再停机。</summary>
        public void ShutdownGracefully()
        {
            if (!NetworkServer.active)
            {
                StopNetwork(LocalDisconnectRequested = true);
                return;
            }
            StartCoroutine(NotifyAndStop());
        }

        /// <summary>不带通知地直接断开（例如玩家点“离开房间”）。</summary>
        public void StopNetwork(bool intentional)
        {
            LocalDisconnectRequested = intentional;
            DisconnectNotice = string.Empty;

            if (NetworkServer.active && NetworkClient.active) StopHost();
            else if (NetworkClient.active) StopClient();
            else if (NetworkServer.active) StopServer();
        }

        IEnumerator NotifyAndStop()
        {
            LocalDisconnectRequested = true;
            // ClientRpc 必须声明在 NetworkBehaviour 上，这里放在 BallSpawner（已同步到所有客户端）。
            ballSpawner?.RpcHostIsShuttingDown();

            // 给消息留一点时间飞出去
            yield return new WaitForSecondsRealtime(0.35f);

            if (NetworkServer.active && NetworkClient.active) StopHost();
            else if (NetworkServer.active) StopServer();
        }

        // callbacks ///////////////////////////////////////////////////////////
        public override void OnStartServer()
        {
            base.OnStartServer();
            Debug.Log($"[RoomNetworkManager] 服务器启动（Transport={Transport.active?.GetType().Name}，上限 {maxPlayers} 人）");

            // 由主机生成房间控制器（里面的 BallSpawner 负责定时生成球）
            ballSpawner = null;
            if (roomControllerPrefab != null)
            {
                GameObject controller = Instantiate(roomControllerPrefab);
                controller.name = roomControllerPrefab.name;
                NetworkServer.Spawn(controller);
                ballSpawner = controller.GetComponent<BallSpawner>();
            }
            else
            {
                Debug.LogWarning("[RoomNetworkManager] 没有指定 roomControllerPrefab，不会自动定时生成球。");
            }
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            ballSpawner = null;
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            Transform start = PickSpawnPoint();
            GameObject player = start != null
                ? Instantiate(playerPrefab, start.position, start.rotation)
                : Instantiate(playerPrefab);

            player.name = $"{playerPrefab.name} [connId={conn.connectionId}]";

            // 人数限制在 OnServerConnect 里已经处理，这里是正常流程
            NetworkServer.AddPlayerForConnection(conn, player);
            RoomHud.Instance?.SetStatus($"玩家加入：connId={conn.connectionId}（房间 {PlayerCount}/{maxPlayers}）");
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            // 人数上限由 Mirror 内部按 NetworkServer.maxConnections 把关（见 OnValidate）。
            // 这里只做日志/UI 提示。
            RoomHud.Instance?.SetStatus($"[房主] 有人正在接入 connId={conn.connectionId}（房间 {PlayerCount}/{maxPlayers}）");
            base.OnServerConnect(conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            RoomHud.Instance?.SetStatus($"玩家离开：connId={conn.connectionId}");
            base.OnServerDisconnect(conn);
        }

        public override void OnClientDisconnect()
        {
            string notice = LocalDisconnectRequested ? DisconnectNotice : "与主机的连接已断开（主机可能已退出）。";
            RoomHud.Instance?.OnDisconnected(notice);
            base.OnClientDisconnect();
            LocalDisconnectRequested = false;
        }

        public override void OnClientError(TransportError error, string reason)
        {
            Debug.LogWarning($"[RoomNetworkManager] 客户端错误: {error} - {reason}");
            RoomHud.Instance?.SetStatus($"网络错误：{error} {reason}");
            base.OnClientError(error, reason);
        }

        Transform PickSpawnPoint()
        {
            spawnPoints.RemoveAll(p => p == null);
            if (spawnPoints.Count == 0) return null;
            return spawnPoints[Random.Range(0, spawnPoints.Count)];
        }

        /// <summary>断开网络回到“离线”状态（所有已生成的球 / 玩家都会被 Mirror 清理）。</summary>
        public void ReturnToMenu()
        {
            StopNetwork(true);
            RoomHud.Instance?.ShowNotice(string.Empty, string.Empty);
        }
    }
}
