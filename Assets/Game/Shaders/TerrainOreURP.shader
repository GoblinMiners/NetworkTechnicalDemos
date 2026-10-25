// Starter URP material: volumetric rock/ore detail plus optional triplanar albedo.
// Gameplay ore identity comes from the cell catalogue, never from shader noise.
Shader "Gobmine/Terrain Ore URP"
{
    Properties
    {
        [MainColor] _BaseColor("Stone Colour",Color)=(0.32,0.34,0.37,1)
        _OreColor("Ore Colour",Color)=(1,0.65,0.12,1)
        [NoScaleOffset] _RockTexture("Optional Rock Albedo",2D)="white" {}
        _TextureScale("Texture Repeats Per Metre",Float)=1.5
        _VeinScale("Vein Detail Scale",Float)=4
        _VeinAmount("Visible Ore Veins",Range(0,0.65))=0.2
        _Smoothness("Stone Smoothness",Range(0,1))=0.2
        _OreMetallic("Ore Metallic",Range(0,1))=0.65
        [Toggle] _WorldSpace("World-space Projection (off for moving rubble)",Float)=1
    }
    SubShader
    {
        Tags {"RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "UniversalMaterialType"="Lit"}
        LOD 250
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        TEXTURE2D(_RockTexture); SAMPLER(sampler_RockTexture);
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor,_OreColor;
            float _TextureScale,_VeinScale,_VeinAmount,_Smoothness,_OreMetallic,_WorldSpace;
        CBUFFER_END
        struct Attributes {float4 positionOS:POSITION;float3 normalOS:NORMAL;UNITY_VERTEX_INPUT_INSTANCE_ID};
        struct Varyings
        {
            float4 positionCS:SV_POSITION;
            float3 positionWS:TEXCOORD0;
            float3 normalWS:TEXCOORD1;
            float3 texturePosition:TEXCOORD2;
            float3 textureNormal:TEXCOORD3;
            half fog:TEXCOORD4;
            float4 screenShadowCoord:TEXCOORD5;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };
        Varyings Vert(Attributes input)
        {
            Varyings o=(Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);UNITY_TRANSFER_INSTANCE_ID(input,o);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            VertexPositionInputs pos=GetVertexPositionInputs(input.positionOS.xyz);
            o.positionCS=pos.positionCS;o.positionWS=pos.positionWS;
            o.normalWS=TransformObjectToWorldNormal(input.normalOS);
            o.texturePosition=lerp(input.positionOS.xyz,pos.positionWS,_WorldSpace);
            o.textureNormal=lerp(input.normalOS,o.normalWS,_WorldSpace);
            o.fog=ComputeFogFactor(pos.positionCS.z);o.screenShadowCoord=GetShadowCoord(pos);return o;
        }
        float RockHash(float3 p){p=frac(p*0.1031);p+=dot(p,p.yzx+33.33);return frac((p.x+p.y)*p.z);}
        float RockNoise(float3 p)
        {
            float3 c=floor(p),f=frac(p);f=f*f*(3-2*f);
            return lerp(lerp(lerp(RockHash(c),RockHash(c+float3(1,0,0)),f.x),
                             lerp(RockHash(c+float3(0,1,0)),RockHash(c+float3(1,1,0)),f.x),f.y),
                        lerp(lerp(RockHash(c+float3(0,0,1)),RockHash(c+float3(1,0,1)),f.x),
                             lerp(RockHash(c+float3(0,1,1)),RockHash(c+float3(1,1,1)),f.x),f.y),f.z);
        }
        half4 Frag(Varyings i):SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(i);UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float3 normal=normalize(i.normalWS);
            float3 weights=pow(abs(normalize(i.textureNormal)),4);weights/=max(dot(weights,float3(1,1,1)),0.0001);
            float3 p=i.texturePosition*_TextureScale;
            half3 tex=SAMPLE_TEXTURE2D(_RockTexture,sampler_RockTexture,p.yz).rgb*weights.x+
                      SAMPLE_TEXTURE2D(_RockTexture,sampler_RockTexture,p.xz).rgb*weights.y+
                      SAMPLE_TEXTURE2D(_RockTexture,sampler_RockTexture,p.xy).rgb*weights.z;
            float detail=RockNoise(i.texturePosition*_VeinScale);
            float vein=_VeinAmount>0?1-smoothstep(_VeinAmount,_VeinAmount+0.08,abs(detail-0.5)*2):0;
            half3 stone=_BaseColor.rgb*tex*(0.65+0.55*RockNoise(i.texturePosition*8));
            SurfaceData surface=(SurfaceData)0;
            surface.albedo=lerp(stone,_OreColor.rgb*(0.7+0.35*detail),vein);
            surface.alpha=1;surface.normalTS=half3(0,0,1);surface.occlusion=1;
            surface.smoothness=lerp(_Smoothness,0.55,vein);surface.metallic=vein*_OreMetallic;
            InputData lighting=(InputData)0;
            lighting.positionWS=i.positionWS;lighting.positionCS=i.positionCS;lighting.normalWS=normal;
            lighting.viewDirectionWS=GetWorldSpaceNormalizeViewDir(i.positionWS);
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                lighting.shadowCoord=i.screenShadowCoord;
            #else
                lighting.shadowCoord=TransformWorldToShadowCoord(i.positionWS);
            #endif
            lighting.fogCoord=i.fog;
            lighting.vertexLighting=VertexLighting(i.positionWS,normal);lighting.bakedGI=SampleSH(normal);
            lighting.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);lighting.shadowMask=half4(1,1,1,1);
            half4 colour=UniversalFragmentPBR(lighting,surface);colour.rgb=MixFog(colour.rgb,i.fog);return colour;
        }
        half4 DepthFrag(Varyings i):SV_Target{return 0;}
        half4 NormalsFrag(Varyings i):SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float3 n=normalize(i.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct=PackNormalOctQuadEncode(n);return half4(PackFloat2To888(saturate(oct*0.5+0.5)),0);
            #else
                return half4(n,0);
            #endif
        }
        float3 _LightDirection,_LightPosition;
        Varyings ShadowVert(Attributes input)
        {
            Varyings o=Vert(input);
            float3 direction=_LightDirection;
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                direction=normalize(_LightPosition-o.positionWS);
            #endif
            o.positionCS=TransformWorldToHClip(ApplyShadowBias(o.positionWS,normalize(o.normalWS),direction));
            #if UNITY_REVERSED_Z
                o.positionCS.z=min(o.positionCS.z,o.positionCS.w*UNITY_NEAR_CLIP_VALUE);
            #else
                o.positionCS.z=max(o.positionCS.z,o.positionCS.w*UNITY_NEAR_CLIP_VALUE);
            #endif
            return o;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags {"LightMode"="UniversalForwardOnly"}
            Cull Back ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags {"LightMode"="ShadowCaster"}
            ZWrite On ZTest LEqual ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags {"LightMode"="DepthOnly"}
            ZWrite On ColorMask R Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags {"LightMode"="DepthNormalsOnly"}
            ZWrite On Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment NormalsFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
