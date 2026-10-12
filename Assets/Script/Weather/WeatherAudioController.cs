using System.Collections;
using UnityEngine;

namespace Game.Weather
{
    /// <summary>
    /// 天气音频分层控制器（自动自举，无需挂场景物体）：
    /// - 环境层：雨声、风声循环，音量跟随 WeatherSystem 强度平滑淡入淡出；
    /// - 事件层：雷声在闪电事件后延迟 0.6~1.7 秒炸响，保留距离感；
    /// - 氛围层：血月低频 drone + 心跳脉冲；
    /// - 雾天：给 AudioListener 挂 AudioLowPassFilter，让所有声音整体变闷；
    /// - 天气隐藏（室内）时环境音整体压低，保留“隔着墙”的临场感。
    /// 音频文件放 Assets/Resources/Weather/Audio/：
    /// rain_loop / wind_loop / thunder_far / blood_drone / heartbeat。
    /// 缺文件只警告一次并静默跳过，不影响游戏运行。
    /// </summary>
    public sealed class WeatherAudioController : MonoBehaviour
    {
        private static WeatherAudioController instance;

        private const string RainClipPath = "Weather/Audio/rain_loop";
        private const string WindClipPath = "Weather/Audio/wind_loop";
        private const string ThunderClipPath = "Weather/Audio/thunder_far";
        private const string DroneClipPath = "Weather/Audio/blood_drone";
        private const string HeartbeatClipPath = "Weather/Audio/heartbeat";

        [Header("音量（0~1）")]
        [SerializeField, Range(0f, 1f)] private float rainVolume = 0.8f;
        [SerializeField, Range(0f, 1f)] private float windVolume = 0.45f;
        [SerializeField, Range(0f, 1f)] private float thunderVolume = 0.9f;
        [SerializeField, Range(0f, 1f)] private float droneVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] private float heartbeatVolume = 0.32f;
        [Tooltip("室内/天气隐藏时环境音保留的比例")]
        [SerializeField, Range(0f, 1f)] private float indoorDuck = 0.22f;
        [Tooltip("雾天低通截止频率（越低越闷）")]
        [SerializeField, Range(300f, 4000f)] private float fogCutoffHz = 1100f;

        private WeatherSystem weather;
        private AudioSource rainSource;
        private AudioSource windSource;
        private AudioSource droneSource;
        private AudioSource sfxSource;
        private AudioClip thunderClip;
        private AudioClip heartbeatClip;
        private AudioListener attachedListener;
        private AudioLowPassFilter listenerFilter;
        private float bloodPulse;
        private float heartbeatTimer = 1.2f;
        private bool warnedMissingClips;

        public static WeatherAudioController Instance => instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            GameObject go = new GameObject("WeatherAudio");
            instance = go.AddComponent<WeatherAudioController>();
            DontDestroyOnLoad(go);
        }

        private void OnDestroy()
        {
            if (weather != null) weather.OnLightningStrike -= HandleLightningStrike;
            if (instance == this) instance = null;
        }

        private void Start()
        {
            rainSource = CreateLoopSource("WeatherRain", RainClipPath);
            windSource = CreateLoopSource("WeatherWind", WindClipPath);
            droneSource = CreateLoopSource("BloodDrone", DroneClipPath);

            GameObject sfxGo = new GameObject("WeatherSfx");
            sfxGo.transform.SetParent(transform, false);
            sfxSource = sfxGo.AddComponent<AudioSource>();
            sfxSource.playOnAwake = false;
            sfxSource.loop = false;
            sfxSource.spatialBlend = 0f;

            thunderClip = Resources.Load<AudioClip>(ThunderClipPath);
            heartbeatClip = Resources.Load<AudioClip>(HeartbeatClipPath);

            if (weather != null) weather.OnLightningStrike += HandleLightningStrike;
        }

        private void Update()
        {
            if (weather == null)
            {
                weather = WeatherSystem.Instance;
                if (weather == null) return;
                weather.OnLightningStrike += HandleLightningStrike;
            }

            WarnMissingClipsOnce();
            bloodPulse = Mathf.MoveTowards(bloodPulse, weather.CurrentBloodMoonStrength, Time.deltaTime * 0.4f);

            // 环境层：天气隐藏时压低到隔墙听感，室外跟随天气强度呼吸
            float duck = weather.IsWeatherVisible ? 1f : indoorDuck;
            MoveVolume(rainSource, weather.CurrentRainStrength * rainVolume * duck);
            MoveVolume(windSource, weather.CurrentWindStrength * windVolume * duck);
            MoveVolume(droneSource, bloodPulse * droneVolume);

            UpdateFogMuffle(weather.CurrentFogStrength);
            UpdateHeartbeat();
        }

        private void UpdateHeartbeat()
        {
            if (sfxSource == null || heartbeatClip == null || bloodPulse <= 0.35f) return;
            heartbeatTimer -= Time.deltaTime;
            if (heartbeatTimer <= 0f)
            {
                heartbeatTimer = 1.15f;
                sfxSource.PlayOneShot(heartbeatClip, heartbeatVolume * bloodPulse);
            }
        }

        private void HandleLightningStrike()
        {
            if (thunderClip == null || sfxSource == null) return;
            StartCoroutine(PlayThunderAfterDelay(Random.Range(0.6f, 1.7f)));
        }

        private IEnumerator PlayThunderAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (sfxSource == null || thunderClip == null || weather == null) yield break;
            // 暴雨越猛雷声越近（音量越大），保持“先闪电后雷声”的自然层次
            sfxSource.PlayOneShot(thunderClip, thunderVolume * Mathf.Clamp01(weather.CurrentStormStrength + 0.3f));
        }

        private void UpdateFogMuffle(float fog)
        {
            if (attachedListener == null)
            {
                AudioListener found = FindObjectOfType<AudioListener>();
                if (found == null) return;
                attachedListener = found;
                listenerFilter = found.GetComponent<AudioLowPassFilter>();
                if (listenerFilter == null) listenerFilter = found.gameObject.AddComponent<AudioLowPassFilter>();
            }

            float target = Mathf.Lerp(22000f, fogCutoffHz, Mathf.Clamp01(fog * 1.2f));
            listenerFilter.cutoffFrequency = Mathf.MoveTowards(listenerFilter.cutoffFrequency, target, Time.deltaTime * 9000f);
        }

        private AudioSource CreateLoopSource(string objectName, string clipPath)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            AudioClip clip = Resources.Load<AudioClip>(clipPath);
            if (clip != null)
            {
                source.clip = clip;
                source.Play();
            }
            return source;
        }

        private static void MoveVolume(AudioSource source, float target)
        {
            if (source == null) return;
            source.volume = Mathf.MoveTowards(source.volume, Mathf.Clamp01(target), Time.deltaTime * 0.35f);
        }

        private void WarnMissingClipsOnce()
        {
            if (warnedMissingClips) return;
            if (rainSource == null || rainSource.clip == null)
            {
                warnedMissingClips = true;
                Debug.LogWarning("[WeatherAudio] Resources/Weather/Audio/ 下缺少天气音频（rain_loop/wind_loop/thunder_far/blood_drone/heartbeat），音频层静默跳过。");
            }
        }
    }
}