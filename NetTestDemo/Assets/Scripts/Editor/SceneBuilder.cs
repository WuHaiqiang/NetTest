// -----------------------------------------------------------------------------
// NetTest > Build Scene
//
// 一键生成整个项目所需的场景与预制体：
//   * Assets/Prefabs/           ball.prefab / networkPlayer.prefab / roomController.prefab
//   * Assets/Prefabs/Materials/ URP Lit 材质 + PhysicsMaterial
//   * Assets/Scenes/BallRoom.unity  房间场景（地面/墙/障碍物/灯光/出生点/NetworkManager）
//   * 自动把 BallRoom 加入 Build Settings
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NetTest.EditorTools
{
    public static class SceneBuilder
    {
        const string RootFolder = "Assets/Prefabs";
        const string MaterialFolder = RootFolder + "/Materials";
        const string SceneFolder = "Assets/Scenes";
        const string ScenePath = SceneFolder + "/BallRoom.unity";

        const string BallPrefabPath = RootFolder + "/ball.prefab";
        const string PlayerPrefabPath = RootFolder + "/networkPlayer.prefab";
        const string RoomPrefabPath = RootFolder + "/roomController.prefab";

        // room layout
        const float RoomHalf = 12f;   // 地面半宽 -> 24x24 的房间
        const float WallHeight = 4f;
        const float WallThickness = 0.5f;

        [MenuItem("NetTest/Build Scene", false, 10)]
        public static void Build()
        {
            if (!EditorUtility.DisplayDialog("NetTest",
                    "将生成/覆盖 Assets/Scenes/BallRoom.unity 与 Assets/Prefabs 下的预制体。\n继续？",
                    "生成", "取消"))
                return;

            EnsureFolders();

            PhysicsMaterial ballPhysics = EnsureBallPhysicsMaterial();
            PhysicsMaterial playerPhysics = EnsurePlayerPhysicsMaterial();

            Material floorMat = URPColor("M_Floor", new Color(0.22f, 0.24f, 0.28f), 0.15f);
            Material wallMat = URPColor("M_Wall", new Color(0.42f, 0.45f, 0.52f), 0.2f);
            Material obstacleMat = URPColor("M_Obstacle", new Color(0.78f, 0.52f, 0.22f), 0.3f);
            Material ballMat = URPColor("M_Ball", new Color(0.95f, 0.35f, 0.25f), 0.35f);
            Material playerMat = URPColor("M_Player", new Color(0.28f, 0.62f, 0.95f), 0.2f);

            GameObject ballPrefab = BuildBallPrefab(ballMat, ballPhysics);
            GameObject playerPrefab = BuildPlayerPrefab(playerMat, playerPhysics);
            GameObject roomPrefab = BuildRoomControllerPrefab(ballPrefab);

            BuildScene(floorMat, wallMat, obstacleMat, ballPrefab, playerPrefab, roomPrefab);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[NetTest] 场景生成完成：" + ScenePath);
        }

        // folders & assets ////////////////////////////////////////////////////
        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder(RootFolder, "Materials");
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets", "Scenes");
        }

        static Material URPColor(string name, Color color, float smoothness)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                    shader = Shader.Find("Standard");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static PhysicsMaterial EnsureBallPhysicsMaterial()
        {
            string path = $"{MaterialFolder}/PM_Ball.physicMaterial";
            PhysicsMaterial material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (material == null)
            {
                material = new PhysicsMaterial("PM_Ball");
                AssetDatabase.CreateAsset(material, path);
            }
            material.bounciness = 0.6f;
            material.bounceCombine = PhysicsMaterialCombine.Maximum;
            material.dynamicFriction = 0.25f;
            material.staticFriction = 0.25f;
            material.frictionCombine = PhysicsMaterialCombine.Average;
            EditorUtility.SetDirty(material);
            return material;
        }

        static PhysicsMaterial EnsurePlayerPhysicsMaterial()
        {
            string path = $"{MaterialFolder}/PM_Player.physicMaterial";
            PhysicsMaterial material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (material == null)
            {
                material = new PhysicsMaterial("PM_Player");
                AssetDatabase.CreateAsset(material, path);
            }
            // 无摩擦：避免贴着墙走时被卡住，移动完全由脚本控制
            material.bounciness = 0f;
            material.bounceCombine = PhysicsMaterialCombine.Minimum;
            material.dynamicFriction = 0f;
            material.staticFriction = 0f;
            material.frictionCombine = PhysicsMaterialCombine.Minimum;
            EditorUtility.SetDirty(material);
            return material;
        }

        // prefabs /////////////////////////////////////////////////////////////
        static GameObject BuildBallPrefab(Material material, PhysicsMaterial physics)
        {
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "ball";
            ball.transform.localScale = Vector3.one; // 半径 0.5

            SphereCollider collider = ball.GetComponent<SphereCollider>();
            collider.material = physics;

            Rigidbody body = ball.GetComponent<Rigidbody>();
            if (body == null) body = ball.AddComponent<Rigidbody>();
            body.mass = 1f;
            body.linearDamping = 0.12f;
            body.angularDamping = 0.08f;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.constraints = RigidbodyConstraints.None;

            ball.GetComponent<MeshRenderer>().sharedMaterial = material;
            ball.AddComponent<NetworkIdentity>();
            ball.AddComponent<NetworkBall>();

            GameObject prefab = SavePrefab(ball, BallPrefabPath);
            Object.DestroyImmediate(ball);
            return prefab;
        }

        static GameObject BuildPlayerPrefab(Material material, PhysicsMaterial physics)
        {
            GameObject root = new GameObject("networkPlayer", typeof(NetworkIdentity));
            root.layer = LayerMask.NameToLayer("Default");

            CapsuleCollider capsule = root.AddComponent<CapsuleCollider>();
            capsule.radius = 0.35f;
            capsule.height = 1.8f;
            capsule.center = Vector3.zero;
            capsule.material = physics;

            Rigidbody body = root.AddComponent<Rigidbody>();
            body.mass = 70f;
            body.linearDamping = 0f;
            body.angularDamping = 0f;
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            NetFirstPersonPlayer player = root.AddComponent<NetFirstPersonPlayer>();

            // 第三人称可以看到的模型（第一人称时会被隐藏）
            GameObject model = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            model.name = "Model";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            model.GetComponent<MeshRenderer>().sharedMaterial = material;
            Object.DestroyImmediate(model.GetComponent<CapsuleCollider>());

            // 摄像机挂在准星锚点下
            GameObject anchor = new GameObject("CameraAnchor");
            anchor.transform.SetParent(root.transform, false);
            anchor.transform.localPosition = new Vector3(0f, 0.68f, 0f);

            GameObject cameraObject = new GameObject("Camera", typeof(Camera));
            cameraObject.transform.SetParent(anchor.transform, false);
            cameraObject.transform.localPosition = Vector3.zero;
            cameraObject.transform.localRotation = Quaternion.identity;
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.fieldOfView = 75f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 500f;
            camera.enabled = false; // 只有本地玩家会打开

            player.cameraAnchor = anchor.transform;
            player.modelRoot = model.transform;
            player.playerCamera = camera;
            EditorUtility.SetDirty(player);

            GameObject prefab = SavePrefab(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static GameObject BuildRoomControllerPrefab(GameObject ballPrefab)
        {
            GameObject root = new GameObject("roomController", typeof(NetworkIdentity));

            BallSpawner spawner = root.AddComponent<BallSpawner>();
            spawner.ballPrefab = ballPrefab;
            spawner.spawnInterval = 15f;
            spawner.initialBallCount = 1;
            spawner.maxBalls = 24;
            spawner.spawnHeight = 1.2f;
            spawner.spawnCenter = Vector3.zero;
            spawner.spawnExtents = new Vector3(RoomHalf - 3f, 0f, RoomHalf - 3f);
            EditorUtility.SetDirty(spawner);

            GameObject prefab = SavePrefab(root, RoomPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static GameObject SavePrefab(GameObject instance, string path)
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            EditorUtility.SetDirty(prefab);
            return prefab;
        }

        // scene ///////////////////////////////////////////////////////////////
        static void BuildScene(Material floorMat, Material wallMat, Material obstacleMat,
                               GameObject ballPrefab, GameObject playerPrefab, GameObject roomPrefab)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject room = new GameObject("Room");

            // floor
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(room.transform, false);
            floor.transform.localScale = new Vector3(RoomHalf * 2f, 1f, RoomHalf * 2f);
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = floorMat;

            // walls: 4 面 + 顶部不需要
            CreateWall(room.transform, wallMat, "Wall_North", new Vector3(0f, WallHeight / 2f, RoomHalf + WallThickness / 2f), new Vector3(RoomHalf * 2f + WallThickness * 2f, WallHeight, WallThickness));
            CreateWall(room.transform, wallMat, "Wall_South", new Vector3(0f, WallHeight / 2f, -RoomHalf - WallThickness / 2f), new Vector3(RoomHalf * 2f + WallThickness * 2f, WallHeight, WallThickness));
            CreateWall(room.transform, wallMat, "Wall_East", new Vector3(RoomHalf + WallThickness / 2f, WallHeight / 2f, 0f), new Vector3(WallThickness, WallHeight, RoomHalf * 2f + WallThickness * 2f));
            CreateWall(room.transform, wallMat, "Wall_West", new Vector3(-RoomHalf - WallThickness / 2f, WallHeight / 2f, 0f), new Vector3(WallThickness, WallHeight, RoomHalf * 2f + WallThickness * 2f));

            // obstacles: 让球能“真实弹回”
            CreateObstacle(room.transform, obstacleMat, "Obstacle_1", new Vector3(-5f, 0.75f, -3f), new Vector3(3f, 1.5f, 1f));
            CreateObstacle(room.transform, obstacleMat, "Obstacle_2", new Vector3(5.5f, 0.75f, 2.5f), new Vector3(1f, 1.5f, 5f));
            CreateObstacle(room.transform, obstacleMat, "Obstacle_3", new Vector3(-2f, 0.5f, 6f), new Vector3(4f, 1f, 1f));
            CreateObstacle(room.transform, obstacleMat, "Obstacle_4", new Vector3(7f, 1f, -6.5f), new Vector3(1f, 2f, 1f));
            CreateObstacle(room.transform, obstacleMat, "Obstacle_5", new Vector3(-7.5f, 1f, 7f), new Vector3(1f, 2f, 1f));
            CreateObstacle(room.transform, obstacleMat, "Obstacle_6", new Vector3(0.5f, 0.6f, -8f), new Vector3(5f, 1.2f, 1f));

            // lighting
            GameObject light = new GameObject("Directional Light", typeof(Light));
            Light lightComponent = light.GetComponent<Light>();
            lightComponent.type = LightType.Directional;
            lightComponent.intensity = 1.15f;
            lightComponent.color = Color.white;
            light.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
            light.transform.position = new Vector3(5f, 12f, -5f);

            // spawn points
            GameObject spawnParent = new GameObject("SpawnPoints");
            Vector3[] spawnPositions =
            {
                new Vector3(-6f, 1.2f, -6f),
                new Vector3(6f, 1.2f, -6f),
                new Vector3(-6f, 1.2f, 6f),
                new Vector3(6f, 1.2f, 6f)
            };
            List<Transform> spawnPoints = new List<Transform>();
            for (int i = 0; i < spawnPositions.Length; i++)
            {
                GameObject point = new GameObject($"Spawn_{i + 1}");
                point.transform.SetParent(spawnParent.transform, false);
                point.transform.position = spawnPositions[i];
                Vector3 lookAt = new Vector3(-spawnPositions[i].x, 0f, -spawnPositions[i].z);
                point.transform.rotation = lookAt.sqrMagnitude > 0.01f ? Quaternion.LookRotation(lookAt) : Quaternion.identity;
                spawnPoints.Add(point.transform);
            }

            // lobby camera
            GameObject lobbyCameraObject = new GameObject("LobbyCamera", typeof(Camera), typeof(AudioListener));
            Camera lobbyCamera = lobbyCameraObject.GetComponent<Camera>();
            lobbyCamera.tag = "MainCamera";
            lobbyCamera.fieldOfView = 60f;
            lobbyCamera.transform.position = new Vector3(0f, 14f, -16f);
            lobbyCamera.transform.rotation = Quaternion.Euler(38f, 0f, 0f);

            // steam manager (SteamAPI 初始化)
            GameObject steamObject = new GameObject("SteamManager");
            steamObject.AddComponent<SteamManager>();

            // network manager
            GameObject networkObject = new GameObject("NetworkManager");
            RoomNetworkManager manager = networkObject.AddComponent<RoomNetworkManager>();

            SteamP2PTransport steamTransport = networkObject.AddComponent<SteamP2PTransport>();
            Transport localTransport = networkObject.AddComponent<kcp2k.KcpTransport>();

            manager.steamTransport = steamTransport;
            manager.localTransport = localTransport;
            manager.transport = localTransport;
            manager.playerPrefab = playerPrefab;
            manager.roomControllerPrefab = roomPrefab;
            manager.maxPlayers = 4;
            manager.maxConnections = 4;
            manager.sendRate = 30;
            manager.spawnPoints = spawnPoints;
            manager.spawnPrefabs = new List<GameObject> { ballPrefab, playerPrefab, roomPrefab };
            EditorUtility.SetDirty(manager);

            // hud
            RoomHud hud = networkObject.AddComponent<RoomHud>();
            hud.lobbyCamera = lobbyCamera;
            EditorUtility.SetDirty(hud);

            steamObject.AddComponent<SteamLobbyService>();

            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), ScenePath);

            // add to build settings
            List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool exists = false;
            foreach (EditorBuildSettingsScene scene in scenes)
                if (scene.path == ScenePath) exists = true;
            if (!exists)
            {
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
        }

        static void CreateWall(Transform parent, Material material, string name, Vector3 position, Vector3 scale)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(parent, false);
            wall.transform.position = position;
            wall.transform.localScale = scale;
            wall.GetComponent<MeshRenderer>().sharedMaterial = material;
            wall.isStatic = true;
        }

        static void CreateObstacle(Transform parent, Material material, string name, Vector3 position, Vector3 scale)
        {
            GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = name;
            obstacle.transform.SetParent(parent, false);
            obstacle.transform.position = position;
            obstacle.transform.localScale = scale;
            obstacle.GetComponent<MeshRenderer>().sharedMaterial = material;
            obstacle.isStatic = true;
        }
    }
}
