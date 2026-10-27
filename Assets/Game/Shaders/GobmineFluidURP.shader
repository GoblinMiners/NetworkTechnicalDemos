Shader "Gobmine/Expedition Fluid URP"
{
    Properties
    {
        _BaseColor("Fluid Color", Color) = (0.05,0.35,0.65,0.65)
        _Glow("Glow", Float) = 0
        [Toggle] _ZWrite("Depth Write", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Glow;
                float _ZWrite;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 world:TEXCOORD0; float fog:TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.world=TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS=TransformWorldToHClip(output.world);
                output.fog=ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                float ripple=0.90+0.10*sin(input.world.x*2.7+input.world.z*1.9+_Time.y*1.5)*sin(input.world.z*3.1-_Time.y);
                half3 color=_BaseColor.rgb*ripple*(1+_Glow);
                return half4(MixFog(color,input.fog),_BaseColor.a);
            }
            ENDHLSL
        }
    }
}
