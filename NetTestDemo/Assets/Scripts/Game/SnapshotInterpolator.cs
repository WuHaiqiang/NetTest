using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace NetTest
{
    /// <summary>一张物理状态快照（主机权威数据）。</summary>
    public struct PhysicsSnapshot
    {
        public double remoteTime;
        public double localTime;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public Vector3 angularVelocity;

        public PhysicsSnapshot(double remoteTime, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            this.remoteTime = remoteTime;
            localTime = NetworkTime.localTime;
            this.position = position;
            this.rotation = rotation;
            this.velocity = velocity;
            this.angularVelocity = angularVelocity;
        }
    }

    /// <summary>
    /// 基于时间的快照插值（world timeline + 追赶/减速修正）。
    /// 参考 https://gafferongames.com/post/snapshot_interpolation/
    /// </summary>
    public class SnapshotInterpolator
    {
        readonly List<PhysicsSnapshot> buffer = new List<PhysicsSnapshot>(32);

        double timeline;
        bool timelineInitialized;
        PhysicsSnapshot current;

        public int Count => buffer.Count;
        public PhysicsSnapshot Latest => buffer.Count > 0 ? buffer[buffer.Count - 1] : default;
        public PhysicsSnapshot CurrentState => current;

        /// <summary>重新开始时清空缓冲区（生成 / 传送）。</summary>
        public void Clear()
        {
            buffer.Clear();
            timelineInitialized = false;
        }

        public void Add(PhysicsSnapshot snapshot)
        {
            if (buffer.Count > 0 && snapshot.remoteTime <= buffer[buffer.Count - 1].remoteTime)
                return; // 过期 / 乱序包直接丢弃（走不可靠通道时会发生）

            buffer.Add(snapshot);
            if (buffer.Count > 32)
                buffer.RemoveAt(0);
        }

        /// <summary>
        /// 取当前应该显示的状态。返回 false 表示还没有任何数据。
        /// </summary>
        public bool Sample(out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angularVelocity)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;

            if (buffer.Count == 0) return false;

            // 时间线初始化：从最早可用的快照开始
            if (!timelineInitialized)
            {
                timeline = buffer[0].remoteTime;
                timelineInitialized = true;
                current = buffer[0];
            }

            timeline += Time.deltaTime;

            PhysicsSnapshot first = buffer[0];
            PhysicsSnapshot last = buffer[buffer.Count - 1];

            // 追赶：缓冲区太靠前（网络卡顿）-> 放慢时间线
            if (timeline > last.remoteTime)
            {
                double ahead = timeline - last.remoteTime;
                timeline -= ahead * 0.5;
                if (timeline < last.remoteTime) timeline = last.remoteTime;
            }
            // 落后太多（延迟突增后的追赶）-> 加速时间线
            else if (timeline < first.remoteTime)
            {
                double behind = first.remoteTime - timeline;
                timeline += behind * 0.5;
                if (timeline > first.remoteTime) timeline = first.remoteTime;
            }

            if (timeline < first.remoteTime)
            {
                Apply(first, out position, out rotation, out velocity, out angularVelocity);
                return true;
            }

            if (timeline >= last.remoteTime)
            {
                current = last;
                Apply(last, out position, out rotation, out velocity, out angularVelocity);
                return true;
            }

            // 在两个快照之间插值
            for (int i = 0; i < buffer.Count - 1; i++)
            {
                PhysicsSnapshot from = buffer[i];
                PhysicsSnapshot to = buffer[i + 1];
                if (timeline >= from.remoteTime && timeline <= to.remoteTime)
                {
                    // 丢弃已经不需要的旧快照
                    if (i > 0) buffer.RemoveRange(0, i);

                    double span = to.remoteTime - from.remoteTime;
                    float t = span > double.Epsilon ? (float)((timeline - from.remoteTime) / span) : 0f;
                    t = Mathf.Clamp01(t);

                    current = new PhysicsSnapshot
                    {
                        remoteTime = timeline,
                        localTime = NetworkTime.localTime,
                        position = Vector3.LerpUnclamped(from.position, to.position, t),
                        rotation = Quaternion.SlerpUnclamped(from.rotation, to.rotation, t),
                        velocity = Vector3.LerpUnclamped(from.velocity, to.velocity, t),
                        angularVelocity = Vector3.LerpUnclamped(from.angularVelocity, to.angularVelocity, t)
                    };
                    Apply(current, out position, out rotation, out velocity, out angularVelocity);
                    return true;
                }
            }

            Apply(last, out position, out rotation, out velocity, out angularVelocity);
            return true;
        }

        static void Apply(PhysicsSnapshot snapshot, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angularVelocity)
        {
            position = snapshot.position;
            rotation = snapshot.rotation;
            velocity = snapshot.velocity;
            angularVelocity = snapshot.angularVelocity;
        }
    }
}
