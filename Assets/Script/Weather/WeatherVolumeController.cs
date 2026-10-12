using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Weather
{
    /// <summary>
    /// 天气后处理调色（自动自举，无需挂场景物体）：
    /// - 优先绑场景里已有的 isGlobal Volume，找不到自动创建 "Global Volume"；
    /// - ColorAdjustments（曝光/对比/饱和/色调滤镜）+ Vignette 暗角按天气预设平滑插值；
    /// - 血月：暗角颜色转深红并做约 3.4 秒周期的低频脉冲，克制不干扰操作；
    /// - 顺带确保主相机开启后处理。
    /// </summary>
    public sealed class WeatherVolumeController : MonoBehaviour
    {
        private static WeatherVolumeController instance;

        private struct Grade
        {
            public float exposure;
            public float contrast;
            public float saturation;
            public float vignette;
            public Color filter;
            public Color vignetteColor;
        }

        private WeatherSystem weather;
        private Volume volume;
        private ColorAdjustments adjustments;
        private Vignette vignette;
        private Grade current;

        public static WeatherVolumeController Instance => instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (instance != null) return;
            GameObject go = new GameObject("WeatherVolume");
            instance = go.AddComponent<WeatherVolumeController>();
            DontDestroyOnLoad(go);
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (weather == null)
            {
                weather = WeatherSystem.Instance;
                if (weather == null) return;
            }

            EnsureVolume();
            if (adjustments == null || vignette == null) return;

            Grade from = GetGrade(weather.PreviousWeather);
            Grade to = GetGrade(weather.CurrentWeather);
            float blend = Mathf.SmoothStep(0f, 1f, weather.TransitionProgress);
            current.exposure = Mathf.Lerp(from.exposure, to.exposure, blend);
            current.contrast = Mathf.Lerp(from.contrast, to.contrast, blend);
            current.saturation = Mathf.Lerp(from.saturation, to.saturation, blend);
            current.vignette = Mathf.Lerp(from.vignette, to.vignette, blend);
            current.filter = Color.Lerp(from.filter, to.filter, blend);
            current.vignetteColor = Color.Lerp(from.vignetteColor, to.vignetteColor, blend);

            adjustments.postExposure.value = current.exposure;
            adjustments.contrast.value = current.contrast;
            adjustments.saturation.value = current.saturation;
            adjustments.colorFilter.value = current.filter;

            // 血月暗角脉冲：低频呼吸叠加在基础暗角上。
            float blood = weather.CurrentBloodMoonStrength;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * (2f * Mathf.PI / 3.4f));
            vignette.intensity.value = Mathf.Clamp01(current.vignette + blood * (0.06f + pulse * 0.1f));
            vignette.color.value = Color.Lerp(current.vignetteColor, new Color(0.28f, 0.01f, 0.03f), blood);
        }

        private void EnsureVolume()
        {
            if (volume != null && adjustments != null && vignette != null) return;

            if (volume == null)
            {
                foreach (Volume candidate in FindObjectsOfType<Volume>())
                {
                    if (candidate.isGlobal)
                    {
                        volume = candidate;
                        break;
                    }
                }
                if (volume == null)
                {
                    GameObject go = new GameObject("Global Volume");
                    volume = go.AddComponent<Volume>();
                    volume.isGlobal = true;
                }
            }

            VolumeProfile profile = volume.profile; // 运行时实例副本，不改资产
            if (!profile.TryGet(out adjustments)) adjustments = profile.Add<ColorAdjustments>(true);
            if (!profile.TryGet(out vignette)) vignette = profile.Add<Vignette>(true);

            adjustments.active = true;
            adjustments.postExposure.overrideState = true;
            adjustments.contrast.overrideState = true;
            adjustments.saturation.overrideState = true;
            adjustments.colorFilter.overrideState = true;

            vignette.active = true;
            vignette.intensity.overrideState = true;
            vignette.color.overrideState = true;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.4f;

            // 确保主相机开启后处理（场景里已配置时此处只是兜底）。
            Camera cam = Camera.main;
            if (cam != null)
            {
                UniversalAdditionalCameraData data = cam.GetComponent<UniversalAdditionalCameraData>();
                if (data != null) data.renderPostProcessing = true;
            }
        }

        private static Grade GetGrade(WeatherSystem.WeatherType type)
        {
            switch (type)
            {
                case WeatherSystem.WeatherType.Cloudy:
                    return GradeOf(-0.15f, -3f, -12f, 0.14f, new Color(0.95f, 0.97f, 1.00f), Color.black);
                case WeatherSystem.WeatherType.Rain:
                    return GradeOf(-0.26f, 4f, -18f, 0.20f, new Color(0.88f, 0.93f, 1.05f), new Color(0.02f, 0.04f, 0.08f));
                case WeatherSystem.WeatherType.Storm:
                    return GradeOf(-0.45f, 10f, -26f, 0.32f, new Color(0.80f, 0.86f, 1.00f), Color.black);
                case WeatherSystem.WeatherType.Fog:
                    return GradeOf(-0.20f, -8f, -22f, 0.24f, new Color(0.95f, 1.00f, 0.97f), new Color(0.45f, 0.50f, 0.47f));
                case WeatherSystem.WeatherType.Snow:
                    return GradeOf(0.20f, -4f, -14f, 0.16f, new Color(1.00f, 1.02f, 1.08f), Color.black);
                case WeatherSystem.WeatherType.Windy:
                    return GradeOf(-0.05f, 2f, -6f, 0.12f, Color.white, Color.black);
                case WeatherSystem.WeatherType.BloodMoon:
                    // 血月：colorFilter 保持接近白（红色已由全局光承担，否则三重相乘会变死黑），
                    // 只做轻压暗 + 暗角脉冲。
                    return GradeOf(-0.12f, 6f, 8f, 0.34f, new Color(0.94f, 0.62f, 0.58f), new Color(0.25f, 0.00f, 0.02f));
                default:
                    return GradeOf(0f, 0f, 0f, 0.12f, Color.white, Color.black);
            }
        }

        private static Grade GradeOf(float exposure, float contrast, float saturation, float vignette, Color filter, Color vignetteColor)
        {
            Grade grade;
            grade.exposure = exposure;
            grade.contrast = contrast;
            grade.saturation = saturation;
            grade.vignette = vignette;
            grade.filter = filter;
            grade.vignetteColor = vignetteColor;
            return grade;
        }
    }
}