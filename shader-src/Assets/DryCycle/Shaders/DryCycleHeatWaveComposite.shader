Shader "DryCycle/HeatWaveComposite"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
    }

    Category
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        ZWrite Off
        Lighting Off
        Cull Off

        SubShader
        {
            GrabPass { }

            Pass
            {
                Blend One Zero

                CGPROGRAM
                #pragma target 3.0
                #pragma vertex vert
                #pragma fragment frag
                #include "UnityCG.cginc"

                sampler2D _MainTex;
                sampler2D _GrabTexture;
                sampler2D _NoiseTex;
                sampler2D _NoiseTex2;
                sampler2D _LevelTex;
                sampler2D _DryCycleHeatOpticalTex;
                sampler2D _DryCycleHeatThermalTex;
                sampler2D _DryCycleHeatVelocityTex;
                sampler2D _DryCycleHeatTerrainTex;

                uniform float4 _spriteRect;
                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleRoomSizePx;
                uniform float _DryCycleHeatWaveIntensity;
                uniform float _DryCycleWhiteHeat;
                uniform float _DryCycleHeatBurst;
                uniform float _DryCycleHeatStillness;
                uniform float _DryCycleHeatTime;
                uniform float _DryCycleHasHeatSimulation;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float2 uv : TEXCOORD0;
                    float4 screenPos : TEXCOORD1;
                    float4 grabPos : TEXCOORD2;
                    float4 color : COLOR;
                };

                v2f vert(appdata_full v)
                {
                    v2f o;
                    o.pos = UnityObjectToClipPos(v.vertex);
                    o.uv = v.texcoord.xy;
                    o.screenPos = ComputeScreenPos(o.pos);
                    o.grabPos = ComputeGrabScreenPos(o.pos);
                    o.color = v.color;
                    return o;
                }

                float Smooth01(float x)
                {
                    x = saturate(x);
                    return x * x * (3.0 - 2.0 * x);
                }

                float2 RoomUV(float2 screenUV)
                {
                    return _camInRoomRect.xy + screenUV * _camInRoomRect.zw;
                }

                float2 LevelUV(float2 screenUV)
                {
                    float2 span = max(abs(_spriteRect.zw - _spriteRect.xy), float2(0.0001, 0.0001));
                    return (screenUV - _spriteRect.xy) / span;
                }

                float DecodePseudoDepth(float2 levelUV)
                {
                    float4 level = tex2D(_LevelTex, saturate(levelUV));
                    if (level.r >= 0.999 && level.g >= 0.999 && level.b >= 0.999)
                        return 1.0;

                    float encoded = fmod(max(0.0, level.r * 255.0 - 1.0), 30.0) / 30.0;
                    return saturate(encoded * 1.34);
                }

                float2 NoiseGradient(sampler2D noiseTex, float2 uv, float2 texel)
                {
                    float xP = tex2D(noiseTex, frac(uv + float2(texel.x, 0.0))).r;
                    float xM = tex2D(noiseTex, frac(uv - float2(texel.x, 0.0))).r;
                    float yP = tex2D(noiseTex, frac(uv + float2(0.0, texel.y))).r;
                    float yM = tex2D(noiseTex, frac(uv - float2(0.0, texel.y))).r;
                    return float2(xP - xM, yP - yM);
                }

                float2 MacroPhase(float2 roomUV, float time)
                {
                    float2 p0 = roomUV * float2(2.1, 1.35) + float2(time * 0.0047, time * 0.0021);
                    float2 p1 = roomUV * float2(3.7, 2.4) + float2(-time * 0.0028, time * 0.0036) + 0.37;
                    float2 g0 = NoiseGradient(_NoiseTex, p0, float2(0.018, 0.018));
                    float2 g1 = NoiseGradient(_NoiseTex2, p1, float2(0.014, 0.014));
                    return g0 * 0.68 + g1 * 0.32;
                }

                float2 MicroPhase(float2 screenUV, float time)
                {
                    float2 pixelScale = max(_screenSize, float2(1.0, 1.0)) / 96.0;
                    float2 p = screenUV * pixelScale + float2(time * 0.081, -time * 0.064);
                    float2 q = screenUV * pixelScale * 1.73 + float2(-time * 0.052, time * 0.093) + 0.41;
                    float2 g0 = NoiseGradient(_NoiseTex2, p, float2(0.032, 0.032));
                    float2 g1 = NoiseGradient(_NoiseTex, q, float2(0.024, 0.024));
                    return g0 * 0.63 + g1 * 0.37;
                }

                float4 SampleOptical(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatOpticalTex, saturate(roomUV));
                }

                float3 WhiteHeatTone(float3 color, float amount, float skyExposure, float thermal)
                {
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadowProtect = 1.0 - smoothstep(0.12, 0.40, luma);
                    float mid = smoothstep(0.18, 0.72, luma) * (1.0 - smoothstep(0.72, 0.96, luma));
                    float high = smoothstep(0.50, 0.94, luma);
                    float desaturate = amount * (mid * 0.38 + high * 0.58) * (1.0 - shadowProtect * 0.92);
                    color = lerp(color, luma.xxx, saturate(desaturate));

                    float bleach = amount * high * (0.34 + skyExposure * 0.36 + thermal * 0.14);
                    color = lerp(color, 1.0.xxx, saturate(bleach));

                    // Low-frequency veiling glare raises exposed air without lifting
                    // protected shadows into a flat full-screen brightness filter.
                    float veil = amount * skyExposure * (0.018 + thermal * 0.022);
                    color += veil * (1.0 - shadowProtect * 0.96);
                    return saturate(color);
                }

                float4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.0001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);
                    float intensity = saturate(_DryCycleHeatWaveIntensity);
                    float4 centerOptical = SampleOptical(roomUV);
                    float residualThermal = centerOptical.b * _DryCycleHasHeatSimulation;
                    float opticalPresence = saturate(max(
                        intensity,
                        max(_DryCycleHeatBurst * 0.45, residualThermal * 0.34)));

                    float4 original = tex2D(_GrabTexture, grabUV);
                    if (opticalPresence <= 0.0001 && _DryCycleWhiteHeat <= 0.0001)
                        return original;

                    float levelDepth = DecodePseudoDepth(LevelUV(screenUV));
                    float depthGain = lerp(0.64, 1.28, levelDepth);
                    float2 pxToRoom = 1.0 / max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                    float2 accumulatedRoom = 0.0;
                    float accumulatedThermal = 0.0;
                    float accumulatedBoundary = 0.0;

                    // Curved optical-path integration. Each step re-reads the field at
                    // the already-displaced room position, so large plumes bend a line
                    // continuously instead of applying one flat UV offset.
                    [unroll]
                    for (int stepIndex = 0; stepIndex < 5; stepIndex++)
                    {
                        float traceT = (stepIndex + 0.5) / 5.0;
                        float2 traceRoomUV = saturate(roomUV + accumulatedRoom * (0.42 + traceT * 0.26));
                        float4 optical = SampleOptical(traceRoomUV);
                        float thermal = lerp(0.28 * intensity, optical.b, _DryCycleHasHeatSimulation);
                        float boundary = lerp(0.18 * intensity, optical.a, _DryCycleHasHeatSimulation);

                        float2 meso = optical.rg * _DryCycleHasHeatSimulation;
                        float2 macro = MacroPhase(traceRoomUV, _DryCycleHeatTime) *
                            pxToRoom * (1.1 + thermal * 2.3);
                        float2 micro = MicroPhase(screenUV + accumulatedRoom, _DryCycleHeatTime) *
                            pxToRoom * (0.34 + thermal * 1.35 + boundary * 1.15);

                        float stillnessScale = lerp(1.0, 0.34, saturate(_DryCycleHeatStillness));
                        float burstScale = 1.0 + saturate(_DryCycleHeatBurst) * 1.85;
                        float2 stepOffset =
                            meso * (0.72 + thermal * 0.92) +
                            macro * (0.72 + intensity * 0.62) +
                            micro * stillnessScale;

                        // Near hot surfaces vertical information is less stable than
                        // horizontal information, approximating inferior-mirage fold.
                        stepOffset.y *= 1.0 + boundary * (0.78 + intensity * 0.72);
                        stepOffset *= opticalPresence * depthGain * burstScale / 5.0;
                        accumulatedRoom += stepOffset;
                        accumulatedThermal += thermal / 5.0;
                        accumulatedBoundary += boundary / 5.0;
                    }

                    float2 screenOffset = accumulatedRoom /
                        max(abs(_camInRoomRect.zw), float2(0.0001, 0.0001));
                    float2 grabOffset = float2(
                        screenOffset.x,
                        screenOffset.y * _ProjectionParams.x);

                    float streakAmount = saturate(
                        accumulatedThermal * 0.52 +
                        accumulatedBoundary * 0.38 +
                        _DryCycleHeatBurst * 0.52);
                    float2 streak = grabOffset * (0.38 + streakAmount * 0.62);
                    float3 scene = tex2D(_GrabTexture, saturate(grabUV + grabOffset)).rgb;

                    // Directional optical smear only where the refractive path is
                    // energetic. Unaffected pixel art remains a single sharp sample.
                    if (streakAmount > 0.08)
                    {
                        float3 a = tex2D(_GrabTexture, saturate(grabUV + grabOffset - streak * 0.45)).rgb;
                        float3 b = tex2D(_GrabTexture, saturate(grabUV + grabOffset + streak * 0.45)).rgb;
                        scene = lerp(scene, (a + scene * 2.0 + b) * 0.25, streakAmount * 0.34);
                    }

                    float4 terrain = tex2D(_DryCycleHeatTerrainTex, saturate(roomUV));
                    float skyExposure = terrain.a;
                    float whiteHeat = saturate(_DryCycleWhiteHeat);
                    scene = WhiteHeatTone(
                        scene,
                        whiteHeat,
                        skyExposure,
                        accumulatedThermal);

                    return float4(scene, 1.0);
                }
                ENDCG
            }
        }
    }
}
