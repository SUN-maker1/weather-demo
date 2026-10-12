using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.Weather
{
    /// <summary>
    /// 场景布置与天气联动（王国：两位君主式 color script）：
    /// - 天空底色按天气平滑插值，血月整体转暗红；
    /// - 月亮在血月时染红并微微脉动；
    /// - 篝火：随机闪烁；雨/雪天火光更暖更亮（风雪夜围火），血月微微转红压暗。
    /// 引用在场景搭建时通过 Inspector 指定。
    /// </summary>
    public sealed class DressingWeatherLink : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer moon;
        [SerializeField] private Light2D campfireLight;
        [SerializeField] private SpriteRenderer campfireGlow;
        [Tooltip("血月时染成暗红棕的树剪影（可选）")]
        [SerializeField] private SpriteRenderer[] trees;
        [Tooltip("篝火基础强度")]
        [SerializeField] private float campfireIntensity = 1.1f;
        [Tooltip("大风时树冠摇摆的最大角度（度）")]
        [SerializeField] private float treeSwayMaxAngle = 4.5f;
        [Tooltip("树摇摆时树冠的水平摆动幅度（世界单位）")]
        [SerializeField] private float treeSwayOffset = 0.06f;

        private WeatherSystem weather;
        private Camera cam;
        private float cloudClock;
        private Vector3[] treeBasePositions;
        private float[] treeBaseRotations;
        private float[] treePhases;

        private void EnsureTreeBases()
        {
            if (treeBasePositions != null && treeBasePositions.Length == trees.Length) return;
            int count = trees.Length;
            treeBasePositions = new Vector3[count];
            treeBaseRotations = new float[count];
            treePhases = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (trees[i] == null) continue;
                treeBasePositions[i] = trees[i].transform.position;
                treeBaseRotations[i] = trees[i].transform.eulerAngles.z;
                treePhases[i] = i * 1.37f;
            }
        }

        private static Color SkyOf(WeatherSystem.WeatherType type)
        {
            switch (type)
            {
                case WeatherSystem.WeatherType.Cloudy: return new Color(0.40f, 0.45f, 0.48f);
                case WeatherSystem.WeatherType.Rain: return new Color(0.19f, 0.26f, 0.32f);
                case WeatherSystem.WeatherType.Storm: return new Color(0.10f, 0.13f, 0.19f);
                case WeatherSystem.WeatherType.Fog: return new Color(0.53f, 0.58f, 0.56f);
                case WeatherSystem.WeatherType.Snow: return new Color(0.58f, 0.64f, 0.70f);
                case WeatherSystem.WeatherType.Windy: return new Color(0.48f, 0.60f, 0.50f);
                case WeatherSystem.WeatherType.BloodMoon: return new Color(0.24f, 0.07f, 0.07f);
                default: return new Color(0.47f, 0.66f, 0.55f); // 晴天
            }
        }

        private void Update()
        {
            if (weather == null)
            {
                weather = WeatherSystem.Instance;
                if (weather == null) return;
            }
            if (cam == null || cam != Camera.main) cam = Camera.main;

            // 1) 天空底色：按过渡进度在前后两种天气的天空色之间插值
            float blend = Mathf.SmoothStep(0f, 1f, weather.TransitionProgress);
            Color sky = Color.Lerp(SkyOf(weather.PreviousWeather), SkyOf(weather.CurrentWeather), blend);
            float blood = weather.CurrentBloodMoonStrength;
            sky = Color.Lerp(sky, new Color(0.15f, 0.04f, 0.05f), blood * 0.85f);
            if (cam != null) cam.backgroundColor = sky;

            // 2) 月亮：血月染成深猩红 + 低频脉动
            if (moon != null)
            {
                cloudClock += Time.deltaTime;
                Color moonBlood = new Color(0.72f, 0.10f, 0.08f);
                moon.color = Color.Lerp(Color.Lerp(Color.white, new Color(0.92f, 0.9f, 0.82f), 0.2f), moonBlood, blood);
                float pulse = 1f + blood * 0.04f * Mathf.Sin(cloudClock * (2f * Mathf.PI / 3.4f));
                moon.transform.localScale = Vector3.one * 2.2f * pulse;
            }

            // 2.5) 树剪影：血月时从暗绿染向暗红棕，消除“绿色污染”；
            //      起风时整排树按不同相位摇摆——风的表现主要靠树冠动，而不是屏幕上的风线。
            if (trees != null)
            {
                EnsureTreeBases();
                float windStrength = weather.CurrentWindStrength;
                float gust = 0.6f + 0.4f * Mathf.PerlinNoise(Time.time * 0.5f, 4.7f);
                float sway = windStrength * gust;
                Color treeBlood = new Color(0.30f, 0.12f, 0.10f);
                Color treeNormal = Color.white;
                for (int i = 0; i < trees.Length; i++)
                {
                    if (trees[i] == null) continue;
                    trees[i].color = Color.Lerp(treeNormal, treeBlood, blood);
                    Transform t = trees[i].transform;
                    float wave = Mathf.Sin(Time.time * (1.05f + i * 0.17f) + treePhases[i]);
                    t.rotation = Quaternion.Euler(0f, 0f, treeBaseRotations[i] + wave * treeSwayMaxAngle * sway);
                    Vector3 p = treeBasePositions[i];
                    p.x += wave * treeSwayOffset * sway;
                    t.position = p;
                }
            }

            // 3) 篝火：闪烁 + 雨/雪更暖更亮 + 血月微红
            if (campfireLight != null)
            {
                float flicker = 0.86f + 0.14f * Mathf.PerlinNoise(Time.time * 3.1f, 9.7f)
                    + 0.08f * Mathf.PerlinNoise(Time.time * 7.7f, 3.3f);
                float warmth = Mathf.Clamp01(weather.CurrentSnowStrength * 0.55f + weather.CurrentRainStrength * 0.25f);
                float intensity = campfireIntensity * (0.9f + 0.35f * warmth) * flicker;
                campfireLight.intensity = Mathf.Lerp(campfireLight.intensity, intensity, Time.deltaTime * 6f);
                Color fire = Color.Lerp(new Color(1f, 0.62f, 0.30f), new Color(1f, 0.52f, 0.22f), warmth);
                fire = Color.Lerp(fire, new Color(0.95f, 0.35f, 0.2f), blood * 0.45f);
                campfireLight.color = fire;
                if (campfireGlow != null)
                {
                    Color g = campfireGlow.color;
                    campfireGlow.color = new Color(fire.r, fire.g, fire.b, 0.30f * flicker * (1f + warmth * 0.4f));
                }
            }
        }
    }
}