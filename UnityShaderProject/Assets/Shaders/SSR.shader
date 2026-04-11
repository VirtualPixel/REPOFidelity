// Screen-Space Reflections (SSR)
// Raymarches the depth buffer to find reflections on surfaces.
// Adds reflective look to floors, wet surfaces, metal, etc.
Shader "Hidden/REPOFidelity/SSR"
{
    Properties
    {
        _MainTex ("Scene Color", 2D) = "white" {}
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

            sampler2D _MainTex;
            sampler2D _CameraDepthTexture;
            float4 _MainTex_TexelSize;
            float4x4 _InverseViewMatrix;
            float4x4 _ProjectionMatrix;
            float _SSRMaxDistance;  // Max ray distance in screen space
            float _SSRStepSize;    // Ray step size
            float _SSRIntensity;   // Reflection intensity
            float _SSRThickness;   // Surface thickness for hit detection
            int _SSRSteps;         // Number of ray steps

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 viewRay : TEXCOORD1;
            };

            v2f vert(float4 vertex : POSITION, float2 uv : TEXCOORD0)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.uv = uv;

                // View-space ray for depth reconstruction
                float4 clipPos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
                float4 viewPos = mul(unity_CameraInvProjection, clipPos);
                o.viewRay = viewPos.xyz / viewPos.w;

                return o;
            }

            float3 GetViewPos(float2 uv, float3 viewRay)
            {
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float linDepth = LinearEyeDepth(depth);
                // Reconstruct view-space position
                float2 ndc = uv * 2.0 - 1.0;
                float4 clipPos = float4(ndc, depth, 1.0);
                float4 viewPos = mul(unity_CameraInvProjection, clipPos);
                return viewPos.xyz / viewPos.w;
            }

            // Estimate normal from depth buffer
            float3 GetNormalFromDepth(float2 uv)
            {
                float2 ts = _MainTex_TexelSize.xy;
                float dc = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float dr = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv + float2(ts.x, 0)));
                float du = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv + float2(0, ts.y)));

                float3 viewC = float3(uv * 2.0 - 1.0, 1.0) * dc;
                float3 viewR = float3((uv + float2(ts.x, 0)) * 2.0 - 1.0, 1.0) * dr;
                float3 viewU = float3((uv + float2(0, ts.y)) * 2.0 - 1.0, 1.0) * du;

                return normalize(cross(viewR - viewC, viewU - viewC));
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 scene = tex2D(_MainTex, i.uv).rgb;
                float depth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv));

                // Skip skybox
                if (depth > 300.0)
                    return float4(scene, 1.0);

                // Estimate surface normal from depth
                float3 normal = GetNormalFromDepth(i.uv);

                // Only reflect on somewhat upward-facing surfaces (floors, tables)
                // and smooth-ish surfaces
                float reflectivity = saturate(normal.z * 0.5 + 0.5); // Favor horizontal surfaces
                reflectivity = pow(reflectivity, 2.0);

                if (reflectivity < 0.1)
                    return float4(scene, 1.0);

                // Screen-space reflection direction
                float3 viewDir = normalize(float3(i.uv * 2.0 - 1.0, 1.0));
                float3 reflDir = reflect(viewDir, normal);

                // Raymarch in screen space
                float2 rayUV = i.uv;
                float rayDepth = depth;
                float2 rayStep = reflDir.xy * _SSRStepSize * _MainTex_TexelSize.xy * 10.0;

                float3 reflection = float3(0, 0, 0);
                float hit = 0.0;

                int steps = clamp(_SSRSteps, 8, 64);

                for (int s = 1; s <= steps; s++)
                {
                    rayUV += rayStep;

                    // Out of screen
                    if (rayUV.x < 0 || rayUV.x > 1 || rayUV.y < 0 || rayUV.y > 1)
                        break;

                    float sampleDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, rayUV));
                    float expectedDepth = depth + reflDir.z * float(s) * _SSRStepSize;

                    float depthDiff = sampleDepth - expectedDepth;

                    if (depthDiff > 0.0 && depthDiff < _SSRThickness)
                    {
                        reflection = tex2Dlod(_MainTex, float4(rayUV, 0, 0)).rgb;
                        // Fade with distance
                        float fade = 1.0 - float(s) / float(steps);
                        // Fade at screen edges
                        float2 edgeFade = smoothstep(0.0, 0.1, rayUV) * smoothstep(0.0, 0.1, 1.0 - rayUV);
                        fade *= edgeFade.x * edgeFade.y;
                        hit = fade;
                        break;
                    }
                }

                float3 result = lerp(scene, reflection, hit * reflectivity * _SSRIntensity);
                return float4(result, 1.0);
            }
            ENDCG
        }
    }
}
