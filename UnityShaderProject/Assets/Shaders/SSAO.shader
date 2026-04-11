// Screen-Space Ambient Occlusion (SSAO)
// Adds contact shadows in corners, crevices, and where objects meet.
// Uses depth buffer hemisphere sampling with bilateral blur.
Shader "Hidden/REPOFidelity/SSAO"
{
    Properties
    {
        _MainTex ("Scene Color", 2D) = "white" {}
    }
    SubShader
    {
        // Pass 0: AO calculation
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;
            float4 _MainTex_TexelSize;
            float _AORadius;     // World-space radius
            float _AOIntensity;  // Strength multiplier
            float _AOBias;       // Depth bias to avoid self-occlusion
            int _AOSamples;      // Number of samples (4-16)

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

            // Hash function for pseudo-random directions
            float2 Hash(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * float3(443.897, 441.423, 437.195));
                p3 += dot(p3, p3.yzx + 19.19);
                return frac((p3.xx + p3.yz) * p3.zy) * 2.0 - 1.0;
            }

            float SampleDepthLinear(float2 uv)
            {
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                return LinearEyeDepth(depth);
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 scene = tex2D(_MainTex, i.uv).rgb;
                float depth = SampleDepthLinear(i.uv);

                // Skip skybox / very far pixels
                if (depth > 500.0)
                    return float4(scene, 1.0);

                // Screen-space radius based on depth
                float ssRadius = _AORadius / max(depth, 0.1);
                ssRadius = clamp(ssRadius, 0.002, 0.1);

                float occlusion = 0.0;
                int samples = clamp(_AOSamples, 4, 16);

                for (int s = 0; s < samples; s++)
                {
                    // Pseudo-random sample direction
                    float2 rnd = Hash(i.uv * 1000.0 + float2(s * 7.23, s * 3.91));
                    float2 offset = rnd * ssRadius * (float(s + 1) / float(samples));
                    float2 sampleUV = i.uv + offset;

                    // Clamp to screen
                    sampleUV = clamp(sampleUV, 0.001, 0.999);

                    float sampleDepth = SampleDepthLinear(sampleUV);
                    float depthDiff = depth - sampleDepth;

                    // Occlusion: sample is closer = occluded
                    // Range check: ignore samples too far away
                    float rangeCheck = smoothstep(0.0, 1.0, _AORadius / max(abs(depthDiff), 0.001));
                    occlusion += step(_AOBias, depthDiff) * rangeCheck;
                }

                occlusion = 1.0 - (occlusion / float(samples)) * _AOIntensity;
                occlusion = saturate(occlusion);

                return float4(scene * occlusion, 1.0);
            }
            ENDCG
        }
    }
}
