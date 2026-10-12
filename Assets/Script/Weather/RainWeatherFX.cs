using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;

namespace Game.Weather
{
    /// <summary>
    /// 分层雨水控制器（中景主雨 + 远景面片雨 + 湿地面涟漪 + 氛围 + 雨声）。
    ///
    /// 三层设计：
    /// 1. 中景主雨：Shuriken 粒子，World 模拟空间，Additive 细长雨丝；
    ///    Box 发射区随相机 LateUpdate 同步 XY（Z 固定），雨滴开 2D 碰撞、
    ///    仅检测 Ground 物理层；碰撞时由子发射器生成水花飞溅，
    ///    同时从对象池取出涟漪圆环落在落点上。
    /// 2. 远景面片雨：单 SpriteRenderer 面片 + Rain/FarRainQuad 噪声滚动雨丝
    ///    （Background 排序层，无碰撞无水花），透明度/密度随雨强联动。
    /// 3. 附加氛围：湿地面暗幕（雨强控透明度，雨停缓慢消退）、
    ///    全局 2D 光压暗 + 冷色偏移（手动模式下生效）、雨声循环。
    ///
    /// 对接 WeatherSystem（天气管理器）：
    /// - followWeatherSystem = true 时自动跟随 CurrentRainStrength，
    ///   并关闭其内置雨表现（BuiltInRainEnabled = false）避免双重降雨；
    /// - 也可独立手动驱动：RainWeatherFX.Instance.SetRainIntensity(0~1)，
    ///   0 = 关闭下雨。
    ///
    /// 快捷键：F6 档位循环（无雨/小雨/中雨/暴雨）、F7/F8 雨强 -/+0.1、
    ///         M 恢复跟随天气系统。所有贴图运行时生成，无外部素材依赖。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RainWeatherFX : MonoBehaviour
    {
        public static RainWeatherFX Instance { get; private set; }

        // ------------------------------------------------------------------
        // 序列化参数（全部可在 Inspector 调整）
        // ------------------------------------------------------------------

        [Header("对接 WeatherSystem（天气管理器）")]
        [Tooltip("存在 WeatherSystem 时自动跟随其雨强（Rain/Storm 档）")]
        [SerializeField] private bool followWeatherSystem = true;
        [Tooltip("接管降雨后关闭 WeatherSystem 内置雨丝/水花，避免双重降雨")]
        [SerializeField] private bool silenceWeatherSystemRain = true;
        [Tooltip("接管后关闭旧 GroundWeatherFX 的湿地面，避免湿润叠加过暗")]
        [SerializeField] private bool disableLegacyGroundWetness = true;

        [Header("主雨发射（性能档位，粒子/秒）")]
        [Tooltip("小雨发射率（存活粒子 ≈ 发射率 × 生存期，约 0.5s）")]
        [SerializeField] private float rateLight = 160f;
        [Tooltip("中雨发射率")]
        [SerializeField] private float rateMedium = 320f;
        [Tooltip("暴雨发射率")]
        [SerializeField] private float rateHeavy = 560f;
        [Tooltip("粒子硬上限（暴雨 ≤ 400，超出后系统自动回收最旧粒子）")]
        [SerializeField, Min(50)] private int maxParticles = 400;
        [Tooltip("雨强变化的全程响应时间（秒），越大过渡越柔和")]
        [SerializeField, Min(0.1f)] private float responseSeconds = 1.2f;

        [Header("主雨运动")]
        [Tooltip("基础下落速度（世界单位/秒）")]
        [SerializeField] private float fallSpeed = 15f;
        [Tooltip("风致 X 偏移强度（叠加在基础侧移上）")]
        [SerializeField] private float windStrength = 1.6f;
        [Tooltip("阵风呼吸周期（秒）")]
        [SerializeField, Min(0.5f)] private float windGustSeconds = 3.5f;
        [Tooltip("发射区超出相机视口的边距（世界单位），防止镜头移动时露边")]
        [SerializeField, Min(0f)] private float viewportPadding = 3f;
        [Tooltip("雨根节点固定 Z（LateUpdate 只同步相机 XY）")]
        [SerializeField] private float rigFixedZ = 0f;

        [Header("水花与涟漪")]
        [Tooltip("每次碰撞生成涟漪的概率（控制涟漪密度，0~1，越小越稀疏）")]
        [SerializeField, Range(0f, 1f)] private float rippleChance = 0.1f;
        [Tooltip("涟漪（扁椭圆水晕）对象池大小")]
        [SerializeField, Min(4)] private int ripplePoolSize = 24;

        [Header("湿地面")]
        [Tooltip("湿表面暗幕最大不透明度")]
        [SerializeField, Range(0f, 0.4f)] private float wetMaxAlpha = 0.16f;
        [Tooltip("变湿耗时（秒）")]
        [SerializeField, Min(1f)] private float wetRiseSeconds = 6f;
        [Tooltip("雨停后完全干透耗时（秒）——湿痕缓慢消退")]
        [SerializeField, Min(1f)] private float wetDrySeconds = 18f;

        [Header("氛围（仅手动模式生效；跟随模式由 WeatherSystem 统一调色）")]
        [SerializeField] private bool applyAtmosphereInManualMode = true;
        [Tooltip("雨天全局光最大压暗比例")]
        [SerializeField, Range(0f, 0.6f)] private float lightDimAmount = 0.30f;
        [Tooltip("冷色偏移目标色")]
        [SerializeField] private Color coldTint = new Color(0.78f, 0.86f, 1f);

        [Header("雨声")]
        [SerializeField] private bool rainAudioEnabled = true;
        [Tooltip("雨声最大音量（雨强线性控制）")]
        [SerializeField, Range(0f, 1f)] private float rainAudioVolume = 0.65f;

        [Header("远景面片雨（默认关闭）")]
        [Tooltip("远景噪声滚动雨丝面片；不需要时保持关闭")]
        [SerializeField] private bool enableFarRain = false;
        [Tooltip("远景雨透明度倍率（内部再乘以雨强），调大注意别出现“光柱”感")]
        [SerializeField, Range(0f, 1f)] private float farRainOpacity = 0.55f;

        [Header("测试快捷键（发布时关闭 enableTestKeys）")]
        [SerializeField] private bool enableTestKeys = true;

        // ------------------------------------------------------------------
        // 运行时状态
        // ------------------------------------------------------------------

        private float targetIntensity;   // 目标雨强 0~1（0 = 无雨）
        private float intensity;         // 平滑后的当前雨强
        private bool manualMode;         // 手动模式：SetRainIntensity / 快捷键触发后不再跟随 WeatherSystem
        private float wetness;           // 湿度记忆 0~1（雨停后缓慢消退）

        private Transform rig;           // 跟随相机的表现根节点
        private Transform worldRoot;     // 世界锚定根节点（涟漪池，不随相机移动）
        private ParticleSystem rainPS;   // 中景主雨
        private ParticleSystem splashPS; // 子发射器水花
        private SpriteRenderer farQuad;  // 远景面片雨
        private SpriteRenderer wetSheen; // 湿地面暗幕
        private Material farMat;         // 远景雨材质实例
        private Material rainMat;
        private Material splashMat;

        private Camera cam;
        private float lastViewW = -1f;
        private float lastViewH = -1f;
        private Collider2D groundCollider;
        private LayerMask groundMask;
        private bool warnedNoGround;

        private Light2D globalLight;
        private float lightBaseIntensity = 1f;
        private Color lightBaseColor = Color.white;

        private AudioSource audioSource;
        private AudioClip rainClip;

        // 涟漪对象池（对象池管理水花，避免反复 Instantiate/Destroy）
        private readonly List<SpriteRenderer> ripplePool = new List<SpriteRenderer>();
        private readonly List<float> rippleAge = new List<float>();
        private readonly List<float> rippleBaseScale = new List<float>(); // 每个涟漪的个体大小（远小近大 + 随机）
        private const float RippleLife = 0.42f; // 水晕短促，“啪嗒”一下就散

        // 碰撞事件复用列表（避免每帧 GC）
        private readonly List<ParticleCollisionEvent> collisionEvents = new List<ParticleCollisionEvent>(32);

        // 运行时生成资源（OnDestroy 释放）
        private Texture2D rainTex;
        private Texture2D splashTex;
        private Texture2D ringTex;
        private Texture2D farQuadTexture;

        // 雨强档位常量（仅用于 HUD 文案）
        public const float IntensityLight = 0.33f;
        public const float IntensityMedium = 0.66f;

        /// <summary>当前平滑雨强（0~1）。</summary>
        public float Intensity => intensity;
        /// <summary>是否处于手动控制模式。</summary>
        public bool IsManualMode => manualMode;

        // ------------------------------------------------------------------
        // 对外接口
        // ------------------------------------------------------------------

        /// <summary>
        /// 设置雨强（供 WeatherDressing / WeatherSystem / 剧情系统调用）。
        /// 0 = 关闭下雨；0~0.33 小雨；0.33~0.66 中雨；0.66~1 暴雨（连续插值）。
        /// 调用后进入手动模式；如需恢复跟随天气系统调用 ResumeFollow()。
        /// </summary>
        public void SetRainIntensity(float intensity01)
        {
            manualMode = true;
            targetIntensity = Mathf.Clamp01(intensity01);
        }

        /// <summary>恢复跟随 WeatherSystem 的雨强（若有）。</summary>
        public void ResumeFollow() => manualMode = false;

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("RainWeatherFX");
            Instance = go.AddComponent<RainWeatherFX>();
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
            BuildRig();
            BindScene();
            SceneManager.activeSceneChanged += HandleSceneChanged;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SceneManager.activeSceneChanged -= HandleSceneChanged;
            RestoreLight();

            if (rainTex != null) Destroy(rainTex);
            if (splashTex != null) Destroy(splashTex);
            if (ringTex != null) Destroy(ringTex);
            if (farQuadTexture != null) Destroy(farQuadTexture);
            if (rainMat != null) Destroy(rainMat);
            if (splashMat != null) Destroy(splashMat);
            if (farMat != null) Destroy(farMat);
            // 运行时生成的雨声属于 audioSource.clip，随组件销毁
        }

