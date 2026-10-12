// 中景主雨粒子材质：URP Unlit（无光照）+ Additive 叠加。
// 等价于 "URP/2D Unlit Transparent + 叠加 Additive"：内置 2D Sprite-Unlit-Transparent
// 固定为普通 Alpha 混合，无法开启 Additive，因此这里手写等价 Unlit 着色器。
// 顶点色来自粒子系统的 startColor / ColorOverLifetime，贴图为运行时生成的细长雨丝。
Shader "Rain/ParticleStreakAdditive"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "white" {}
        _TintColor ("Tint", Color) = (1, 1, 1, 1)
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

            // Additive：rain.rgb * alpha 叠加到画面，越亮越透
            Blend SrcAlpha One
            ZWrite Off
            Cull Off
            Lighting Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 粒子系统走 instanced 渲染，必须带实例化宏
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;          // 粒子顶点色
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                float4 _BaseMap_ST;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.color = IN.color * _TintColor;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                return tex * IN.color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
