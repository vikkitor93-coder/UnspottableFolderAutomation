using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Unity.Mono;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnspottableExpanded
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.viktordadi.unspottableexpanded";
        public const string PluginName = "Unspottable Expanded";
        public const string PluginVersion = "0.9.8";

        private const string ArenaScene = "level_arena_main";
        private const string StartMenuScene = "menu_start_main";
        private const string StartMenuAiScene = "menu_start_ia";
        private const string GlobalPlayerUiScene = "global_player_ui";
        private const float HalfSize = 8.0f;
        private const float PlayableBound = 7.55f;

        private Assembly _gameAssembly;
        private Type _playerType;
        private Type _botType;
        private GameObject _squareRoot;
        private bool _installed;
        private bool _readyReported;
        private bool _cleanupApplied;
        private float _nextProbe;
        private UnityEngine.Object[] _livePlayers = Array.Empty<UnityEngine.Object>();
        private UnityEngine.Object[] _liveBots = Array.Empty<UnityEngine.Object>();

        // Background QA foundation. Completely dormant unless the process is launched with UE_QA_MODE=1.
        private bool _qaEnabled;
        private bool _qaLowImpact;
        private bool _qaHeadlessRequested;
        private bool _qaHasFocus = true;
        private bool _qaRewiredDumped;
        private int _qaHeartbeat;
        private int _qaTargetFps = 30;
        private float _nextQaTelemetry;
        private float _nextQaObjectRefresh;
        private float _nextQaFileWrite;
        private UnityEngine.Object[] _qaPlayers = Array.Empty<UnityEngine.Object>();
        private UnityEngine.Object[] _qaBots = Array.Empty<UnityEngine.Object>();
        private volatile string _qaSnapshotJson = "{}";
        private string _qaDir;
        private string _qaStatePath;
        private TcpListener _qaListener;
        private Thread _qaThread;
        private volatile bool _qaStop;

        // H1/A2 process-local synthetic input. Active only when UE_QA_INPUT=1.
        private static Plugin _instance;
        private bool _qaInputEnabled;
        private bool _qaInputPatched;
        private int _qaInputPatchCount;
        private long _qaInputInterceptCount;
        private object _qaHarmony;
        private readonly ConcurrentQueue<QaBridgeCommand> _qaCommandQueue = new ConcurrentQueue<QaBridgeCommand>();
        private readonly object _qaInputLock = new object();
        private readonly Dictionary<int, Dictionary<string, QaInjectedAction>> _qaInjected =
            new Dictionary<int, Dictionary<string, QaInjectedAction>>();
        private readonly Dictionary<int, string> _qaActionNamesById = new Dictionary<int, string>();

        // H2: QA-only headless gameplay bootstrap. Never active in a normal launch.
        private bool _qaGameplayEnabled;
        private int _qaGameplayStage;
        private bool _qaGameplayReady;
        private bool _qaGameplayFailed;
        private string _qaGameplayMessage = "disabled";
        private float _qaGameplayAt;
        private float _qaGameplayStageStartedAt;
        private int _qaGameplayAttempts;
        private bool _qaLifecycleProbePreWritten;
        private bool _qaLifecycleProbeMeadowWritten;
        private string _qaLifecycleProbePath;

        private readonly List<ColliderState> _disabledArenaColliders = new List<ColliderState>();

        private readonly List<RendererState> _hiddenRenderers = new List<RendererState>();

        private sealed class RendererState
        {
            public Renderer Renderer;
            public bool Enabled;
        }

        private sealed class ColliderState
        {
            public Collider Collider;
            public bool Enabled;
        }

        private sealed class QaInjectedAction
        {
            public bool AxisOverride;
            public float Axis;
            public float AxisUntil;
            public bool ButtonOverride;
            public bool ButtonHeld;
            public float ButtonUntil;
            public float ButtonDownUntil;
            public float ButtonUpUntil;
        }

        private sealed class QaBridgeCommand
        {
            public string Line;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public string Response;
        }

        private void Awake()
        {
            Logger.LogInfo("========================================");
            Logger.LogInfo("Unspottable Expanded v0.9.8 H2.4 NORMAL LIFECYCLE loaded");
            Logger.LogInfo("SAFE CORE: normal vanilla menus/player selection/start flow preserved. No quick boot, solo start, Rewired manipulation, or movement overrides.");
            Logger.LogInfo("QA FOUNDATION: dormant during normal launches. UE_QA_GAMEPLAY=1 drives the real boot/menu/gameplay lifecycle using process-local player-like Rewired input only.");
            Logger.LogInfo("WHITE SQUARE LOGICAL BOUNDS enabled only when Arena is loaded.");
            Logger.LogInfo("Arena visuals are replaced and known Arena obstacle/interaction colliders are disabled after spawn.");
            Logger.LogInfo("Character positions are constrained to White Square's +/-7.55 playable bounds because Unspottable movement bypasses ordinary wall colliders.");
            Logger.LogInfo("Vanilla Arena NavMesh remains active temporarily.");
            Logger.LogInfo("Geometry: 16x16 white floor, pale-gray perimeter rails, empty interior.");
            Logger.LogInfo("========================================");

            _instance = this;
            _gameAssembly = FindAssembly("Assembly-CSharp");
            CacheGameTypes();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            InitializeQaFoundationIfRequested();

            Scene current = SceneManager.GetActiveScene();
            if (string.Equals(current.name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                Install(current);
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            CleanupQaVerificationWorld();
            StopQaFoundation();
            RemoveQaInputPatches();
            if (ReferenceEquals(_instance, this))
                _instance = null;
            Cleanup(true);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo("Scene loaded: '" + scene.name + "' (buildIndex=" + scene.buildIndex + ", mode=" + mode + ")");

            if (_qaEnabled)
            {
                _nextQaObjectRefresh = 0f;
                _nextQaTelemetry = 0f;
            }

            if (mode == LoadSceneMode.Single && !string.Equals(scene.name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                Cleanup(false);

            if (string.Equals(scene.name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                Install(scene);
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (string.Equals(scene.name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                Cleanup(false);
        }

        private void Update()
        {
            if (_qaEnabled)
            {
                ProcessQaCommands();
                UpdateQaFoundation();
                UpdateQaGameplayHarness();
                UpdateQaVerification();
            }

            if (!_installed)
                return;

            if (!string.Equals(SceneManager.GetActiveScene().name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                return;

            if (Time.unscaledTime >= _nextProbe)
            {
                _nextProbe = Time.unscaledTime + 1.5f;
                ProbeCrowd();
            }
        }

        private void LateUpdate()
        {
            if (!_installed || !_readyReported)
                return;

            if (!string.Equals(SceneManager.GetActiveScene().name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                return;

            ClampCharacters(_livePlayers);
            ClampCharacters(_liveBots);
        }

        private void Install(Scene arenaScene)
        {
            if (_installed || _squareRoot != null)
                return;

            try
            {
                Logger.LogInfo("WHITE SQUARE SAFE: replacing Arena visuals only...");
                HideArenaRenderers(arenaScene);
                LogArenaColliders(arenaScene);

                _squareRoot = new GameObject("UE_WhiteSquareRoot");
                SceneManager.MoveGameObjectToScene(_squareRoot, arenaScene);

                BuildFloor();
                BuildRails();

                _installed = true;
                _readyReported = false;
                _cleanupApplied = false;
            _livePlayers = Array.Empty<UnityEngine.Object>();
            _liveBots = Array.Empty<UnityEngine.Object>();
                _nextProbe = Time.unscaledTime + 0.5f;

                Logger.LogInfo("WHITE SQUARE CLEANUP SUCCESS: visual/bootstrap geometry installed.");
                Logger.LogInfo("WHITE SQUARE CLEANUP: waiting until players/bots exist before disabling Arena-specific obstacles.");
                Logger.LogInfo("WHITE SQUARE SAFE: waiting for local players and bots to spawn...");
            }
            catch (Exception ex)
            {
                Logger.LogError("WHITE SQUARE SAFE bootstrap failed: " + ex);
                Cleanup(true);
            }
        }

        private void HideArenaRenderers(Scene arenaScene)
        {
#pragma warning disable CS0618
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
#pragma warning restore CS0618

            int hidden = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.gameObject.scene != arenaScene)
                    continue;

                _hiddenRenderers.Add(new RendererState { Renderer = renderer, Enabled = renderer.enabled });
                if (renderer.enabled)
                {
                    renderer.enabled = false;
                    hidden++;
                }
            }

            Logger.LogInfo("WHITE SQUARE SAFE: hid " + hidden + " Arena renderers; no Arena colliders were changed.");
        }

        private void LogArenaColliders(Scene arenaScene)
        {
#pragma warning disable CS0618
            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);
#pragma warning restore CS0618
            int count = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || c.gameObject.scene != arenaScene || !c.enabled)
                    continue;
                count++;
                Bounds b = c.bounds;
                Logger.LogInfo("ARENA COLLIDER " + count + ": name='" + c.gameObject.name + "' type=" + c.GetType().Name +
                    " center=(" + b.center.x.ToString("F2") + "," + b.center.y.ToString("F2") + "," + b.center.z.ToString("F2") + ")" +
                    " size=(" + b.size.x.ToString("F2") + "," + b.size.y.ToString("F2") + "," + b.size.z.ToString("F2") + ")");
            }
            Logger.LogInfo("WHITE SQUARE SAFE: observed " + count + " enabled Arena colliders for later selective cleanup.");
        }

        private void BuildFloor()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "White Square Floor";
            floor.transform.SetParent(_squareRoot.transform, false);
            floor.transform.localPosition = new Vector3(0f, -0.12f, 0f);
            floor.transform.localScale = new Vector3(16f, 0.24f, 16f);

            Collider collider = floor.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider); // visual only; vanilla Arena ground remains authoritative in this safe build.

            Renderer renderer = floor.GetComponent<Renderer>();
            if (renderer != null)
                ApplySimpleMaterial(renderer, Color.white);
        }

        private void BuildRails()
        {
            Color railColor = new Color(0.77f, 0.81f, 0.80f);

            for (int side = -1; side <= 1; side += 2)
            {
                for (int n = -8; n <= 8; n += 2)
                {
                    CreateVisualPart(PrimitiveType.Cylinder, "Rail post Z", new Vector3(n, 0.48f, side * HalfSize),
                        new Vector3(0.11f, 0.48f, 0.11f), railColor);

                    if (n != -8 && n != 8)
                    {
                        CreateVisualPart(PrimitiveType.Cylinder, "Rail post X", new Vector3(side * HalfSize, 0.48f, n),
                            new Vector3(0.11f, 0.48f, 0.11f), railColor);
                    }
                }

                float[] heights = { 0.36f, 0.77f };
                for (int i = 0; i < heights.Length; i++)
                {
                    float h = heights[i];
                    CreateVisualPart(PrimitiveType.Cube, "Rail Z", new Vector3(0f, h, side * HalfSize),
                        new Vector3(16f, 0.07f, 0.07f), railColor);
                    CreateVisualPart(PrimitiveType.Cube, "Rail X", new Vector3(side * HalfSize, h, 0f),
                        new Vector3(0.07f, 0.07f, 16f), railColor);
                }
            }
        }

        private void BuildBoundaryWalls()
        {
            const float thickness = 0.18f;
            const float height = 2.5f;
            float edge = HalfSize + thickness * 0.5f;

            CreateInvisibleWall("White Square Boundary North", new Vector3(0f, height * 0.5f, edge), new Vector3(16.5f, height, thickness));
            CreateInvisibleWall("White Square Boundary South", new Vector3(0f, height * 0.5f, -edge), new Vector3(16.5f, height, thickness));
            CreateInvisibleWall("White Square Boundary East", new Vector3(edge, height * 0.5f, 0f), new Vector3(thickness, height, 16.5f));
            CreateInvisibleWall("White Square Boundary West", new Vector3(-edge, height * 0.5f, 0f), new Vector3(thickness, height, 16.5f));
        }

        private GameObject CreateVisualPart(PrimitiveType primitive, string name, Vector3 position, Vector3 scale, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(_squareRoot.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;

            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null)
                ApplySimpleMaterial(renderer, color);

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            return part;
        }

        private void CreateInvisibleWall(string name, Vector3 position, Vector3 scale)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(_squareRoot.transform, false);
            wall.transform.localPosition = position;
            wall.transform.localScale = scale;

            Renderer renderer = wall.GetComponent<Renderer>();
            if (renderer != null)
                renderer.enabled = false;
        }

        private static void ApplySimpleMaterial(Renderer renderer, Color color)
        {
            renderer.material.color = color;
            if (renderer.material.HasProperty("_Glossiness"))
                renderer.material.SetFloat("_Glossiness", 0f);
            if (renderer.material.HasProperty("_Smoothness"))
                renderer.material.SetFloat("_Smoothness", 0f);
        }

        private void ProbeCrowd()
        {
            if (_gameAssembly == null)
                _gameAssembly = FindAssembly("Assembly-CSharp");
            CacheGameTypes();

            UnityEngine.Object[] players = FindLiveObjects(_playerType);
            UnityEngine.Object[] bots = FindLiveObjects(_botType);

            if (players.Length > 0)
                _livePlayers = players;
            if (bots.Length > 0)
                _liveBots = bots;

            if (!_readyReported && players.Length > 0 && bots.Length > 0)
            {
                _readyReported = true;

                if (!_cleanupApplied)
                    DisableKnownArenaObstacles();

                Logger.LogInfo("WHITE SQUARE BOUNDS READY: players=" + players.Length + ", bots=" + bots.Length +
                    ", disabledArenaObstacles=" + _disabledArenaColliders.Count + ", playableBound=+/-" + PlayableBound.ToString("F2") + ".");
                Logger.LogInfo("WHITE SQUARE TEST: walk directly into each rail. Players/bots should remain inside the square even though the rails themselves are visual-only.");
            }
            else if (!_readyReported)
            {
                Logger.LogInfo("WHITE SQUARE CLEANUP: waiting... players=" + players.Length + ", bots=" + bots.Length + ".");
            }
        }


        private void DisableKnownArenaObstacles()
        {
            _cleanupApplied = true;

#pragma warning disable CS0618
            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);
#pragma warning restore CS0618

            int disabled = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || !c.enabled || !string.Equals(c.gameObject.scene.name, ArenaScene, StringComparison.OrdinalIgnoreCase))
                    continue;

                string n = c.gameObject.name ?? string.Empty;
                bool remove =
                    n.StartsWith("colider_statue", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "hit_box", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "interaction_box", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n, "Cube", StringComparison.OrdinalIgnoreCase);

                if (!remove)
                    continue;

                _disabledArenaColliders.Add(new ColliderState { Collider = c, Enabled = c.enabled });
                c.enabled = false;
                disabled++;
                Logger.LogInfo("WHITE SQUARE CLEANUP: disabled Arena collider '" + n + "' (" + c.GetType().Name + ").");
            }

            Logger.LogInfo("WHITE SQUARE CLEANUP: disabled " + disabled +
                " known Arena obstacle/interaction colliders after crowd initialization.");
            Logger.LogInfo("WHITE SQUARE CLEANUP: retained Floor, coliders_floor, OutsideArea, BotPNJ and Arena NavMesh for stability.");
        }

        private static void ClampCharacters(UnityEngine.Object[] objects)
        {
            if (objects == null)
                return;

            for (int i = 0; i < objects.Length; i++)
            {
                Component component = objects[i] as Component;
                if (component == null)
                    continue;

                Transform t = component.transform;
                Vector3 p = t.position;
                float x = Mathf.Clamp(p.x, -PlayableBound, PlayableBound);
                float z = Mathf.Clamp(p.z, -PlayableBound, PlayableBound);

                if (Mathf.Abs(x - p.x) > 0.0001f || Mathf.Abs(z - p.z) > 0.0001f)
                    t.position = new Vector3(x, p.y, z);
            }
        }

        private UnityEngine.Object[] FindLiveObjects(Type type)
        {
            if (type == null)
                return Array.Empty<UnityEngine.Object>();
#pragma warning disable CS0618
            return UnityEngine.Object.FindObjectsOfType(type);
#pragma warning restore CS0618
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            _qaHasFocus = hasFocus;
        }

        private void InitializeQaFoundationIfRequested()
        {
            _qaEnabled = ReadEnvBool("UE_QA_MODE", false);
            if (!_qaEnabled)
                return;

            _qaLowImpact = ReadEnvBool("UE_QA_LOW_IMPACT", true);
            _qaHeadlessRequested = ReadEnvBool("UE_QA_HEADLESS", false);
            _qaInputEnabled = ReadEnvBool("UE_QA_INPUT", false);
            _qaGameplayEnabled = ReadEnvBool("UE_QA_GAMEPLAY", false);
            _qaTargetFps = ReadEnvInt("UE_QA_FPS", 30, 5, 120);

            if (_qaGameplayEnabled && !_qaInputEnabled)
                _qaInputEnabled = true;

            Application.runInBackground = true;
            if (_qaLowImpact)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = _qaTargetFps;
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _qaDir = Path.Combine(local, "UnspottableExpanded", "QA");
            Directory.CreateDirectory(_qaDir);
            _qaStatePath = Path.Combine(_qaDir, "state.json");
            _qaLifecycleProbePath = Path.Combine(_qaDir, "lifecycle-probe.txt");

            Logger.LogInfo("QA FOUNDATION ACTIVE: process-local QA bridge enabled on 127.0.0.1:24783.");
            Logger.LogInfo("QA MODE: " + (_qaHeadlessRequested ? "TRUE HEADLESS LOGIC TEST" : "BACKGROUND RENDERED") +
                "; Application.isBatchMode=" + Application.isBatchMode +
                "; graphicsDevice='" + SafeGraphicsDeviceName() + "'.");
            Logger.LogInfo("QA BACKGROUND MODE: Application.runInBackground=True; lowImpact=" + _qaLowImpact + "; targetFps=" + (_qaLowImpact ? _qaTargetFps.ToString() : "unchanged") + ".");
            Logger.LogInfo(@"QA STATE FILE: %LOCALAPPDATA%\UnspottableExpanded\QA\state.json");

            if (_qaInputEnabled)
            {
                BuildQaActionIdMap();
                InstallQaInputPatches();
                Logger.LogInfo("QA INPUT HARNESS: process-local synthetic input " +
                    (_qaInputPatched ? "READY" : "NOT READY") + "; patches=" + _qaInputPatchCount + ".");
            }

            if (_qaGameplayEnabled)
            {
                _qaGameplayStage = 0;
                _qaGameplayReady = false;
                _qaGameplayFailed = false;
                _qaGameplayMessage = "starting";
                _qaGameplayAt = Time.unscaledTime + 0.75f;
                _qaGameplayStageStartedAt = Time.unscaledTime;
                _qaLifecycleProbePreWritten = false;
                _qaLifecycleProbeMeadowWritten = false;
                try
                {
                    if (!string.IsNullOrEmpty(_qaLifecycleProbePath) && File.Exists(_qaLifecycleProbePath))
                        File.Delete(_qaLifecycleProbePath);
                }
                catch { }
                Logger.LogInfo("QA H2 GAMEPLAY HARNESS: ENABLED. H2 observes the real lifecycle and supplies only player-like Rewired input; no scene loads, player spawning, or manager initialization.");
            }

            StartQaBridge();
            _nextQaTelemetry = 0f;
            _nextQaObjectRefresh = 0f;
            _nextQaFileWrite = 0f;
        }

        private void UpdateQaFoundation()
        {
            if (_qaLowImpact)
            {
                if (QualitySettings.vSyncCount != 0)
                    QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != _qaTargetFps)
                    Application.targetFrameRate = _qaTargetFps;
            }

            float now = Time.unscaledTime;
            if (_qaInputEnabled)
            {
                ExpireQaInput(now);
                if (_qaActionNamesById.Count == 0 && SafeRewiredReady())
                    BuildQaActionIdMap();
            }
            if (now >= _nextQaObjectRefresh)
            {
                _nextQaObjectRefresh = now + 2.0f;
                RefreshQaObjects();
                DumpRewiredMetadataOnce();
            }

            if (now < _nextQaTelemetry)
                return;

            _nextQaTelemetry = now + 0.25f;
            _qaHeartbeat++;
            _qaSnapshotJson = BuildQaSnapshot();

            if (now >= _nextQaFileWrite)
            {
                _nextQaFileWrite = now + 1.0f;
                try
                {
                    File.WriteAllText(_qaStatePath, _qaSnapshotJson, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("QA STATE FILE write failed: " + ex.Message);
                }
            }
        }

        private void RefreshQaObjects()
        {
            if (_gameAssembly == null)
                _gameAssembly = FindAssembly("Assembly-CSharp");
            CacheGameTypes();

            try
            {
                _qaPlayers = FindLiveObjects(_playerType);
                _qaBots = FindLiveObjects(_botType);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("QA object refresh failed: " + ex.Message);
                _qaPlayers = Array.Empty<UnityEngine.Object>();
                _qaBots = Array.Empty<UnityEngine.Object>();
            }
        }

        private string BuildQaSnapshot()
        {
            Scene scene = SceneManager.GetActiveScene();
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonString(sb, "pluginVersion", PluginVersion); sb.Append(',');
            AppendJsonNumber(sb, "heartbeat", _qaHeartbeat); sb.Append(',');
            AppendJsonString(sb, "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); sb.Append(',');
            AppendJsonString(sb, "scene", scene.name ?? string.Empty); sb.Append(',');
            AppendJsonNumber(sb, "sceneBuildIndex", scene.buildIndex); sb.Append(',');
            AppendJsonBool(sb, "focused", _qaHasFocus); sb.Append(',');
            AppendJsonString(sb, "qaMode", _qaHeadlessRequested ? "headless" : "background-rendered"); sb.Append(',');
            AppendJsonBool(sb, "headlessRequested", _qaHeadlessRequested); sb.Append(',');
            AppendJsonBool(sb, "isBatchMode", Application.isBatchMode); sb.Append(',');
            AppendJsonString(sb, "graphicsDevice", SafeGraphicsDeviceName()); sb.Append(',');
            AppendJsonBool(sb, "runInBackground", Application.runInBackground); sb.Append(',');
            AppendJsonNumber(sb, "targetFps", Application.targetFrameRate); sb.Append(',');
            AppendJsonBool(sb, "qaInputEnabled", _qaInputEnabled); sb.Append(',');
            AppendJsonBool(sb, "qaInputPatched", _qaInputPatched); sb.Append(',');
            AppendJsonNumber(sb, "qaInputPatchCount", _qaInputPatchCount); sb.Append(',');
            AppendJsonLong(sb, "qaInputInterceptCount", _qaInputInterceptCount); sb.Append(',');
            AppendJsonNumber(sb, "qaActiveInputs", CountQaActiveInputs()); sb.Append(',');
            AppendJsonBool(sb, "qaGameplayEnabled", _qaGameplayEnabled); sb.Append(',');
            AppendJsonNumber(sb, "qaGameplayStage", _qaGameplayStage); sb.Append(',');
            AppendJsonBool(sb, "qaGameplayReady", _qaGameplayReady); sb.Append(',');
            AppendJsonBool(sb, "qaGameplayFailed", _qaGameplayFailed); sb.Append(',');
            AppendJsonString(sb, "qaGameplayMessage", _qaGameplayMessage ?? string.Empty); sb.Append(',');
            AppendJsonFloat(sb, "timeScale", Time.timeScale); sb.Append(',');
            AppendJsonFloat(sb, "unscaledTime", Time.unscaledTime); sb.Append(',');
            AppendJsonBool(sb, "whiteSquareInstalled", _installed); sb.Append(',');
            AppendJsonBool(sb, "rewiredReady", SafeRewiredReady()); sb.Append(',');
            AppendJsonNumber(sb, "rewiredPlayerCount", SafeRewiredPlayerCount()); sb.Append(',');
            AppendJsonNumber(sb, "controllerCount", SafeRewiredControllerCount()); sb.Append(',');
            AppendJsonNumber(sb, "playerCount", CountLiveComponents(_qaPlayers)); sb.Append(',');
            AppendJsonNumber(sb, "botCount", CountLiveComponents(_qaBots)); sb.Append(',');
            sb.Append("\"players\":[");
            AppendQaObjects(sb, _qaPlayers);
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static int CountLiveComponents(UnityEngine.Object[] objects)
        {
            if (objects == null)
                return 0;
            int count = 0;
            for (int i = 0; i < objects.Length; i++)
                if (objects[i] is Component)
                    count++;
            return count;
        }

        private static void AppendQaObjects(StringBuilder sb, UnityEngine.Object[] objects)
        {
            if (objects == null)
                return;

            List<Component> components = new List<Component>();
            for (int i = 0; i < objects.Length; i++)
            {
                Component c = objects[i] as Component;
                if (c != null)
                    components.Add(c);
            }
            components.Sort((a, b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));

            for (int i = 0; i < components.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Component c = components[i];
                Vector3 p = c.transform.position;
                sb.Append('{');
                AppendJsonNumber(sb, "instanceId", c.gameObject.GetInstanceID()); sb.Append(',');
                AppendJsonString(sb, "name", c.gameObject.name ?? string.Empty); sb.Append(',');
                AppendJsonBool(sb, "active", c.gameObject.activeInHierarchy); sb.Append(',');
                AppendJsonFloat(sb, "x", p.x); sb.Append(',');
                AppendJsonFloat(sb, "y", p.y); sb.Append(',');
                AppendJsonFloat(sb, "z", p.z);
                sb.Append('}');
            }
        }

        private void DumpRewiredMetadataOnce()
        {
            if (_qaRewiredDumped || !SafeRewiredReady())
                return;

            try
            {
                StringBuilder sb = new StringBuilder(4096);
                sb.Append('{');
                AppendJsonString(sb, "rewiredVersion", ReInput.programVersion ?? string.Empty); sb.Append(',');
                AppendJsonNumber(sb, "playerCount", ReInput.players.playerCount); sb.Append(',');
                sb.Append("\"players\":[");
                for (int i = 0; i < ReInput.players.playerCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    Player p = ReInput.players.Players[i];
                    sb.Append('{');
                    AppendJsonNumber(sb, "id", p.id); sb.Append(',');
                    AppendJsonBool(sb, "isPlaying", p.isPlaying);
                    sb.Append('}');
                }
                sb.Append("],\"actions\":[");
                bool first = true;
                foreach (InputAction action in ReInput.mapping.Actions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{');
                    AppendJsonNumber(sb, "id", action.id); sb.Append(',');
                    AppendJsonString(sb, "name", action.name ?? string.Empty); sb.Append(',');
                    AppendJsonString(sb, "descriptiveName", action.descriptiveName ?? string.Empty); sb.Append(',');
                    AppendJsonString(sb, "type", action.type.ToString());
                    sb.Append('}');
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(_qaDir, "rewired-metadata.json"), sb.ToString(), Encoding.UTF8);
                _qaRewiredDumped = true;
                Logger.LogInfo("QA REWIRED METADATA: dumped read-only Player/Action definitions for the automation controller work.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("QA Rewired metadata dump failed: " + ex.Message);
            }
        }

        private static string SafeGraphicsDeviceName()
        {
            try
            {
                return SystemInfo.graphicsDeviceType.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool SafeRewiredReady()
        {
            try { return ReInput.isReady; }
            catch { return false; }
        }

        private static int SafeRewiredPlayerCount()
        {
            try { return ReInput.isReady ? ReInput.players.playerCount : 0; }
            catch { return 0; }
        }

        private static int SafeRewiredControllerCount()
        {
            try { return ReInput.isReady ? ReInput.controllers.controllerCount : 0; }
            catch { return 0; }
        }

        private void StartQaBridge()
        {
            try
            {
                _qaStop = false;
                _qaListener = new TcpListener(IPAddress.Loopback, 24783);
                _qaListener.Start(4);
                _qaThread = new Thread(QaBridgeLoop);
                _qaThread.IsBackground = true;
                _qaThread.Name = "UE-QA-Bridge";
                _qaThread.Start();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("QA BRIDGE could not start: " + ex.Message + ". state.json telemetry will still be available.");
            }
        }

        private void QaBridgeLoop()
        {
            while (!_qaStop)
            {
                try
                {
                    if (_qaListener == null || !_qaListener.Pending())
                    {
                        Thread.Sleep(75);
                        continue;
                    }

                    using (TcpClient client = _qaListener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 1500;
                        client.SendTimeout = 1500;
                        using (NetworkStream stream = client.GetStream())
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                        {
                            writer.AutoFlush = true;
                            string command = reader.ReadLine();
                            if (string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine("{\"ok\":true,\"pong\":true}");
                            else if (string.Equals(command, "INFO", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine("{\"ok\":true,\"protocol\":4,\"mode\":\"qa\",\"headless\":" + (_qaHeadlessRequested ? "true" : "false") + ",\"inputEnabled\":" + (_qaInputEnabled ? "true" : "false") + ",\"gameplayEnabled\":" + (_qaGameplayEnabled ? "true" : "false") + ",\"qaVerification\":true,\"port\":24783}");
                            else if (!string.IsNullOrEmpty(command) && command.StartsWith("INPUT ", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine(HandleQaBridgeInputCommand(command));
                            else if (!string.IsNullOrEmpty(command) && command.StartsWith("QA ", StringComparison.OrdinalIgnoreCase))
                                writer.WriteLine(HandleQaBridgeVerificationCommand(command));
                            else
                                writer.WriteLine(_qaSnapshotJson ?? "{}");
                        }
                    }
                }
                catch (SocketException)
                {
                    if (!_qaStop)
                        Thread.Sleep(100);
                }
                catch
                {
                    if (!_qaStop)
                        Thread.Sleep(100);
                }
            }
        }



        private void UpdateQaGameplayHarness()
        {
            if (!_qaGameplayEnabled || _qaGameplayReady || _qaGameplayFailed)
                return;

            float now = Time.unscaledTime;
            if (now < _qaGameplayAt)
                return;

            try
            {
                string activeScene = SceneManager.GetActiveScene().name ?? string.Empty;

                // H2.4 deliberately never performs a direct scene load and never invokes
                // ControlerManager initialization/assignment helpers. The game owns every
                // lifecycle transition. QA only returns synthetic values from the same
                // Rewired getters normal local-player input uses.
                if (_qaGameplayStage == 0)
                {
                    if (string.Equals(activeScene, StartMenuScene, StringComparison.OrdinalIgnoreCase))
                    {
                        SetQaGameplayStage(1, "normal start menu reached; waiting for native support scenes");
                        _qaGameplayAt = now + 0.35f;
                        return;
                    }

                    string bootAction;
                    TryPressQaLifecycleButton(0, 140, out bootAction,
                        "start", "menu_accept", "action");
                    RetryQaGameplay("waiting for game's normal boot to reach menu_start_main; scene=" + activeScene +
                        (string.IsNullOrEmpty(bootAction) ? string.Empty : "; playerInput=" + bootAction),
                        0.85f, 60f);
                    return;
                }

                if (_qaGameplayStage == 1)
                {
                    if (!string.Equals(activeScene, StartMenuScene, StringComparison.OrdinalIgnoreCase))
                    {
                        RetryQaGameplay("normal start menu not stable; scene=" + activeScene, 0.35f, 15f);
                        return;
                    }

                    bool globalReady = SceneManager.GetSceneByName(GlobalPlayerUiScene).isLoaded;
                    bool aiReady = SceneManager.GetSceneByName(StartMenuAiScene).isLoaded;
                    if (!globalReady || !aiReady || !SafeRewiredReady())
                    {
                        RetryQaGameplay("waiting for game's own support scenes/Rewired: global=" + globalReady +
                            ", ai=" + aiReady + ", rewired=" + SafeRewiredReady(), 0.35f, 25f);
                        return;
                    }

                    if (SafeRewiredPlayerCount() < 2)
                    {
                        RetryQaGameplay("normal two-player selection requires two Rewired player slots; available=" +
                            SafeRewiredPlayerCount(), 0.50f, 20f);
                        return;
                    }

                    Logger.LogInfo("QA H2.4: normal menu/support lifecycle ready. No support scene was loaded by QA.");
                    SetQaGameplayStage(2, "joining P1 through normal player input");
                    _qaGameplayAt = now + 0.25f;
                    return;
                }

                if (_qaGameplayStage == 2)
                {
                    RefreshQaObjects();
                    int menuPlayers = CountLiveComponents(_qaPlayers);
                    if (menuPlayers >= 1)
                    {
                        Logger.LogInfo("QA H2.4: P1 appeared through normal player-selection flow.");
                        SetQaGameplayStage(3, "joining P2 through normal player input");
                        _qaGameplayAt = now + 0.50f;
                        return;
                    }

                    string joinAction;
                    if (!TryPressQaLifecycleButton(0, 180, out joinAction,
                        "JoinGame", "action", "menu_accept", "start"))
                    {
                        RetryQaGameplay("P1 join input unavailable; mapped Rewired join/menu action not found",
                            0.75f, 20f);
                        return;
                    }

                    RetryQaGameplay("waiting for P1 after normal input '" + joinAction + "'", 0.75f, 20f);
                    return;
                }

                if (_qaGameplayStage == 3)
                {
                    RefreshQaObjects();
                    int menuPlayers = CountLiveComponents(_qaPlayers);
                    if (menuPlayers >= 2)
                    {
                        if (!_qaLifecycleProbePreWritten)
                        {
                            _qaLifecycleProbePreWritten = true;
                            WriteQaLifecycleProbe("NORMAL TWO-PLAYER SELECTION COMPLETE");
                        }

                        Logger.LogInfo("QA H2.4: P2 appeared through normal player-selection flow; players=" + menuPlayers + ".");
                        SetQaGameplayStage(4, "using player movement/input to enter the native START flow");
                        _qaGameplayAt = now + 0.65f;
                        return;
                    }

                    string joinAction;
                    if (!TryPressQaLifecycleButton(1, 180, out joinAction,
                        "JoinGame", "action", "menu_accept", "start"))
                    {
                        RetryQaGameplay("P2 join input unavailable; Rewired player 1 or mapped join action unavailable",
                            0.75f, 24f);
                        return;
                    }

                    RetryQaGameplay("waiting for P2 after normal input '" + joinAction + "'", 0.75f, 24f);
                    return;
                }

                if (_qaGameplayStage == 4)
                {
                    if (string.Equals(activeScene, "menu_levels", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogInfo("QA H2.4: native START flow advanced to menu_levels without a QA scene load.");
                        SetQaGameplayStage(5, "selecting the highlighted level through normal menu input");
                        _qaGameplayAt = now + 0.75f;
                        return;
                    }

                    if (!string.Equals(activeScene, StartMenuScene, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsQaGameplayMainScene(activeScene))
                        {
                            SetQaGameplayStage(6, "normal lifecycle reached gameplay; waiting for actors");
                            _qaGameplayAt = now + 0.50f;
                            return;
                        }
                        RetryQaGameplay("waiting for native START transition; scene=" + activeScene, 0.50f, 40f);
                        return;
                    }

                    // The normal Local flow uses a physical START area. We do not move a
                    // transform or send a PlayMaker event. Instead, move the selected
                    // players with ordinary MoveX/MoveY values in a deterministic search.
                    // If the build also exposes a normal start/menu action, pulse it too.
                    DriveQaStartAreaSearch(_qaGameplayAttempts);
                    if ((_qaGameplayAttempts % 4) == 0)
                    {
                        string startAction;
                        TryPressQaLifecycleButton(0, 160, out startAction,
                            "start", "menu_accept", "action");
                    }
                    RetryQaGameplay("walking selected players through the native START area; attempt=" +
                        (_qaGameplayAttempts + 1), 0.80f, 40f);
                    return;
                }

                if (_qaGameplayStage == 5)
                {
                    if (IsQaGameplayMainScene(activeScene))
                    {
                        SetQaGameplayStage(6, "normal lifecycle reached gameplay; waiting for actors");
                        _qaGameplayAt = now + 0.50f;
                        return;
                    }

                    if (!string.Equals(activeScene, "menu_levels", StringComparison.OrdinalIgnoreCase))
                    {
                        RetryQaGameplay("waiting for normal level-selection scene; scene=" + activeScene,
                            0.40f, 20f);
                        return;
                    }

                    string acceptAction;
                    if (!TryPressQaLifecycleButton(0, 180, out acceptAction,
                        "menu_accept", "action", "start"))
                    {
                        RetryQaGameplay("level-selection accept input unavailable", 0.75f, 20f);
                        return;
                    }

                    RetryQaGameplay("waiting for highlighted level after normal input '" + acceptAction + "'",
                        0.90f, 20f);
                    return;
                }

                if (_qaGameplayStage == 6)
                {
                    if (!IsQaGameplayMainScene(activeScene))
                    {
                        RetryQaGameplay("waiting for a real level_*_main gameplay scene; scene=" + activeScene,
                            0.40f, 35f);
                        return;
                    }

                    RefreshQaObjects();
                    int playerCount = CountLiveComponents(_qaPlayers);
                    int botCount = CountLiveComponents(_qaBots);
                    if (playerCount < 2)
                    {
                        RetryQaGameplay("gameplay loaded through normal lifecycle; waiting for both selected players, currently " +
                            playerCount, 0.40f, 30f);
                        return;
                    }

                    if (!_qaLifecycleProbeMeadowWritten)
                    {
                        _qaLifecycleProbeMeadowWritten = true;
                        WriteQaLifecycleProbe("GAMEPLAY READY THROUGH NORMAL LIFECYCLE");
                    }

                    _qaGameplayReady = true;
                    _qaGameplayStage = 7;
                    _qaGameplayMessage = "ready: normal lifecycle scene=" + activeScene +
                        ", players=" + playerCount + ", bots=" + botCount;
                    Logger.LogInfo("QA H2.4 GAMEPLAY READY: normal lifecycle reached '" + activeScene +
                        "'; players=" + playerCount + ", bots=" + botCount + ".");
                    return;
                }
            }
            catch (Exception ex)
            {
                FailQaGameplay("stage " + _qaGameplayStage + " exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool TryPressQaLifecycleButton(int playerId, int ms, out string selectedAction, params string[] candidates)
        {
            selectedAction = string.Empty;
            if (!_qaInputEnabled || !_qaInputPatched || !SafeRewiredReady())
                return false;

            Player player = null;
            try
            {
                player = ReInput.players.GetPlayer(playerId);
            }
            catch { }
            if (player == null)
                return false;

            int count = candidates == null ? 0 : candidates.Length;
            if (count == 0)
                return false;

            int startIndex = _qaGameplayAttempts % count;
            for (int offset = 0; offset < count; offset++)
            {
                string candidate = candidates[(startIndex + offset) % count];
                if (!QaActionExists(candidate))
                    continue;

                PressQaButton(playerId, candidate, ms);
                selectedAction = candidate;
                Logger.LogInfo("QA H2.4 PLAYER INPUT: P" + (playerId + 1) + " pressed '" + candidate + "'.");
                return true;
            }
            return false;
        }

        private bool QaActionExists(string actionName)
        {
            if (string.IsNullOrEmpty(actionName) || !SafeRewiredReady())
                return false;
            try
            {
                foreach (InputAction action in ReInput.mapping.Actions)
                {
                    if (action != null && string.Equals(action.name, actionName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private void DriveQaStartAreaSearch(int attempt)
        {
            float[,] directions = new float[,]
            {
                { 0f, 1f }, { 1f, 0f }, { 0f, -1f }, { -1f, 0f },
                { 0.7f, 0.7f }, { 0.7f, -0.7f }, { -0.7f, -0.7f }, { -0.7f, 0.7f }
            };
            int index = Math.Abs(attempt) % directions.GetLength(0);
            float x = directions[index, 0];
            float y = directions[index, 1];

            for (int playerId = 0; playerId < 2; playerId++)
            {
                if (QaActionExists("MoveX"))
                    SetQaAxis(playerId, "MoveX", x, 650);
                if (QaActionExists("MoveY"))
                    SetQaAxis(playerId, "MoveY", y, 650);
            }
            Logger.LogInfo("QA H2.4 PLAYER INPUT: walking P1/P2 for native START search x=" +
                x.ToString("0.0", CultureInfo.InvariantCulture) + ", y=" +
                y.ToString("0.0", CultureInfo.InvariantCulture) + ".");
        }

        private static bool IsQaGameplayMainScene(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) &&
                sceneName.StartsWith("level_", StringComparison.OrdinalIgnoreCase) &&
                sceneName.EndsWith("_main", StringComparison.OrdinalIgnoreCase);
        }

        private void SetQaGameplayStage(int stage, string message)
        {
            _qaGameplayStage = stage;
            _qaGameplayMessage = message;
            _qaGameplayStageStartedAt = Time.unscaledTime;
            _qaGameplayAttempts = 0;
            Logger.LogInfo("QA H2 stage " + stage + ": " + message);
        }

        private void RetryQaGameplay(string message, float delay, float maxStageSeconds)
        {
            _qaGameplayMessage = message;
            _qaGameplayAttempts++;
            _qaGameplayAt = Time.unscaledTime + delay;
            if (Time.unscaledTime - _qaGameplayStageStartedAt > maxStageSeconds)
                FailQaGameplay("timeout: " + message);
        }

        private void FailQaGameplay(string message)
        {
            _qaGameplayFailed = true;
            _qaGameplayReady = false;
            _qaGameplayMessage = message;
            Logger.LogError("QA H2 GAMEPLAY FAILED: " + message);
        }

        private void WriteQaLifecycleProbe(string label)
        {
            if (string.IsNullOrEmpty(_qaLifecycleProbePath))
                return;

            try
            {
                StringBuilder sb = new StringBuilder(8192);
                sb.AppendLine("============================================================");
                sb.AppendLine("UNSPOTTABLE QA LIFECYCLE PROBE");
                sb.AppendLine("label=" + label);
                sb.AppendLine("utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.AppendLine("scene=" + SceneManager.GetActiveScene().name);
                sb.AppendLine("gameplayStage=" + _qaGameplayStage);
                sb.AppendLine("rewiredReady=" + SafeRewiredReady());
                sb.AppendLine("rewiredPlayerCount=" + SafeRewiredPlayerCount());
                sb.AppendLine("inputInterceptCount=" + Interlocked.Read(ref _qaInputInterceptCount));
                sb.AppendLine();

                sb.AppendLine("[LOADED SCENES]");
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene sc = SceneManager.GetSceneAt(i);
                    sb.AppendLine("  " + sc.name + " loaded=" + sc.isLoaded + " roots=" + sc.rootCount);
                }
                sb.AppendLine();

                if (ReInput.isReady)
                {
                    sb.AppendLine("[REWIRED PLAYERS]");
                    for (int i = 0; i < ReInput.players.playerCount; i++)
                    {
                        Player rp = ReInput.players.GetPlayer(i);
                        if (rp == null)
                            continue;
                        sb.AppendLine("  id=" + rp.id + " isPlaying=" + rp.isPlaying);
                    }
                    sb.AppendLine();
                }

                AppendQaControlerManagerProbe(sb);

                RefreshQaObjects();
                sb.AppendLine("[PLAYER OBJECTS]");
                int shown = 0;
                for (int i = 0; i < _qaPlayers.Length && shown < 4; i++)
                {
                    Component player = _qaPlayers[i] as Component;
                    if (player == null)
                        continue;

                    shown++;
                    GameObject go = player.gameObject;
                    sb.AppendLine("PLAYER id=" + go.GetInstanceID() +
                        " active=" + go.activeInHierarchy + " scene=" + go.scene.name +
                        " pos=" + go.transform.position);

                    MonoBehaviour[] behaviours = go.GetComponents<MonoBehaviour>();
                    for (int b = 0; b < behaviours.Length; b++)
                    {
                        MonoBehaviour mb = behaviours[b];
                        if (mb == null)
                        {
                            sb.AppendLine("    component=<missing/null>");
                            continue;
                        }

                        Type type = mb.GetType();
                        sb.Append("    component=" + type.FullName + " enabled=" + mb.enabled);
                        if (string.Equals(type.Name, "PlayMakerFSM", StringComparison.Ordinal))
                        {
                            string fsmName = SafeReflectString(mb, "FsmName");
                            string activeState = SafeReflectString(mb, "ActiveStateName");
                            if (string.IsNullOrEmpty(activeState))
                            {
                                object fsm = SafeReflectValue(mb, "Fsm");
                                if (fsm != null)
                                    activeState = SafeReflectString(fsm, "ActiveStateName");
                            }
                            sb.Append(" fsm='" + fsmName + "' state='" + activeState + "'");
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }

                File.AppendAllText(_qaLifecycleProbePath, sb.ToString(), Encoding.UTF8);
                Logger.LogInfo("QA H2.4: wrote targeted lifecycle probe '" + label + "'.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("QA H2.4 lifecycle probe failed: " + ex.Message);
            }
        }

        private void AppendQaControlerManagerProbe(StringBuilder sb)
        {
            sb.AppendLine("[CONTROLER MANAGER]");
            try
            {
                if (_gameAssembly == null)
                {
                    sb.AppendLine("  Assembly-CSharp unavailable");
                    return;
                }

                Type managerType = _gameAssembly.GetType("Rewired.Unspottable.ControlerManager", false);
                if (managerType == null)
                {
                    sb.AppendLine("  type unavailable");
                    return;
                }

#pragma warning disable CS0618
                UnityEngine.Object[] managers = UnityEngine.Object.FindObjectsOfType(managerType, true);
#pragma warning restore CS0618
                if (managers == null || managers.Length == 0 || managers[0] == null)
                {
                    sb.AppendLine("  instance unavailable");
                    return;
                }

                object manager = managers[0];
                string[] fields = new string[]
                {
                    "canJoinGame", "rewiredPlayerIdCounter", "isKeyboardAssigned",
                    "online", "current_source", "assignedJoysticks"
                };
                BindingFlags all = BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic;

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo fi = managerType.GetField(fields[i], all);
                    if (fi == null)
                    {
                        sb.AppendLine("  " + fields[i] + "=<field missing>");
                        continue;
                    }

                    object value = fi.GetValue(fi.IsStatic ? null : manager);
                    System.Collections.ICollection collection = value as System.Collections.ICollection;
                    if (collection != null)
                        sb.AppendLine("  " + fields[i] + "=Count=" + collection.Count);
                    else
                        sb.AppendLine("  " + fields[i] + "=" + (value == null ? "<null>" : value.ToString()));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  probe error=" + ex.GetType().Name + ": " + ex.Message);
            }
            sb.AppendLine();
        }

        private static object SafeReflectValue(object target, string name)
        {
            if (target == null)
                return null;
            try
            {
                Type t = target.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.GetIndexParameters().Length == 0)
                    return p.GetValue(target, null);

                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                    return f.GetValue(target);
            }
            catch { }
            return null;
        }

        private static string SafeReflectString(object target, string name)
        {
            object value = SafeReflectValue(target, name);
            return value == null ? string.Empty : value.ToString();
        }

        private void BuildQaActionIdMap()
        {
            _qaActionNamesById.Clear();
            try
            {
                if (!ReInput.isReady)
                    return;
                foreach (InputAction action in ReInput.mapping.Actions)
                    _qaActionNamesById[action.id] = action.name ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("QA INPUT action map failed: " + ex.Message);
            }
        }

        private void InstallQaInputPatches()
        {
            if (!_qaInputEnabled || _qaInputPatched)
                return;

            try
            {
                Type harmonyType = FindLoadedType("HarmonyLib.Harmony");
                Type harmonyMethodType = FindLoadedType("HarmonyLib.HarmonyMethod");
                if (harmonyType == null || harmonyMethodType == null)
                {
                    TryLoadHarmony();
                    harmonyType = FindLoadedType("HarmonyLib.Harmony");
                    harmonyMethodType = FindLoadedType("HarmonyLib.HarmonyMethod");
                }
                if (harmonyType == null || harmonyMethodType == null)
                    throw new InvalidOperationException("HarmonyLib was not found in BepInEx.");

                _qaHarmony = Activator.CreateInstance(harmonyType, new object[] { PluginGuid + ".qa.input" });
                int count = 0;

                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetAxis", typeof(string), nameof(QaGetAxisStringPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetAxis", typeof(int), nameof(QaGetAxisIntPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetAxisRaw", typeof(string), nameof(QaGetAxisStringPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetAxisRaw", typeof(int), nameof(QaGetAxisIntPostfix));

                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetButton", typeof(string), nameof(QaGetButtonStringPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetButton", typeof(int), nameof(QaGetButtonIntPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetButtonDown", typeof(string), nameof(QaGetButtonDownStringPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetButtonDown", typeof(int), nameof(QaGetButtonDownIntPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetButtonUp", typeof(string), nameof(QaGetButtonUpStringPostfix));
                count += PatchQaPostfix(harmonyType, harmonyMethodType, "GetButtonUp", typeof(int), nameof(QaGetButtonUpIntPostfix));

                _qaInputPatchCount = count;
                _qaInputPatched = count >= 6;
                if (!_qaInputPatched)
                    Logger.LogWarning("QA INPUT installed only " + count + " Rewired getter patches; expected at least 6.");
            }
            catch (Exception ex)
            {
                _qaInputPatched = false;
                Logger.LogError("QA INPUT patch installation failed: " + ex);
            }
        }

        private int PatchQaPostfix(Type harmonyType, Type harmonyMethodType, string methodName, Type argType, string postfixName)
        {
            MethodInfo original = typeof(Player).GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public, null, new Type[] { argType }, null);
            MethodInfo postfix = typeof(Plugin).GetMethod(postfixName,
                BindingFlags.Static | BindingFlags.NonPublic);
            if (original == null || postfix == null)
                return 0;

            object harmonyMethod = null;
            ConstructorInfo ctor = harmonyMethodType.GetConstructor(new Type[] { typeof(MethodInfo) });
            if (ctor != null)
                harmonyMethod = ctor.Invoke(new object[] { postfix });
            else
            {
                harmonyMethod = Activator.CreateInstance(harmonyMethodType);
                FieldInfo methodField = harmonyMethodType.GetField("method",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (methodField == null)
                    throw new MissingFieldException("HarmonyMethod.method");
                methodField.SetValue(harmonyMethod, postfix);
            }

            MethodInfo patch = null;
            foreach (MethodInfo candidate in harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!string.Equals(candidate.Name, "Patch", StringComparison.Ordinal))
                    continue;
                ParameterInfo[] ps = candidate.GetParameters();
                if (ps.Length < 2)
                    continue;
                if (!typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType))
                    continue;
                patch = candidate;
                break;
            }
            if (patch == null)
                throw new MissingMethodException("Harmony.Patch");

            ParameterInfo[] parms = patch.GetParameters();
            object[] args = new object[parms.Length];
            args[0] = original;
            for (int i = 1; i < parms.Length; i++)
            {
                if (string.Equals(parms[i].Name, "postfix", StringComparison.OrdinalIgnoreCase))
                    args[i] = harmonyMethod;
                else
                    args[i] = null;
            }
            patch.Invoke(_qaHarmony, args);
            Logger.LogInfo("QA INPUT patched Rewired.Player." + methodName + "(" + argType.Name + ").");
            return 1;
        }

        private void RemoveQaInputPatches()
        {
            if (_qaHarmony == null)
                return;
            try
            {
                MethodInfo unpatchSelf = _qaHarmony.GetType().GetMethod("UnpatchSelf",
                    BindingFlags.Instance | BindingFlags.Public);
                if (unpatchSelf != null)
                    unpatchSelf.Invoke(_qaHarmony, null);
            }
            catch { }
            _qaHarmony = null;
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null)
                        return t;
                }
                catch { }
            }
            return null;
        }

        private static void TryLoadHarmony()
        {
            try
            {
                string core = Path.Combine(Paths.BepInExRootPath, "core");
                string[] candidates = new string[]
                {
                    Path.Combine(core, "0Harmony.dll"),
                    Path.Combine(core, "HarmonyX.dll")
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (!File.Exists(candidates[i]))
                        continue;
                    try
                    {
                        Assembly.LoadFrom(candidates[i]);
                        if (FindLoadedType("HarmonyLib.Harmony") != null)
                            return;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void QaGetAxisStringPostfix(Player __instance, string __0, ref float __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            float value;
            if (p.TryGetQaAxis(__instance, __0, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetAxisIntPostfix(Player __instance, int __0, ref float __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            string action;
            if (!p.TryQaActionName(__0, out action)) return;
            float value;
            if (p.TryGetQaAxis(__instance, action, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetButtonStringPostfix(Player __instance, string __0, ref bool __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            bool value;
            if (p.TryGetQaButton(__instance, __0, QaButtonQuery.Held, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetButtonIntPostfix(Player __instance, int __0, ref bool __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            string action;
            if (!p.TryQaActionName(__0, out action)) return;
            bool value;
            if (p.TryGetQaButton(__instance, action, QaButtonQuery.Held, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetButtonDownStringPostfix(Player __instance, string __0, ref bool __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            bool value;
            if (p.TryGetQaButton(__instance, __0, QaButtonQuery.Down, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetButtonDownIntPostfix(Player __instance, int __0, ref bool __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            string action;
            if (!p.TryQaActionName(__0, out action)) return;
            bool value;
            if (p.TryGetQaButton(__instance, action, QaButtonQuery.Down, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetButtonUpStringPostfix(Player __instance, string __0, ref bool __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            bool value;
            if (p.TryGetQaButton(__instance, __0, QaButtonQuery.Up, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private static void QaGetButtonUpIntPostfix(Player __instance, int __0, ref bool __result)
        {
            Plugin p = _instance;
            if (p == null) return;
            string action;
            if (!p.TryQaActionName(__0, out action)) return;
            bool value;
            if (p.TryGetQaButton(__instance, action, QaButtonQuery.Up, out value))
            {
                __result = value;
                Interlocked.Increment(ref p._qaInputInterceptCount);
            }
        }

        private enum QaButtonQuery
        {
            Held,
            Down,
            Up
        }

        private bool TryQaActionName(int actionId, out string name)
        {
            return _qaActionNamesById.TryGetValue(actionId, out name);
        }

        private bool TryGetQaAxis(Player player, string actionName, out float value)
        {
            value = 0f;
            if (!_qaInputEnabled || !_qaInputPatched || player == null || string.IsNullOrEmpty(actionName))
                return false;

            lock (_qaInputLock)
            {
                Dictionary<string, QaInjectedAction> actions;
                QaInjectedAction state;
                if (_qaInjected.TryGetValue(player.id, out actions) &&
                    actions.TryGetValue(actionName, out state) &&
                    state.AxisOverride)
                {
                    value = state.Axis;
                    RecordQaInputConsumptionNoLock(player.id, actionName, "axis", true, Math.Abs(value) > 0.0001f);
                    return true;
                }

                // During unattended gameplay QA, block real keyboard/joystick contribution
                // inside Unspottable so the foreground user cannot accidentally influence tests.
                if (_qaGameplayEnabled && IsQaControlledAction(actionName))
                {
                    value = 0f;
                    RecordQaInputConsumptionNoLock(player.id, actionName, "axis", false, false);
                    return true;
                }

                return false;
            }
        }

        private bool TryGetQaButton(Player player, string actionName, QaButtonQuery query, out bool value)
        {
            value = false;
            if (!_qaInputEnabled || !_qaInputPatched || player == null || string.IsNullOrEmpty(actionName))
                return false;

            float now = Time.unscaledTime;
            lock (_qaInputLock)
            {
                Dictionary<string, QaInjectedAction> actions;
                QaInjectedAction state;
                if (_qaInjected.TryGetValue(player.id, out actions) &&
                    actions.TryGetValue(actionName, out state) &&
                    state.ButtonOverride)
                {
                    if (query == QaButtonQuery.Held)
                        value = state.ButtonHeld && now < state.ButtonUntil;
                    else if (query == QaButtonQuery.Down)
                        value = now <= state.ButtonDownUntil;
                    else
                        value = now <= state.ButtonUpUntil;
                    RecordQaInputConsumptionNoLock(player.id, actionName, query.ToString(), true, value);
                    return true;
                }

                if (_qaGameplayEnabled && IsQaControlledAction(actionName))
                {
                    value = false;
                    RecordQaInputConsumptionNoLock(player.id, actionName, query.ToString(), false, false);
                    return true;
                }

                return false;
            }
        }

        private static bool IsQaControlledAction(string actionName)
        {
            if (string.IsNullOrEmpty(actionName))
                return false;

            return string.Equals(actionName, "MoveX", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "MoveY", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "punch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "run", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "action", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "start", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "JoinGame", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "menu_horizontal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "menu_vertical", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "menu_accept", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionName, "menu_cancel", StringComparison.OrdinalIgnoreCase);
        }

        private void ExpireQaInput(float now)
        {
            lock (_qaInputLock)
            {
                List<int> emptyPlayers = null;
                foreach (KeyValuePair<int, Dictionary<string, QaInjectedAction>> playerPair in _qaInjected)
                {
                    List<string> remove = null;
                    foreach (KeyValuePair<string, QaInjectedAction> pair in playerPair.Value)
                    {
                        QaInjectedAction state = pair.Value;
                        if (state.AxisOverride && now >= state.AxisUntil)
                            state.AxisOverride = false;

                        if (state.ButtonOverride && state.ButtonHeld && now >= state.ButtonUntil)
                        {
                            state.ButtonHeld = false;
                            state.ButtonUpUntil = now + 0.08f;
                        }

                        bool buttonDone = !state.ButtonOverride ||
                            (!state.ButtonHeld && now > state.ButtonUpUntil && now > state.ButtonDownUntil);
                        if (!state.AxisOverride && buttonDone)
                        {
                            if (remove == null) remove = new List<string>();
                            remove.Add(pair.Key);
                        }
                    }
                    if (remove != null)
                        for (int i = 0; i < remove.Count; i++)
                            playerPair.Value.Remove(remove[i]);
                    if (playerPair.Value.Count == 0)
                    {
                        if (emptyPlayers == null) emptyPlayers = new List<int>();
                        emptyPlayers.Add(playerPair.Key);
                    }
                }
                if (emptyPlayers != null)
                    for (int i = 0; i < emptyPlayers.Count; i++)
                        _qaInjected.Remove(emptyPlayers[i]);
            }
        }

        private int CountQaActiveInputs()
        {
            int count = 0;
            lock (_qaInputLock)
            {
                foreach (Dictionary<string, QaInjectedAction> actions in _qaInjected.Values)
                    count += actions.Count;
            }
            return count;
        }

        private string HandleQaBridgeInputCommand(string line)
        {
            if (!_qaInputEnabled)
                return "{\"ok\":false,\"error\":\"input harness disabled; launch with UE_QA_INPUT=1\"}";

            QaBridgeCommand cmd = new QaBridgeCommand();
            cmd.Line = line;
            _qaCommandQueue.Enqueue(cmd);
            if (!cmd.Done.WaitOne(1500))
                return "{\"ok\":false,\"error\":\"main-thread command timeout\"}";
            return cmd.Response ?? "{\"ok\":false,\"error\":\"empty response\"}";
        }

        private void ProcessQaCommands()
        {
            QaBridgeCommand cmd;
            int budget = 16;
            while (budget-- > 0 && _qaCommandQueue.TryDequeue(out cmd))
            {
                try
                {
                    cmd.Response = ExecuteQaCommand(cmd.Line);
                }
                catch (Exception ex)
                {
                    cmd.Response = "{\"ok\":false,\"error\":\"" + JsonEscape(ex.GetType().Name + ": " + ex.Message) + "\"}";
                }
                finally
                {
                    cmd.Done.Set();
                }
            }
        }

        private string ExecuteQaCommand(string line)
        {
            string[] parts = (line ?? string.Empty).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "{\"ok\":false,\"error\":\"invalid QA command\"}";
            if (string.Equals(parts[0], "QA", StringComparison.OrdinalIgnoreCase))
                return ExecuteQaVerificationCommand(parts);
            if (!string.Equals(parts[0], "INPUT", StringComparison.OrdinalIgnoreCase))
                return "{\"ok\":false,\"error\":\"invalid input command\"}";

            string op = parts[1].ToUpperInvariant();
            if (op == "CLEARALL")
            {
                lock (_qaInputLock) _qaInjected.Clear();
                return "{\"ok\":true,\"op\":\"CLEARALL\"}";
            }

            int playerId;
            if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out playerId))
                return "{\"ok\":false,\"error\":\"player id required\"}";

            if (op == "CLEAR")
            {
                lock (_qaInputLock) _qaInjected.Remove(playerId);
                return "{\"ok\":true,\"op\":\"CLEAR\",\"player\":" + playerId.ToString(CultureInfo.InvariantCulture) + "}";
            }

            if (parts.Length < 4)
                return "{\"ok\":false,\"error\":\"action name required\"}";
            string action = parts[3];

            Player player;
            try
            {
                if (!ReInput.isReady)
                    return "{\"ok\":false,\"error\":\"Rewired not ready\"}";
                player = ReInput.players.GetPlayer(playerId);
            }
            catch
            {
                player = null;
            }
            if (player == null)
                return "{\"ok\":false,\"error\":\"Rewired player not found\"}";

            if (op == "SETAXIS")
            {
                if (parts.Length < 5)
                    return "{\"ok\":false,\"error\":\"axis value required\"}";
                float value;
                if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return "{\"ok\":false,\"error\":\"bad axis value\"}";
                value = Mathf.Clamp(value, -1f, 1f);
                int ms = 1000;
                if (parts.Length >= 6)
                    int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);
                ms = Math.Max(30, Math.Min(60000, ms));
                SetQaAxis(playerId, action, value, ms);
                return "{\"ok\":true,\"op\":\"SETAXIS\",\"player\":" + playerId.ToString(CultureInfo.InvariantCulture) +
                    ",\"action\":\"" + JsonEscape(action) + "\",\"value\":" + value.ToString("0.###", CultureInfo.InvariantCulture) +
                    ",\"ms\":" + ms.ToString(CultureInfo.InvariantCulture) + "}";
            }

            if (op == "PRESS")
            {
                int ms = 250;
                if (parts.Length >= 5)
                    int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);
                ms = Math.Max(50, Math.Min(10000, ms));
                PressQaButton(playerId, action, ms);
                return "{\"ok\":true,\"op\":\"PRESS\",\"player\":" + playerId.ToString(CultureInfo.InvariantCulture) +
                    ",\"action\":\"" + JsonEscape(action) + "\",\"ms\":" + ms.ToString(CultureInfo.InvariantCulture) + "}";
            }

            if (op == "PROBEAXIS")
            {
                float value = player.GetAxis(action);
                return "{\"ok\":true,\"op\":\"PROBEAXIS\",\"player\":" + playerId.ToString(CultureInfo.InvariantCulture) +
                    ",\"action\":\"" + JsonEscape(action) + "\",\"value\":" + value.ToString("0.###", CultureInfo.InvariantCulture) + "}";
            }

            if (op == "PROBEBUTTON")
            {
                bool held = player.GetButton(action);
                bool down = player.GetButtonDown(action);
                bool up = player.GetButtonUp(action);
                return "{\"ok\":true,\"op\":\"PROBEBUTTON\",\"player\":" + playerId.ToString(CultureInfo.InvariantCulture) +
                    ",\"action\":\"" + JsonEscape(action) + "\",\"held\":" + (held ? "true" : "false") +
                    ",\"down\":" + (down ? "true" : "false") + ",\"up\":" + (up ? "true" : "false") + "}";
            }

            return "{\"ok\":false,\"error\":\"unknown input op\"}";
        }

        private void SetQaAxis(int playerId, string action, float value, int ms)
        {
            float now = Time.unscaledTime;
            lock (_qaInputLock)
            {
                Dictionary<string, QaInjectedAction> actions = GetQaPlayerActions(playerId);
                QaInjectedAction state;
                if (!actions.TryGetValue(action, out state))
                {
                    state = new QaInjectedAction();
                    actions[action] = state;
                }
                state.AxisOverride = true;
                state.Axis = value;
                state.AxisUntil = now + (ms / 1000f);
                RecordQaInputInjectionNoLock(playerId, action, "axis");
            }
        }

        private void PressQaButton(int playerId, string action, int ms)
        {
            float now = Time.unscaledTime;
            lock (_qaInputLock)
            {
                Dictionary<string, QaInjectedAction> actions = GetQaPlayerActions(playerId);
                QaInjectedAction state;
                if (!actions.TryGetValue(action, out state))
                {
                    state = new QaInjectedAction();
                    actions[action] = state;
                }
                state.ButtonOverride = true;
                state.ButtonHeld = true;
                state.ButtonUntil = now + (ms / 1000f);
                state.ButtonDownUntil = now + 0.08f;
                state.ButtonUpUntil = 0f;
                RecordQaInputInjectionNoLock(playerId, action, "button");
            }
        }

        private Dictionary<string, QaInjectedAction> GetQaPlayerActions(int playerId)
        {
            Dictionary<string, QaInjectedAction> actions;
            if (!_qaInjected.TryGetValue(playerId, out actions))
            {
                actions = new Dictionary<string, QaInjectedAction>(StringComparer.OrdinalIgnoreCase);
                _qaInjected[playerId] = actions;
            }
            return actions;
        }

        private void StopQaFoundation()
        {
            _qaStop = true;
            try { _qaListener?.Stop(); } catch { }
            try
            {
                if (_qaThread != null && _qaThread.IsAlive)
                    _qaThread.Join(500);
            }
            catch { }
            _qaListener = null;
            _qaThread = null;
        }

        private static bool ReadEnvBool(string name, bool fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) return fallback;
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadEnvInt(string name, int fallback, int min, int max)
        {
            string value = Environment.GetEnvironmentVariable(name);
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return fallback;
            return Math.Max(min, Math.Min(max, parsed));
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void AppendJsonString(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(JsonEscape(name)).Append("\":\"").Append(JsonEscape(value)).Append('"');
        }

        private static void AppendJsonBool(StringBuilder sb, string name, bool value)
        {
            sb.Append('"').Append(JsonEscape(name)).Append("\":").Append(value ? "true" : "false");
        }

        private static void AppendJsonNumber(StringBuilder sb, string name, int value)
        {
            sb.Append('"').Append(JsonEscape(name)).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendJsonLong(StringBuilder sb, string name, long value)
        {
            sb.Append('"').Append(JsonEscape(name)).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendJsonFloat(StringBuilder sb, string name, float value)
        {
            sb.Append('"').Append(JsonEscape(name)).Append("\":").Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void Cleanup(bool restoreArenaRenderers)
        {
            _installed = false;
            _readyReported = false;
            _cleanupApplied = false;

            for (int i = 0; i < _disabledArenaColliders.Count; i++)
            {
                ColliderState state = _disabledArenaColliders[i];
                if (state.Collider != null)
                    state.Collider.enabled = state.Enabled;
            }
            _disabledArenaColliders.Clear();

            if (_squareRoot != null)
            {
                Destroy(_squareRoot);
                _squareRoot = null;
            }

            if (restoreArenaRenderers)
            {
                for (int i = 0; i < _hiddenRenderers.Count; i++)
                {
                    RendererState state = _hiddenRenderers[i];
                    if (state.Renderer != null)
                        state.Renderer.enabled = state.Enabled;
                }
            }

            _hiddenRenderers.Clear();
        }

        private void CacheGameTypes()
        {
            if (_gameAssembly == null)
                return;
            if (_playerType == null)
                _playerType = _gameAssembly.GetType("PlayerUnspottable", false);
            if (_botType == null)
                _botType = _gameAssembly.GetType("Bot", false);
        }

        private static Assembly FindAssembly(string simpleName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                AssemblyName name = assemblies[i].GetName();
                if (string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return assemblies[i];
            }
            return null;
        }
    }
}