        private void HandleSceneChanged(Scene previous, Scene next) => BindScene();

        private void Update()
        {
            // 编辑器内播放中若发生域重载：静态引用丢失；Unity 会恢复对象引用（rig 等非空），
            // 但纯 managed 的 List 会被重置为空——自愈条件必须覆盖这种情况。
            if (Instance == null) Instance = this;
            if (rig == null || rainPS == null || ripplePool.Count == 0)
            {
                BuildRig();
                BindScene();
            }

            if (cam == null || cam != Camera.main) cam = Camera.main;

            // 1) 跟随天气管理器（手动模式除外）
            if (!manualMode && followWeatherSystem && WeatherSystem.Instance != null)
            {
                TakeOverOnce();
                targetIntensity = WeatherSystem.Instance.CurrentRainStrength;
            }

            ReadTestKeys();

            // 2) 雨强平滑过渡
            intensity = Mathf.MoveTowards(intensity, targetIntensity,
                Time.deltaTime / Mathf.Max(0.1f, responseSeconds));

            ApplyRain();
            ApplyFarRain();
            UpdateWetness();
            ApplyAtmosphere();
            UpdateAudio();
            UpdateRipples();
        }

        private void LateUpdate()
        {
            // 需求：表现根节点 LateUpdate 同步相机 XY，Z 固定；
            // 主雨用 World 模拟空间，已生成的雨滴留在世界坐标，
            // 发射器跟着相机走 → 任何位置都有新雨，雨不会停留在原地。
            if (cam == null || rig == null) return;

            Vector3 p = cam.transform.position;
            rig.position = new Vector3(p.x, p.y, rigFixedZ);
            FitViewport();
        }

