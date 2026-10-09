# 球体房间 — Unity 6 多人联机（Mirror + Steamworks）

基于 **Mirror 96.11.3** + **Steamworks.NET** 实现的「球体房间」多人 Demo，覆盖基础功能 + 进阶功能 + Steam 超级加分项。

## 一、快速开始

1. 用 **Unity 6000.3.14f1**（Unity 6.3 LTS）打开本项目。
2. 打开并运行 `Assets/Scenes/BallRoom`，屏幕中央出现连接菜单。

## 二、操作方式

| 操作 | 按键 |
| --- | --- |
| 移动 | WASD / 方向键 |
| 跳跃 | Space |
| 视角 | 鼠标（进入第一人称后自动锁定光标） |
| 暂停 / 解锁光标 | Esc（暂停菜单内，Steam 房主可见「邀请 Steam 好友」） |
| 隐藏/显示左上角信息面板 | 点击面板标题 |

## 三、功能对照表

### 基础功能
| 需求 | 实现位置 |
| --- | --- |
| 1. 1–4 人联机，一人做主机，主机权威 | `RoomNetworkManager`（`maxPlayers=4`、Mirror 内置人数上限校验）、所有物理只由主机推进 |
| 2. 共享物理球：可推动、碰障碍真实弹回 | `ball.prefab`（`Rigidbody` + bounce=0.6 的 `PM_Ball` 物理材质） + `NetworkBall` |
| 2. 两人同一物理帧撞击不错位 | 球的物理**只在主机**的一个 PhysX 世界里结算；客户端做快照插值显示，本地不跑物理 —— 结构上不存在“两边算出不同位置” |
| 3. 第一人称视角 | `networkPlayer.prefab` + `NetFirstPersonPlayer`（yaw/pitch 分离、本地隐藏自身模型） |

### 进阶功能
| 需求 | 实现位置 |
| --- | --- |
| 中途加入同步位置与速度 | `NetworkBall.OnSerialize/OnDeserialize`：`initialState` 时下发 位置+旋转+**线速度+角速度**，加入瞬间即吸附到主机当前状态 |
| 主机退出处理 | `RoomNetworkManager.ShutdownGracefully()` 先 `RpcHostIsShuttingDown()` 再停机；异常掉线由 `OnClientDisconnect` → HUD 弹窗提示，并提供「返回房间菜单 / 自己开房」 |
| 每 15 秒随机位置生成球 | `BallSpawner`（`spawnInterval=15f`，纯主机执行 `NetworkServer.Spawn`，超过 `maxBalls` 销毁最旧） |
| 触碰提示 `Tapped` | `NetFirstPersonPlayer.OnCollisionEnter` → `RoomHud.ShowTapped()`（屏幕中央淡出文字）(Tapped括号中表示球编号) |
| Steam 联机 + 好友/房间邀请 | `SteamP2PTransport` + `SteamLobbyService`（AppID 480，见下一节） |

## 四、体系结构与主机权威策略

```
Player(本地) --CmdInput(输入帧)--> 主机 --> 在同一个 FixedUpdate 序列里重放输入
                                       --> 权威位置/速度/朝向广播给所有人
本地：同一个输入帧立刻驱动自己的 Rigidbody（预测）+ 历史环形缓冲
本地：收到主机权威状态后，与同一输入帧的预测结果对比 -> 平滑修正 / 远距离传送
```

* **玩家**：输入 -> 服务器 -> 广播。本地客户端预测 + 平滑和解。
* **球体**：单向同步（主机 -> 客户端）。客户端 `isKinematic=true`，只按时间戳做快照插值（`SnapshotInterpolator`），绝不在本地用物理推进球，因此**不可能出现两侧球位置不一致**。

### 网络方案选择理由
* **Mirror（HLAPI）**：其「服务器权威 + 客户端预测」模型与题目“主机权威，共享状态以主机为唯一数据源”的要求天然契合，成熟稳定、可读性好、开发成本低。
* **Steam 联机采用 SteamP2PTransport（`ISteamNetworkingMessages` + Steam SDR Relay）** 而非自建服务器：零服务器成本、自动 NAT 穿透，且使用免费 AppID 480 即可，无需 Steamworks 账号或商店页面。

## 五、Transport 与连接模式

场景中的 `NetworkManager` 上挂了 **两套 Transport**，运行时动态切换 `Transport.active`：

