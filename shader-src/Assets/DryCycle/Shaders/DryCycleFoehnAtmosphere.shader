Shader "DryCycle/FoehnAtmosphere"
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

                sampler2D _GrabTexture;
                sampler2D _DryCycleFoehnFlowField;
                sampler2D _DryCycleFoehnStreakField;
                sampler2D _DryCycleFoehnTerrainField;

                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleFoehnRoomSizePx;
                uniform float _DryCycleFoehnIntensity;
                uniform float _DryCycleFoehnTime;
                uniform float2 _DryCycleFoehnWindDir;
                uniform float _DryCycleHasFoehnTextures;
                uniform float _DryCycleHasFoehnTerrainField;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float4 screenPos : TEXCOORD0;
                    float4 grabPos : TEXCOORD1;
                };

                v2f vert(appdata_full v)
                {
                    v2f o;
                    o.pos = UnityObjectToClipPos(v.vertex);
                    o.screenPos = ComputeScreenPos(o.pos);
                    o.grabPos = ComputeGrabScreenPos(o.pos);
                    return o;
                }

                float2 SafeNormalize(float2 value)
                {
                    float len = length(value);
                    return len > 0.00001 ? value / len : float2(1.0, -0.16);
                }

                float2 ClampMagnitude(float2 value, float maximum)
                {
                    float len = length(value);
                    if (len <= maximum || len <= 0.00001)
                        return value;
                    return value * (maximum / len);
                }

                float Smooth01(float value)
                {
                    float t = saturate(value);
                    return t * t * (3.0 - 2.0 * t);
                }

                float PhaseBlend(float phase)
                {
                    return abs(0.5 - phase) * 2.0;
                }

                float2 RoomUV(float2 screenUV)
                {
                    return _camInRoomRect.xy + screenUV * _camInRoomRect.zw;
                }

                float4 SampleTerrain(float2 roomPx)
                {
                    if (_DryCycleHasFoehnTerrainField <= 0.5)
                        return float4(1.0, 0.0, 0.0, 0.0);

                    float2 room01 = saturate(
                        roomPx / max(_DryCycleFoehnRoomSizePx, float2(1.0, 1.0)));
                    return tex2D(_DryCycleFoehnTerrainField, room01);
                }

                float4 SampleFlow(float2 roomPx, float2 windDir, float2 crossDir)
                {
                    float along = dot(roomPx, windDir);
                    float across = dot(roomPx, crossDir);

                    if (_DryCycleHasFoehnTextures > 0.5)
                    {
                        float phaseA = frac(_DryCycleFoehnTime * 0.112);
                        float phaseB = frac(phaseA + 0.5);
                        float blend = PhaseBlend(phaseA);
                        float2 uv = float2(along / 1180.0, across / 610.0);

                        float4 a = tex2D(
                            _DryCycleFoehnFlowField,
                            frac(uv - float2(phaseA * 0.46, phaseA * 0.025)));
                        float4 b = tex2D(
                            _DryCycleFoehnFlowField,
                            frac(uv - float2(phaseB * 0.46, phaseB * 0.025) + float2(0.371, 0.163)));
                        return lerp(a, b, blend);
                    }

                    float broad = sin(
                        (along / 520.0 + across / 770.0 - _DryCycleFoehnTime * 0.36) * 6.2831853);
                    float curl = sin(
                        (along / 310.0 - across / 245.0 - _DryCycleFoehnTime * 0.57) * 6.2831853);
                    return float4(
                        0.92,
                        curl * 0.15 + 0.5,
                        broad * 0.24 + 0.62,
                        abs(curl) * 0.68);
                }

                float4 SampleStreak(float2 roomPx, float2 windDir, float2 crossDir, float seed)
                {
                    float along = dot(roomPx, windDir);
                    float across = dot(roomPx, crossDir);

                    if (_DryCycleHasFoehnTextures > 0.5)
                    {
                        float phaseA = frac(_DryCycleFoehnTime * 0.154 + seed);
                        float phaseB = frac(phaseA + 0.5);
                        float blend = PhaseBlend(phaseA);
                        float2 uv = float2(along / 930.0, across / 360.0);

                        float4 a = tex2D(
                            _DryCycleFoehnStreakField,
                            frac(uv - float2(phaseA * 0.78, phaseA * 0.018)));
                        float4 b = tex2D(
                            _DryCycleFoehnStreakField,
                            frac(uv - float2(phaseB * 0.78, phaseB * 0.018) + float2(0.417, 0.233)));
                        return lerp(a, b, blend);
                    }

                    float macro = sin(
                        (across / 82.0 + along / 880.0 - _DryCycleFoehnTime * 1.10) * 6.2831853);
                    float fine = sin(
                        (across / 34.0 - along / 510.0 - _DryCycleFoehnTime * 1.83) * 6.2831853);
                    return float4(
                        pow(saturate(macro * 0.5 + 0.5), 2.1),
                        pow(saturate(fine * 0.5 + 0.5), 3.0),
                        frac(across / 317.0 + along / 701.0),
                        saturate(macro * 0.28 + fine * 0.20 + 0.52));
                }

                float3 SampleDirectionalBlur(
                    float2 grabUv,
                    float2 windDir,
                    float blurPx,
                    float3 center)
                {
                    if (blurPx <= 0.05)
                        return center;

                    float2 stepUv = windDir * blurPx / max(_screenSize, float2(1.0, 1.0));
                    float3 a = tex2D(_GrabTexture, grabUv - stepUv * 1.60).rgb;
                    float3 b = tex2D(_GrabTexture, grabUv - stepUv * 0.72).rgb;
                    float3 c = tex2D(_GrabTexture, grabUv + stepUv * 0.72).rgb;
                    float3 d = tex2D(_GrabTexture, grabUv + stepUv * 1.60).rgb;
                    return center * 0.40 + (b + c) * 0.19 + (a + d) * 0.11;
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    float2 screenUv = i.screenPos.xy / max(i.screenPos.w, 0.00001);
                    float2 grabUv = i.grabPos.xy / max(i.grabPos.w, 0.00001);
                    float2 roomUv = RoomUV(screenUv);
                    float2 roomPx = roomUv * max(_DryCycleFoehnRoomSizePx, float2(1.0, 1.0));

                    float intensity = saturate(_DryCycleFoehnIntensity);
                    if (intensity <= 0.0001)
                        return tex2D(_GrabTexture, grabUv);

                    float heatDrive = pow(intensity, 0.66);
                    float2 windDir = SafeNormalize(_DryCycleFoehnWindDir);
                    float2 crossDir = float2(-windDir.y, windDir.x);
                    float4 terrain = SampleTerrain(roomPx);
                    float exposure = lerp(1.0, terrain.r, _DryCycleHasFoehnTerrainField);
                    float wake = terrain.g * _DryCycleHasFoehnTerrainField;
                    float nozzle = terrain.b * _DryCycleHasFoehnTerrainField;
                    float edgeTurbulence = terrain.a * _DryCycleHasFoehnTerrainField;

                    float4 flowSample = SampleFlow(roomPx, windDir, crossDir);
                    float2 localFlow = flowSample.rg * 2.0 - 1.0;
                    localFlow.x = max(0.18, localFlow.x);
                    float2 flowDir = SafeNormalize(
                        windDir * localFlow.x + crossDir * localFlow.y * 0.82);

                    float4 streak = SampleStreak(roomPx, windDir, crossDir, flowSample.a * 0.37);
                    float4 streakUp = SampleStreak(
                        roomPx + crossDir * 7.5,
                        windDir,
                        crossDir,
                        flowSample.a * 0.37);
                    float4 streakDown = SampleStreak(
                        roomPx - crossDir * 7.5,
                        windDir,
                        crossDir,
                        flowSample.a * 0.37);

                    float sheet = saturate(streak.r * 0.78 + streak.g * 0.42);
                    float sheetEdge = (streakUp.r - streakDown.r) * 1.42 +
                                      (streakUp.g - streakDown.g) * 0.52;
                    float gust = saturate(flowSample.b * 0.66 + sheet * 0.72 + streak.a * 0.18);

                    // Lee wakes remain turbulent even when mean exposure falls. Narrow
                    // channels accelerate the coherent component instead of merely
                    // increasing random noise.
                    float localStrength =
                        0.34 + exposure * 0.66 + wake * 0.52 + nozzle * 0.72;
                    float wakeWave = sin(
                        (dot(roomPx, windDir) / 184.0 -
                         dot(roomPx, crossDir) / 117.0 -
                         _DryCycleFoehnTime * 2.20 + streak.b * 3.7) * 6.2831853);

                    float alongPulse =
                        sheetEdge * (9.4 + nozzle * 4.2) +
                        (gust - 0.52) * 4.4 +
                        sin((dot(roomPx, crossDir) / 152.0 - _DryCycleFoehnTime * 1.48) * 6.2831853) *
                            (1.2 + sheet * 1.5);
                    float crossPulse =
                        localFlow.y * 5.6 +
                        sheetEdge * 3.8 +
                        wakeWave * (wake * 7.2 + edgeTurbulence * 2.8);

                    float2 offsetPx =
                        windDir * alongPulse +
                        crossDir * crossPulse +
                        (flowDir - windDir) * (3.4 + flowSample.a * 2.4);
                    offsetPx *= heatDrive * localStrength;
                    offsetPx = ClampMagnitude(offsetPx, 18.5 * heatDrive);

                    // A second coherent high-speed sheet is phase-shifted instead of
                    // adding isotropic UV noise. This is what keeps Foehn from reading
                    // as underwater wobble.
                    float secondary = sin(
                        (dot(roomPx, crossDir) / 43.0 +
                         dot(roomPx, windDir) / 520.0 -
                         _DryCycleFoehnTime * 2.73 + streak.b * 4.0) * 6.2831853);
                    offsetPx += crossDir * secondary *
                                (0.9 + 2.2 * sheet) * heatDrive *
                                (0.46 + exposure * 0.54);

                    float2 displacedUv = grabUv +
                        offsetPx / max(_screenSize, float2(1.0, 1.0));
                    float3 scene = tex2D(_GrabTexture, displacedUv).rgb;

                    float blurPx =
                        heatDrive *
                        saturate(sheet * 0.72 + gust * 0.38 + nozzle * 0.34 - 0.31) *
                        (0.70 + exposure * 1.35);
                    float3 blurred = SampleDirectionalBlur(
                        displacedUv,
                        windDir,
                        blurPx * 2.75,
                        scene);
                    scene = lerp(scene, blurred, saturate(blurPx * 0.46));

                    // Dry-hot color treatment: stronger amber/yellow midtones and local
                    // gust contrast, but no white bleaching and no fog-like opacity.
                    float luma = dot(scene, float3(0.299, 0.587, 0.114));
                    float midtone = 1.0 - abs(luma * 2.0 - 1.0);
                    float tintAmount = heatDrive *
                        (0.075 + gust * 0.075 + sheet * 0.052) *
                        (0.70 + exposure * 0.30);
                    float3 dryScene = scene * float3(1.055, 1.005, 0.885);
                    dryScene += float3(0.115, 0.062, -0.018) * midtone;
                    scene = lerp(scene, dryScene, saturate(tintAmount * 2.15));

                    float focus = clamp(sheetEdge * 0.048 + (nozzle - wake) * 0.022, -0.055, 0.070);
                    scene *= 1.0 + focus * heatDrive;
                    scene = saturate((scene - 0.5) * (1.0 + heatDrive * 0.045) + 0.5);

                    return fixed4(scene, 1.0);
                }
                ENDCG
            }
        }
    }
}