        // ------------------------------------------------------------------
        // 搭建表现层
        // ------------------------------------------------------------------

        private void BuildRig()
        {
            // 域重载自愈：旧表现层 GameObject 仍在场景里但引用已丢，先整体清理再重建
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);

            rig = new GameObject("RainRig").transform;
            rig.SetParent(transform, false);

            // ---- 运行时贴图 ----
            rainTex = CreateStreakTexture(16, 64);
            splashTex = CreateSoftDotTexture(16);
            ringTex = CreateEllipticBlotTexture(64);

            // ---- 主雨材质（URP Unlit Additive，见 Rain/ParticleStreakAdditive 注释）----
            Shader streakShader = Shader.Find("Rain/ParticleStreakAdditive");
            if (streakShader == null) streakShader = Shader.Find("Sprites/Default");
            rainMat = new Material(streakShader) { name = "RainStreakAdditive" };
            rainMat.SetTexture("_BaseMap", rainTex);
            rainMat.SetColor("_TintColor", new Color(0.62f, 0.78f, 0.96f, 1f));

            splashMat = new Material(streakShader) { name = "RainSplashAdditive" };
            splashMat.SetTexture("_BaseMap", splashTex);
            splashMat.SetColor("_TintColor", new Color(0.72f, 0.86f, 1f, 1f));

            // ---- 中景主雨 + 子发射器水花 ----
            rainPS = CreateMainRain();
            splashPS = CreateSplashSubEmitter();
            ParticleSystem.SubEmittersModule subs = rainPS.subEmitters;
            subs.enabled = true;
            // 雨滴碰撞地面时，在碰撞点生成一颗水花粒子（对象池语义：Unity 内部复用，无实例化开销）
            subs.AddSubEmitter(splashPS,
                ParticleSystemSubEmitterType.Collision,
                ParticleSystemSubEmitterProperties.InheritNothing);

            // 碰撞回调路由：把落点交给涟漪对象池
            rainPS.gameObject.AddComponent<RainCollisionRouter>().owner = this;

            // ---- 远景面片雨（默认关闭；enableFarRain = true 时启用）----
            farQuad = enableFarRain ? CreateFarQuad() : null;

            // ---- 湿地面暗幕 ----
            wetSheen = CreateScreenSprite("RainWetSheen", ResolveSortingLayer("Overlay"), 39);

            // ---- 涟漪池（世界锚定，不随相机移动）----
            worldRoot = new GameObject("RainWorldFX").transform;
            worldRoot.SetParent(transform, false);
            BuildRipplePool();

