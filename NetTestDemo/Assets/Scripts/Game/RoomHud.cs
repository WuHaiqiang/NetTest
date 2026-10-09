// -----------------------------------------------------------------------------
// RoomHud —— 全部用 IMGUI 实现，无需 Canvas / 字体资源，任何渲染管线下都能工作。
//
// 提供：
//   * 连接菜单：本机 KCP 房主 / 加入，Steam 房主 / 好友邀请 / 通过 SteamID 加入
//   * 房间信息：人数、Transport、下一次生成球倒计时、球的主机权威速度
//   * 碰触提示："Tapped"
//   * 主机退出 / 断线提示浮层
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace NetTest
{
    [AddComponentMenu("NetTest/Room HUD")]
    public class RoomHud : MonoBehaviour
    {
        public static RoomHud Instance
        {
            get
            {
                if (instance == null)
                {
                    RoomHud found = FindFirstObjectByType<RoomHud>();
                    instance = found != null ? found : new GameObject("RoomHud").AddComponent<RoomHud>();
                }
                return instance;
            }
        }
        static RoomHud instance;

        [Header("UI")]
        [Tooltip("离线/连接界面使用的场景摄像机，进入第一人称后会自动关闭")]
        public Camera lobbyCamera;
        public float tappedDuration = 1.2f;
        public KeyCode unlockKey = KeyCode.Escape;
        public bool cursorLockedInGame = true;

        // runtime state ///////////////////////////////////////////////////////
        string statusLine = string.Empty;
        string noticeTitle = string.Empty;
        string noticeBody = string.Empty;
        string tappedText = string.Empty;
        double tappedUntil;
        string steamIdInput = string.Empty;
        string addressInput = "localhost";
        bool showDebug = true;
        bool cursorUnlockedByUser;

        GUIStyle panelStyle;
        GUIStyle titleStyle;
        GUIStyle labelStyle;
        GUIStyle buttonStyle;
        GUIStyle tappedStyle;
        GUIStyle playerTagStyle;
        Texture2D panelTexture;
        bool stylesReady;

        RoomNetworkManager Manager => RoomNetworkManager.Instance;
        SteamLobbyService Lobby => SteamLobbyService.Instance;

        static bool Online => NetworkClient.active || NetworkServer.active;

        // ---------------------------------------------------------------------
        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Start()
        {
            if (Lobby != null)
                Lobby.OnStatus += SetStatus;
            SetStatus(SteamLobbyService.SteamReady ? "Steam 已初始化。" : "Steam 未初始化（本机模式可用）。");
        }

        void OnDestroy()
        {
            if (Lobby != null)
                Lobby.OnStatus -= SetStatus;
        }

        void Update()
        {
            if (!Online)
            {
                cursorUnlockedByUser = false;
                HandleCursor();
                return;
            }

            // Esc 切换 锁定 / 解锁。解锁后鼠标自由移动，可正常点击 UI 按钮；
            // 没有“点一下就自动锁回”的逻辑，避免锁回动作把点击吞掉。
            if (KeyWasPressed(unlockKey))
                cursorUnlockedByUser = !cursorUnlockedByUser;

            HandleCursor();
        }

        // 场景 Input Handling = Input System Package，因此不能直接用 UnityEngine.Input。
        static bool KeyWasPressed(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return false;
            switch (key)
            {
                case KeyCode.Escape: return keyboard.escapeKey.wasPressedThisFrame;
                case KeyCode.F1: return keyboard.f1Key.wasPressedThisFrame;
                default: return false;
            }
#else
            return Input.GetKeyDown(key);
#endif
        }

        static bool MouseWasPressed()
        {
#if ENABLE_INPUT_SYSTEM
            return UnityEngine.InputSystem.Mouse.current != null &&
                   UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        /// <summary>鼠标是否处于“可用”状态（不控制视角）</summary>
        public bool CursorFree => cursorUnlockedByUser || !string.IsNullOrEmpty(noticeTitle) || !Online;

        void HandleCursor()
        {
            if (!cursorLockedInGame) return;

            // 离线 / 弹窗 / 已解锁：鼠标自由，方便点按钮
            if (!Online || !string.IsNullOrEmpty(noticeTitle) || cursorUnlockedByUser)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            // 正常游戏：锁定鼠标
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // public API //////////////////////////////////////////////////////////
        public void SetStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            statusLine = message;
        }

        public void ShowTapped(string ballName)
        {
            // document 要求：玩家触碰到球体时 UI 显示 “Tapped”
            tappedText = string.IsNullOrEmpty(ballName) ? "Tapped" : $"Tapped ({ballName})";
            tappedUntil = Time.time + tappedDuration;
        }

        public void ShowNotice(string title, string body)
        {
            noticeTitle = title ?? string.Empty;
            noticeBody = body ?? string.Empty;
            if (!string.IsNullOrEmpty(noticeTitle))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        public void OnDisconnected(string notice)
        {
            SetLobbyCameraActive(true);
            ShowNotice("连接已断开", string.IsNullOrEmpty(notice) ? "与主机的连接已断开。" : notice);
        }

        /// <summary>第一人称接管/释放画面</summary>
        public void SetLobbyCameraActive(bool active)
        {
            if (lobbyCamera != null) lobbyCamera.gameObject.SetActive(active);
        }

        // gui /////////////////////////////////////////////////////////////////
        void InitStyles()
        {
            if (stylesReady) return;

            panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            panelTexture.SetPixel(0, 0, new Color(0.05f, 0.06f, 0.09f, 0.88f));
            panelTexture.Apply();

            panelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = panelTexture },
                padding = new RectOffset(18, 18, 14, 14)
            };
            panelStyle.normal.textColor = Color.white;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            titleStyle.normal.textColor = Color.white;

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true
            };
            labelStyle.normal.textColor = new Color(0.9f, 0.92f, 0.96f);

            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, padding = new RectOffset(10, 10, 8, 8) };

            tappedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 34,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            tappedStyle.normal.textColor = new Color(1f, 0.86f, 0.2f);

            playerTagStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            playerTagStyle.normal.textColor = Color.white;

            stylesReady = true;
        }

        void OnGUI()
        {
            InitStyles();

            if (Online) DrawInGameHud();
            else DrawMainMenu();

            if (Online) DrawPlayerLabels();

            if (Online && cursorUnlockedByUser) DrawPauseMenu();

            DrawTapped();
            DrawNotice();
        }

        void DrawTapped()
        {
            if (Time.time > tappedUntil || string.IsNullOrEmpty(tappedText)) return;
            float alpha = Mathf.Clamp01((float)(tappedUntil - Time.time) / tappedDuration);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(0, Screen.height * 0.36f, Screen.width, 60f), tappedText, tappedStyle);
            GUI.color = Color.white;
        }

        void DrawCrosshair()
        {
            if (string.IsNullOrEmpty(noticeTitle) && !cursorUnlockedByUser)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                GUI.DrawTexture(new Rect(Screen.width * 0.5f - 1f, Screen.height * 0.5f - 8f, 2f, 16f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(Screen.width * 0.5f - 8f, Screen.height * 0.5f - 1f, 16f, 2f), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
        }

        /// <summary>在每名玩家头顶投影显示「1P / 2P / 3P …」标签，颜色按序号区分。
        /// 序号由各端按 netId 升序本地推导，无需依赖 SyncVar，保证主机/客户端一致。</summary>
        void DrawPlayerLabels()
        {
            Camera cam = ActiveGameCamera();
            if (cam == null) return;

            var players = new List<NetFirstPersonPlayer>(FindObjectsOfType<NetFirstPersonPlayer>());
            players.Sort((a, b) => a.netId.CompareTo(b.netId));

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];

                // 本地第一人称玩家看不到自己头顶的标签（在摄像机背后），直接跳过。
                if (p.isLocalPlayer) continue;

                int number = i + 1;
                string text = number + "P";

                Vector3 screen = cam.WorldToScreenPoint(p.LabelAnchor);
                if (screen.z <= 0f) continue; // 在摄像机背后

                var style = new GUIStyle(playerTagStyle) { normal = { textColor = NetFirstPersonPlayer.ColorForIndex(number) } };
                Vector2 wsize = style.CalcSize(new GUIContent(text));
                float x = screen.x - wsize.x * 0.5f;
                float y = Screen.height - screen.y - wsize.y;

                // 半透明描边底，保证在任意背景下都清晰可读
                GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTexture(new Rect(x - 5f, y - 3f, wsize.x + 10f, wsize.y + 6f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y, wsize.x, wsize.y), text, style);
            }
        }

        /// <summary>取当前正在渲染世界的摄像机：优先用本地玩家的 playerCamera（保证投影坐标与画面一致），
        /// 否则回退到 MainCamera / 第一个启用的摄像机。</summary>
        static Camera ActiveGameCamera()
        {
            var players = FindObjectsOfType<NetFirstPersonPlayer>();
            foreach (var p in players)
                if (p.isLocalPlayer && p.playerCamera != null && p.playerCamera.isActiveAndEnabled)
                    return p.playerCamera;

            if (Camera.main != null && Camera.main.isActiveAndEnabled) return Camera.main;
            foreach (Camera c in Camera.allCameras)
                if (c.isActiveAndEnabled && c.targetTexture == null)
                    return c;
            return null;
        }

        void DrawInGameHud()
        {
            DrawCrosshair();

            RoomNetworkManager manager = Manager;
            string transport = Transport.active != null ? Transport.active.GetType().Name : "<none>";
            string role = RoomNetworkManager.Instance == null
                ? "<no manager>"
                : NetworkServer.active && NetworkClient.active ? "房主 (Host)" : NetworkClient.active ? "客户端" : "仅服务器";

            int players = manager != null ? manager.PlayerCountUI : 0;
            int max = manager != null ? manager.maxPlayers : 4;

            GUILayout.BeginArea(new Rect(16, 16, 340, 260), panelStyle);
            if (GUILayout.Button("球体房间 · 多人联机  (点击隐藏/显示信息)", buttonStyle))
                showDebug = !showDebug;

            if (showDebug)
            {
                GUILayout.Space(6);
                GUILayout.Label($"状态：{role}", labelStyle);
                GUILayout.Label($"Transport：{transport}", labelStyle);
                GUILayout.Label($"房间人数：{players}/{max}", labelStyle);
                GUILayout.Label($"已同步球数：{CountBalls()}", labelStyle);
                if (manager != null && manager.ballSpawner != null && NetworkServer.active)
                    GUILayout.Label($"下一次生成球：{manager.ballSpawner.SecondsUntilNextSpawn:F1}s / 每 {manager.ballSpawner.spawnInterval:F0}s", labelStyle);
                GUILayout.Label($"球的主机速度：{NearestBallVelocity()}", labelStyle);
                if (Lobby != null)
                    GUILayout.Label(Lobby.Status, labelStyle);
                GUILayout.Space(4);
                GUILayout.Label("WASD 移动 · Space 跳 · 鼠标看 · Esc 解锁鼠标", labelStyle);
            }
            GUILayout.EndArea();
        }

        /// <summary>断开并返回房间菜单（房主会先通知客户端）。</summary>
        void LeaveRoom()
        {
            if (NetworkServer.active) Manager?.ShutdownGracefully();
            else Manager?.StopNetwork(true);
        }

        /// <summary>按 Esc 解锁后显示的中央暂停菜单，按钮大、居中，避免点不到。</summary>
        void DrawPauseMenu()
        {
            int w = 360, h = 240;
            Rect area = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUILayout.BeginArea(area, panelStyle);
            GUILayout.Label("已暂停", titleStyle);
            GUILayout.Space(10);
            if (GUILayout.Button("继续游戏", buttonStyle, GUILayout.Height(40)))
                cursorUnlockedByUser = false;
            GUILayout.Space(8);
            if (GUILayout.Button("返回房间菜单", buttonStyle, GUILayout.Height(40)))
                LeaveRoom();
            if (Online && NetworkServer.active && Lobby != null)
            {
                GUILayout.Space(8);
                if (GUILayout.Button("邀请 Steam 好友", buttonStyle, GUILayout.Height(40)))
                    Lobby.InviteFriends();
            }
            GUILayout.EndArea();
        }

        void DrawMainMenu()
        {
            int width = Mathf.Min(560, Screen.width - 40);
            int height = Mathf.Min(520, Screen.height - 40);
            Rect area = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUILayout.BeginArea(area, panelStyle);
            GUILayout.Label("球体房间 · Unity 6 多人联机", titleStyle);
            GUILayout.Space(8);
            GUILayout.Label(Lobby != null ? Lobby.Status ?? string.Empty : string.Empty, labelStyle);
            if (!string.IsNullOrEmpty(statusLine))
                GUILayout.Label(statusLine, labelStyle);
            GUILayout.Space(10);

            GUILayout.Label("【本机 / 局域网（KCP，无需 Steam）】", labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("创建房间 (Host)", buttonStyle, GUILayout.Height(34)))
                Manager?.HostViaLocal();
            GUILayout.Space(8);
            addressInput = GUILayout.TextField(addressInput, GUILayout.Width(160));
            if (GUILayout.Button("加入", buttonStyle, GUILayout.Height(34)))
                Manager?.JoinViaLocal(addressInput);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("【Steam P2P（好友 / 房间邀请，走 Steam Relay）】", labelStyle);
            bool steamReady = SteamLobbyService.SteamReady;
            GUI.enabled = steamReady;
            if (GUILayout.Button("创建 Steam 房间（可直接邀请好友）", buttonStyle, GUILayout.Height(34)))
                Lobby?.CreateRoom();
            if (GUILayout.Button("邀请 Steam 好友到当前房间", buttonStyle, GUILayout.Height(34)))
                Lobby?.InviteFriends();
            GUILayout.BeginHorizontal();
            steamIdInput = GUILayout.TextField(steamIdInput, GUILayout.Height(34));
            if (GUILayout.Button("用 SteamID 加入", buttonStyle, GUILayout.Height(34)))
            {
                if (ulong.TryParse(steamIdInput.Trim(), out ulong hostId))
                    Lobby?.JoinSteamId(hostId);
                else
                    SetStatus("请输入正确的 SteamID64（纯数字）。");
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (!steamReady)
                GUILayout.Label("提示：Steam 未初始化。请确认 steam_appid.txt = 480 且游戏由 Steam 客户端启动。", labelStyle);

            GUILayout.Space(12);
            GUILayout.Label(Lobby != null ? $"本机 SteamID：{Lobby.LocalSteamId}  昵称：{Lobby.LocalPersonaName}" : string.Empty, labelStyle);
            GUILayout.EndArea();
        }

        void DrawNotice()
        {
            if (string.IsNullOrEmpty(noticeTitle)) return;

            int width = Mathf.Min(460, Screen.width - 60);
            int height = 220;
            Rect area = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUI.color = new Color(0.18f, 0.05f, 0.05f, 0.92f);
            GUILayout.BeginArea(area, panelStyle);
            GUI.color = Color.white;

            GUILayout.Label(noticeTitle, titleStyle);
            GUILayout.Space(8);
            GUILayout.Label(noticeBody, labelStyle);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("OK", buttonStyle, GUILayout.Height(36)))
            {
                Manager?.ReturnToMenu();
                noticeTitle = string.Empty;
                noticeBody = string.Empty;
            }
            GUILayout.EndArea();
        }

        // helpers /////////////////////////////////////////////////////////////
        static int CountBalls()
        {
            int count = 0;
            // 客户端没有 NetworkServer.spawned，必须用当前所在端对应的集合
            var source = NetworkServer.active ? NetworkServer.spawned : NetworkClient.spawned;
            foreach (KeyValuePair<uint, NetworkIdentity> entry in source)
                if (entry.Value != null && entry.Value.TryGetComponent(out NetworkBall _))
                    count++;
            return count;
        }

        static string NearestBallVelocity()
        {
            NetworkBall target = null;
            foreach (KeyValuePair<uint, NetworkIdentity> entry in NetworkServer.spawned)
            {
                if (entry.Value == null || !entry.Value.TryGetComponent(out NetworkBall ball)) continue;
                if (ball.HasServerState) target = ball;
            }

            if (NetworkClient.active)
            {
                foreach (KeyValuePair<uint, NetworkIdentity> entry in NetworkClient.spawned)
                {
                    if (entry.Value == null || !entry.Value.TryGetComponent(out NetworkBall ball)) continue;
                    if (ball.HasServerState) target = ball;
                }
            }

            if (target == null) return "-";
            Vector3 v = target.Velocity;
            return $"({v.x:F1}, {v.y:F1}, {v.z:F1}) |v|={v.magnitude:F2}";
        }
    }
}
