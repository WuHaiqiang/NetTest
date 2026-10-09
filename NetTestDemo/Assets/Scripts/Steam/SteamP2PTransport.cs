// -----------------------------------------------------------------------------
// SteamP2PTransport
//
// Mirror 自定义 Transport，基于 Steamworks.NET 的 ISteamNetworkingMessages
// （Steam P2P + Steam Relay），因此联机不需要端口映射，也不需要 localhost。
//
//   * 通道 0 => Reliable（可靠有序）
//   * 通道 1 => Unreliable（不可靠无序，用于物理/状态同步）
//   * 每个连接用对方的 SteamID 作为唯一标识（Steam 网络自带 NAT 穿透 / Relay）
// -----------------------------------------------------------------------------
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Mirror;
using UnityEngine;

#if !DISABLESTEAMWORKS
using Steamworks;
#endif

namespace NetTest
{
    [DisallowMultipleComponent]
    [AddComponentMenu("NetTest/Steam P2P Transport")]
    public class SteamP2PTransport : Transport
    {
        public const string Scheme = "steam";

        // 一次 EarlyUpdate 里最多取多少条 Steam 网络消息。
        const int MaxReceivePerPoll = 256;

        [Header("Steam Channels")]
        [Tooltip("可靠有序通道使用的 Steam channel")]
        public int reliableChannel = 0;
        [Tooltip("不可靠无序通道使用的 Steam channel")]
        public int unreliableChannel = 1;

        [Header("Stability")]
        [Tooltip("超过该时间没有收到对方任何数据，则认为连接已断开（秒）")]
        public float connectionTimeout = 15f;
        [Tooltip("客户端连接后在该时间内收不到任何服务器数据则报错超时（秒）")]
        public float connectTimeout = 10f;

        // server side /////////////////////////////////////////////////////////
        sealed class Connection
        {
            public int connectionId;
#if !DISABLESTEAMWORKS
            public CSteamID steamId;
#endif
            public double lastReceive;
        }

#if !DISABLESTEAMWORKS
        readonly Dictionary<CSteamID, Connection> bySteamId = new Dictionary<CSteamID, Connection>();
#endif
        readonly Dictionary<int, Connection> byConnectionId = new Dictionary<int, Connection>();
        int nextConnectionId = 1;
        bool serverRunning;

        // client side /////////////////////////////////////////////////////////
        bool clientRunning;
#if !DISABLESTEAMWORKS
        CSteamID hostSteamId;
#endif
        ulong hostSteamIdRaw;
        double connectStarted;
        double clientLastReceive;
        bool clientConnectedRaised;

        // receive buffers (reused, processed one message at a time)
        readonly IntPtr[] messagePointers = new IntPtr[MaxReceivePerPoll];
        byte[] receiveBuffer = new byte[MaxPacketSizeReliable];

        const int MaxPacketSizeReliable = 64 * 1024;
        const int MaxPacketSizeUnreliable = 1200; // Steam (un)reliable MTU

        // ---------------------------------------------------------------------

        /// <summary>本机 SteamID（Steam 未初始化时返回 0）</summary>
        public ulong LocalSteamId
        {
            get
            {
#if !DISABLESTEAMWORKS
                return SteamManager.Initialized ? SteamUser.GetSteamID().m_SteamID : 0;
#else
                return 0;
#endif
            }
        }

        public override bool Available()
        {
#if !DISABLESTEAMWORKS
            return SteamManager.Initialized;
#else
            return false;
#endif
        }

        public override bool IsEncrypted => true;
        public override string EncryptionCipher => "Steam (AES-256 over SDR)";

        // helpers /////////////////////////////////////////////////////////////
        static bool TryParseSteamId(string address, out ulong steamId)
        {
            steamId = 0;
            if (string.IsNullOrWhiteSpace(address)) return false;

            string trimmed = address.Trim();
            int schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0)
                trimmed = trimmed.Substring(schemeIndex + 3);
            trimmed = trimmed.TrimEnd('/');

