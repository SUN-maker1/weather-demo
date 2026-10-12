using System;
using UnityEngine;

namespace Game.TimeOfDay
{
    /// <summary>
    /// 昼夜与四季时钟（王国：两位君主规则）：
    /// - 一天 = dayDurationSeconds 秒（原作约 240 秒）；一季 = daysPerSeason 天；四季循环；
    /// - 时刻 0~1：0=清晨，0.25=正午，0.5=黄昏，0.75=午夜；
    /// - 事件驱动：OnDayStarted / OnSeasonChanged / OnTimeChanged；
    /// - 自动自举（无需挂场景），也可挂场景物体用 Inspector 调参。
    /// 与 Game.Weather.WeatherSystem 的关系：本系统只负责“时间”，天气调色由 WeatherSystem 读取 TimeOfDay 完成。
    /// </summary>
    [DefaultExecutionOrder(-120)]
    [DisallowMultipleComponent]
    public sealed class GameTimeManager : MonoBehaviour
    {
        public enum Season { Spring, Summer, Autumn, Winter }

        public static GameTimeManager Instance { get; private set; }

        [Header("时间设置")]
        [Tooltip("一个完整昼夜的真实时长（秒），原作约 240")]
        [SerializeField, Min(1f)] private float dayDurationSeconds = 240f;
        [Tooltip("每个季节持续的天数（原作 16 天）")]
        [SerializeField, Min(1)] private int daysPerSeason = 16;

        [Header("调试")]
        [SerializeField] private bool showDebugGui = true;
        [Tooltip("时间倍速（调试用，正式游戏保持 1）")]
        [SerializeField, Min(0.1f)] private float timeScale = 1f;

        /// <summary>当前时刻 0~1：0=清晨，0.25=正午，0.5=黄昏，0.75=午夜</summary>
        public float TimeOfDay { get; private set; } = 0.25f;
        public int DayNumber { get; private set; } = 1;
        public Season CurrentSeason { get; private set; } = Season.Spring;

        public event Action<int> OnDayStarted;
        public event Action<Season> OnSeasonChanged;
        public event Action<float> OnTimeChanged;

        public static string SeasonChinese(Season s)
        {
            switch (s)
            {
                case Season.Summer: return "夏季";
                case Season.Autumn: return "秋季";
                case Season.Winter: return "冬季";
                default: return "春季";
            }
        }

        /// <summary>时段名（清晨/白天/黄昏/夜晚），给 GUI 和调色调试用。</summary>
        public string PhaseChinese()
        {
            float t = TimeOfDay;
            if (t < 0.12f || t >= 0.88f) return "黎明";
            if (t < 0.40f) return "白天";
            if (t < 0.62f) return "黄昏";
            return "夜晚";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            Instance = FindObjectOfType<GameTimeManager>();
            if (Instance != null) return;
            GameObject go = new GameObject("GameTimeManager");
            Instance = go.AddComponent<GameTimeManager>();
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
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            TimeOfDay += Time.deltaTime * timeScale / Mathf.Max(1f, dayDurationSeconds);
            if (TimeOfDay >= 1f)
            {
                TimeOfDay -= 1f;
                AdvanceDay();
            }
            OnTimeChanged?.Invoke(TimeOfDay);
        }

        private void AdvanceDay()
        {
            DayNumber++;
            OnDayStarted?.Invoke(DayNumber);

            int seasonIndex = (DayNumber - 1) / Mathf.Max(1, daysPerSeason);
            Season newSeason = (Season)(seasonIndex % 4);
            if (newSeason != CurrentSeason)
            {
                CurrentSeason = newSeason;
                OnSeasonChanged?.Invoke(CurrentSeason);
                Debug.Log($"[时间] 进入 {SeasonChinese(CurrentSeason)}，第 {DayNumber} 天");
            }
        }

        private void OnGUI()
        {
            if (!showDebugGui) return;

            GUILayout.BeginArea(new Rect(12f, 12f, 240f, 110f), GUI.skin.box);
            GUILayout.Label($"第 {DayNumber} 天 · {SeasonChinese(CurrentSeason)} · {PhaseChinese()}");
            GUILayout.Label($"时刻 {TimeOfDay * 100f:0}%");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("x1")) timeScale = 1f;
            if (GUILayout.Button("x5")) timeScale = 5f;
            if (GUILayout.Button("x20")) timeScale = 20f;
            if (GUILayout.Button("跳到午夜")) TimeOfDay = 0.75f;
            GUILayout.EndHorizontal();
            GUILayout.Label("(调试面板，Inspector 可关)");
            GUILayout.EndArea();
        }
    }
}