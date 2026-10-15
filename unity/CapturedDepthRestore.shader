// Uploads a captured depth attachment; this is a replay adapter, not a game Shader.
Shader "Hidden/RenderDoc/CapturedDepthRestore"
{
    Properties { _MainTex ("Captured depth", 2D) = "black" {} }
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest Always
            Cull Off
            ColorMask 0
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment RestoreDepth
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float RestoreDepth(v2f_img input) : SV_Depth
            {
                return tex2D(_MainTex, input.uv).r;
            }
            ENDCG
        }
        Pass
        {
            Name "ProbeGreaterEqual"
            ZWrite Off
            ZTest GEqual
            Cull Off
            CGPROGRAM
            #pragma vertex ProbeVertex
            #pragma fragment ProbeColor
            #include "UnityCG.cginc"
            v2f_img ProbeVertex(appdata_img input)
            {
                v2f_img output = vert_img(input);
                output.pos.z = 0.25;
                return output;
            }
            float4 ProbeColor(v2f_img input) : SV_Target { return float4(1, 0, 0, 1); }
            ENDCG
        }
        Pass
        {
            Name "ProbeOrientation"
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ProbeVertex
            #pragma fragment ProbeColor
            float4 ProbeVertex(uint index : SV_VertexID) : SV_Position
            {
                float2 positions[3] = {float2(-0.5, 0.25), float2(0.5, 0.25), float2(0, 0.75)};
                return float4(positions[index], 0.5, 1);
            }
            float4 ProbeColor() : SV_Target { return float4(1, 0, 0, 1); }
            ENDHLSL
        }
    }
}
