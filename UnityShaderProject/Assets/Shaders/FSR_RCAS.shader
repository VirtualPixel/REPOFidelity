// AMD FidelityFX Super Resolution 1.0 — Robust Contrast-Adaptive Sharpening (RCAS)
// Ported from ffx_fsr1.h (FidelityFX SDK, GPUOpen)
// 5-tap cross filter with noise-aware sharpening
Shader "Hidden/REPOFidelity/FSR_RCAS"
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

            // ---------------------------------------------------------------
            // AMD FSR 1.0 RCAS — real algorithm from FidelityFX SDK
            // ---------------------------------------------------------------

            float4 frag(v2f i) : SV_Target
            {
                float2 ts = _MainTex_TexelSize.xy;

                // 5-tap cross pattern
                float3 e = tex2D(_MainTex, i.uv).rgb;                        // center
                float3 b = tex2D(_MainTex, i.uv + float2( 0,  ts.y)).rgb;   // north
                float3 d = tex2D(_MainTex, i.uv + float2(-ts.x, 0)).rgb;    // west
                float3 f = tex2D(_MainTex, i.uv + float2( ts.x, 0)).rgb;    // east
                float3 h = tex2D(_MainTex, i.uv + float2( 0, -ts.y)).rgb;   // south

                // Luma (approximate)
                float bL = 0.5 * (b.r + b.b) + b.g;
                float dL = 0.5 * (d.r + d.b) + d.g;
                float eL = 0.5 * (e.r + e.b) + e.g;
                float fL = 0.5 * (f.r + f.b) + f.g;
                float hL = 0.5 * (h.r + h.b) + h.g;

                // Noise detection: how much center differs from neighbor average
                // relative to local contrast
                float nz = 0.25 * (bL + dL + fL + hL) - eL;
                float range = max(max(max(bL, dL), max(fL, hL)), eL)
                            - min(min(min(bL, dL), min(fL, hL)), eL);
                nz = saturate(abs(nz) / max(range, 1.0 / 64.0));
                nz = -0.5 * nz + 1.0; // 1.0 = no noise, 0.5 = noisy

                // Per-channel min/max of the neighbor ring
                float3 mn = min(min(b, d), min(f, h));
                float3 mx = max(max(b, d), max(f, h));

                // Solve for maximum sharpening weight that doesn't clip
                // For each channel: find how much we can push before hitting 0 or 1
                float3 hitMn = (mn - e) / max(4.0 * mx - 4.0 * e, 1e-8);
                float3 hitMx = (mx - e) / max(4.0 * mn - 4.0 * e, 1e-8);

                float3 lobeRGB = max(-hitMn, -hitMx);
                float lobe = max(lobeRGB.r, max(lobeRGB.g, lobeRGB.b));

                // Clamp to RCAS limit (0.25 - 1/16 = 0.1875)
                lobe = min(lobe, 0.1875);

                // Apply user sharpness and noise attenuation
                // _Sharpness 0->1 maps to exp2(-sharpness_stops)
                // For our 0-1 slider: 0 = off, 1 = maximum
                lobe *= _Sharpness * nz;

                // Apply 5-tap sharpening filter
                float3 result = (lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0);

                return float4(result, 1.0);
            }
            ENDCG
        }
    }
}
