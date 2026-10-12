using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;

namespace Game.Weather
{
    /// <summary>
    /// 2D 俯视角 RPG 天气系统（王国：两位君主式克制风格）。
    ///
    /// 天气由四层表现共同组成：
    /// 1. 环境层：全局光色调 + Volume 调色/暗角（Volume 由 WeatherVolumeController 负责）；
    /// 2. 远景层：稀疏、低对比度的雨/雪，负责空间纵深；
    /// 3. 近景层：更清晰的雨丝、雪花和风痕，负责“天气正在发生”的触感；
    /// 4. 接触层：雨滴落地的水花、闪电白帧和云雾漂移。
    ///
    /// 所有贴图运行时生成，不依赖外部素材。常驻跨场景，可放场景物体也可自动自举。
    /// 快捷键：F4 调试面板 / F5 下一种天气 / 数字键 1~8 直达。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class WeatherSystem : MonoBehaviour
    {
        public enum WeatherType
        {
            Sunny,
            Cloudy,
            Rain,
            Storm,
            Fog,
            Snow,
            Windy,
            BloodMoon
        }

        private struct WeatherProfile
        {
            public float rain;
            public float snow;
            public float fog;
            public float cloud;
            public float wind;
            public float lightingFactor;
            public Color lightingTint;
            public Color atmosphereTint;
            public Color precipitationTint;
            public float lightning;
            public float bloodMoon;

            public WeatherProfile(
                float rain,
                float snow,
                float fog,
                float cloud,
                float wind,
                float lightingFactor,
                Color lightingTint,
                Color atmosphereTint,
                Color precipitationTint,
                float lightning,
                float bloodMoon = 0f)
            {
                this.rain = rain;
                this.snow = snow;
                this.fog = fog;
                this.cloud = cloud;
                this.wind = wind;
                this.lightingFactor = lightingFactor;
                this.lightingTint = lightingTint;
                this.atmosphereTint = atmosphereTint;
                this.precipitationTint = precipitationTint;
                this.lightning = lightning;
                this.bloodMoon = bloodMoon;
            }
        }

        public static WeatherSystem Instance { get; private set; }

        [Header("天气状态")]
        [SerializeField] private WeatherType startingWeather = WeatherType.Sunny;
        [SerializeField, Min(0.1f)] private float transitionSeconds = 3.5f;
        [SerializeField] private bool showDebugHud;
        [Tooltip("自动轮播仅用于看效果，每种天气保留一段时间。")]
        [SerializeField] private bool previewAutoCycle;
        [SerializeField, Min(5f)] private float previewIntervalSeconds = 15f;

        [Header("表现质量")]
        [Tooltip("远景雨丝数量。远景密而淡，形成雨幕底子。")]
        [SerializeField, Min(0f)] private float farRainParticlesPerSecond = 320f;
        [Tooltip("近景雨丝数量。少量高对比度雨丝保持地图可读性。")]
        [SerializeField, Min(0f)] private float nearRainParticlesPerSecond = 130f;
        [SerializeField, Min(0f)] private float snowParticlesPerSecond = 70f;
        [SerializeField, Min(0f)] private float windParticlesPerSecond = 22f;
        [SerializeField, Min(0f)] private float leafParticlesPerSecond = 26f;
        [SerializeField, Min(0f)] private float splashParticlesPerSecond = 130f;

        [Header("场景规则")]
        [Tooltip("天气系统在这些场景中自动隐藏表现（室内）。名称不区分大小写。")]
        [SerializeField] private string[] indoorSceneNames = new string[0];

        private WeatherType currentWeather;
        private WeatherType previousWeather;
        private WeatherProfile targetProfile;
        private WeatherProfile activeProfile;
        private WeatherProfile fromProfile;
        private float transitionElapsed;
        private float previewElapsed;

        private Light2D globalLight;
        private Color globalLightBaseColor = Color.white;
        private float globalLightBaseIntensity = 1f;

        private float lastCameraSize;
        private float lastCameraAspect;
        private bool presentationVisible;
        private readonly System.Random random = new System.Random(7219);
        private Camera currentCamera;
        private GameObject visualRoot;
        private GameObject worldParticleRoot;

        private ParticleSystem farRainParticles;
        private ParticleSystem nearRainParticles;
        private ParticleSystem midRainParticles;
        private ParticleSystem snowParticles;
        private ParticleSystem snowNearParticles;
        private ParticleSystem windParticles;
        private ParticleSystem leafParticles;
        private ParticleSystem splashParticles;
        private ParticleSystemRenderer nearRainRenderer;
        private SpriteRenderer fogOverlay;
        private SpriteRenderer groundFogOverlay;
        private SpriteRenderer wetSheenOverlay;
        private SpriteRenderer frontMistOverlay;
        private SpriteRenderer stormFlash;
        private readonly List<SpriteRenderer> fogClouds = new List<SpriteRenderer>();

        private Material rainMaterial;
        private Material snowMaterial;
        private Material windMaterial;
        private Material leafMaterial;
        private Material atmosphereMaterial;
        private Material splashMaterial;
        private Texture2D rainTexture;
        private Texture2D snowTexture;
        private Texture2D windTexture;
        private Texture2D leafTexture;
        private Texture2D ringTexture;
        private Texture2D cloudTexture;
        private Texture2D gradientTexture;
        private Sprite solidSprite;
        private Sprite cloudSprite;
        private Sprite ringSprite;
        private Sprite gradientSprite;
        private bool sceneAllowsWeather = true;
        private float lightningTimer;
        private float nextLightningTime = 8f;
        private float lightningAlpha;
        private float lightningEchoTimer;
        private float lightningEchoAlpha;
        private float cloudClock;
        private Vector3 lastCameraPosition;
        private bool hasLastCameraPosition;
        private bool warnedSortingLayer;

        public WeatherType CurrentWeather => currentWeather;
        public WeatherType PreviousWeather => previousWeather;

        /// <summary>
        /// 新分层雨系统（RainWeatherFX）接管降雨时置为 false：
        /// 关闭本系统内置的远/近/中雨丝与水花，避免双重降雨。雪/雾/风表现不受影响。
        /// </summary>
        public bool BuiltInRainEnabled { get; set; } = true;

        public bool IsWeatherVisible => presentationVisible;
        public float TransitionProgress => Mathf.Clamp01(transitionElapsed / Mathf.Max(0.1f, transitionSeconds));
        public float CurrentRainStrength => activeProfile.rain;
        public float CurrentFogStrength => activeProfile.fog;
        public float CurrentSnowStrength => activeProfile.snow;
        public float CurrentStormStrength => activeProfile.lightning;
        public float CurrentBloodMoonStrength => activeProfile.bloodMoon;
        public float CurrentWindStrength => activeProfile.wind;
        public bool IsBloodMoon => currentWeather == WeatherType.BloodMoon;

        /// <summary>战斗系统读取：当前天气下的敌人强度倍率（血月 > 1）。</summary>
        public float CurrentEnemyStrength => Mathf.Lerp(1f, 1.35f, activeProfile.bloodMoon);

        /// <summary>一道闪电落下时触发（音频层用它安排延迟雷声）。</summary>
        public event Action OnLightningStrike;
        /// <summary>血月开始/结束时触发（战斗、剧情订阅）。</summary>
        public event Action<bool> OnBloodMoonChanged;
        /// <summary>天气切换时触发（previous -> current）。</summary>
        public event Action<WeatherType, WeatherType> OnWeatherChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            Instance = FindObjectOfType<WeatherSystem>();
            if (Instance != null) return;

            GameObject go = new GameObject("WeatherSystem");
            Instance = go.AddComponent<WeatherSystem>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            currentWeather = startingWeather;
            previousWeather = startingWeather;
            targetProfile = GetProfile(currentWeather);
            activeProfile = GetProfile(WeatherType.Sunny);
            fromProfile = activeProfile;
            CreateVisuals();
        }

