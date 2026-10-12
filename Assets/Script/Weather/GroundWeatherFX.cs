using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Weather
{
    /// <summary>
    /// 地面天气反馈（自动自举，无需挂场景物体）：
    /// - 雨天：地面湿润变暗（屏幕叠加）+ 世界坐标水洼（随雨渐现，雨停后缓慢蒸发）；
    /// - 雪天：积雪白幕 + 世界雪块渐厚 + 玩家脚印（对象池，走出后自动淡出）；
    /// - 水洼/脚印/雪块渲染在 GroundDetail 排序层（地图之上、角色之下），受 2D 光照影响；
    /// - 全部贴图运行时生成，不依赖外部素材；切场景时自动清理；
    /// - 没有 Player 标签的角色时脚印静默跳过。
    /// </summary>
    public sealed class GroundWeatherFX : MonoBehaviour
    {
        private static GroundWeatherFX instance;

        [Header("雨——湿润地面")]
        [Tooltip("地面变暗叠加的最大不透明度")]
        [SerializeField, Range(0f, 0.3f)] private float wetDarkAlpha = 0.12f;
        [Tooltip("水洼数量")]
        [SerializeField, Min(0)] private int puddleCount = 14;
        [Tooltip("雨停后地面完全干透的时间（秒）")]
        [SerializeField, Min(5f)] private float dryOutSeconds = 25f;

        [Header("雪——积雪")]
        [Tooltip("积雪白幕最大不透明度")]
        [SerializeField, Range(0f, 0.3f)] private float snowTintAlpha = 0.13f;
        [Tooltip("雪块数量")]
        [SerializeField, Min(0)] private int snowPatchCount = 16;
        [Tooltip("积雪达到该厚度才出现脚印")]
        [SerializeField, Range(0f, 1f)] private float footprintMinSnow = 0.15f;
        [Tooltip("脚印存活时间（秒）")]
        [SerializeField, Min(1f)] private float footprintLifetime = 7f;
        [Tooltip("脚印间隔距离（世界单位）")]
        [SerializeField, Min(0.1f)] private float footprintSpacing = 0.55f;

        private WeatherSystem weather;
        private Camera currentCamera;
        private Transform screenRoot;     // 跟随相机的屏幕层（湿润暗幕/积雪白幕）
        private Transform decalRoot;      // 世界锚定层（水洼/雪块/脚印）
        private SpriteRenderer wetDark;
        private SpriteRenderer snowTint;
        private readonly List<SpriteRenderer> puddles = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> snowPatches = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> footprints = new List<SpriteRenderer>();
        private readonly List<float> footprintAges = new List<float>();

        private float wetness;      // 0~1，雨落在地面的记忆
        private float snowDepth;    // 0~1，积雪厚度
        private bool puddlesPlaced;
        private bool snowPatchesPlaced;
        private float footprintDistance;
        private bool footprintSide;
        private Rigidbody2D playerBody;
        private Material decalMaterial;   // Sprites/Default：受 2D 光照影响
        private Material screenMaterial;
        private Texture2D puddleTexture;
        private Texture2D snowPatchTexture;
        private Texture2D footprintTexture;
        private readonly System.Random random = new System.Random(4711);
        private string decalLayer = "Default";
        private string screenLayer = "Default";

        public static GroundWeatherFX Instance => instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            GameObject go = new GameObject("GroundWeatherFX");
            instance = go.AddComponent<GroundWeatherFX>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            puddleTexture = CreatePuddleTexture(128);
            snowPatchTexture = CreateSoftCircleTexture(96);
            footprintTexture = CreateFootprintTexture(48);
            decalMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Weather Decal" };
            screenMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Weather Ground Screen" };

            // 排序层不存在时回落 Default，只在首次警告。
            decalLayer = ResolveLayer("GroundDetail");
            screenLayer = ResolveLayer("Overlay");

            screenRoot = new GameObject("GroundScreenFX").transform;
            screenRoot.SetParent(transform, false);
            wetDark = CreateScreenSprite("WetDark", screenMaterial, 38);
            wetDark.color = new Color(0.05f, 0.09f, 0.14f, 0f);
            snowTint = CreateScreenSprite("SnowTint", screenMaterial, 38);
            snowTint.color = new Color(0.92f, 0.96f, 1f, 0f);

            decalRoot = new GameObject("GroundDecals").transform;
            decalRoot.SetParent(transform, false);
        }

        private static string ResolveLayer(string preferred)
        {
            if (SortingLayer.NameToID(preferred) != 0) return preferred;
            Debug.LogWarning("[GroundWeatherFX] 缺少排序层 " + preferred + "，回落到 Default 层。建议在 Project Settings > Tags and Layers 添加 GroundDetail 与 Overlay。");
            return "Default";
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (puddleTexture != null) Destroy(puddleTexture);
            if (snowPatchTexture != null) Destroy(snowPatchTexture);
            if (footprintTexture != null) Destroy(footprintTexture);
            if (decalMaterial != null) Destroy(decalMaterial);
            if (screenMaterial != null) Destroy(screenMaterial);
        }

        private void OnEnable() => SceneManager.activeSceneChanged += HandleSceneChanged;
        private void OnDisable() => SceneManager.activeSceneChanged -= HandleSceneChanged;

        private void HandleSceneChanged(Scene previous, Scene next)
        {
            // 新场景从干地面/无积雪开始，脚印与水洼全部清理
            wetness = 0f;
            snowDepth = 0f;
            puddlesPlaced = false;
            snowPatchesPlaced = false;
            playerBody = null;
            ClearList(puddles);
            ClearList(snowPatches);
            ClearList(footprints);
            footprintAges.Clear();
        }

        private void Update()
        {
            if (weather == null)
            {
                weather = WeatherSystem.Instance;
                if (weather == null) return;
            }
            if (currentCamera == null || currentCamera != Camera.main) currentCamera = Camera.main;

            bool visible = weather.IsWeatherVisible;
            float rain = visible ? weather.CurrentRainStrength : 0f;
            float snow = visible ? weather.CurrentSnowStrength : 0f;

            // 湿润记忆：下雨时快速积湿（约 8 秒到顶），雨停后缓慢干透
            wetness = rain > 0.05f
                ? Mathf.MoveTowards(wetness, rain, Time.deltaTime / 8f)
                : Mathf.MoveTowards(wetness, 0f, Time.deltaTime / Mathf.Max(5f, dryOutSeconds));
            // 积雪：下雪时缓慢堆厚（约 45 秒到顶），雪停后化得很慢
            snowDepth = snow > 0.05f
                ? Mathf.MoveTowards(snowDepth, snow, Time.deltaTime / 45f)
                : Mathf.MoveTowards(snowDepth, 0f, Time.deltaTime / 60f);

            if (wetness > 0.08f && !puddlesPlaced) PlacePuddles();
            if (wetness < 0.03f && puddlesPlaced) { puddlesPlaced = false; }
            if (snowDepth > 0.05f && !snowPatchesPlaced) PlaceSnowPatches();
            if (snowDepth < 0.02f && snowPatchesPlaced) { snowPatchesPlaced = false; }

            screenRoot.gameObject.SetActive(visible);
            decalRoot.gameObject.SetActive(visible);
            if (!visible) return;

            ApplyScreenTints();
            ApplyPuddles();
            ApplySnowPatches();
            UpdateFootprints();
            TickFootprints();
        }

        private void LateUpdate()
        {
            if (currentCamera == null || screenRoot == null) return;
            screenRoot.position = currentCamera.transform.position;
            float height = currentCamera.orthographic ? currentCamera.orthographicSize * 2f : 20f;
            float width = height * currentCamera.aspect;
            wetDark.transform.localScale = new Vector3(width + 0.5f, height + 0.5f, 1f);
            snowTint.transform.localScale = new Vector3(width + 0.5f, height + 0.5f, 1f);
        }

        private void ApplyScreenTints()
        {
            if (wetDark != null) wetDark.color = new Color(0.05f, 0.09f, 0.14f, wetness * wetDarkAlpha);
            if (snowTint != null) snowTint.color = new Color(0.92f, 0.96f, 1f, snowDepth * snowTintAlpha);
        }

        private void PlacePuddles()
        {
            puddlesPlaced = true;
            ClearList(puddles);
            if (currentCamera == null || puddleCount <= 0) return;

            float height = currentCamera.orthographicSize * 2f;
            float width = height * currentCamera.aspect;
            Vector3 center = currentCamera.transform.position;
            for (int i = 0; i < puddleCount; i++)
            {
                SpriteRenderer puddle = CreateDecal("Puddle", puddleTexture, 30);
                float scale = RandomRange(1.2f, 3.2f);
                puddle.transform.localPosition = new Vector3(
                    center.x + RandomRange(-width * 0.5f - 1f, width * 0.5f + 1f),
                    center.y + RandomRange(-height * 0.5f - 1f, height * 0.5f + 1f), 0f);
                puddle.transform.localScale = new Vector3(scale, scale * RandomRange(0.32f, 0.48f), 1f);
                puddle.transform.localRotation = Quaternion.Euler(0f, 0f, RandomRange(0f, 360f));
                puddles.Add(puddle);
            }
        }

        private void ApplyPuddles()
        {
            for (int i = 0; i < puddles.Count; i++)
            {
                float factor = 0.55f + (i % 5) * 0.12f;
                // 提亮+提浓：深色地面上水洼要有可读的冷蓝反光
                Color color = new Color(0.13f, 0.22f, 0.32f, wetness * factor);
                puddles[i].color = color;
            }
        }

        private void PlaceSnowPatches()
        {
            snowPatchesPlaced = true;
            ClearList(snowPatches);
            if (currentCamera == null || snowPatchCount <= 0) return;

            float height = currentCamera.orthographicSize * 2f;
            float width = height * currentCamera.aspect;
            Vector3 center = currentCamera.transform.position;
            for (int i = 0; i < snowPatchCount; i++)
            {
                SpriteRenderer patch = CreateDecal("SnowPatch", snowPatchTexture, 20);
                float scale = RandomRange(2.4f, 6.5f);
                patch.transform.localPosition = new Vector3(
                    center.x + RandomRange(-width * 0.5f - 1.5f, width * 0.5f + 1.5f),
                    center.y + RandomRange(-height * 0.5f - 1.5f, height * 0.5f + 1.5f), 0f);
                patch.transform.localScale = new Vector3(scale, scale * RandomRange(0.5f, 0.8f), 1f);
                snowPatches.Add(patch);
            }
        }

        private void ApplySnowPatches()
        {
            for (int i = 0; i < snowPatches.Count; i++)
            {
                float factor = 0.3f + (i % 4) * 0.1f;
                snowPatches[i].color = new Color(0.95f, 0.97f, 1f, Mathf.Min(0.75f, snowDepth * 0.9f) * factor);
            }
        }

        private void UpdateFootprints()
        {
            if (playerBody == null)
            {
                GameObject player = GameObject.FindGameObjectWithTag("Player");
                if (player == null) return;
                playerBody = player.GetComponent<Rigidbody2D>();
                if (playerBody == null) return;
            }

            Vector2 velocity = playerBody.velocity;
            float speed = velocity.magnitude;
            if (snowDepth < footprintMinSnow || speed < 0.6f) return;

            footprintDistance += speed * Time.deltaTime;
            if (footprintDistance < footprintSpacing) return;
            footprintDistance = 0f;

            SpriteRenderer print = GetFootprintFromPool();
            Vector2 direction = velocity / speed;
            Vector2 side = new Vector2(-direction.y, direction.x) * (footprintSide ? 0.12f : -0.12f);
            footprintSide = !footprintSide;

            print.transform.localPosition = playerBody.transform.position + (Vector3)side;
            print.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            footprintAges[footprints.IndexOf(print)] = 0f;
        }

        private void TickFootprints()
        {
            for (int i = footprints.Count - 1; i >= 0; i--)
            {
                float age = footprintAges[i] + Time.deltaTime;
                footprintAges[i] = age;
                float fade = 1f - Mathf.Clamp01(age / Mathf.Max(1f, footprintLifetime));
                Color color = new Color(0.25f, 0.3f, 0.38f, fade * snowDepth * 0.55f);
                footprints[i].color = color;
            }
        }

        private SpriteRenderer GetFootprintFromPool()
        {
            for (int i = 0; i < footprints.Count; i++)
            {
                if (footprintAges[i] >= footprintLifetime) { return footprints[i]; }
            }
            if (footprints.Count >= 40)
            {
                int oldest = 0;
                for (int i = 1; i < footprintAges.Count; i++)
                    if (footprintAges[i] > footprintAges[oldest]) oldest = i;
                footprintAges[oldest] = 0f;
                return footprints[oldest];
            }

            SpriteRenderer print = CreateDecal("Footprint", footprintTexture, 40);
            print.transform.localScale = new Vector3(0.32f, 0.2f, 1f);
            footprints.Add(print);
            footprintAges.Add(0f);
            return print;
        }

        private SpriteRenderer CreateDecal(string objectName, Texture2D texture, int order)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(decalRoot, false);
            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), texture.width);
            renderer.sharedMaterial = decalMaterial;
            renderer.sortingLayerName = decalLayer;
            renderer.sortingOrder = order;
            return renderer;
        }

        private SpriteRenderer CreateScreenSprite(string objectName, Material material, int order)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(screenRoot, false);
            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            renderer.sharedMaterial = material;
            renderer.sortingLayerName = screenLayer;
            renderer.sortingOrder = order;
            return renderer;
        }

        private static void ClearList(List<SpriteRenderer> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) Destroy(list[i].gameObject);
            list.Clear();
        }

        private float RandomRange(float min, float max) => min + (float)random.NextDouble() * (max - min);

        /// <summary>水洼：椭圆渐变，边缘略亮模拟水面反光。</summary>
        private static Texture2D CreatePuddleTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center.x) / (size * 0.5f);
                    float dy = (y - center.y) / (size * 0.5f);
                    float distance = Mathf.Sqrt(dx * dx * 0.82f + dy * dy);
                    float body = Mathf.Clamp01(1f - distance);
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(distance - 0.88f) / 0.1f);
                    float alpha = Mathf.Clamp01(body * body * 0.8f + rim * 0.22f);
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
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha * alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        /// <summary>脚印：横向椭圆，中心略实、边缘柔化。</summary>
        private static Texture2D CreateFootprintTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center.x) / (size * 0.5f);
                    float dy = (y - center.y) / (size * 0.5f);
                    float ellipse = dx * dx / 0.72f + dy * dy / 0.16f;
                    float alpha = Mathf.Clamp01(1f - ellipse);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha * alpha));
                }
            }
            texture.Apply();
            return texture;
        }
    }
}