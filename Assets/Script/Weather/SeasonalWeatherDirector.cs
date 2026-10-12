using System.Collections.Generic;
using Game.TimeOfDay;
using UnityEngine;

namespace Game.Weather
{
    /// <summary>
    /// 季节驱动的天气导演（王国：两位君主规则：春雨、夏雷、秋雾、冬雪）：
    /// - 按当前季节用权重随机挑天气，调用 WeatherSystem.SetWeather 播放；
    /// - 天气持续 min~max 秒后重掷，换季立即重掷；
    /// - 血月作为低概率事件混进每季权重；
    /// - 自动自举，也可挂场景物体调参。
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [DisallowMultipleComponent]
    public sealed class SeasonalWeatherDirector : MonoBehaviour
    {
        public static SeasonalWeatherDirector Instance { get; private set; }

        [Header("节奏")]
        [Tooltip("每种天气最短持续时间（秒）")]
        [SerializeField, Min(3f)] private float minWeatherSeconds = 30f;
        [Tooltip("每种天气最长持续时间（秒）")]
        [SerializeField, Min(3f)] private float maxWeatherSeconds = 90f;

        [Header("特殊天气")]
        [Tooltip("血月权重：叠在每季权重表上的低概率事件（0=禁用）")]
        [SerializeField, Min(0f)] private float bloodMoonWeight = 3f;

        private GameTimeManager time;
        private float timer;
        private readonly List<KeyValuePair<WeatherSystem.WeatherType, float>> weights =
            new List<KeyValuePair<WeatherSystem.WeatherType, float>>(8);
        private static readonly System.Random random = new System.Random(3571);

        public WeatherSystem.WeatherType LastRolled { get; private set; } = WeatherSystem.WeatherType.Sunny;
        public float SecondsUntilRoll => Mathf.Max(0f, timer);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            Instance = FindObjectOfType<SeasonalWeatherDirector>();
            if (Instance != null) return;
            GameObject go = new GameObject("SeasonalWeatherDirector");
            Instance = go.AddComponent<SeasonalWeatherDirector>();
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
        }

        private void OnDestroy()
        {
            if (time != null) time.OnSeasonChanged -= HandleSeasonChanged;
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (time == null)
            {
                time = GameTimeManager.Instance;
                if (time == null) return;
                time.OnSeasonChanged += HandleSeasonChanged;
                timer = 0f; // 首次接管后立即掷一次
            }

            timer -= Time.deltaTime;
            if (timer <= 0f) Roll();
        }

        /// <summary>按当前季节重掷天气。</summary>
        public void Roll()
        {
            WeatherSystem.WeatherType picked = PickWeighted(
                time != null ? time.CurrentSeason : GameTimeManager.Season.Spring);
            timer = RandomRange(minWeatherSeconds, maxWeatherSeconds);
            LastRolled = picked;

            if (WeatherSystem.Instance != null)
                WeatherSystem.Instance.SetWeather(picked);
        }

        private void HandleSeasonChanged(GameTimeManager.Season season)
        {
            timer = 0f; // 换季立即重掷，让新季节的天气特色马上出现
        }

        private WeatherSystem.WeatherType PickWeighted(GameTimeManager.Season season)
        {
            weights.Clear();
            switch (season)
            {
                case GameTimeManager.Season.Spring:
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Sunny, 40f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Cloudy, 15f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Rain, 30f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Fog, 10f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Storm, 5f));
                    break;
                case GameTimeManager.Season.Summer:
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Sunny, 52f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Cloudy, 12f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Storm, 16f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Windy, 12f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Rain, 8f));
                    break;
                case GameTimeManager.Season.Autumn:
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Sunny, 34f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Cloudy, 16f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Fog, 22f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Windy, 20f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Rain, 8f));
                    break;
                case GameTimeManager.Season.Winter:
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Snow, 62f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Cloudy, 14f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Fog, 16f));
                    weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.Windy, 8f));
                    break;
            }
            if (bloodMoonWeight > 0f)
                weights.Add(new KeyValuePair<WeatherSystem.WeatherType, float>(WeatherSystem.WeatherType.BloodMoon, bloodMoonWeight));

            float total = 0f;
            for (int i = 0; i < weights.Count; i++) total += weights[i].Value;

            double r = random.NextDouble() * total;
            for (int i = 0; i < weights.Count; i++)
            {
                r -= weights[i].Value;
                if (r <= 0.0) return weights[i].Key;
            }
            return WeatherSystem.WeatherType.Sunny;
        }

        private static float RandomRange(float min, float max) =>
            min + (float)random.NextDouble() * Mathf.Max(0f, max - min);
    }
}