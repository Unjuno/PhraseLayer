Shader "Hidden/PhraseLayer/PaddleOcrRecognizerPreprocess"
{
    Properties
    {
        _MainTex ("Rectified text crop", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _ValidRatio;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float3 ToEncodedRgb(float3 sampledRgb)
            {
                // In Linear projects, sampling an sRGB rectified crop produces linear-light values; convert back to
                // the byte-style encoded values consumed by PaddleOCR before applying its (x-0.5)/0.5 normalization.
                // In Gamma projects, texture samples are already in that encoded domain.
                #if defined(UNITY_COLORSPACE_GAMMA)
                    return sampledRgb;
                #else
                    return LinearToGammaSpace(sampledRgb);
                #endif
            }

            float4 frag(v2f i) : SV_Target
            {
                float validRatio = saturate(_ValidRatio);
                if (validRatio <= 0.0 || i.uv.x >= validRatio)
                    return float4(0.0, 0.0, 0.0, 1.0);

                // The left validRatio portion is exactly ResizedWidth pixels wide in the model-width target.
                // Mapping x/validRatio therefore reproduces the same bilinear resize that the old resized-width
                // RenderTexture performed, while the remainder stays normalized zero padding.
                float2 sourceUv = float2(saturate(i.uv.x / validRatio), i.uv.y);
                float3 encoded = ToEncodedRgb(tex2D(_MainTex, sourceUv).rgb);
                float3 normalized = (encoded - 0.5) / 0.5;
                return float4(normalized, 1.0);
            }
            ENDCG
        }
    }
}