            // ---- 雨声 ----
            if (rainAudioEnabled) SetupAudio();
        }

        /// <summary>中景主雨粒子：World 空间 + Box 发射 + 2D 碰撞（仅 Ground 层）。</summary>
        private ParticleSystem CreateMainRain()
        {
            GameObject go = new GameObject("RainMain");
            go.transform.SetParent(rig, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            // World 模拟：相机移动时已生成的雨滴留在原地，发射器跟随相机补新雨
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;   // 硬上限（暴雨 ≤ 400）
            main.gravityModifier = 0f;          // 重力交给 VelocityOverLifetime，便于控速
            main.startSpeed = 0f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 0.8f); // FitViewport 按视口高度校准
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.045f, 0.07f); // 雨丝宽度
            main.startSizeY = new ParticleSystem.MinMaxCurve(0.55f, 0.85f);  // 雨丝长度
            main.startSizeZ = 1f;
            main.startRotation = 0f;            // 每帧按当前风向写入，保证雨丝与运动方向一致
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 1f, 0.55f),
                new Color(1f, 1f, 1f, 0.30f));  // 明暗随机，避免“复印线”感
            main.scalingMode = ParticleSystemScalingMode.Local;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;         // SetRainIntensity 驱动

            // Box 发射区：横跨视口宽度，置于视口上方（FitViewport 按相机重设尺寸与位置）
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(20f, 0.6f, 0f);

            // 下落速度 + 风致 X 偏移（每帧按风向/阵风刷新）
            ParticleSystem.VelocityOverLifetimeModule velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-1f, -1f);
            velocity.y = new ParticleSystem.MinMaxCurve(-fallSpeed * 0.92f, -fallSpeed * 1.08f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            // 头尾淡入淡出，避免雨丝生硬地出现/消失
            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            fade.color = new Gradient
            {
                colorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.08f),
                    new GradientAlphaKey(1f, 0.8f),
                    new GradientAlphaKey(0f, 1f)
                }
            };

            // ---- 2D 粒子碰撞：仅 Ground 物理层（禁止全场景碰撞）----
            ParticleSystem.CollisionModule collision = ps.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision2D;
            collision.collidesWith = ResolveGroundMask();
            collision.sendCollisionMessages = true; // OnParticleCollision → 涟漪对象池
            collision.dampen = 0f;
            collision.bounce = 0f;
            // 碰掉 95% 余生（几乎立即消失），但保留极短余命：
            // lifetimeLoss=1 会让粒子在碰撞瞬间死亡，子发射器来不及在碰撞点生成水花。
            collision.lifetimeLoss = 0.95f;

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = rainMat;
            renderer.sortingLayerName = ResolveSortingLayer("Overlay");
            renderer.sortingOrder = 84;
            renderer.lengthScale = 1.35f;           // 拉长雨丝
            renderer.velocityScale = 0.02f;         // 速度快时略再拉长
            return ps;
        }

        /// <summary>子发射器水花：主雨碰撞地面时在碰撞点弹出的小水滴（受重力回落）。</summary>
        private ParticleSystem CreateSplashSubEmitter()
        {
            GameObject go = new GameObject("RainSplash");
            go.transform.SetParent(rainPS.transform, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            // 注意：子发射器源必须处于 Playing 状态（emission rate=0，播放中不会自发射），
            // Stopped 状态下碰撞触发的子发射器不会生成粒子。
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // 在世界坐标的碰撞点爆开
            main.maxParticles = 320;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.9f, 1.7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.045f, 0.09f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 1f, 0.5f), new Color(1f, 1f, 1f, 0.3f));
            main.gravityModifier = 1.6f;        // 上抛后回落
            main.scalingMode = ParticleSystemScalingMode.Local;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;         // 只由子发射器事件触发

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.04f;

            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            fade.color = new Gradient
            {
                colorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                }
            };

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = splashMat;
            renderer.sortingLayerName = ResolveSortingLayer("Overlay");
            renderer.sortingOrder = 86;
            ps.Play(true);
            return ps;
        }

        /// <summary>远景面片雨：单 SpriteRenderer + Rain/FarRainQuad 噪声滚动雨丝，置于场景后方。</summary>
        private SpriteRenderer CreateFarQuad()
        {
            Shader shader = Shader.Find("Rain/FarRainQuad");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            farMat = new Material(shader) { name = "RainFarQuad" };

            // 注意：不能用 Texture2D.whiteTexture（4x4）+ rect(0,0,1,1)——
            // 那样 sprite 的 UV 只覆盖 [0, 0.25]x[0, 0.25] 纹理区域，
            // shader 里按 UV 划分的雨柱会退化成 2 根竖条（“光柱”bug 的根因）。
            // 这里用专属 8x8 全白纹理，保证 sprite UV 完整覆盖 0~1。
            Texture2D white8 = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            Color32[] fill = new Color32[8 * 8];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(255, 255, 255, 255);
            white8.SetPixels32(fill);
            white8.Apply();
            farQuadTexture = white8;

            GameObject go = new GameObject("RainFarQuad");
            go.transform.SetParent(rig, false);
            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite.Create(white8, new Rect(0f, 0f, 8f, 8f),
                new Vector2(0.5f, 0.5f), 8f);
            renderer.sharedMaterial = farMat;
            renderer.sortingLayerName = ResolveSortingLayer("Background"); // 场景物体后方
            renderer.sortingOrder = 50;
            renderer.color = Color.white;
            return renderer;
        }

        /// <summary>湿地面暗幕：跟随相机的整屏叠加（Sprites/Default，受 2D 光照影响）。</summary>
        private SpriteRenderer CreateScreenSprite(string objectName, string sortingLayer, int order)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(rig, false);
            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f), 1f);
            Shader spriteShader = Shader.Find("Sprites/Default");
            if (spriteShader != null) renderer.sharedMaterial = new Material(spriteShader) { name = objectName };
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = order;
            return renderer;
        }

        /// <summary>预建涟漪对象池：所有圆环一次性创建，运行时只激活/回收。</summary>
        private void BuildRipplePool()
        {
            Sprite ringSprite = Sprite.Create(ringTex, new Rect(0f, 0f, ringTex.width, ringTex.height),
                new Vector2(0.5f, 0.5f), ringTex.width);
            Shader spriteShader = Shader.Find("Sprites/Default");
            for (int i = 0; i < ripplePoolSize; i++)
            {
                GameObject go = new GameObject($"Ripple_{i}");
                go.transform.SetParent(worldRoot, false);
                go.SetActive(false);
                SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
                renderer.sprite = ringSprite;
                if (spriteShader != null) renderer.sharedMaterial = new Material(spriteShader) { name = "RainRipple" };
                renderer.sortingLayerName = ResolveSortingLayer("GroundDetail"); // 贴在地面细节层
                renderer.sortingOrder = 36;
                ripplePool.Add(renderer);
                rippleAge.Add(RippleLife);
                rippleBaseScale.Add(1f);
            }
        }

        private void SetupAudio()
        {
            // 优先用真实素材（Assets/Resources/Weather/Audio/rain_loop.wav），
            // 缺失时用滤波白噪声生成一个可循环的雨声，保证开箱即听。
            rainClip = Resources.Load<AudioClip>("Weather/Audio/rain_loop");
            if (rainClip == null) rainClip = GenerateRainClip();

            GameObject go = new GameObject("RainAudio");
            go.transform.SetParent(rig, false);
            audioSource = go.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.spatialBlend = 0f;  // 2D 全局声
            audioSource.clip = rainClip;
        }

        // ------------------------------------------------------------------
        // 每帧应用
        // ------------------------------------------------------------------

        private void ApplyRain()
        {
            if (rainPS == null) return;

            // 雨强 → 发射率（分段线性：0 → 小雨 → 中雨 → 暴雨）
            ParticleSystem.EmissionModule emission = rainPS.emission;
            emission.rateOverTime = EvalRate(intensity);

            if (intensity > 0.001f)
            {
                if (!rainPS.isEmitting) rainPS.Play(true);
            }
            else if (rainPS.isEmitting)
            {
                // 只停止发射，已落下的雨滴自然落地消失
                rainPS.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }

            // 风速 = 基础侧移 + 雨强加成，叠加低频阵风呼吸
            float gust = 0.5f + 0.5f * Mathf.Sin(Time.time * (2f * Mathf.PI / Mathf.Max(0.5f, windGustSeconds)));
            float windX = -(0.35f + windStrength * intensity) * Mathf.Lerp(0.75f, 1.25f, gust);

            ParticleSystem.VelocityOverLifetimeModule velocity = rainPS.velocityOverLifetime;
            velocity.x = new ParticleSystem.MinMaxCurve(windX * 1.15f, windX * 0.85f);

            // 雨丝旋转角 = 运动方向角，保证雨丝与下落方向一致
            ParticleSystem.MainModule main = rainPS.main;
            main.startRotation = new ParticleSystem.MinMaxCurve(Mathf.Atan2(windX, fallSpeed));

            // 雨越大雨丝越亮
            float alpha = 0.3f + 0.35f * intensity;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 1f, 1f, alpha),
                new Color(1f, 1f, 1f, alpha * 0.55f));
        }

        /// <summary>雨强 → 发射率（粒子/秒）：0 → 小雨 → 中雨 → 暴雨 分段线性。</summary>
        private float EvalRate(float value)
        {
            if (value <= 0f) return 0f;
            if (value < IntensityLight) return Mathf.Lerp(0f, rateLight, value / IntensityLight);
            if (value < IntensityMedium) return Mathf.Lerp(rateLight, rateMedium, (value - IntensityLight) / (IntensityMedium - IntensityLight));
            return Mathf.Lerp(rateMedium, rateHeavy, (value - IntensityMedium) / (1f - IntensityMedium));
        }

        private void ApplyFarRain()
        {
            if (farMat == null || farMat.shader == null) return; // 未启用远景雨时跳过
            if (!farMat.HasProperty("_Opacity")) return; // 回退材质时跳过
            // 远景雨与主雨强度联动：透明度 = 雨强 × 倍率；密度随雨强增大
            farMat.SetFloat("_Opacity", intensity * farRainOpacity);
            farMat.SetFloat("_Density", Mathf.Lerp(0.10f, 0.92f, intensity));
        }

        /// <summary>湿地面：雨强控制透明度；雨停后按 wetDrySeconds 缓慢消退。</summary>
        private void UpdateWetness()
        {
            if (wetSheen == null) return;

            wetness = intensity > 0.02f
                ? Mathf.MoveTowards(wetness, intensity, Time.deltaTime / Mathf.Max(1f, wetRiseSeconds))
                : Mathf.MoveTowards(wetness, 0f, Time.deltaTime / Mathf.Max(1f, wetDrySeconds));

            wetSheen.color = new Color(0.05f, 0.09f, 0.14f, wetness * wetMaxAlpha);
        }

        /// <summary>氛围：全局 2D 光压暗 + 冷色偏移（手动模式生效，跟随模式交给 WeatherSystem）。</summary>
        private void ApplyAtmosphere()
        {
            bool ownsAtmosphere = manualMode || WeatherSystem.Instance == null;
            if (!ownsAtmosphere || !applyAtmosphereInManualMode) return;
            if (globalLight == null) return;

            float factor = 1f - lightDimAmount * intensity;
            globalLight.intensity = lightBaseIntensity * factor;
            globalLight.color = lightBaseColor * Color.Lerp(Color.white, coldTint, 0.45f * intensity);
        }

        private void RestoreLight()
        {
            if (globalLight == null) return;
            globalLight.intensity = lightBaseIntensity;
            globalLight.color = lightBaseColor;
            globalLight = null;
        }

        private void UpdateAudio()
        {
            if (audioSource == null) return;
            float volume = rainAudioVolume * intensity;
            audioSource.volume = volume;
            if (volume > 0.01f)
            {
                if (!audioSource.isPlaying) audioSource.Play();
            }
            else if (audioSource.isPlaying)
            {
                audioSource.Pause();
            }
        }

        // ------------------------------------------------------------------
        // 相机适配
        // ------------------------------------------------------------------

        /// <summary>按相机视口重设发射区/面片尺寸，并校准雨滴生存期（落到地面刚好消失）。</summary>
        private void FitViewport()
        {
            if (cam == null) return;
            if (!cam.orthographic) return;

            float h = cam.orthographicSize * 2f;
            float w = h * cam.aspect;
            if (Mathf.Approximately(w, lastViewW) && Mathf.Approximately(h, lastViewH)) return;
            lastViewW = w;
            lastViewH = h;

            // 发射区：横跨视口 + 边距，置于视口上方
            if (rainPS != null)
            {
                ParticleSystem.ShapeModule shape = rainPS.shape;
                shape.scale = new Vector3(w + viewportPadding * 2f, 0.6f, 0f);
                rainPS.transform.localPosition = new Vector3(0f, h * 0.5f + 1.0f, 0f);

                // 生存期 = 从发射点到地面的下落时间（留 10% 余量），碰不到地面也会自然消亡
                float spawnTopY = cam.transform.position.y + h * 0.5f + 1.0f;
                float fallDist = groundCollider != null
                    ? spawnTopY - groundCollider.bounds.max.y
                    : h + 2f;
                fallDist = Mathf.Max(1f, fallDist);
                float life = fallDist / Mathf.Max(1f, fallSpeed);
                ParticleSystem.MainModule main = rainPS.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.92f, life * 1.08f);
            }

            if (farQuad != null)
                farQuad.transform.localScale = new Vector3(w * 1.05f, h * 1.05f, 1f);

            if (wetSheen != null)
                wetSheen.transform.localScale = new Vector3(w + 0.5f, h + 0.5f, 1f);
        }

        // ------------------------------------------------------------------
        // 碰撞 → 涟漪（对象池）
        // ------------------------------------------------------------------

        /// <summary>由 RainCollisionRouter 调用：雨滴碰撞地面时，从对象池取圆环生成涟漪。</summary>
        private void HandleRainCollision(GameObject ground)
        {
            if (rainPS == null) return;
            int count = rainPS.GetCollisionEvents(ground, collisionEvents);
            for (int i = 0; i < count; i++)
            {
                ParticleCollisionEvent ev = collisionEvents[i];
                // 水花飞溅：碰撞点向上扇形弹出 2~3 颗小水滴。
                // 说明：已按需求配置了子发射器（RainSplash），但实测部分 Unity 版本
                // 在 2D 碰撞模式下子发射器不触发，这里用碰撞事件 Emit 兜底——
                // Emit 同样复用粒子系统内部对象池，无实例化/销毁开销。若你的版本
                // 子发射器生效，删除 SpawnSplash 调用即可（避免双倍水花）。
                SpawnSplash(ev.intersection, ev.velocity.magnitude);

                // 涟漪：按概率生成（控制密度），落点随机散布
                if (Random.value > rippleChance) continue;
                SpawnRipple(ev.intersection, ev.velocity.magnitude);
            }
        }

        /// <summary>水花：碰撞点向上扇形弹出 2~3 颗小水滴，落速越快水花越猛。</summary>
        private void SpawnSplash(Vector3 pos, float impactSpeed)
        {
            if (splashPS == null) return;
            int n = 2 + (int)(Random.value * 1.99f); // 2~3 颗
            float speedBoost = Mathf.Lerp(0.85f, 1.25f,
                Mathf.InverseLerp(fallSpeed * 0.85f, fallSpeed * 1.2f, impactSpeed));

            for (int i = 0; i < n; i++)
            {
                ParticleSystem.EmitParams ep = new ParticleSystem.EmitParams();
                ep.position = pos;
                float angle = Random.Range(35f, 145f) * Mathf.Deg2Rad; // 向上扇形
                float speed = Random.Range(0.8f, 1.8f) * speedBoost;
                ep.velocity = new Vector3(Mathf.Cos(angle) * speed * 0.7f, Mathf.Sin(angle) * speed, 0f);
                ep.startSize = Random.Range(0.04f, 0.09f);
                ep.startLifetime = Random.Range(0.18f, 0.32f);
                splashPS.Emit(ep, 1);
            }
        }

        private void SpawnRipple(Vector3 worldPos, float impactSpeed)
        {
            int index = -1;
            for (int i = 0; i < ripplePool.Count; i++)
            {
                if (!ripplePool[i].gameObject.activeSelf) { index = i; break; }
            }
            if (index < 0)
            {
                // 池满：回收最旧的涟漪
                int oldest = 0;
                for (int i = 1; i < rippleAge.Count; i++)
                    if (rippleAge[i] > rippleAge[oldest]) oldest = i;
                index = oldest;
            }

            // 落点散布：碰撞点都在地面顶面同一条线上，这里加入随机偏移——
            // x 侧向散开，y 向上偏移模拟“更远的地面层次”，打破一条直线的整齐感。
            float depth = Random.value; // 0 = 近（贴碰撞线） 1 = 远（更高更远）

            // 大小看“高度/远近”：越远越小、越近越大；再按落速（下落动能）与个体随机微调
            float speedFactor = Mathf.Lerp(0.75f, 1.35f,
                Mathf.InverseLerp(fallSpeed * 0.85f, fallSpeed * 1.2f, impactSpeed));
            float scale = Mathf.Lerp(1.1f, 0.35f, depth)  // 远小近大
                * speedFactor
                * Random.Range(0.7f, 1.4f);               // 个体随机差异（拉大层次感）

            rippleAge[index] = 0f;
            rippleBaseScale[index] = scale;
            ripplePool[index].gameObject.SetActive(true);
            ripplePool[index].transform.position = new Vector3(
                worldPos.x + Random.Range(-0.9f, 0.9f),
                worldPos.y + 0.02f + depth * 0.9f,
                0f);
        }

        private void UpdateRipples()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < ripplePool.Count; i++)
            {
                if (!ripplePool[i].gameObject.activeSelf) continue;

                float age = rippleAge[i] + dt;
                if (age >= RippleLife)
                {
                    rippleAge[i] = RippleLife;
                    ripplePool[i].gameObject.SetActive(false);
                    continue;
                }
                rippleAge[i] = age;

                float t = age / RippleLife;
                // 扁椭圆水晕：从个体基础尺寸向外扩散，纵向压扁贴地
                float s = (0.18f + 0.55f * t) * rippleBaseScale[i];
                ripplePool[i].transform.localScale = new Vector3(s, s * 0.35f, 1f);
                ripplePool[i].color = new Color(0.85f, 0.93f, 1f, (1f - t) * (0.24f + 0.22f * intensity));
            }
        }

        // ------------------------------------------------------------------
        // 场景绑定
        // ------------------------------------------------------------------

        private void BindScene()
        {
            groundMask = ResolveGroundMask();
            groundCollider = FindGroundCollider();
            if (rainPS != null)
            {
                ParticleSystem.CollisionModule collision = rainPS.collision;
                collision.collidesWith = groundMask;
            }

            // 找全局 2D 光并记录基准值
            RestoreLight();
            Scene scene = SceneManager.GetActiveScene();
            foreach (Light2D light in FindObjectsOfType<Light2D>())
            {
                if (light.gameObject.scene != scene || light.lightType != Light2D.LightType.Global) continue;
                globalLight = light;
                lightBaseIntensity = light.intensity;
                lightBaseColor = light.color;
                break;
            }
        }

        /// <summary>地面碰撞层：优先 Ground 物理层；未配置时回退到场景 Ground/Tilemap 物体所在层。</summary>
        private LayerMask ResolveGroundMask()
        {
            int index = LayerMask.NameToLayer("Ground");
            if (index >= 0) return 1 << index;

            GameObject ground = GameObject.Find("Ground");
            if (ground == null) ground = GameObject.Find("Tilemap");
            if (ground != null) return 1 << ground.layer;

            if (!warnedNoGround)
            {
                warnedNoGround = true;
                Debug.LogWarning("[RainWeatherFX] 未找到 Ground 物理层或地面物体，雨滴碰撞/水花不会触发。请添加 Ground 层并赋给地面 Tilemap。");
            }
            return 0;
        }

        private Collider2D FindGroundCollider()
        {
            GameObject ground = GameObject.Find("Ground");
            if (ground == null) ground = GameObject.Find("Tilemap");
            Collider2D collider = ground != null ? ground.GetComponent<Collider2D>() : null;

            if (collider == null)
            {
                // 兜底：找 Ground 层上的任意碰撞体
                int index = LayerMask.NameToLayer("Ground");
                if (index >= 0)
                {
                    foreach (Collider2D candidate in FindObjectsOfType<Collider2D>())
                    {
                        if (candidate.gameObject.layer == index) { collider = candidate; break; }
                    }
                }
            }

            // 兜底：给地面物体补一个贴图大小的静态 BoxCollider2D（便于直接测试）
            if (collider == null && ground != null)
            {
                BoxCollider2D box = ground.AddComponent<BoxCollider2D>();
                SpriteRenderer sr = ground.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null)
                {
                    Bounds b = sr.sprite.bounds;
                    box.size = new Vector2(b.size.x, b.size.y);
                    box.offset = b.center;
                }
                collider = box;
            }
            return collider;
        }

        /// <summary>跟随模式首次生效时，关闭 WeatherSystem 内置雨与旧湿地面，避免表现叠加。</summary>
        private void TakeOverOnce()
        {
            if (silenceWeatherSystemRain)
                WeatherSystem.Instance.BuiltInRainEnabled = false;
            if (disableLegacyGroundWetness && GroundWeatherFX.Instance != null)
                GroundWeatherFX.Instance.gameObject.SetActive(false);
            silenceWeatherSystemRain = false; // 只需执行一次
            disableLegacyGroundWetness = false;
        }

        private static string ResolveSortingLayer(string preferred)
        {
            return SortingLayer.NameToID(preferred) != 0 ? preferred : "Default";
        }

        // ------------------------------------------------------------------
        // 测试快捷键与 HUD
        // ------------------------------------------------------------------

        private void ReadTestKeys()
        {
            if (!enableTestKeys) return;

            if (Input.GetKeyDown(KeyCode.F6))
            {
                // 无雨 → 小雨 → 中雨 → 暴雨 循环
                float next;
                if (targetIntensity <= 0f) next = IntensityLight;
                else if (targetIntensity < IntensityMedium) next = IntensityMedium;
                else if (targetIntensity < 1f) next = 1f;
                else next = 0f;
                SetRainIntensity(next);
            }
            if (Input.GetKeyDown(KeyCode.F7)) SetRainIntensity(targetIntensity - 0.1f);
            if (Input.GetKeyDown(KeyCode.F8)) SetRainIntensity(targetIntensity + 0.1f);
            if (Input.GetKeyDown(KeyCode.M)) ResumeFollow();
        }

        private void OnGUI()
        {
            if (!enableTestKeys) return;
            GUILayout.BeginArea(new Rect(12f, 12f, 250f, 120f), GUI.skin.box);
            string mode = manualMode || WeatherSystem.Instance == null ? "手动" : "跟随天气系统";
            GUILayout.Label($"分层雨 RainWeatherFX（{mode}）");
            GUILayout.Label($"雨强 {intensity:0.00} → 目标 {targetIntensity:0.00}（{BandName(targetIntensity)}）");
            if (rainPS != null)
                GUILayout.Label($"存活粒子 {rainPS.particleCount} / 上限 {maxParticles}");
            GUILayout.Label("F6 档位循环  F7/F8 -/+0.1  M 跟随天气");
            GUILayout.EndArea();
        }

        private static string BandName(float value)
        {
            if (value <= 0f) return "无雨";
            if (value <= IntensityLight) return "小雨";
            if (value <= IntensityMedium) return "中雨";
            return "暴雨";
        }

        // ------------------------------------------------------------------
        // 贴图生成
        // ------------------------------------------------------------------

        /// <summary>雨丝贴图：顶部亮头 + 向下收窄变暗的尾迹（渲染时贴图 +Y 指向运动前方）。</summary>
        private static Texture2D CreateStreakTexture(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            float cx = (width - 1) * 0.5f;
            float headY = (height - 1) * 0.88f;
            float headRadius = width * 0.32f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = Mathf.Abs(x - cx);

                    // 尾迹：从雨头向下收窄、按幂次变暗
                    float tailT = Mathf.Clamp01(((height - 1) * 0.82f - y) / ((height - 1) * 0.82f));
                    float tailHalfWidth = Mathf.Lerp(width * 0.02f, width * 0.10f, tailT);
                    float tail = dx <= tailHalfWidth ? Mathf.Pow(tailT, 1.35f) * 0.55f : 0f;

                    // 雨头：圆润亮斑 + 中心高光
                    float dy = y - headY;
                    float headDist = Mathf.Sqrt(dx * dx + dy * dy * 2.2f);
                    float head = Mathf.Clamp01(1f - headDist / headRadius);
                    head *= head;
                    float core = Mathf.Clamp01(1f - headDist / (headRadius * 0.45f));
                    core *= core;

                    float alpha = Mathf.Clamp01(tail + head * 0.85f + core * 0.45f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        /// <summary>水花水滴：柔和实心圆点。</summary>
        private static Texture2D CreateSoftDotTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center) / (size * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            texture.Apply();
            return texture;
        }

        /// <summary>涟漪水晕：横椭圆柔光斑（2D 游戏最常用的雨落水晕表现）——
        /// 中心亮、边缘快速衰减，渲染时纵向压扁贴地，快速扩散淡出。</summary>
        private static Texture2D CreateEllipticBlotTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 横椭圆距离场：x 半径是 y 的 2.4 倍
                    float dx = (x - center.x) / (size * 0.5f);
                    float dy = (y - center.y) / (size * 0.5f) * 2.4f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // 中心最亮，向边缘二次方衰减，边缘留一点亮边（水面反光）
                    float body = Mathf.Clamp01(1f - d);
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(d - 0.85f) / 0.12f);
                    float alpha = Mathf.Clamp01(body * body * 0.75f + rim * 0.25f);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        /// <summary>生成可循环雨声：滤波白噪声 + 低频幅度调制（无音频素材时的兜底）。</summary>
        private static AudioClip GenerateRainClip()
        {
            const int sampleRate = 22050;
            const int seconds = 3;
            int length = sampleRate * seconds;
            float[] data = new float[length];
            System.Random random = new System.Random(20261012);
            float lp1 = 0f, lp2 = 0f;

            for (int i = 0; i < length; i++)
            {
                float t = (float)i / sampleRate;
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                lp1 += (noise - lp1) * 0.22f;  // 两级单极低通 → 沙沙声
                lp2 += (lp1 - lp2) * 0.22f;
                float am = 0.85f + 0.15f * Mathf.Sin(t * Mathf.PI * 2f * 0.37f); // 缓慢起伏
                data[i] = lp2 * 2.4f * am;
            }

            // 归一化到安全峰值
            float peak = 0.001f;
            for (int i = 0; i < length; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            float gain = 0.55f / peak;
            for (int i = 0; i < length; i++) data[i] *= gain;

            AudioClip clip = AudioClip.Create("RainLoopGenerated", length, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ------------------------------------------------------------------
        // 碰撞事件路由
        // ------------------------------------------------------------------

        /// <summary>挂在主雨粒子对象上，把 2D 碰撞事件转发给 RainWeatherFX。</summary>
        private sealed class RainCollisionRouter : MonoBehaviour
        {
            public RainWeatherFX owner;

            private void OnParticleCollision(GameObject other)
            {
                if (owner != null) owner.HandleRainCollision(other);
            }
        }
    }
}
