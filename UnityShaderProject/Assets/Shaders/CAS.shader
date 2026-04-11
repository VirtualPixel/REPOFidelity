Shader "Hidden/REPOFidelity/CAS"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Sharpness ("Sharpness", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Sharpness;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION, float2 uv : TEXCOORD0)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.uv = uv;
                return o;
            }

            float3 cas(float2 uv)
            {
                float2 ts = _MainTex_TexelSize.xy;

                float3 c = tex2D(_MainTex, uv).rgb;
                float3 n = tex2D(_MainTex, uv + float2(0, ts.y)).rgb;
                float3 s = tex2D(_MainTex, uv - float2(0, ts.y)).rgb;
                float3 e = tex2D(_MainTex, uv + float2(ts.x, 0)).rgb;
                float3 w = tex2D(_MainTex, uv - float2(ts.x, 0)).rgb;

                float3 mn = min(c, min(min(n, s), min(e, w)));
                float3 mx = max(c, max(max(n, s), max(e, w)));

                float3 d = mx - mn;
                float3 amp = saturate(min(mn, 1.0 - mx) / (d + 0.001));
                amp = sqrt(amp);

                float peak = -3.0 * _Sharpness + 8.0;
                float3 wt = amp / peak;

                return (c + (n + s + e + w) * wt) / (1.0 + 4.0 * wt);
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(cas(i.uv), 1.0);
            }
            ENDCG
        }
    }
}