            return ulong.TryParse(trimmed, out steamId) && steamId != 0;
        }

#if !DISABLESTEAMWORKS
        static int SendFlagsFor(int channelId) =>
            channelId == Channels.Unreliable
                ? Constants.k_nSteamNetworkingSend_UnreliableNoNagle
                : Constants.k_nSteamNetworkingSend_Reliable;

        void Send(CSteamID remote, ArraySegment<byte> segment, int channelId)
        {
            SteamNetworkingIdentity identity = default;
            identity.SetSteamID(remote);

            GCHandle handle = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = IntPtr.Add(handle.AddrOfPinnedObject(), segment.Offset);
                EResult result = SteamNetworkingMessages.SendMessageToUser(
                    ref identity, ptr, (uint)segment.Count, SendFlagsFor(channelId), channelId);

                if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
                    Debug.LogWarning($"[SteamP2PTransport] 发送给 {remote.m_SteamID} 失败: {result}");
            }
            finally
            {
                handle.Free();
            }
        }
#endif

        // client //////////////////////////////////////////////////////////////
        public override bool ClientConnected() => clientRunning;

        public override void ClientConnect(string address)
        {
            if (!TryParseSteamId(address, out ulong steamId))
            {
                OnClientError.Invoke(TransportError.Unexpected, $"[SteamP2PTransport] 无效的 SteamID 地址: '{address}'");
                OnClientDisconnected.Invoke();
                return;
            }

#if !DISABLESTEAMWORKS
            if (!SteamManager.Initialized)
            {
                OnClientError.Invoke(TransportError.Unexpected, "[SteamP2PTransport] Steam 未初始化，无法联机。请通过 Steam 启动游戏（AppID 480）。");
                OnClientDisconnected.Invoke();
                return;
            }

            CSteamID self = SteamUser.GetSteamID();
            if (steamId == self.m_SteamID)
            {
                OnClientError.Invoke(TransportError.Unexpected, "[SteamP2PTransport] 不能连接自己。");
                OnClientDisconnected.Invoke();
                return;
            }

            Shutdown();

            hostSteamId = new CSteamID(steamId);
            hostSteamIdRaw = steamId;
            connectStarted = Time.realtimeSinceStartup;
            clientLastReceive = Time.realtimeSinceStartup;
            clientConnectedRaised = false;

            // SteamNetworkingMessages 没有显式的连接握手：
            // 收到第一条服务器数据之前，连接处于 "connecting" 状态。
            clientRunning = true;

            // 主动打通会话（如果 Steam 已经有 session，这里是 no-op）
            SteamNetworkingIdentity identity = default;
            identity.SetSteamID(hostSteamId);
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
#else
            OnClientError.Invoke(TransportError.Unexpected, "[SteamP2PTransport] 当前平台不支持 Steamworks。");
            OnClientDisconnected.Invoke();
#endif
        }

        public override void ClientConnect(Uri uri) => ClientConnect(uri.ToString());

        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            if (!clientRunning) return;
#if !DISABLESTEAMWORKS
            if (segment.Count == 0) return;
            Send(hostSteamId, segment, channelId);
