// -----------------------------------------------------------------------------
// NetworkBall —— 房间里的共享物理球
//
// 设计要点（满足“主机权威 + 不同步 / 不错位”的要求）：
//   * 球体物理 **只在主机上仿真**（主机 = 唯一数据源）。
//   * 客户端把球设为 isKinematic，用快照插值显示插值后的结果，
//     客户端本地**绝不**用自己的物理去推进球，因此两个客户端不可能算出不同的球位置。
//   * 每帧（server tick）把 位置 / 旋转 / 线速度 / 角速度 一起同步，
//     中途加入的玩家会拿到 initialState（包含速度），能直接“接着滚”。
//   * 两个玩家在同一个物理帧同时撞击时，两次碰撞都由同一个 PhysX 世界、
//     同一个 FixedUpdate 步骤求解，天然不会出现状态错位。
// -----------------------------------------------------------------------------
using Mirror;
using UnityEngine;

namespace NetTest
{
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("NetTest/Network Ball")]
    public class NetworkBall : NetworkBehaviour
    {
        [Header("Debug")]
        [Tooltip("显示来自主机的权威速度")]
        public bool logVelocity;

        Rigidbody rb;
        Renderer[] visuals;
        MaterialPropertyBlock propertyBlock;

        readonly SnapshotInterpolator interpolator = new SnapshotInterpolator();
        bool clientPrimed;

        /// <summary>球的当前速度（客户端读取的是主机同步过来的速度）。</summary>
        public Vector3 Velocity
        {
            get
            {
                if (rb == null) rb = GetComponent<Rigidbody>();
                return isServer ? rb.linearVelocity : interpolator.CurrentState.velocity;
            }
        }

        public bool HasServerState => isServer || interpolator.Count > 0;

        // ---------------------------------------------------------------------
        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            visuals = GetComponentsInChildren<Renderer>(true);
            propertyBlock = new MaterialPropertyBlock();
        }

        public override void OnStartServer()
        {
            rb.isKinematic = false;
            // 让球不要睡着，否则轻微推动没有反应
            rb.sleepThreshold = -1f;
            ApplyTint();
        }

        public override void OnStartClient()
        {
            if (!isServer)
            {
                // 纯客户端：物理不做本地仿真，只按主机状态插值
                rb.isKinematic = true;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            ApplyTint();

            // initialState 已经在 spawn 消息里下发，这里直接把球摆到正确位置（含速度）
            if (!isServer && interpolator.Count > 0)
            {
                clientPrimed = false;
                ApplyInterpolatedState();
            }
        }

        void Update()
        {
            if (rb == null) return;

            if (isServer)
            {
                // 每个 server tick 把权威状态推给所有客户端
                SetDirty();
            }
            else
            {
                ApplyInterpolatedState();
            }
        }

        // 服务器/客户端都在醒来时就地回滚显示
        void ApplyTint()
        {
            if (visuals == null || visuals.Length == 0) visuals = GetComponentsInChildren<Renderer>(true);
            if (visuals == null || visuals.Length == 0) return;

            // 颜色由 netId 决定，保证各端一致（不需要额外的网络同步）
            uint seed = netId > 0 ? netId : 1;
            Color color = Color.HSVToRGB((seed * 0.6180339f) % 1f, 0.65f, 0.9f);

            if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
            foreach (Renderer renderer in visuals)
            {
                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor("_BaseColor", color);
                propertyBlock.SetColor("_Color", color);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }

        void ApplyInterpolatedState()
        {
            if (!interpolator.Sample(out Vector3 position, out Quaternion rotation, out Vector3 _, out Vector3 _))
                return;

            if (!clientPrimed)
            {
                // 第一次（或中途加入）直接吸附到权威位置，避免从原点插值过去
                clientPrimed = true;
                rb.position = position;
                rb.rotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            rb.MovePosition(position);
            rb.MoveRotation(rotation);

            if (logVelocity)
                Debug.Log($"[Ball {netId}] v={interpolator.CurrentState.velocity:F2}");
        }

        // network /////////////////////////////////////////////////////////////
        public override void OnSerialize(NetworkWriter writer, bool initialState)
        {
            if (rb == null) rb = GetComponent<Rigidbody>();

            writer.WriteVector3(rb.position);
            writer.WriteQuaternion(rb.rotation);
            writer.WriteVector3(rb.linearVelocity);
            writer.WriteVector3(rb.angularVelocity);
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            Vector3 velocity = reader.ReadVector3();
            Vector3 angularVelocity = reader.ReadVector3();

            if (isServer)
            {
                // 主机是权威，忽略自己的回环数据
                return;
            }

            interpolator.Add(new PhysicsSnapshot(NetworkTime.localTime, position, rotation, velocity, angularVelocity));

            if (initialState)
            {
                // 中途加入：立刻对齐到主机当前状态（位置 + 速度）
                clientPrimed = false;
                ApplyInterpolatedState();
            }
        }

        // 由客户端的玩家 / 由服务器自身调用：给球一个推动冲量。
        [Server]
        public void ServerApplyPush(Vector3 impulse, Vector3 atPosition)
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            rb.AddForceAtPosition(impulse, atPosition, ForceMode.Impulse);
            rb.WakeUp();
        }
    }
}
