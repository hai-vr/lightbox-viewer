Shader "Hai/LightboxViewerCounterRoll"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _CounterRoll ("CounterRoll", Range(-1, 1)) = 0
        _Ratio ("Ratio", Range(0, 100)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
                float4 diff : TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;

            float _CounterRoll;
            float _Ratio;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                float c, s;
                sincos(_CounterRoll * 3.14159265, s, c);
                float2x2 rotOp = float2x2(c, -s, s, c);

                float2x2 scaleOp = float2x2(_Ratio, 0, 0, 1);
                float2x2 unscaleOp = float2x2(1.0 / _Ratio, 0, 0, 1);
                o.uv = mul(scaleOp, o.uv - 0.5);
                o.uv = mul(rotOp, o.uv);
                o.uv = mul(unscaleOp, o.uv) + 0.5;

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float thres = 0.02;
                if (i.uv.y > 1-thres && i.uv.y < 1 && i.uv.x > thres && i.uv.x < 1-thres) {
                    return fixed4(lerp(0, 0.25, saturate((i.uv.y - 1 + thres) / thres)), 0, 0, 1);
                }
                if (i.uv.x < thres || i.uv.x > 1-thres || i.uv.y < thres || i.uv.y > 1-thres) {
                    return fixed4(0, 0, 0, 1);
                }
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}