        private void OnEnable()
        {
            if (Instance != this) return;
            SceneManager.activeSceneChanged += HandleActiveSceneChanged;
            BindScene();
        }

        private void OnDisable()
        {
            if (Instance != this) return;
            SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
            RestoreLighting();
            if (visualRoot != null) visualRoot.SetActive(false);
            if (worldParticleRoot != null) worldParticleRoot.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            DestroyRuntimeObject(rainMaterial);
            DestroyRuntimeObject(snowMaterial);
            DestroyRuntimeObject(windMaterial);
            DestroyRuntimeObject(leafMaterial);
            DestroyRuntimeObject(atmosphereMaterial);
            DestroyRuntimeObject(splashMaterial);
            DestroyRuntimeObject(rainTexture);
            DestroyRuntimeObject(snowTexture);
            DestroyRuntimeObject(windTexture);
            DestroyRuntimeObject(leafTexture);
            DestroyRuntimeObject(ringTexture);
            DestroyRuntimeObject(cloudTexture);
            DestroyRuntimeObject(gradientTexture);
            DestroyRuntimeObject(solidSprite);
            DestroyRuntimeObject(cloudSprite);
            DestroyRuntimeObject(ringSprite);
            DestroyRuntimeObject(gradientSprite);
        }

        private static void DestroyRuntimeObject(UnityEngine.Object value)
        {
            if (value != null) Destroy(value);
        }

        private void Update()
        {
            // 运行时自愈：编辑器内播放中若发生域重载，静态引用会被清空，这里收回所有权。
            if (Instance == null) Instance = this;
            if (currentCamera == null || currentCamera != Camera.main)
                RebindCamera();

            transitionElapsed += Time.deltaTime;
            float blend = Mathf.SmoothStep(0f, 1f, TransitionProgress);
            activeProfile.rain = Mathf.Lerp(fromProfile.rain, targetProfile.rain, blend);
            activeProfile.snow = Mathf.Lerp(fromProfile.snow, targetProfile.snow, blend);
            activeProfile.fog = Mathf.Lerp(fromProfile.fog, targetProfile.fog, blend);
            activeProfile.cloud = Mathf.Lerp(fromProfile.cloud, targetProfile.cloud, blend);
            activeProfile.wind = Mathf.Lerp(fromProfile.wind, targetProfile.wind, blend);
            activeProfile.lightingFactor = Mathf.Lerp(fromProfile.lightingFactor, targetProfile.lightingFactor, blend);
            activeProfile.lightingTint = Color.Lerp(fromProfile.lightingTint, targetProfile.lightingTint, blend);
            activeProfile.atmosphereTint = Color.Lerp(fromProfile.atmosphereTint, targetProfile.atmosphereTint, blend);
            activeProfile.precipitationTint = Color.Lerp(fromProfile.precipitationTint, targetProfile.precipitationTint, blend);
            activeProfile.lightning = Mathf.Lerp(fromProfile.lightning, targetProfile.lightning, blend);
            activeProfile.bloodMoon = Mathf.Lerp(fromProfile.bloodMoon, targetProfile.bloodMoon, blend);

            presentationVisible = sceneAllowsWeather && currentCamera != null;
            SetPresentationVisible(presentationVisible);

            ApplyParticles();
            ApplyAtmosphere();
            ApplyLightingMood();
            UpdateLightning();
            UpdateFogClouds();
            ReadPreviewKeys();

            if (previewAutoCycle && presentationVisible)
            {
                previewElapsed += Time.deltaTime;
                if (previewElapsed >= Mathf.Max(5f, previewIntervalSeconds)) CycleWeather(1);
            }
        }

        private void LateUpdate()
        {
            // 屏幕层跟随相机；粒子使用 World 模拟空间，镜头移动只会带来新的天气区域。
            if (currentCamera == null || visualRoot == null) return;

            Vector3 cameraPosition = currentCamera.transform.position;
            Vector3 cameraDelta = hasLastCameraPosition ? cameraPosition - lastCameraPosition : Vector3.zero;
            PreserveWorldCloudPositions(cameraDelta);
            visualRoot.transform.position = cameraPosition;
            visualRoot.transform.rotation = currentCamera.transform.rotation;
            if (worldParticleRoot != null)
            {
                worldParticleRoot.transform.position = SnapWeatherOrigin(cameraPosition);
                worldParticleRoot.transform.rotation = Quaternion.identity;
            }
            lastCameraPosition = cameraPosition;
            hasLastCameraPosition = true;
            if (!Mathf.Approximately(lastCameraSize, currentCamera.orthographicSize) ||
                !Mathf.Approximately(lastCameraAspect, currentCamera.aspect)) FitCamera();
        }

        private void PreserveWorldCloudPositions(Vector3 cameraDelta)
        {
            if (cameraDelta.sqrMagnitude < 0.000001f) return;
            for (int i = 0; i < fogClouds.Count; i++)
            {
                Transform cloud = fogClouds[i].transform;
                Vector3 localPosition = cloud.localPosition;
                localPosition -= cameraDelta;
                cloud.localPosition = localPosition;
            }
        }

        private static Vector3 SnapWeatherOrigin(Vector3 cameraPosition)
        {
            const float gridSize = 6f;
            return new Vector3(
                Mathf.Floor(cameraPosition.x / gridSize) * gridSize,
                Mathf.Floor(cameraPosition.y / gridSize) * gridSize,
                cameraPosition.z);
        }

