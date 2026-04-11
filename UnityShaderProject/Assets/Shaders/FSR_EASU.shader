// AMD FidelityFX Super Resolution 1.0 — Edge-Adaptive Spatial Upsampling (EASU)
// Ported from ffx_fsr1.h (FidelityFX SDK, GPUOpen)
// 12-tap directionally-shaped Lanczos2 filter
Shader "Hidden/REPOFidelity/FSR_EASU"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
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
            float4 _MainTex_TexelSize; // (1/w, 1/h, w, h)
            float4 _InputSize;  // (inputW, inputH, 1/inputW, 1/inputH)
            float4 _OutputSize; // (outputW, outputH, 1/outputW, 1/outputH)

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
            // AMD FSR 1.0 EASU — real algorithm from FidelityFX SDK
            // ---------------------------------------------------------------

            // Tap accumulator: rotates offset by edge direction, applies
            // Lanczos2-approximation kernel weight, accumulates color*weight
            void FsrEasuTap(
                inout float3 aC, // accumulated color
                inout float aW,  // accumulated weight
                float2 off,      // offset from center (in input pixel units)
                float2 dir,      // edge direction (normalized)
                float2 len,      // edge length (x=along, y=perpendicular stretch)
                float lob,       // negative lobe strength
                float clp,       // clipping limit for weight
                float3 c)        // tap color
            {
                // Rotate offset by edge direction
                float2 v;
                v.x = dot(float2( dir.x, dir.y), off);
                v.y = dot(float2(-dir.y, dir.x), off);

                // Anisotropic stretch
                v *= len;

                // Squared distance
                float d2 = min(v.x * v.x + v.y * v.y, clp);

                // Lanczos2 approximation (no sin, no rcp, no sqrt)
                // (25/16 * (2/5 * d2 - 1)^2 - (25/16 - 1)) * (1/4 * d2 - 1)^2
                float wB = (2.0 / 5.0) * d2 - 1.0;
                float wA = lob * d2 - 1.0;
                wB *= wB;
                wA *= wA;
                wA *= wB;

                // No negative weights
                wA = max(wA, 0.0);

                aC += c * wA;
                aW += wA;
            }

            // Edge direction accumulator for one bilinear quadrant
            // Takes 5 luma values in a cross pattern:
            //   b
            // d e a
            //   c
            void FsrEasuSet(
                inout float2 dir,  // accumulated direction
                inout float len,   // accumulated length
                float w,           // bilinear weight for this quadrant
                float lA, float lB, float lC, float lD, float lE)
            {
                // Gradient
                float2 g = float2(lD - lB, lA - lC);

                // Length: measures gradient consistency
                // Consistent gradients = long edges, reversals = short
                float lenG = max(abs(g.x), abs(g.y));
                float dirG = 1.0;
                if (lenG > 0.0)
                {
                    float minG = min(abs(g.x), abs(g.y)) / lenG;
                    dirG = minG * minG; // Squash to [0,1], sharper for clearer edges
                }

                // Length: how much the cross pattern center differs from neighbors
                float meanLuma = 0.25 * (lA + lB + lC + lD);
                float delta = abs(lE - meanLuma);
                float edgeLen = max(abs(g.x), abs(g.y)) / max(delta, 0.001);
                edgeLen = saturate(edgeLen * 0.5);

                // Accumulate
                dir += g * w;
                len += edgeLen * edgeLen * dirG * w;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 outputSize = _OutputSize.xy;
                float2 inputSize = _InputSize.xy;
                float2 inputSizeRcp = _InputSize.zw;

                // Map output pixel to input pixel position
                float2 pp = i.uv * inputSize - 0.5;
                float2 fp = floor(pp);
                pp -= fp;

                // Input texel centers for the 12 taps (4x3 cross pattern)
                // Gather 12 taps around the 4 nearest input pixels
                float2 p0 = (fp - float2(0.5, 0.5)) * inputSizeRcp;
                float2 p1 = (fp + float2(0.5, 0.5)) * inputSizeRcp;
                float2 ts = inputSizeRcp;

                // Sample the 12 taps (cross pattern, skip corners of 4x4)
                // Row 0: 3 taps
                float3 t0 = tex2Dlod(_MainTex, float4((fp + float2(0.0, -1.0)) * ts, 0, 0)).rgb;
                float3 t1 = tex2Dlod(_MainTex, float4((fp + float2(1.0, -1.0)) * ts, 0, 0)).rgb;

                // Row 1: 4 taps (full row)
                float3 t2 = tex2Dlod(_MainTex, float4((fp + float2(-1.0, 0.0)) * ts, 0, 0)).rgb;
                float3 t3 = tex2Dlod(_MainTex, float4((fp + float2( 0.0, 0.0)) * ts, 0, 0)).rgb;
                float3 t4 = tex2Dlod(_MainTex, float4((fp + float2( 1.0, 0.0)) * ts, 0, 0)).rgb;
                float3 t5 = tex2Dlod(_MainTex, float4((fp + float2( 2.0, 0.0)) * ts, 0, 0)).rgb;

                // Row 2: 4 taps (full row)
                float3 t6 = tex2Dlod(_MainTex, float4((fp + float2(-1.0, 1.0)) * ts, 0, 0)).rgb;
                float3 t7 = tex2Dlod(_MainTex, float4((fp + float2( 0.0, 1.0)) * ts, 0, 0)).rgb;
                float3 t8 = tex2Dlod(_MainTex, float4((fp + float2( 1.0, 1.0)) * ts, 0, 0)).rgb;
                float3 t9 = tex2Dlod(_MainTex, float4((fp + float2( 2.0, 1.0)) * ts, 0, 0)).rgb;

                // Row 3: 2 taps
                float3 tA = tex2Dlod(_MainTex, float4((fp + float2(0.0, 2.0)) * ts, 0, 0)).rgb;
                float3 tB = tex2Dlod(_MainTex, float4((fp + float2(1.0, 2.0)) * ts, 0, 0)).rgb;

                // Compute luma for all 12 taps (approximate: R*0.5 + B*0.5 + G)
                // This weighting reduces ALU while maintaining good edge detection
                #define LUMA(c) (0.5 * (c.r + c.b) + c.g)
                float l0 = LUMA(t0); float l1 = LUMA(t1);
                float l2 = LUMA(t2); float l3 = LUMA(t3); float l4 = LUMA(t4); float l5 = LUMA(t5);
                float l6 = LUMA(t6); float l7 = LUMA(t7); float l8 = LUMA(t8); float l9 = LUMA(t9);
                float lA = LUMA(tA); float lB = LUMA(tB);
                #undef LUMA

                // Detect edge direction and length from 4 bilinear quadrants
                //
                // Quadrant layout around the 4 nearest input pixels (3,4,7,8):
                //    0  1
                //  2 3  4  5
                //  6 7  8  9
                //    A  B
                float2 dir = float2(0, 0);
                float len = 0.0;

                // Each quadrant: cross of 5 luma values {right, up, left, down, center}
                float bx = 1.0 - pp.x;
                float by = 1.0 - pp.y;

                FsrEasuSet(dir, len, bx * by, l4, l0, l6, l2, l3); // top-left quad
                FsrEasuSet(dir, len, pp.x * by, l5, l1, l7, l3, l4); // top-right quad
                FsrEasuSet(dir, len, bx * pp.y, l8, l3, lA, l6, l7); // bottom-left quad
                FsrEasuSet(dir, len, pp.x * pp.y, l9, l4, lB, l7, l8); // bottom-right quad

                // Normalize direction
                float dirLen = max(abs(dir.x), abs(dir.y));
                if (dirLen < 1.0 / 32768.0) dir = float2(0, 1); // Default if no edge
                else dir /= dirLen;

                // Shape the length: controls kernel anisotropy
                len = saturate(len);
                len = len * len; // Sharpen the response

                // Compute kernel stretch
                // Along edge: wide (2.0). Across edge: narrow (1.0 when no edge, tighter with stronger edge)
                float stretch = 1.0 / (1.0 + len * (2.0 - 1.0)); // 1/(1+len)
                float2 kernelLen = float2(1.0 + (stretch - 1.0) * len, 1.0 + (1.0 / stretch - 1.0) * len);

                // Negative lobe strength: stronger with less anisotropy
                float lob = 0.5 - 0.25 * len;
                lob = max(lob, 0.125); // Minimum negative lobe

                // Clipping distance (maximum kernel reach)
                float clp = rcp(lob);

                // Accumulate all 12 taps through the shaped kernel
                float3 aC = float3(0, 0, 0);
                float aW = 0.0;

                FsrEasuTap(aC, aW, float2( 0.0,-1.0) - pp, dir, kernelLen, lob, clp, t0);
                FsrEasuTap(aC, aW, float2( 1.0,-1.0) - pp, dir, kernelLen, lob, clp, t1);
                FsrEasuTap(aC, aW, float2(-1.0, 0.0) - pp, dir, kernelLen, lob, clp, t2);
                FsrEasuTap(aC, aW, float2( 0.0, 0.0) - pp, dir, kernelLen, lob, clp, t3);
                FsrEasuTap(aC, aW, float2( 1.0, 0.0) - pp, dir, kernelLen, lob, clp, t4);
                FsrEasuTap(aC, aW, float2( 2.0, 0.0) - pp, dir, kernelLen, lob, clp, t5);
                FsrEasuTap(aC, aW, float2(-1.0, 1.0) - pp, dir, kernelLen, lob, clp, t6);
                FsrEasuTap(aC, aW, float2( 0.0, 1.0) - pp, dir, kernelLen, lob, clp, t7);
                FsrEasuTap(aC, aW, float2( 1.0, 1.0) - pp, dir, kernelLen, lob, clp, t8);
                FsrEasuTap(aC, aW, float2( 2.0, 1.0) - pp, dir, kernelLen, lob, clp, t9);
                FsrEasuTap(aC, aW, float2( 0.0, 2.0) - pp, dir, kernelLen, lob, clp, tA);
                FsrEasuTap(aC, aW, float2( 1.0, 2.0) - pp, dir, kernelLen, lob, clp, tB);

                // Normalize
                float3 result = aC / max(aW, 1e-8);

                // De-ringing: clamp against the min/max of the 4 nearest taps
                float3 mn = min(min(t3, t4), min(t7, t8));
                float3 mx = max(max(t3, t4), max(t7, t8));
                result = clamp(result, mn, mx);

                return float4(result, 1.0);
            }
            ENDCG
        }
    }
}