| 模式 | Transport | 地址 |
| --- | --- | --- |
| 本机 / 局域网 | `kcp2k.KcpTransport`（端口 7777） | `localhost` 或 IP |
| Steam P2P | `NetTest.SteamP2PTransport`（`ISteamNetworkingMessages` + Steam SDR Relay） | 主机 SteamID64 |

启动时默认使用本机 KCP；Steam 模式会切换 `Transport.active` 到 `SteamP2PTransport`。切换逻辑见 `RoomNetworkManager.SelectTransport()`。

## 六、Steam Transport 测试步骤（AppID 480）

### 前置条件
1. Steam 客户端已登录并运行。
2. 项目根目录已有 `steam_appid.txt`，内容为 `480`（本项目已自带；Steamworks.NET 用它初始化 AppID，**无需** Steamworks 账号也**无需**上架商店）。
3. 在 Editor 中点 Play。

### 运行验证

#### 方案 A：两台机器 / 两个 Steam 账号（推荐，真实 NAT 穿透）
1. Steam 里「添加非 Steam 游戏」，选择 "../Build/PCDemo/NetTestDemo.exe"
2. 两台机器都用各自的 Steam 账号登录 Steam（互相加为好友）。
3. 两边都通过 Steam 启动 Unity，打开 `BallRoom` 场景并 Play。
4. **A 机器**：点 `创建 Steam 房间`。
   - 内部流程：`SteamMatchmaking.CreateLobby(FriendsOnly)` → `LobbyCreated_t` → 设置富文本 `connect=<SteamID>` → `StartHost()`。
5. **邀请**：A 机器建房后按 `Esc` 打开暂停菜单 → 点 `邀请 Steam 好友`（仅 Steam 房主可见）→ 弹出 Steam 邀请面板；或把 A 机器 HUD 上显示的 **本机 SteamID64** 直接发给 B。
6. **B 机器**二选一：
   - 在游戏里把 SteamID 填入输入框 → `用 SteamID 加入`；
   - 或先退出到 Steam 好友列表，右键 A → `加入游戏`（触发 `GameLobbyJoinRequested_t`，游戏会自动 `JoinLobby` → `StartClient`）。
7. 连上后：A 为房主，推动球，B 端应立即看到同一颗球的位置/滚动；每 15 秒新生成的球两端一致；B 撞球时在 B 端显示 `Tapped!`。

#### 方案 B：本机 / 局域网（KCP，不需要 Steam，用于日常联调）
1. 机器 A：Play → `创建房间 (Host)`（默认 `localhost`）。
2. 机器 B（或 ParrelSync 第二个 Editor）：Play → 地址填 `localhost`（同一机器）或 A 的局域网 IP（不同机器） → `加入`。
3. 其余功能与 Steam 模式完全一致（Transport 换成 KCP）。

### 主机退出的验证
* 优雅退出：按下ESC，点击 `返回房间菜单` → 主机先广播 `RpcHostIsShuttingDown`，所有客户端弹出「连接已断开」浮层。
* 强制退出：直接关闭房主的 Play 窗口 / 结束进程 → 客户端 transport 超时（`SteamP2PTransport.connectionTimeout`）后触发 `OnClientDisconnect`，弹出「连接已断开」浮层。

## 七、目录结构

```
Assets/Scripts/
  Steam/
    SteamP2PTransport.cs    Mirror Transport 实现（SteamNetworkingMessages，双通道 Reliable/Unreliable）
    SteamLobbyService.cs    大厅创建/加入、好友邀请、Rich Presence "加入游戏"
  Game/
    RoomNetworkManager.cs   NetworkManager 派生：人数上限、Transport 切换、礼貌停机
    NetFirstPersonPlayer.cs 第一人称玩家：输入 -> 主机 -> 广播，本地预测 + 平滑和解
    NetworkBall.cs          共享球：只在主机仿真，客户端快照插值 + initialState 速度同步
    BallSpawner.cs          每 15 秒在随机位置生成一个球（主机执行）
    SnapshotInterpolator.cs 通用快照插值（世界时间线 + 追赶/减速修正）
    RoomHud.cs              IMGUI 界面：连接菜单、房间信息、Tapped、弹窗
  Editor/
    SceneBuilder.cs         NetTest > Build Scene：一键生成场景与所有预制体
```

## 八、构建与提交说明
* **Windows 构建**：`File → Build Settings`，平台选 **Windows x64**，Build 到 `Build/` 目录。
* Steam 联机时，构建出的 exe 同目录需保留 `steam_appid.txt`（内容 `480`）。
* 保留 Git 提交历史。
* 本 README 已包含：运行说明、功能对照、网络方案理由、Steam 测试步骤。