#endif
        }

        public override void ClientDisconnect()
        {
            if (!clientRunning) return;
            clientRunning = false;
#if !DISABLESTEAMWORKS
            if (SteamManager.Initialized && hostSteamId.IsValid())
            {
                SteamNetworkingIdentity identity = default;
                identity.SetSteamID(hostSteamId);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }
            hostSteamId = CSteamID.Nil;
#endif
            hostSteamIdRaw = 0;
            OnClientDisconnected.Invoke();
        }

        // server //////////////////////////////////////////////////////////////
        public override bool ServerActive() => serverRunning;

        public override void ServerStart()
        {
            if (serverRunning) return;
#if !DISABLESTEAMWORKS
            if (!SteamManager.Initialized)
            {
                Debug.LogError("[SteamP2PTransport] Steam 未初始化，无法作为主机。");
                return;
            }
#endif
            serverRunning = true;
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            if (!serverRunning) return;
#if !DISABLESTEAMWORKS
            if (!byConnectionId.TryGetValue(connectionId, out Connection conn)) return;
            if (segment.Count == 0) return;
            Send(conn.steamId, segment, channelId);
#endif
        }

        public override void ServerDisconnect(int connectionId)
        {
            if (!byConnectionId.TryGetValue(connectionId, out Connection conn)) return;
            RemoveConnection(conn);
#if !DISABLESTEAMWORKS
            if (SteamManager.Initialized)
            {
                SteamNetworkingIdentity identity = default;
                identity.SetSteamID(conn.steamId);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }
#endif
            OnServerDisconnected.Invoke(connectionId);
        }

        public override string ServerGetClientAddress(int connectionId)
        {
#if !DISABLESTEAMWORKS
            return byConnectionId.TryGetValue(connectionId, out Connection conn) ? conn.steamId.m_SteamID.ToString() : string.Empty;
#else
            return string.Empty;
#endif
        }

        void RemoveConnection(Connection conn)
        {
            byConnectionId.Remove(conn.connectionId);
#if !DISABLESTEAMWORKS
            bySteamId.Remove(conn.steamId);
#endif
        }

        public override void ServerStop()
        {
            if (!serverRunning) return;
            serverRunning = false;

            if (byConnectionId.Count > 0)
            {
                List<Connection> snapshot = new List<Connection>(byConnectionId.Values);
                foreach (Connection conn in snapshot)
                {
                    RemoveConnection(conn);
#if !DISABLESTEAMWORKS
                    if (SteamManager.Initialized)
                    {
                        SteamNetworkingIdentity identity = default;
                        identity.SetSteamID(conn.steamId);
                        SteamNetworkingMessages.CloseSessionWithUser(ref identity);
                    }
#endif
                    OnServerDisconnected.Invoke(conn.connectionId);
                }
            }
            byConnectionId.Clear();
#if !DISABLESTEAMWORKS
            bySteamId.Clear();
#endif
            nextConnectionId = 1;
        }

        public override Uri ServerUri() => new Uri($"{Scheme}://{LocalSteamId}");

        public override int GetMaxPacketSize(int channelId = Channels.Reliable) =>
            channelId == Channels.Unreliable ? MaxPacketSizeUnreliable : MaxPacketSizeReliable;

        public override int GetBatchThreshold(int channelId = Channels.Reliable) => GetMaxPacketSize(channelId);

        public override void Shutdown()
        {
            ServerStop();
            if (clientRunning) ClientDisconnect();
        }

        // session handling ////////////////////////////////////////////////////
        // SteamNetworkingMessages 会在别人第一次给你发消息前抛出 SessionRequest。
        // 这里统一接受（人数上限由 NetworkManager.maxConnections 在应用层控制）。
