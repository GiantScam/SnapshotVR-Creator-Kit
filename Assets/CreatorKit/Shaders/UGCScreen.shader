// Screen material for UGC maps: the one surface the game writes to at runtime.
//
// The live game finds every object tagged UGCScoreboardScreen and pushes the live scoreboard
// render texture into a MaterialPropertyBlock as "_Image". A MaterialPropertyBlock set on a shader
// that has no such property is a SILENT no-op - no warning, no error, the mesh just keeps its
// authored colour. A screen on an ordinary lit material (URP/Lit has no _Image) therefore never
// displays a score, and nothing tells you.
//
// So this exists: the smallest possible URP-compatible unlit shader whose texture slot is named
// _Image. Unlit on purpose - a screen should read at full brightness under any lighting a creator
// has, and it costs nothing.
//
// The instancing/stereo macros are not optional. PC VR renders both eyes in one instanced draw
// (single-pass instanced); a shader without them only ever lands in the left eye. Quest's multiview
// path happens to cope without them, which is how this shipped with a right-eye-blank scoreboard.
Shader "UGC/Screen"
{
    Properties
    {
        _Image ("Screen Image", 2D) = "black" {}
        _BaseColor ("Image Tint", Color) = (1, 1, 1, 1)
        // Added, not multiplied, so an un-injected screen (no texture yet, or a creator just
        // eyeballing the map in the editor) reads as a dark powered-off panel rather than a void.
        _OffColor ("Unpowered Colour", Color) = (0.04, 0.045, 0.06, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Name "UGCScreenUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_Image);
            SAMPLER(sampler_Image);

            // One CBUFFER named UnityPerMaterial keeps this SRP Batcher compatible.
            CBUFFER_START(UnityPerMaterial)
                float4 _Image_ST;
                half4 _BaseColor;
                half4 _OffColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _Image);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                half3 img = SAMPLE_TEXTURE2D(_Image, sampler_Image, IN.uv).rgb * _BaseColor.rgb;
                return half4(img + _OffColor.rgb, 1.0h);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Unlit"
}
