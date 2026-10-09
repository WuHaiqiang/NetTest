// -----------------------------------------------------------------------------
// NetFirstPersonPlayer —— 第一人称玩家角色（主机权威 + 客户端预测 + 平滑和解）
//
// 数据流：
//   1. 本地客户端采样输入（移动 / 跳跃 / 朝向），每个 FixedUpdate 生成一个输入帧。
//   2. 本地客户端**立即**用这个输入帧驱动自己的 Rigidbody（预测，手感零延迟）。
//   3. 同一个输入帧通过 CmdInput() 发给主机。
//   4. 主机在自己的 FixedUpdate 中、按同样的顺序、用同样的运动函数重放这些输入，
//      主机算出来的结果才是**权威状态**（位置 / 速度 / 朝向 + 已处理的输入帧号）。
//   5. 主机把权威状态广播给所有客户端；本地客户端拿它和自己历史里同一帧的预测结果对比，
//      有偏差就平滑修正（修正河口 players 不会被“拉扯”，也不会作弊）。
//   6. 其他客户端看到的是插值后的幽灵状态（Rigidbody 设为 kinematic）。
//
// 由于角色的物理仿真只由主机推进，所有“撞球”、“互撞”都在同一个 PhysX 世界里结算，
// 因此两名玩家同一帧一起撞球时不会出现两边状态错位的问题。
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace NetTest
{
    public struct InputFrame
    {
        public uint tick;
        public float horizontal;
        public float forward;
        public float yaw;
        public bool jump;

        public InputFrame(uint tick, float horizontal, float forward, float yaw, bool jump)
        {
            this.tick = tick;
            this.horizontal = horizontal;
            this.forward = forward;
            this.yaw = yaw;
            this.jump = jump;
        }
    }

    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("NetTest/Net First Person Player")]
    public class NetFirstPersonPlayer : NetworkBehaviour
    {
        [Header("Movement")]
        public float moveSpeed = 6f;
        public float jumpSpeed = 6.5f;
        public float gravityMultiplier = 2f;

        [Header("Look")]
        public float mouseSensitivity = 0.12f;
        public float maxPitch = 85f;

        [Header("References")]
        public Transform cameraAnchor;
        public Transform modelRoot;
        public Camera playerCamera;

        [Header("Ball Interaction")]
        [Tooltip("本地立刻向主机上报“撞球”，避免等一个 RTT 才有反应")]
        public bool sendContactImpulses = true;
        public float pushImpulseScale = 1.1f;
        public float maxPushSpeed = 8f;
        public float pushCooldown = 0.25f;

        [Header("Reconciliation")]
        [Tooltip("预测误差超过这个值才开始修正（米）")]
        public float correctionThreshold = 0.03f;
        [Tooltip("预测误差超过这个值直接传送（米）")]
        public float teleportThreshold = 3f;
        public float correctionSpeed = 12f;

        // runtime /////////////////////////////////////////////////////////////
        Rigidbody rb;
        CapsuleCollider capsule;

        // 本地视角
        float yaw;
        float pitch;

        // 输入
        uint localTick;
        bool jumpQueued;
        Vector2 moveInput;

        // 服务器侧
        readonly Queue<InputFrame> pendingInputs = new Queue<InputFrame>();
        InputFrame lastInput;
        bool hasInput;
        uint lastProcessedTick;

        // 客户端预测历史
        readonly List<PredictedState> history = new List<PredictedState>(128);
        readonly SnapshotInterpolator proxyInterpolator = new SnapshotInterpolator();
        Vector3 pendingCorrection;
        bool clientPrimed;

        readonly Dictionary<uint, float> lastBallPush = new Dictionary<uint, float>();

        struct PredictedState
        {
            public uint tick;
            public Vector3 position;
            public Vector3 velocity;
        }

        // helpers /////////////////////////////////////////////////////////////
        bool LocalControlled => isLocalPlayer;
        bool RemoteProxy => isClient && !isLocalPlayer;

        float Height => capsule != null ? capsule.height : 1.8f;

        public string DisplayName => $"Player#{netId}";

        // ---------------------------------------------------------------------
        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();
        }

        public override void OnStartServer()
        {
            // 主机：玩家是动态刚体，参与权威物理仿真
            rb.isKinematic = false;
        }

        public override void OnStartClient()
        {
            // 视角初始化
            Transform t = transform;
            yaw = t.eulerAngles.y;
            UpdateCameraAnchor();

            if (LocalControlled)
            {
                // 第一人称：隐藏自己的模型，启用自己的摄像机，关掉离线摄像机
                RoomHud.Instance?.SetLobbyCameraActive(false);
                if (modelRoot != null) modelRoot.gameObject.SetActive(false);
                if (playerCamera != null)
                {
                    playerCamera.enabled = true;
                    playerCamera.gameObject.SetActive(true);
                }
            }
            else
            {
                if (playerCamera != null) playerCamera.gameObject.SetActive(false);

                // 别人的角色用主机广播的状态插值显示，本地不再仿真
                if (!isServer)
                {
                    rb.isKinematic = true;
                    clientPrimed = false;
                }
            }
        }

        public override void OnStopClient()
        {
            if (LocalControlled)
                RoomHud.Instance?.SetLobbyCameraActive(true);

            history.Clear();
            proxyInterpolator.Clear();
            pendingInputs.Clear();
        }

        // input ///////////////////////////////////////////////////////////////
        void Update()
        {
            if (!LocalControlled) return;

            // 光标处于界面状态（Esc / 弹窗）时不吃输入
            if (RoomHud.Instance != null && RoomHud.Instance.CursorFree)
            {
                moveInput = Vector2.zero;
                jumpQueued = false;
                return;
            }

            ReadLookInput();
            ReadMoveInput();
            UpdateCameraAnchor();
        }

        void UpdateCameraAnchor()
        {
            if (cameraAnchor == null) return;
            cameraAnchor.localRotation = Quaternion.Euler(-pitch, 0f, 0f);
        }

        void ReadLookInput()
        {
            Vector2 delta = InputLook();
            yaw += delta.x * mouseSensitivity;
            pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, -maxPitch, maxPitch);

            rb.MoveRotation(Quaternion.Euler(0f, yaw, 0f));
        }

        void ReadMoveInput()
        {
            moveInput = InputMove();
            if (InputJump()) jumpQueued = true;
        }

        static Vector2 InputMove()
        {
            float x = 0f, y = 0f;
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.aKey.isPressed) x -= 1f;
                if (keyboard.dKey.isPressed) x += 1f;
                if (keyboard.sKey.isPressed) y -= 1f;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) y += 1f;
                if (keyboard.leftArrowKey.isPressed) x -= 1f;
                if (keyboard.rightArrowKey.isPressed) x += 1f;
                if (keyboard.downArrowKey.isPressed) y -= 1f;
            }