#if !DISABLESTEAMWORKS
        Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequestCallback;

        protected virtual void OnEnable()
        {
            if (!sessionRequestCallbackRegistered)
            {
                sessionRequestCallback = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
                sessionRequestCallbackRegistered = true;
            }
        }

        protected virtual void OnDisable()
        {
            if (sessionRequestCallback != null)
            {
                try { sessionRequestCallback.Unregister(); } catch { /* ignore */ }
                sessionRequestCallback = null;
                sessionRequestCallbackRegistered = false;
            }
        }

        bool sessionRequestCallbackRegistered;

        void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            SteamNetworkingIdentity identity = request.m_identityRemote;
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        }
#endif

        // updates /////////////////////////////////////////////////////////////
        public override void ClientEarlyUpdate()
        {
            if (!clientRunning) return;
#if !DISABLESTEAMWORKS
            try
            {
                PollMessages();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                OnClientTransportException?.Invoke(e);
                ClientDisconnect();
                return;
            }
#endif
            // 超时处理
            if (!clientConnectedRaised)
            {
                if (Time.realtimeSinceStartup - connectStarted > connectTimeout)
                {
                    Debug.LogWarning($"[SteamP2PTransport] 连接 {hostSteamIdRaw} 超时。");
                    OnClientError.Invoke(TransportError.Timeout, "连接主机超时（SteamID 可能不正确，或对方已离线）。");
                    ClientDisconnect();
                }
            }
            else if (Time.realtimeSinceStartup - clientLastReceive > connectionTimeout)
            {
                Debug.LogWarning("[SteamP2PTransport] 与主机的连接超时。");
                OnClientError.Invoke(TransportError.Timeout, "与主机的连接超时。");
                ClientDisconnect();
            }
        }

        public override void ServerEarlyUpdate()
        {
            if (!serverRunning) return;
#if !DISABLESTEAMWORKS
            try
            {
                PollMessages();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return;
            }
#endif
            // 断开长时间没有数据的连接
            double now = Time.realtimeSinceStartup;
            foreach (Connection conn in new List<Connection>(byConnectionId.Values))
            {
                if (now - conn.lastReceive > connectionTimeout)
                {
                    Debug.LogWarning($"[SteamP2PTransport] 连接 {conn.connectionId} 超时。");
                    ServerDisconnect(conn.connectionId);
                }
            }
        }

#if !DISABLESTEAMWORKS
        void PollMessages()
        {
            PollChannel(reliableChannel);
            PollChannel(unreliableChannel);
        }

        void PollChannel(int channel)
        {
            int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, messagePointers, MaxReceivePerPoll);
            for (int i = 0; i < count; i++)
            {
                IntPtr pointer = messagePointers[i];
                SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                try
                {
                    int size = message.m_cbSize;
                    if (size > 0)
                    {
                        if (receiveBuffer.Length < size)
                            receiveBuffer = new byte[size];
                        Marshal.Copy(message.m_pData, receiveBuffer, 0, size);
                        ArraySegment<byte> segment = new ArraySegment<byte>(receiveBuffer, 0, size);
                        ulong from = message.m_identityPeer.GetSteamID64();
                        Dispatch(from, segment, channel);
                    }
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                }
            }
        }

        void Dispatch(ulong fromRaw, ArraySegment<byte> segment, int channel)
        {
            CSteamID from = new CSteamID(fromRaw);
            double now = Time.realtimeSinceStartup;

            if (serverRunning)
            {
                // 新的 client -> server 连接在这里被创建
                if (!bySteamId.TryGetValue(from, out Connection conn))
                    conn = CreateConnection(from);
                conn.lastReceive = now;

                OnServerDataReceived.Invoke(conn.connectionId, segment, channel);
            }

            if (clientRunning && fromRaw == hostSteamIdRaw)
            {
                clientLastReceive = now;

                // SteamNetworkingMessages 没有握手，收到第一条数据才算真正连上。
                if (!clientConnectedRaised)
                {
                    clientConnectedRaised = true;
                    OnClientConnected.Invoke();
                }

                OnClientDataReceived.Invoke(segment, channel);
            }
        }

        Connection CreateConnection(CSteamID steamId)
        {
            Connection conn = new Connection
            {
                connectionId = nextConnectionId++,
                steamId = steamId,
                lastReceive = Time.realtimeSinceStartup
            };
            bySteamId[steamId] = conn;
            byConnectionId[conn.connectionId] = conn;
            OnServerConnectedWithAddress.Invoke(conn.connectionId, steamId.m_SteamID.ToString());
#pragma warning disable CS0618
            OnServerConnected.Invoke(conn.connectionId);
#pragma warning restore CS0618
            return conn;
        }
#endif
    }
}