        /// <summary>切换天气。剧情、日期系统和 UI 都可以直接调用。</summary>
        public void SetWeather(WeatherType weather)
        {
            if (!Enum.IsDefined(typeof(WeatherType), weather)) return;

            WeatherType previous = currentWeather;
            // 注：不早退。同天气重设只是无害地重新过渡一次；
            // 反而能自愈“域重载后状态残留”的异常（如编辑器内重编译导致 targetProfile 丢失）。

            previousWeather = previous;
            fromProfile = activeProfile;
            transitionElapsed = 0f;
            previewElapsed = 0f;
            currentWeather = weather;
            targetProfile = GetProfile(weather);
            lightningTimer = 0f;
            lightningAlpha = 0f;
            nextLightningTime = RandomRange(5f, 12f);

            if (previous != weather)
            {
                OnWeatherChanged?.Invoke(previous, weather);
                bool bloodNow = weather == WeatherType.BloodMoon;
                if (bloodNow != (previous == WeatherType.BloodMoon))
                    OnBloodMoonChanged?.Invoke(bloodNow);
                Debug.Log($"[WeatherSystem] 天气切换为：{GetWeatherName(weather)}");
            }
        }

        public void SetSunny() => SetWeather(WeatherType.Sunny);
        public void SetCloudy() => SetWeather(WeatherType.Cloudy);
        public void SetRain() => SetWeather(WeatherType.Rain);
        public void SetStorm() => SetWeather(WeatherType.Storm);
        public void SetFog() => SetWeather(WeatherType.Fog);
        public void SetSnow() => SetWeather(WeatherType.Snow);
        public void SetWindy() => SetWeather(WeatherType.Windy);
        public void SetBloodMoon() => SetWeather(WeatherType.BloodMoon);

        private void HandleActiveSceneChanged(Scene previous, Scene next) => BindScene();

        private void BindScene()
        {
            RestoreLighting();
            Scene scene = SceneManager.GetActiveScene();
            sceneAllowsWeather = !IsIndoorScene(scene.name);
            globalLight = null;

            foreach (Light2D light in FindObjectsOfType<Light2D>())
            {
                if (light.gameObject.scene != scene || light.lightType != Light2D.LightType.Global) continue;
                globalLight = light;
                globalLightBaseColor = light.color;
                globalLightBaseIntensity = light.intensity;
                break;
            }

            SetPresentationVisible(false);
            RebindCamera();
        }

        private void SetPresentationVisible(bool visible)
        {
            if (visualRoot == null) return;
            if (visualRoot.activeSelf == visible &&
                (worldParticleRoot == null || worldParticleRoot.activeSelf == visible)) return;

            visualRoot.SetActive(visible);
            if (worldParticleRoot != null) worldParticleRoot.SetActive(visible);
            SetParticlePlaying(farRainParticles, visible);
            SetParticlePlaying(nearRainParticles, visible);
            SetParticlePlaying(midRainParticles, visible);
            SetParticlePlaying(snowParticles, visible);
            SetParticlePlaying(snowNearParticles, visible);
            SetParticlePlaying(windParticles, visible);
            SetParticlePlaying(leafParticles, visible);
            SetParticlePlaying(splashParticles, visible);
        }