#else
            x = Input.GetAxisRaw("Horizontal");
            y = Input.GetAxisRaw("Vertical");
#endif
            return new Vector2(x, y);
        }

        static bool InputJump()
        {
#if ENABLE_INPUT_SYSTEM
            return UnityEngine.InputSystem.Keyboard.current != null &&
                   UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }

        static Vector2 InputLook()
        {
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return Vector2.zero;
            Vector2 delta = mouse.delta.ReadValue();
            return delta;
#else
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#endif
        }

        // fixed steps /////////////////////////////////////////////////////////
        void FixedUpdate()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();

            if (isServer)
            {
                ServerFixedStep();
                ApplyGravityCorrection();
            }
            else if (LocalControlled)
            {
                ClientPredictedFixedStep();
                ApplyGravityCorrection();
            }
            else if (RemoteProxy)
            {
                ApplyProxyState();
            }
        }

        void ServerFixedStep()
        {
            InputFrame input;

            if (isLocalPlayer)
            {
                // 房主自己的角色：直接读取本地输入，无需走网络
                input = new InputFrame(0, moveInput.x, moveInput.y, yaw, false);
                if (jumpQueued)
                {
                    input.jump = true;
                    jumpQueued = false;
                }
                lastInput = input;
                hasInput = true;
                lastProcessedTick = localTick++;
            }
            else if (pendingInputs.Count > 0)
            {
                input = pendingInputs.Dequeue();
                lastInput = input;
                hasInput = true;
                lastProcessedTick = input.tick;
            }
            else
            {
                input = lastInput; // 复用上一次输入，保证持续位移正确
                input.jump = false;
            }

            if (hasInput) ApplyMotion(input);
            SetDirty(); // 每个 server tick 广播权威状态
        }

        void ClientPredictedFixedStep()
        {
            bool jump = jumpQueued;
            jumpQueued = false;

            InputFrame input = new InputFrame(++localTick, moveInput.x, moveInput.y, yaw, jump);
            ApplyMotion(input);

            history.Add(new PredictedState { tick = input.tick, position = rb.position, velocity = rb.linearVelocity });
            if (history.Count > 128) history.RemoveAt(0);

            CmdInput(input.tick, input.horizontal, input.forward, input.yaw, input.jump);
        }

        void ApplyMotion(InputFrame input)
        {
            // 用 yaw 决定朝向，和客户端看得见的朝向一致
            Quaternion yawRotation = Quaternion.Euler(0f, input.yaw, 0f);
            Vector3 wish = yawRotation * new Vector3(input.horizontal, 0f, input.forward);
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            Vector3 velocity = rb.linearVelocity;
            velocity.x = wish.x * moveSpeed;
            velocity.z = wish.z * moveSpeed;

            if (input.jump && IsGrounded())
                velocity.y = jumpSpeed;

            rb.linearVelocity = velocity;
            rb.MoveRotation(yawRotation);
        }

        void ApplyGravityCorrection()
        {
            // 让下落比 Unity 默认重力稍快一点，手感更好，且各端一致
            Vector3 velocity = rb.linearVelocity;
            if (!IsGrounded() && velocity.y < 0f)
            {
                velocity.y -= Physics.gravity.y * (gravityMultiplier - 1f) * Time.fixedDeltaTime;
                rb.linearVelocity = velocity;
            }
        }

        bool IsGrounded()
        {
            Vector3 origin = rb != null ? rb.position : transform.position;
            float distance = Height * 0.5f + 0.15f;
            return Physics.Raycast(origin + Vector3.up * 0.2f, Vector3.down, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }

        // remote proxies //////////////////////////////////////////////////////
        void ApplyProxyState()
        {
            if (!proxyInterpolator.Sample(out Vector3 position, out Quaternion rotation, out Vector3 _, out Vector3 _))
                return;

            if (!clientPrimed)
            {
                clientPrimed = true;
                rb.position = position;
                rb.rotation = rotation;
                return;
            }

            rb.MovePosition(position);
            rb.MoveRotation(rotation);
        }

        void LateUpdate()
        {
            if (!LocalControlled) return;

            // 平滑修正：把预测误差分摊到若干帧里吸收
            if (pendingCorrection.sqrMagnitude > 1e-8f && rb != null)
            {
                Vector3 step = pendingCorrection * Mathf.Clamp01(Time.deltaTime * correctionSpeed);
                rb.position += step;
                pendingCorrection -= step;
            }
        }

        // network /////////////////////////////////////////////////////////////
        // 输入必须可靠：丢一个包就会导致跳跃丢失
        [Command]
        void CmdInput(uint tick, float horizontal, float forward, float yaw, bool jump)
        {
            InputFrame input = new InputFrame(tick, horizontal, forward, yaw, jump);

            // 简单的反作弊：只接受递增的输入帧
            if (pendingInputs.Count > 0 && tick <= pendingInputs.Peek().tick) return;

            pendingInputs.Enqueue(input);
            if (pendingInputs.Count > 8) pendingInputs.Dequeue(); // 网络抖动保护
        }

        public override void OnSerialize(NetworkWriter writer, bool initialState)
        {
            writer.WriteVector3(rb.position);
            writer.WriteFloat(lastInput.yaw);
            writer.WriteVector3(rb.linearVelocity);
            writer.WriteUInt(lastProcessedTick);
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            Vector3 position = reader.ReadVector3();
            float authorityYaw = reader.ReadFloat();
            Vector3 velocity = reader.ReadVector3();
            uint tick = reader.ReadUInt();

            if (isServer) return; // 主机自己是权威，忽略回环数据

            if (LocalControlled)
                Reconcile(position, velocity, tick, authorityYaw);
            else
                proxyInterpolator.Add(new PhysicsSnapshot(NetworkTime.localTime, position, Quaternion.Euler(0f, authorityYaw, 0f), velocity, Vector3.zero));
        }

        void Reconcile(Vector3 position, Vector3 velocity, uint tick, float authorityYaw)
        {
            if (!clientPrimed)
            {
                clientPrimed = true;
                rb.position = position;
                lastInput.yaw = authorityYaw;
                history.Clear();
                return;
            }

            // 找出主机处理到第 tick 帧时，自己当时预测的结果
            PredictedState state = default;
            bool found = false;
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].tick == tick)
                {
                    state = history[i];
                    found = true;
                    break;
                }
            }

            if (found)
            {
                // 丢掉已经确认的历史
                while (history.Count > 0 && history[0].tick <= tick)
                    history.RemoveAt(0);

                float error = (state.position - position).sqrMagnitude;
                if (error > teleportThreshold * teleportThreshold)
                {
                    Teleport(position);
                    return;
                }

                if (error > correctionThreshold * correctionThreshold)
                {
                    // 注意：这里用“当前位置 -> 主机位置”的差值，而不是历史误差，
                    // 因为我们已经在历史的更前方了。
                    pendingCorrection = position - rb.position;
                }
            }
        }

        void Teleport(Vector3 position)
        {
            rb.position = position;
            pendingCorrection = Vector3.zero;
            history.Clear();
            proxyInterpolator.Clear();
        }

        // ball contact ////////////////////////////////////////////////////////
        void OnCollisionEnter(Collision collision)
        {
            NetworkBall ball = collision.collider.GetComponentInParent<NetworkBall>();
            if (ball == null) return;

            if (RoomHud.Instance != null && LocalControlled)
                RoomHud.Instance.ShowTapped(ball.netId.ToString());

            if (!sendContactImpulses) return;
            if (!LocalControlled && !isOwned) return;

            uint id = ball.netId;
            if (lastBallPush.TryGetValue(id, out float last) && Time.time - last < pushCooldown)
                return;
            lastBallPush[id] = Time.time;

            Vector3 velocity = rb.linearVelocity;
            velocity.y = 0f;
            if (velocity.magnitude > maxPushSpeed)
                velocity = velocity.normalized * maxPushSpeed;

            Vector3 point = collision.GetContact(0).point;
            Rigidbody ballBody = ball.GetComponent<Rigidbody>();
            if (ballBody == null) return;

            Vector3 impulse = velocity * ballBody.mass * pushImpulseScale;
            if (impulse.sqrMagnitude < 0.01f) return;

            if (isServer)
                ball.ServerApplyPush(impulse, point);
            else
                CmdPushBall(ball.netIdentity, point, velocity);
        }

        // 撞球的即时反馈也走可靠通道，避免按下之后完全没反应
        [Command]
        void CmdPushBall(NetworkIdentity ballIdentity, Vector3 point, Vector3 velocity)
        {
            if (ballIdentity == null || !ballIdentity.TryGetComponent(out NetworkBall ball)) return;

            // 主机侧校验：速度上限，防止作弊
            if (velocity.magnitude > maxPushSpeed)
                velocity = velocity.normalized * maxPushSpeed;

            Rigidbody ballBody = ball.GetComponent<Rigidbody>();
            if (ballBody == null) return;

            ball.ServerApplyPush(velocity * ballBody.mass * pushImpulseScale, point);
        }
    }
}
