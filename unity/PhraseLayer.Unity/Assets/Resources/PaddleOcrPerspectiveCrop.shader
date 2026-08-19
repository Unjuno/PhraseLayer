Shader "Hidden/PhraseLayer/PaddleOcrPerspectiveCrop"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
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
            float4 _SourceSize;
            float4 _H0;
            float4 _H1;
            float4 _H2;
            float _RotateCCW90;

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

            fixed4 frag(v2f i) : SV_Target
            {
                // Graphics.Blit UVs are bottom-left based. Paddle/OpenCV image coordinates are top-left based.
                float2 outputTop = float2(i.uv.x, 1.0 - i.uv.y);
                float2 preRotationTop = outputTop;

                // np.rot90(dst) is CCW 90 degrees. Inverse-map final output back into the unrotated warp.
                if (_RotateCCW90 > 0.5)
                    preRotationTop = float2(1.0 - outputTop.y, outputTop.x);

                float3 p = float3(preRotationTop, 1.0);
                float3 q;
                q.x = dot(_H0.xyz, p);
                q.y = dot(_H1.xyz, p);
                q.z = dot(_H2.xyz, p);

                float safeW = abs(q.z) < 1e-7 ? (q.z < 0.0 ? -1e-7 : 1e-7) : q.z;
                float2 sourceTopPixels = q.xy / safeW;
                float2 sourceUv = float2(
                    sourceTopPixels.x / _SourceSize.x,
                    1.0 - (sourceTopPixels.y / _SourceSize.y));

                // Mirrors BORDER_REPLICATE at the sampling-domain level.
                sourceUv = saturate(sourceUv);
                return tex2D(_MainTex, sourceUv);
            }
            ENDCG
        }
    }
}
