Shader "Hidden/SpinnakerUnity/EvaluationOverlay" {
    Properties {
        _MainTex ("Camera Texture", 2D) = "white" {}
        _ZebraTex ("Zebra Texture", 2D) = "white" {}
        _UpperLimit ("Upper Limit", Range(0, 1)) = 0.95
        _LowerLimit ("Lower Limit", Range(0, 1)) = 0.05
        _ZebraPeriodPixels ("Zebra Period Pixels", Float) = 24
        _ZebraColor ("Zebra Color", Color) = (0, 0, 0, 0.9)
        _UnderColor ("Underexposed Color", Color) = (1, 0, 1, 0.38)
    }

    SubShader {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend One Zero

        Pass {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _ZebraTex;
            float4 _MainTex_TexelSize;
            float _UpperLimit;
            float _LowerLimit;
            float _ZebraPeriodPixels;
            float4 _ZebraColor;
            float4 _UnderColor;

            fixed4 frag(v2f_img input) : SV_Target {
                float3 c = tex2D(_MainTex, input.uv).rgb;
                float luma = dot(c, float3(0.2126, 0.7152, 0.0722));

                if (luma > _UpperLimit) {
                    float period = max(_ZebraPeriodPixels, 1.0);
                    float2 zebra_uv = input.uv * _MainTex_TexelSize.zw / period;
                    float zebra_alpha = tex2D(_ZebraTex, zebra_uv).a;
                    return float4(_ZebraColor.rgb, zebra_alpha * _ZebraColor.a);
                }

                if (luma < _LowerLimit) {
                    return _UnderColor;
                }

                return float4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
