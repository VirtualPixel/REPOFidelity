// Temporal Upscaler — inspired by FSR 2/3 temporal accumulation
// Uses motion vectors + depth to reproject previous frame and accumulate
// detail over time, producing much higher quality than spatial-only FSR 1.0
Shader "Hidden/REPOFidelity/FSR_Temporal"
{
    Properties
    {
        _MainTex ("Current Frame (low res)", 2D) = "white" {}
        _PrevTex ("Previous Output (high res)", 2D) = "black" {}
        _MotionVectorTex ("Motion Vectors", 2D) = "black" {}
        _DepthTex ("Depth", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;          // Current frame (input resolution)
            sampler2D _PrevTex;          // Previous upscaled output (output resolution)
            sampler2D _MotionVectorTex;  // Screen-space motion vectors
            sampler2D _DepthTex;         // Depth buffer

            float4 _MainTex_TexelSize;
            float4 _OutputSize;          // (outW, outH, 1/outW, 1/outH)
            float4 _InputSize;           // (inW, inH, 1/inW, 1/inH)
            float2 _Jitter;              // Current frame jitter offset (in output pixels)
            float _Reset;                // 1.0 on first frame or scene change

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
            // Temporal upscaling algorithm
            // ---------------------------------------------------------------

            // Catmull-Rom 4-tap filter for high-quality sampling of the low-res input
            float3 SampleCatmullRom(sampler2D tex, float2 uv, float2 texelSize)
            {
                float2 pos = uv / texelSize - 0.5;
                float2 f = frac(pos);
                float2 p = (floor(pos) + 0.5) * texelSize;

                // Catmull-Rom weights
                float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
                float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
                float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
                float2 w3 = f * f * (-0.5 + 0.5 * f);

                // Optimized: combine inner 2 taps
                float2 w12 = w1 + w2;
                float2 tc12 = p + (w2 / w12) * texelSize;
                float2 tc0 = p - texelSize;
                float2 tc3 = p + 2.0 * texelSize;

                // 4 bilinear taps (9 texels covered)
                float3 result =
                    tex2Dlod(tex, float4(tc12.x, tc12.y, 0, 0)).rgb * (w12.x * w12.y) +
                    tex2Dlod(tex, float4(tc0.x,  tc12.y, 0, 0)).rgb * (w0.x  * w12.y) +
                    tex2Dlod(tex, float4(tc3.x,  tc12.y, 0, 0)).rgb * (w3.x  * w12.y) +
                    tex2Dlod(tex, float4(tc12.x, tc0.y,  0, 0)).rgb * (w12.x * w0.y)  +
                    tex2Dlod(tex, float4(tc12.x, tc3.y,  0, 0)).rgb * (w12.x * w3.y);

                float totalW = w12.x * w12.y + w0.x * w12.y + w3.x * w12.y +
                               w12.x * w0.y + w12.x * w3.y;

                return result / max(totalW, 1e-6);
            }

            // Neighborhood clamp — limits reprojected color to prevent ghosting
            void GetNeighborhoodMinMax(float2 uv, float2 ts, out float3 cMin, out float3 cMax)
            {
                float3 c  = tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb;
                float3 n  = tex2Dlod(_MainTex, float4(uv + float2(0, ts.y), 0, 0)).rgb;
                float3 s  = tex2Dlod(_MainTex, float4(uv - float2(0, ts.y), 0, 0)).rgb;
                float3 e  = tex2Dlod(_MainTex, float4(uv + float2(ts.x, 0), 0, 0)).rgb;
                float3 w  = tex2Dlod(_MainTex, float4(uv - float2(ts.x, 0), 0, 0)).rgb;
                float3 ne = tex2Dlod(_MainTex, float4(uv + float2(ts.x, ts.y), 0, 0)).rgb;
                float3 nw = tex2Dlod(_MainTex, float4(uv + float2(-ts.x, ts.y), 0, 0)).rgb;
                float3 se = tex2Dlod(_MainTex, float4(uv + float2(ts.x, -ts.y), 0, 0)).rgb;
                float3 sw = tex2Dlod(_MainTex, float4(uv + float2(-ts.x, -ts.y), 0, 0)).rgb;

                cMin = min(c, min(min(min(n, s), min(e, w)), min(min(ne, nw), min(se, sw))));
                cMax = max(c, max(max(max(n, s), max(e, w)), max(max(ne, nw), max(se, sw))));

                // Slightly expand the box to reduce flickering on near-edge pixels
                float3 avg = (c + n + s + e + w) * 0.2;
                float3 range = cMax - cMin;
                cMin = min(cMin, avg - range * 0.1);
                cMax = max(cMax, avg + range * 0.1);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 outTS = _OutputSize.zw;
                float2 inTS = _InputSize.zw;

                // De-jitter: map output UV to the input position for THIS frame
                // The camera was jittered, so we need to undo that to sample the right input pixel
                float2 jitterUV = _Jitter * outTS;
                float2 inputUV = i.uv - jitterUV;

                // Sample current frame with high-quality filter
                float3 current = SampleCatmullRom(_MainTex, inputUV, inTS);

                // On reset (first frame, scene change), just output current
                if (_Reset > 0.5)
                    return float4(current, 1.0);

                // Get motion vectors at this pixel (in UV space)
                float2 motion = tex2Dlod(_MotionVectorTex, float4(inputUV, 0, 0)).rg;

                // Reproject: where was this pixel in the previous frame?
                float2 reprojUV = i.uv - motion;

                // Check if reprojection is valid (within screen bounds)
                float2 uvValid = step(float2(0, 0), reprojUV) * step(reprojUV, float2(1, 1));
                float isValid = uvValid.x * uvValid.y;

                if (isValid < 0.5)
                    return float4(current, 1.0);

                // Sample previous frame at reprojected position
                float3 history = tex2Dlod(_PrevTex, float4(reprojUV, 0, 0)).rgb;

                // Neighborhood clamp: prevent ghosting by constraining history
                // to the current frame's local color range
                float3 cMin, cMax;
                GetNeighborhoodMinMax(inputUV, inTS, cMin, cMax);
                float3 clampedHistory = clamp(history, cMin, cMax);

                // Compute blend factor
                // More weight to history = more detail accumulation, but more ghosting risk
                // Less weight = sharper current frame, less temporal detail
                float3 diff = abs(clampedHistory - current);
                float lumaDiff = dot(diff, float3(0.299, 0.587, 0.114));

                // Adaptive blend: trust history more when it's close to current
                float blend = lerp(0.95, 0.75, saturate(lumaDiff * 5.0));

                // If history was clamped significantly, reduce its weight
                float3 clampDist = abs(history - clampedHistory);
                float clampAmount = dot(clampDist, float3(0.333, 0.334, 0.333));
                blend *= lerp(1.0, 0.5, saturate(clampAmount * 10.0));

                float3 result = lerp(current, clampedHistory, blend);

                return float4(result, 1.0);
            }
            ENDCG
        }
    }
}