        private static void SetParticlePlaying(ParticleSystem particles, bool visible)
        {
            if (particles == null) return;
            if (visible) particles.Play();
            else particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void RestoreLighting()
        {
            if (globalLight == null) return;
            globalLight.intensity = globalLightBaseIntensity;
            globalLight.color = globalLightBaseColor;
            globalLight = null;
        }

        private bool IsIndoorScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            if (indoorSceneNames == null) return false;

            for (int i = 0; i < indoorSceneNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(indoorSceneNames[i]) &&
                    string.Equals(sceneName, indoorSceneNames[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void RebindCamera()
        {
            currentCamera = Camera.main;
            if (currentCamera == null || visualRoot == null) return;

            visualRoot.transform.SetParent(transform, false);
            visualRoot.transform.position = currentCamera.transform.position;
            visualRoot.transform.rotation = Quaternion.identity;
            visualRoot.transform.localScale = Vector3.one;
            if (worldParticleRoot != null)
            {
                worldParticleRoot.transform.position = SnapWeatherOrigin(currentCamera.transform.position);
                worldParticleRoot.transform.rotation = Quaternion.identity;
                worldParticleRoot.transform.localScale = Vector3.one;
            }
            lastCameraPosition = currentCamera.transform.position;
            hasLastCameraPosition = true;
            FitCamera();
        }

        private void FitCamera()
        {
            if (currentCamera == null) return;

            lastCameraSize = currentCamera.orthographicSize;
            lastCameraAspect = currentCamera.aspect;
            float height = currentCamera.orthographic ? currentCamera.orthographicSize * 2f : 20f;
            float width = height * currentCamera.aspect;

            FitScreenSprite(fogOverlay, 4f, width, height);
            FitScreenSprite(groundFogOverlay, 4.5f, width, height);
            FitScreenSprite(wetSheenOverlay, 3.5f, width, height);
            FitScreenSprite(frontMistOverlay, 6f, width, height);
            FitScreenSprite(stormFlash, 8f, width, height);
            ResizeParticleArea(farRainParticles, 5f, width, height, 1.5f);
            ResizeParticleArea(nearRainParticles, 7f, width, height, 1.5f);
            ResizeParticleArea(midRainParticles, 6f, width, height, 1.5f);
            ResizeParticleArea(snowParticles, 5f, width, height, 1.5f);
            ResizeParticleArea(snowNearParticles, 7.5f, width, height, 1.5f);
            ResizeParticleArea(windParticles, 4f, width, height, 1.5f, 0f);
            ResizeParticleArea(leafParticles, 3.5f, width, height, 1.5f);
            ResizeSplashArea(width, height);
        }

        private void CreateVisuals()
        {
            if (SortingLayer.NameToID("Overlay") == 0 && !warnedSortingLayer)
            {
                warnedSortingLayer = true;
                Debug.LogWarning("[WeatherSystem] 缺少排序层 Overlay/GroundDetail，天气表现将渲染在 Default 层（功能不受影响）。建议在 Project Settings > Tags and Layers 中添加。");
            }

            visualRoot = new GameObject("WeatherVisuals");
            visualRoot.transform.SetParent(transform, false);
            // 世界粒子根节点必须存在，粒子发射器挂在它下面并随相机步进移动。
            worldParticleRoot = new GameObject("WeatherParticles");
            worldParticleRoot.transform.SetParent(transform, false);

            solidSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f), 1f);
            cloudTexture = CreateSoftCircleTexture(96);
            cloudSprite = Sprite.Create(cloudTexture, new Rect(0f, 0f, cloudTexture.width, cloudTexture.height),
                new Vector2(0.5f, 0.5f), cloudTexture.width);
            ringTexture = CreateRingTexture(32);
            ringSprite = Sprite.Create(ringTexture, new Rect(0f, 0f, ringTexture.width, ringTexture.height),
                new Vector2(0.5f, 0.5f), ringTexture.width);

            rainTexture = CreateRainTexture(16, 64);
            snowTexture = CreateSoftCircleTexture(24);
            windTexture = CreateWindTexture(48, 12);
            leafTexture = CreateLeafTexture(24);
            rainMaterial = CreateParticleMaterial("Weather Rain", rainTexture);
            snowMaterial = CreateParticleMaterial("Weather Snow", snowTexture);
            windMaterial = CreateParticleMaterial("Weather Wind", windTexture);
            leafMaterial = CreateParticleMaterial("Weather Leaves", leafTexture);
            atmosphereMaterial = CreateParticleMaterial("Weather Atmosphere", Texture2D.whiteTexture);
            splashMaterial = CreateParticleMaterial("Weather Splash", ringTexture);

            gradientTexture = CreateVerticalGradientTexture(64);
            gradientSprite = Sprite.Create(gradientTexture, new Rect(0f, 0f, gradientTexture.width, gradientTexture.height),
                new Vector2(0.5f, 0.5f), gradientTexture.width);
            groundFogOverlay = CreateSprite("GroundFog", gradientSprite, 44);
            groundFogOverlay.color = new Color(0.75f, 0.8f, 0.78f, 0f);
            fogOverlay = CreateSprite("Atmosphere", solidSprite, 42);
            fogOverlay.color = new Color(0.7f, 0.78f, 0.8f, 0f);
            wetSheenOverlay = CreateSprite("RainSheen", solidSprite, 40);
            wetSheenOverlay.color = new Color(0.2f, 0.33f, 0.46f, 0f);
            frontMistOverlay = CreateSprite("FrontMist", solidSprite, 108);
            frontMistOverlay.color = new Color(0.75f, 0.82f, 0.83f, 0f);
            stormFlash = CreateSprite("StormFlash", solidSprite, 122);
            stormFlash.color = new Color(0.78f, 0.86f, 1f, 0f);

            farRainParticles = CreateRainParticles("RainFar", false);
            nearRainParticles = CreateRainParticles("RainNear", true);
            midRainParticles = CreateMidRainParticles();
            snowParticles = CreateSnowParticles();
            snowNearParticles = CreateSnowNearParticles();
            windParticles = CreateWindParticles();
            leafParticles = CreateLeafParticles();
            splashParticles = CreateSplashParticles();
            SetWorldSimulation(farRainParticles);
            SetWorldSimulation(nearRainParticles);
            SetWorldSimulation(midRainParticles);
            SetWorldSimulation(snowParticles);
            SetWorldSimulation(snowNearParticles);
            SetWorldSimulation(windParticles);
            SetWorldSimulation(leafParticles);
            SetWorldSimulation(splashParticles);
            CreateFogClouds();

            visualRoot.SetActive(false);
            worldParticleRoot.SetActive(false);
        }

        private SpriteRenderer CreateSprite(string objectName, Sprite sprite, int sortingOrder)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(visualRoot.transform, false);
            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sharedMaterial = atmosphereMaterial;
            renderer.sortingLayerName = "Overlay";
            renderer.sortingOrder = sortingOrder;
            return renderer;
        }

        private ParticleSystem CreateRainParticles(string objectName, bool nearLayer)
        {
            ParticleSystem particles = CreateParticleSystem(
                objectName,
                rainMaterial,
                ParticleSystemRenderMode.Billboard,
                nearLayer ? 420 : 780,
                nearLayer ? 102 : 68);

            ParticleSystem.MainModule main = particles.main;
            // 雨丝倾斜角由每层统一的 x/y 速度比决定；y 的随机区间收窄，
            // 保证所有雨丝朝向一致（两位君主的雨是“一整片斜下来”，不是乱向的）。
            main.startLifetime = nearLayer
                ? new ParticleSystem.MinMaxCurve(0.42f, 0.72f)
                : new ParticleSystem.MinMaxCurve(0.9f, 1.4f);
            main.startSpeed = 0f;
            main.startSize3D = true;
            main.startSizeX = nearLayer
                ? new ParticleSystem.MinMaxCurve(0.035f, 0.06f)
                : new ParticleSystem.MinMaxCurve(0.02f, 0.04f);
            main.startSizeY = nearLayer
                ? new ParticleSystem.MinMaxCurve(0.5f, 0.9f)
                : new ParticleSystem.MinMaxCurve(0.22f, 0.45f);
            main.startSizeZ = 1f;
            main.startRotation = -0.14f;
            main.startColor = nearLayer
                ? new Color(0.78f, 0.9f, 1f, 0.66f)
                : new Color(0.7f, 0.82f, 0.95f, 0.34f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 12f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = nearLayer ? new ParticleSystem.MinMaxCurve(-3.2f, -2.4f) :
                new ParticleSystem.MinMaxCurve(-1.7f, -1.3f);
            velocity.y = nearLayer ? new ParticleSystem.MinMaxCurve(-27f, -23.5f) :
                new ParticleSystem.MinMaxCurve(-17f, -14.5f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.velocityScale = nearLayer ? 0.06f : 0.035f;
            renderer.lengthScale = nearLayer ? 1.4f : 1.15f;
            if (nearLayer) nearRainRenderer = renderer;
            particles.Play();
            return particles;
        }

        /// <summary>中层雨：衔接远/近两层，让雨有连贯景深。</summary>
        private ParticleSystem CreateMidRainParticles()
        {
            ParticleSystem particles = CreateParticleSystem("RainMid", rainMaterial,
                ParticleSystemRenderMode.Billboard, 560, 86);
            ParticleSystem.MainModule main = particles.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.65f, 1f);
            main.startSpeed = 0f;
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.025f, 0.05f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
            main.startSizeZ = 1f;
            main.startRotation = -0.14f;
            main.startColor = new Color(0.72f, 0.85f, 1f, 0.5f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 12f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-2.4f, -1.8f);
            velocity.y = new ParticleSystem.MinMaxCurve(-21.5f, -18.5f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.velocityScale = 0.045f;
            renderer.lengthScale = 1.3f;
            particles.Play();
            return particles;
        }

        /// <summary>前景雪：少量大而虚化的雪花，制造镜头前的深度。</summary>
        private ParticleSystem CreateSnowNearParticles()
        {
            ParticleSystem particles = CreateParticleSystem("SnowNear", snowMaterial,
                ParticleSystemRenderMode.Billboard, 120, 112);
            ParticleSystem.MainModule main = particles.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 3.6f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.28f, 0.55f);
            main.startColor = new Color(0.94f, 0.97f, 1f, 0.32f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 10f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-1.6f, 1.6f);
            velocity.y = new ParticleSystem.MinMaxCurve(-3.4f, -1.6f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            ParticleSystem.NoiseModule noise = particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = 1.1f;
            noise.strengthY = 0.2f;
            noise.strengthZ = 0f;
            noise.frequency = 0.24f;
            noise.scrollSpeed = 0.3f;
            particles.Play();
            return particles;
        }

        private ParticleSystem CreateSnowParticles()
        {
            ParticleSystem particles = CreateParticleSystem("SnowParticles", snowMaterial,
                ParticleSystemRenderMode.Billboard, 520, 92);
            ParticleSystem.MainModule main = particles.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 6.5f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.055f, 0.15f);
            main.startColor = new Color(0.9f, 0.96f, 1f, 0.76f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 10f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-1.1f, 1.1f);
            velocity.y = new ParticleSystem.MinMaxCurve(-2.2f, -0.8f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            ParticleSystem.NoiseModule noise = particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = 0.7f;
            noise.strengthY = 0.12f;
            noise.strengthZ = 0f;
            noise.frequency = 0.32f;
            noise.scrollSpeed = 0.22f;
            particles.Play();
            return particles;
        }

        private ParticleSystem CreateWindParticles()
        {
            // 风痕：短促、低对比度的气流条纹，只在起风时隐约可见。
            // 风的主力表现交给树叶/树摇摆/雨丝倾斜，这些条纹只作陪衬，
            // 因此必须短、细、淡——长横线是“屏幕划痕”式廉价感的来源。
            ParticleSystem particles = CreateParticleSystem("WindGusts", windMaterial,
                ParticleSystemRenderMode.Billboard, 70, 78);
            ParticleSystem.MainModule main = particles.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.8f);
            main.startSpeed = 0f;
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.8f, 2.0f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(0.02f, 0.045f);
            main.startSizeZ = 1f;
            main.startColor = new Color(0.82f, 0.88f, 0.86f, 0.07f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 5f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(5f, 9f);
            velocity.y = new ParticleSystem.MinMaxCurve(-0.5f, 0.1f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);
            particles.Play();
            return particles;
        }

        private ParticleSystem CreateLeafParticles()
        {
            ParticleSystem particles = CreateParticleSystem("WindLeaves", leafMaterial,
                ParticleSystemRenderMode.Billboard, 140, 82);
            ParticleSystem.MainModule main = particles.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 3.6f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
            main.startColor = new Color(0.85f, 0.78f, 0.42f, 0.72f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 8f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(3.5f, 7.5f);
            velocity.y = new ParticleSystem.MinMaxCurve(-1.4f, 1.4f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            ParticleSystem.NoiseModule noise = particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = 0.4f;
            noise.strengthY = 0.9f;
            noise.strengthZ = 0f;
            noise.frequency = 0.6f;
            noise.scrollSpeed = 0.45f;
            particles.Play();
            return particles;
        }

        private ParticleSystem CreateSplashParticles()
        {
            // 落地涟漪：小而快的水纹，贴地炸开即散，不能像气泡一样上飘。
            ParticleSystem particles = CreateParticleSystem("RainSplashes", splashMaterial,
                ParticleSystemRenderMode.Billboard, 220, 104);
            ParticleSystem.MainModule main = particles.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.08f, 0.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            main.startColor = new Color(0.72f, 0.86f, 0.98f, 0.4f);

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 0.05f, 0f);

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
            velocity.y = new ParticleSystem.MinMaxCurve(0.02f, 0.3f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            // 涟漪：落地后圆环向外扩张（两位君主式的水洼涟漪）
            ParticleSystem.SizeOverLifetimeModule ripple = particles.sizeOverLifetime;
            ripple.enabled = true;
            ripple.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.45f), new Keyframe(1f, 1.25f)));
            return particles;
        }

        private ParticleSystem CreateParticleSystem(
            string objectName,
            Material material,
            ParticleSystemRenderMode renderMode,
            int maxParticles,
            int sortingOrder)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(worldParticleRoot.transform, false);
            ParticleSystem particles = go.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = particles.main;
            main.playOnAwake = false;
            main.loop = true;
            // 天气粒子属于世界：发射器跟随镜头补充视野，已生成的粒子留在世界坐标里。
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.gravityModifier = 0f;
            main.scalingMode = ParticleSystemScalingMode.Local;

            ParticleSystem.ColorOverLifetimeModule fade = particles.colorOverLifetime;
            fade.enabled = true;
            fade.color = new Gradient
            {
                colorKeys = new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.1f),
                    new GradientAlphaKey(1f, 0.78f),
                    new GradientAlphaKey(0f, 1f)
                }
            };

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = renderMode;
            renderer.sharedMaterial = material;
            renderer.sortingLayerName = "Overlay";
            renderer.sortingOrder = sortingOrder;
            renderer.alignment = ParticleSystemRenderSpace.View;
            return particles;
        }

        private static void SetWorldSimulation(ParticleSystem particles)
        {
            if (particles == null) return;
            ParticleSystem.MainModule main = particles.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
        }

        private void CreateFogClouds()
        {
            // 雾墙分层：远层更宽更淡、近层更窄更浓，漂移速度随深度变化
            for (int i = 0; i < 10; i++)
            {
                float depth = i / 9f; // 0=远 1=近
                SpriteRenderer cloud = CreateSprite($"MistWisp_{i + 1}", cloudSprite, 46 + i * 2);
                cloud.transform.localPosition = new Vector3(RandomRange(-14f, 14f), RandomRange(-6.5f, 6.5f), 5f);
                float scale = Mathf.Lerp(13f, 8f, depth) * RandomRange(0.85f, 1.2f);
                cloud.transform.localScale = new Vector3(scale, RandomRange(0.18f, 0.34f) * scale, 1f);
                cloud.color = new Color(0.72f, 0.8f, 0.81f, 0f);
                fogClouds.Add(cloud);
            }
        }

        private float RandomRange(float min, float max) => min + (float)random.NextDouble() * (max - min);

        private void ResizeParticleArea(ParticleSystem particles, float localZ, float width, float height, float padding, float localY = 2f)
        {
            if (particles == null) return;
            particles.transform.localPosition = new Vector3(0f, localY, localZ);
            ParticleSystem.ShapeModule shape = particles.shape;
            shape.scale = new Vector3(width + padding, height + padding, 0f);
        }

        private void ResizeSplashArea(float width, float height)
        {
            if (splashParticles == null) return;
            splashParticles.transform.localPosition = new Vector3(0f, -height * 0.38f, 6.5f);
            ParticleSystem.ShapeModule shape = splashParticles.shape;
            shape.scale = new Vector3(width + 2f, 0.05f, 0f);
        }

        private void FitScreenSprite(SpriteRenderer renderer, float localZ, float width, float height)
        {
            if (renderer == null) return;
            renderer.transform.localPosition = new Vector3(0f, 0f, localZ);
            renderer.transform.localScale = new Vector3(width, height, 1f);
        }

        private void ApplyParticles()
        {
            if (farRainParticles == null || nearRainParticles == null || midRainParticles == null || snowParticles == null ||
                snowNearParticles == null || windParticles == null || leafParticles == null || splashParticles == null) return;

            ParticleSystem.EmissionModule farRainEmission = farRainParticles.emission;
            ParticleSystem.EmissionModule nearRainEmission = nearRainParticles.emission;
            ParticleSystem.EmissionModule midRainEmission = midRainParticles.emission;
            ParticleSystem.EmissionModule snowEmission = snowParticles.emission;
            ParticleSystem.EmissionModule snowNearEmission = snowNearParticles.emission;
            ParticleSystem.EmissionModule windEmission = windParticles.emission;
            ParticleSystem.EmissionModule splashEmission = splashParticles.emission;
            ParticleSystem.EmissionModule leafEmission = leafParticles.emission;

            // 低频噪声做天气强弱呼吸，避免粒子像固定贴图循环。
            float rainPulse = Mathf.Lerp(0.82f, 1.12f, Mathf.PerlinNoise(Time.time * 0.16f, 7.31f));
            float windPulse = Mathf.Lerp(0.7f, 1.22f, Mathf.PerlinNoise(Time.time * 0.23f, 3.17f));
            // 暴雨加成：雷暴的雨量要肉眼可见地大于小雨，不只是更暗。
            float stormBoost = 1f + activeProfile.lightning * 0.9f;
            // 新分层雨系统（RainWeatherFX）接管时置 0，避免双重降雨。
            float rainGate = BuiltInRainEnabled ? 1f : 0f;
            farRainEmission.rateOverTime = activeProfile.rain * rainPulse * stormBoost * rainGate * farRainParticlesPerSecond;
            nearRainEmission.rateOverTime = activeProfile.rain * rainPulse * stormBoost * rainGate * nearRainParticlesPerSecond;
            midRainEmission.rateOverTime = activeProfile.rain * rainPulse * stormBoost * rainGate * farRainParticlesPerSecond * 0.8f;
            snowEmission.rateOverTime = activeProfile.snow * snowParticlesPerSecond;
            snowNearEmission.rateOverTime = activeProfile.snow * 14f;
            windEmission.rateOverTime = activeProfile.wind * windPulse * windParticlesPerSecond;
            leafEmission.rateOverTime = activeProfile.wind * windPulse * leafParticlesPerSecond;
            splashEmission.rateOverTime = activeProfile.rain * activeProfile.rain * rainPulse * rainGate * splashParticlesPerSecond;

            ParticleSystem.MainModule farMain = farRainParticles.main;
            ParticleSystem.MainModule nearMain = nearRainParticles.main;
            ParticleSystem.MainModule midMain = midRainParticles.main;
            ParticleSystem.MainModule snowMain = snowParticles.main;
            ParticleSystem.MainModule snowNearMain = snowNearParticles.main;
            ParticleSystem.MainModule splashMain = splashParticles.main;
            ParticleSystem.MainModule leafMain = leafParticles.main;
            // 明暗随机：同一层里每颗雨的透明度有细微差异，避免“复印线”感
            float farAlpha = 0.28f + activeProfile.rain * 0.14f;
            farMain.startColor = new ParticleSystem.MinMaxGradient(
                WithAlpha(activeProfile.precipitationTint, farAlpha),
                WithAlpha(activeProfile.precipitationTint, farAlpha * 0.55f));
            float nearAlpha = 0.55f + activeProfile.rain * 0.25f;
            nearMain.startColor = new ParticleSystem.MinMaxGradient(
                WithAlpha(activeProfile.precipitationTint, nearAlpha),
                WithAlpha(activeProfile.precipitationTint, nearAlpha * 0.6f));
            float midAlpha = 0.38f + activeProfile.rain * 0.2f;
            midMain.startColor = new ParticleSystem.MinMaxGradient(
                WithAlpha(activeProfile.precipitationTint, midAlpha),
                WithAlpha(activeProfile.precipitationTint, midAlpha * 0.55f));
            snowMain.startColor = WithAlpha(activeProfile.precipitationTint, 0.55f + activeProfile.snow * 0.28f);
            snowNearMain.startColor = WithAlpha(activeProfile.precipitationTint, 0.22f + activeProfile.snow * 0.16f);
            splashMain.startColor = WithAlpha(activeProfile.precipitationTint, 0.22f + activeProfile.rain * 0.28f);
            leafMain.startColor = Color.Lerp(
                new Color(0.76f, 0.68f, 0.3f, 0.2f),
                new Color(0.95f, 0.84f, 0.42f, 0.82f),
                activeProfile.wind);

            float wind = activeProfile.wind * windPulse;
            // 阵风摆动：雨丝角度随低频正弦缓慢摇摆（两位君主的雨会随风倾斜）
            float gust = Mathf.Sin(Time.time * 0.45f) * 0.5f * (0.4f + wind);
            // x/y/z 曲线必须保持同一模式（RandomBetweenConstants），否则 Unity 报错。
            float farX = Mathf.Lerp(-1.1f, -3.4f, wind) + gust;
            ParticleSystem.VelocityOverLifetimeModule farVelocity = farRainParticles.velocityOverLifetime;
            farVelocity.x = new ParticleSystem.MinMaxCurve(farX * 1.25f, farX * 0.85f);
            float midX = Mathf.Lerp(-1.6f, -4.6f, wind) + gust * 1.15f;
            ParticleSystem.VelocityOverLifetimeModule midVelocity = midRainParticles.velocityOverLifetime;
            midVelocity.x = new ParticleSystem.MinMaxCurve(midX * 1.25f, midX * 0.85f);
            float nearX = Mathf.Lerp(-2.2f, -6f, wind) + gust * 1.3f;
            ParticleSystem.VelocityOverLifetimeModule nearVelocity = nearRainParticles.velocityOverLifetime;
            nearVelocity.x = new ParticleSystem.MinMaxCurve(nearX * 1.25f, nearX * 0.85f);
            if (nearRainRenderer != null) nearRainRenderer.lengthScale = Mathf.Lerp(1.35f, 1.8f, wind);
        }

        private void ApplyAtmosphere()
        {
            Color tint = activeProfile.atmosphereTint;
            // 雨幕灰罩：下雨时整幅画面罩一层雨色，让场景“沉”进雨天里（而不只是变暗）
            if (fogOverlay != null)
                fogOverlay.color = WithAlpha(tint,
                    activeProfile.fog * 0.17f + activeProfile.cloud * 0.028f + activeProfile.rain * 0.08f);
            if (groundFogOverlay != null)
                groundFogOverlay.color = WithAlpha(tint,
                    activeProfile.fog * 0.3f + activeProfile.snow * 0.05f + activeProfile.rain * 0.04f);
            if (wetSheenOverlay != null)
                wetSheenOverlay.color = WithAlpha(
                    Color.Lerp(new Color(0.18f, 0.28f, 0.38f), tint, 0.35f),
                    activeProfile.rain * 0.035f + activeProfile.lightning * 0.012f);
            if (frontMistOverlay != null)
                frontMistOverlay.color = WithAlpha(tint,
                    activeProfile.fog * 0.075f + activeProfile.snow * 0.018f + activeProfile.rain * 0.02f);

            // 云雾条：雾天是浅色流雾；雨天转为压境的深灰雨云，横向漂移。
            Color rainCloud = Color.Lerp(tint, new Color(0.3f, 0.36f, 0.44f), activeProfile.rain * 0.65f);
            for (int i = 0; i < fogClouds.Count; i++)
            {
                float variation = 0.72f + (i % 3) * 0.12f;
                fogClouds[i].color = WithAlpha(rainCloud,
                    Mathf.Clamp01(
                        (activeProfile.fog * 0.42f + activeProfile.cloud * 0.08f + activeProfile.rain * 0.18f) * variation));
            }
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = Mathf.Clamp01(alpha);
            return color;
        }

        private void ApplyLightingMood()
        {
            if (globalLight == null) return;
            float factor = sceneAllowsWeather ? activeProfile.lightingFactor : 1f;
            Color tint = sceneAllowsWeather ? activeProfile.lightingTint : Color.white;

            // 昼夜底色：正午最亮最暖，午夜压暗偏蓝（接入 GameTimeManager 后生效，无时间系统时保持原样）
            Color dayNightTint = Color.white;
            float dayNightMul = 1f;
            Game.TimeOfDay.GameTimeManager time = Game.TimeOfDay.GameTimeManager.Instance;
            if (time != null)
            {
                float dayFactor = 0.5f + 0.5f * Mathf.Cos((time.TimeOfDay - 0.25f) * Mathf.PI * 2f);
                dayNightTint = Color.Lerp(new Color(0.36f, 0.44f, 0.72f), Color.white, dayFactor);
                dayNightMul = Mathf.Lerp(0.30f, 1f, dayFactor);
            }

            globalLight.intensity = globalLightBaseIntensity * dayNightMul * factor;
            globalLight.color = globalLightBaseColor * dayNightTint * tint;
        }

        private void UpdateLightning()
        {
            if (stormFlash == null) return;

            lightningAlpha = Mathf.MoveTowards(lightningAlpha, 0f, Time.deltaTime * 5.5f);
            // 余闪：主闪后约 0.1 秒的第二次较暗闪光，两位君主的闪电有这种“回闪”
            if (lightningEchoTimer > 0f)
            {
                lightningEchoTimer -= Time.deltaTime;
                if (lightningEchoTimer <= 0f)
                    lightningAlpha = Mathf.Max(lightningAlpha, lightningEchoAlpha);
            }
            if (presentationVisible && activeProfile.lightning > 0.05f && activeProfile.rain > 0.45f)
            {
                lightningTimer += Time.deltaTime;
                if (lightningTimer >= nextLightningTime)
                {
                    lightningTimer = 0f;
                    nextLightningTime = RandomRange(4.5f, 12f) / Mathf.Lerp(1f, 1.35f, activeProfile.lightning);
                    float primary = RandomRange(0.16f, 0.3f) * activeProfile.lightning;
                    lightningAlpha = primary;
                    lightningEchoTimer = RandomRange(0.09f, 0.16f);
                    lightningEchoAlpha = primary * RandomRange(0.45f, 0.65f);
                    OnLightningStrike?.Invoke();
                }
            }
            stormFlash.color = WithAlpha(Color.Lerp(Color.white, activeProfile.precipitationTint, 0.35f), lightningAlpha);
        }

        private void UpdateFogClouds()
        {
            if (!presentationVisible || currentCamera == null) return;

            cloudClock += Time.deltaTime;
            float halfHeight = currentCamera.orthographic ? currentCamera.orthographicSize : 10f;
            float halfWidth = halfHeight * Mathf.Max(1f, currentCamera.aspect);
            float drift = 0.08f + activeProfile.wind * 0.5f;

            for (int i = 0; i < fogClouds.Count; i++)
            {
                Transform cloud = fogClouds[i].transform;
                Vector3 position = cloud.localPosition;
                float depth = fogClouds.Count > 1 ? i / (float)(fogClouds.Count - 1) : 0f;
                position.x += drift * Time.deltaTime * (1f + depth * 1.6f) * (i % 2 == 0 ? 1f : -0.65f);
                position.y += Mathf.Sin(cloudClock * (0.22f + i * 0.025f) + i) * Time.deltaTime * 0.04f;
                if (position.x > halfWidth + 6f) position.x = -halfWidth - 6f;
                if (position.x < -halfWidth - 6f) position.x = halfWidth + 6f;
                cloud.localPosition = position;
            }
        }

        private void ReadPreviewKeys()
        {
            if (!sceneAllowsWeather) return;
            if (Input.GetKeyDown(KeyCode.F4)) showDebugHud = !showDebugHud;
            if (Input.GetKeyDown(KeyCode.F5)) CycleWeather(1);
            for (int i = 0; i < 8; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) SetWeather((WeatherType)i);
            }
        }

        private void CycleWeather(int direction)
        {
            int count = Enum.GetValues(typeof(WeatherType)).Length;
            int next = ((int)currentWeather + direction) % count;
            if (next < 0) next += count;
            SetWeather((WeatherType)next);
        }

        private void OnGUI()
        {
            if (!showDebugHud || !sceneAllowsWeather) return;

            GUILayout.BeginArea(new Rect(Mathf.Max(12f, Screen.width - 242f), 12f, 230f, 210f), GUI.skin.box);
            Game.TimeOfDay.GameTimeManager time = Game.TimeOfDay.GameTimeManager.Instance;
            if (time != null)
                GUILayout.Label($"第 {time.DayNumber} 天 · {Game.TimeOfDay.GameTimeManager.SeasonChinese(time.CurrentSeason)} · {time.PhaseChinese()} {time.TimeOfDay * 100f:0}%");
            GUILayout.Label($"天气 · {GetWeatherName(currentWeather)}");
            GUILayout.Label($"雨 {activeProfile.rain:0.00}  雾 {activeProfile.fog:0.00}  风 {activeProfile.wind:0.00}");
            GUILayout.Label($"过渡 {TransitionProgress * 100f:0}%  显示 {presentationVisible}");
            if (activeProfile.bloodMoon > 0.01f)
                GUILayout.Label($"血月 {activeProfile.bloodMoon:0.00}  敌人强度 ×{CurrentEnemyStrength:0.00}");
            GUILayout.Label("F4 面板 / F5 下一种 / 数字键 1~8 直达");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 4; i++)
                if (GUILayout.Button(GetWeatherName((WeatherType)i), GUILayout.Height(22f))) SetWeather((WeatherType)i);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 4; i < 8; i++)
                if (GUILayout.Button(GetWeatherName((WeatherType)i), GUILayout.Height(22f))) SetWeather((WeatherType)i);
            GUILayout.EndHorizontal();
            previewAutoCycle = GUILayout.Toggle(previewAutoCycle, "自动轮播");
            GUILayout.EndArea();
        }

        private static string GetWeatherName(WeatherType weather)
        {
            switch (weather)
            {
                case WeatherType.Cloudy: return "阴天";
                case WeatherType.Rain: return "小雨";
                case WeatherType.Storm: return "暴雨";
                case WeatherType.Fog: return "雾天";
                case WeatherType.Snow: return "降雪";
                case WeatherType.Windy: return "风起";
                case WeatherType.BloodMoon: return "血月";
                default: return "晴天";
            }
        }

        private static WeatherProfile GetProfile(WeatherType weather)
        {
            switch (weather)
            {
                case WeatherType.Cloudy:
                    return new WeatherProfile(0f, 0f, 0.03f, 0.95f, 0.08f, 0.86f,
                        new Color(0.86f, 0.88f, 0.9f), new Color(0.66f, 0.72f, 0.72f),
                        new Color(0.8f, 0.86f, 0.92f), 0f);
                case WeatherType.Rain:
                    return new WeatherProfile(0.78f, 0f, 0.16f, 0.48f, 0.28f, 0.82f,
                        new Color(0.76f, 0.83f, 0.91f), new Color(0.48f, 0.59f, 0.66f),
                        new Color(0.58f, 0.74f, 0.9f), 0f);
                case WeatherType.Storm:
                    return new WeatherProfile(1f, 0f, 0.2f, 0.98f, 0.82f, 0.64f,
                        new Color(0.58f, 0.66f, 0.82f), new Color(0.28f, 0.36f, 0.5f),
                        new Color(0.43f, 0.58f, 0.82f), 1f);
                case WeatherType.Fog:
                    return new WeatherProfile(0f, 0f, 0.96f, 0.28f, 0.07f, 0.86f,
                        new Color(0.82f, 0.86f, 0.85f), new Color(0.7f, 0.77f, 0.75f),
                        new Color(0.85f, 0.9f, 0.88f), 0f);
                case WeatherType.Snow:
                    return new WeatherProfile(0f, 0.9f, 0.14f, 0.58f, 0.27f, 0.9f,
                        new Color(0.84f, 0.9f, 0.98f), new Color(0.68f, 0.76f, 0.83f),
                        new Color(0.9f, 0.96f, 1f), 0f);
                case WeatherType.Windy:
                    return new WeatherProfile(0f, 0f, 0.02f, 0.2f, 1f, 0.98f,
                        new Color(0.94f, 0.94f, 0.9f), new Color(0.76f, 0.78f, 0.7f),
                        new Color(0.86f, 0.9f, 0.86f), 0f);
                case WeatherType.BloodMoon:
                    // 血月：红色只交给全局光（避免和 Volume 的 colorFilter 三重相乘变死黑），
                    // Volume 层只做轻压暗与暗角脉冲。
                    return new WeatherProfile(0f, 0f, 0.1f, 0.12f, 0.06f, 0.78f,
                        new Color(0.88f, 0.40f, 0.32f), new Color(0.34f, 0.1f, 0.1f),
                        new Color(0.55f, 0.16f, 0.16f), 0f, 1f);
                default:
                    return new WeatherProfile(0f, 0f, 0f, 0f, 0f, 1f,
                        Color.white, new Color(0.8f, 0.84f, 0.78f), Color.white, 0f);
            }
        }

        private static Material CreateParticleMaterial(string materialName, Texture2D texture)
        {
            Shader shader = Resources.Load<Shader>("Weather/WeatherUnlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            Material material = new Material(shader) { name = materialName };
            material.mainTexture = texture;
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            return material;
        }

        /// <summary>
        /// 雨滴拖尾贴图：拉伸渲染时贴图 +Y 轴指向运动方向（向下），
        /// 因此雨头（圆润亮斑 + 亮芯）画在贴图顶部，向上拖出逐渐变细、变暗的雨尾。
        /// 形状：雨头宽约 60%，尾迹从 20% 宽线性收窄到 4%，亮度按幂次衰减——
        /// 远处缩小采样时是干净的小雨丝，近处放大后能看到头尾细节。
        /// </summary>
        private static Texture2D CreateRainTexture(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            float cx = (width - 1) * 0.5f;
            float headY = (height - 1) * 0.88f;   // 雨头中心（贴图顶部 = 运动前方）
            float headRadius = width * 0.32f;     // 雨头半径
            float tailTop = (height - 1) * 0.82f; // 尾迹起点（雨头下缘附近）

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = Mathf.Abs(x - cx);

                    // 雨尾：从雨头向下（贴图下方 =  trailing）收窄、变暗
                    float tailT = Mathf.Clamp01((tailTop - y) / tailTop); // 0=尾端 1=雨头
                    float tailHalfWidth = Mathf.Lerp(width * 0.02f, width * 0.10f, tailT);
                    float tailAlpha = Mathf.Pow(tailT, 1.35f) * 0.55f;
                    float tail = dx <= tailHalfWidth ? tailAlpha : 0f;

                    // 雨头：略压扁的圆润亮斑
                    float dy = y - headY;
                    float headDist = Mathf.Sqrt(dx * dx + dy * dy * 2.2f);
                    float head = Mathf.Clamp01(1f - headDist / headRadius);
                    head *= head;

                    // 亮芯：雨头中心一点高光，近景放大时有“水珠反光”的细节
                    float core = Mathf.Clamp01(1f - headDist / (headRadius * 0.45f));
                    core *= core;

                    float alpha = Mathf.Clamp01(tail + head * 0.85f + core * 0.45f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateSoftCircleTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center) / radius;
                    float alpha = Mathf.Clamp01(1f - distance);
                    alpha *= alpha;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        /// <summary>垂直渐变：底部实、顶部透，用于地面雾。</summary>
        private static Texture2D CreateVerticalGradientTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < size; y++)
            {
                float t = y / (float)(size - 1);
                float alpha = Mathf.Clamp01(1f - t) * Mathf.Clamp01(1f - t);
                for (int x = 0; x < size; x++)
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateWindTexture(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < height; y++)
            {
                float vertical = 1f - Mathf.Abs(y - (height - 1) * 0.5f) / (height * 0.5f);
                for (int x = 0; x < width; x++)
                {
                    float edgeFade = Mathf.Clamp01(Mathf.Min(x / 5f, (width - 1 - x) / 8f));
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, vertical * edgeFade));
                }
            }
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateLeafTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 offset = new Vector2(x, y) - center;
                    float ellipse = (offset.x * offset.x) / (size * size * 0.12f) +
                        (offset.y * offset.y) / (size * size * 0.035f);
                    float alpha = Mathf.Clamp01(1f - ellipse);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha * alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        private static Texture2D CreateRingTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center) / (size * 0.5f);
                    float ring = Mathf.Clamp01(1f - Mathf.Abs(distance - 0.57f) / 0.16f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, ring * ring));
                }
            }
            texture.Apply();
            return texture;
        }
    }
}