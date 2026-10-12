// 远景背景雨：单面片噪声滚动雨丝（低开销，仅视觉，无碰撞无水花）。
// 挂在 SpriteRenderer（1x1 白图）上，置于 Background 排序层、场景物体后方。
// 参数说明：
//   _Opacity    雨丝整体透明度（与主雨强度联动，0 = 不可见）
//   _Density    雨丝密度：多少比例的“雨柱”存在雨丝（与主雨强度联动）
//   _Columns    横向雨柱数量（越大雨丝越细密）
//   _ScrollSpeed 纵向滚动速度（向下 = 雨落）
//   _WindTilt   风倾斜：uv.y 随 uv.x 的斜率，非 0 时雨丝呈斜线
Shader "Rain/FarRainQuad"
{
    Properties
    {
        _TintColor ("Tint", Color) = (0.60, 0.72, 0.86, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0
        _Density ("Density", Range(0, 1)) = 0.5
        _Columns ("Columns", Float) = 30
        _ScrollSpeed ("Scroll Speed", Float) = 3.2
        _WindTilt ("Wind Tilt", Float) = -0.35
        _EdgeFade ("Edge Fade", Range(0.01, 0.5)) = 0.18
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "Unlit"

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                half _Opacity;
                half _Density;
                float _Columns;
                float _ScrollSpeed;
                float _WindTilt;
                half _EdgeFade;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            // 一维哈希：给每根“雨柱”随机相位/存在性
            half Hash11 (half p)
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                p *= p + p;
                return frac(p);
            }

            // 单层雨丝：把面片 UV 划分成若干纵向雨柱，
            // 每柱随机决定是否存在雨丝并随机抖动位置（打破等距栅栏感）；
            // 柱内用横向 smoothstep 收窄成细丝，纵向 UV 随时间向下滚动，
            // 雨段短促、头部亮尾部渐隐——避免长竖条带来的“光柱”感。
            //
            // 精度陷阱：_Time.y 会一直增大，half 精度下大数的小数位被吞掉，
            // frac 相位退化 → 画面出现贯穿全屏的竖向亮带（“光柱”）。
            // 因此：t 取模 12 循环，且 period 只取 {0.5, 0.75, 1.0}——
            // 12×每档均为整数，t 循环回绕时相位无缝；大数值运算全部在 float 上做。
            half RainLayer (float2 uv, half columns, half speedMul, half seed)
            {
                float t = fmod(_Time.y * _ScrollSpeed * speedMul, 12.0);
                // 纵向：下落滚动 + 风倾斜（斜向滚动）；横向不做时间漂移，避免列图案回绕跳变
                float scrollY = uv.y + t + uv.x * (float)_WindTilt;

                float colF = floor(uv.x * (float)columns);
                float fxF = frac(uv.x * (float)columns);
                half col = (half)colF;
                half rnd = Hash11(col + seed);
                half rnd2 = Hash11((half)fmod(colF * 1.37 + (float)seed + 3.1, 251.0));

                // 每列横向随机抖动：雨柱间距不再等距，消除“栅栏/光柱阵列”感
                float fxD = frac(fxF + ((float)rnd2 - 0.5) * 0.7);

                // 密度阈值：rnd < density 的雨柱才有雨丝（epsilon 防止 density=0 时 rnd≈0 的列仍然出现）
                half exists = step(rnd + 0.001, saturate(_Density * 1.15));

                // 横向：更细的丝（半宽 16%）
                half streakX = smoothstep(0.5, 0.16, abs((half)fxD - 0.5) * 2.0);

                // 纵向：短雨段，头部（下端）亮、向上快速衰减 = 短尾迹；
                // period 离散三档（0.5/0.75/1.0），相位按列随机错开
                float period = rnd2 < 0.33 ? 0.5 : (rnd2 < 0.66 ? 0.75 : 1.0);
                half y = (half)frac(scrollY * period + (float)rnd * 7.0);
                half head = smoothstep(0.0, 0.12, y);      // 下端快速起亮 = 雨丝头部
                half tail = smoothstep(0.62, 0.28, y);     // 向上快速衰减 = 短尾迹
                half fall = head * tail * tail;

                return exists * streakX * fall;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // 两层不同速度/粗细的雨丝叠加出纵深
                half rain = RainLayer(IN.uv, (half)_Columns, 1.0, 17.13)
                          + RainLayer(IN.uv, (half)_Columns * 1.7, 1.45, 41.7) * 0.65;

                // 面片四边淡出，避免贴图边缘出现“雨帘断口”
                half2 e = min(IN.uv, 1.0 - IN.uv);
                half edge = smoothstep(0.0, _EdgeFade, e.x) * smoothstep(0.0, _EdgeFade, e.y);

                half a = saturate(rain) * _Opacity * edge;
                return half4(_TintColor.rgb, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
