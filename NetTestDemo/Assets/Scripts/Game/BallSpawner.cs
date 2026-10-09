// -----------------------------------------------------------------------------
// BallSpawner —— 进阶功能：由主机控制，每 15 秒在房间随机位置生成一个球。
//
// 生成完全发生在主机侧（NetworkServer.Spawn），
// 中途加入的客户端会在 spawn 消息里拿到球的完整状态（位置 + 速度 + 角速度）。
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace NetTest
{
    [AddComponentMenu("NetTest/Ball Spawner")]
    public class BallSpawner : NetworkBehaviour
    {
        [Header("Spawn")]
        public GameObject ballPrefab;
        [Tooltip("每隔多少秒生成一个球（document 要求 15 秒）")]
        public float spawnInterval = 15f;
        [Tooltip("开局立刻生成的球数量（保证房间里一开始就有共享球）")]
        public int initialBallCount = 1;
        [Tooltip("房间内球的上限，超出后销毁最旧的球")]
        public int maxBalls = 24;
        public float spawnHeight = 1.2f;

        [Header("Spawn Volume")]
        public Vector3 spawnCenter = Vector3.zero;
        public Vector3 spawnExtents = new Vector3(8f, 0f, 8f);

        readonly List<NetworkBall> balls = new List<NetworkBall>();
        double nextSpawnTime;

        /// <summary>距离下一次生成还有多少秒（UI 用）</summary>
        public float SecondsUntilNextSpawn =>
            NetworkServer.active ? Mathf.Max(0f, (float)(nextSpawnTime - NetworkTime.time)) : 0f;

        public int BallCount => balls.Count;

        // ---------------------------------------------------------------------
        public override void OnStartServer()
        {
            balls.Clear();

            for (int i = 0; i < initialBallCount; i++)
                SpawnBall();

            nextSpawnTime = NetworkTime.time + spawnInterval;
        }

        public override void OnStopServer()
        {
            balls.Clear();
        }

        [ServerCallback]
        void Update()
        {
            if (!NetworkServer.active) return;
            if (NetworkTime.time < nextSpawnTime) return;

            // 只按 interval 对齐，避免大 lag 之后连续补生成
            double missed = NetworkTime.time - nextSpawnTime;
            if (missed > spawnInterval * 2)
                nextSpawnTime = NetworkTime.time;
            else
                nextSpawnTime += spawnInterval;

            SpawnBall();
        }

        [Server]
        public void SpawnBall()
        {
            if (ballPrefab == null)
            {
                Debug.LogError("[BallSpawner] 没有指定 ballPrefab，无法生成球。");
                return;
            }

            Vector3 position = FindSpawnPosition();
            GameObject spawned = Instantiate(ballPrefab, position, Random.rotation);
            NetworkServer.Spawn(spawned);

            if (spawned.TryGetComponent(out NetworkBall ball))
            {
                balls.Add(ball);
                TrimBalls();
            }
        }

        [Server]
        Vector3 FindSpawnPosition()
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                Vector3 candidate = new Vector3(
                    spawnCenter.x + Random.Range(-spawnExtents.x, spawnExtents.x),
                    spawnCenter.y + spawnHeight,
                    spawnCenter.z + Random.Range(-spawnExtents.z, spawnExtents.z));

                // 不要卡在任何已有物体里
                if (!Physics.CheckSphere(candidate, 0.75f, ~0, QueryTriggerInteraction.Ignore))
                    return candidate;
            }

            Debug.LogWarning("[BallSpawner] 没找到理想的生成位置，使用房间中心。");
            return spawnCenter + Vector3.up * (spawnHeight + 1f);
        }

        [Server]
        void TrimBalls()
        {
            while (balls.Count > maxBalls)
            {
                NetworkBall oldest = balls[0];
                balls.RemoveAt(0);
                if (oldest != null)
                    NetworkServer.Destroy(oldest.gameObject);
            }
        }

        // ---------------------------------------------------------------------
        /// <summary>房主主动退出时，先通知所有客户端（ClientRpc 只能声明在 NetworkBehaviour 上）。</summary>
        [ClientRpc]
        public void RpcHostIsShuttingDown()
        {
            if (NetworkServer.active) return; // 房主自己不需要提示
            RoomNetworkManager mgr = RoomNetworkManager.Instance;
            if (mgr != null)
            {
                mgr.DisconnectNotice = "房主已关闭房间。";
                mgr.LocalDisconnectRequested = true;
            }
            RoomHud.Instance?.ShowNotice("房主已关闭房间", "请创建自己的房间，或等待其他房主开放房间。");
        }
    }
}
