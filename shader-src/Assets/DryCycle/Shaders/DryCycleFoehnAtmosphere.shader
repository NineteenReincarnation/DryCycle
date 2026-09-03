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
                uniform float _DryCycleFoehnGustSeed;
                uniform float _DryCycleHasFoehnTextures;
                uniform float _DryCycleHasFoehnTerrainField;
                uniform float _DryCycleFoehnDebugMode;

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

                // x = broad gust body, y = narrow leading front, z = turbulence.
                // Constants mirror FoehnGustField.cs so particles, physics, audio and
                // the fullscreen resolve all react to the same moving wind event.
                float3 SampleSharedGust(
                    float2 roomPx,
                    float2 windDir,
                    float2 crossDir,
                    float intensity)
                {
                    float along = dot(roomPx, windDir);
                    float across = dot(roomPx, crossDir);
                    float speed = lerp(178.0, 286.0, pow(saturate(intensity), 0.72));

                    float warp =
                        sin(across / 244.0 + _DryCycleFoehnGustSeed * 6.2831853) * 54.0 +
                        sin(across / 91.0 - _DryCycleFoehnTime * 0.29 +
                            _DryCycleFoehnGustSeed * 13.7) * 18.0;
                    float primaryCoord =
                        along - _DryCycleFoehnTime * speed + warp +
                        _DryCycleFoehnGustSeed * 1080.0 * 3.17;
                    float primaryPhase = frac(primaryCoord / 1080.0) * 1080.0;
                    float primaryDistance = min(primaryPhase, 1080.0 - primaryPhase);
                    float primaryFront = 1.0 - smoothstep(30.0, 108.0, primaryDistance);
                    float primaryBody = 1.0 - smoothstep(92.0, 340.0, primaryDistance);

                    float secondaryWarp =
                        sin(across / 137.0 + _DryCycleFoehnTime * 0.18 +
                            _DryCycleFoehnGustSeed * 21.1) * 27.0;
                    float secondaryCoord =
                        along - _DryCycleFoehnTime * speed * 1.09 + secondaryWarp +
                        _DryCycleFoehnGustSeed * 640.0 * 7.41;
                    float secondaryPhase = frac(secondaryCoord / 640.0) * 640.0;
                    float secondaryDistance = min(secondaryPhase, 640.0 - secondaryPhase);
                    float secondaryFront = 1.0 - smoothstep(24.0, 76.0, secondaryDistance);
                    float secondaryBody = 1.0 - smoothstep(72.0, 208.0, secondaryDistance);

                    float body = saturate(
                        0.18 + primaryBody * 0.68 + secondaryBody * 0.23);
                    float front = saturate(max(primaryFront, secondaryFront * 0.62));
                    float turbulenceWave = abs(sin(
                        along / 177.0 - across / 109.0 -
                        _DryCycleFoehnTime * 2.06 +
                        _DryCycleFoehnGustSeed * 9.3));
                    float turbulence = saturate(
                        turbulenceWave * 0.22 +
                        secondaryBody * 0.16 +
                        front * 0.68);
                    return float3(body, front, turbulence);
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
                    float heatDrive = pow(max(intensity, 0.00001), 0.66);
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
                    float3 sharedGust = SampleSharedGust(
                        roomPx,
                        windDir,
                        crossDir,
                        intensity);
                    float gust = saturate(max(
                        sharedGust.x,
                        flowSample.b * 0.46 + sheet * 0.37 + streak.a * 0.12));
                    float gustFront = sharedGust.y;
                    float gustTurbulence = sharedGust.z;

                    int debugMode = (int)floor(_DryCycleFoehnDebugMode + 0.5);
                    if (debugMode == 1)
                    {
                        // R/G = tangent-space flow, B = the shared resolved gust signal.
                        return fixed4(flowSample.r, flowSample.g, gust, 1.0);
                    }
                    if (debugMode == 2)
                    {
                        float3 debugTerrain = float3(exposure, wake, nozzle);
                        debugTerrain += edgeTurbulence * 0.18;
                        return fixed4(saturate(debugTerrain), 1.0);
                    }
                    if (debugMode == 3)
                    {
                        return fixed4(streak.r, streak.g, streak.a, 1.0);
                    }

                    if (intensity <= 0.0001)
                        return tex2D(_GrabTexture, grabUv);

                    // Mean air stays comparatively stable. Stronger refraction is now
                    // concentrated into moving gust fronts, which stops the whole scene
                    // from reading as permanent underwater/HeatWave wobble.
                    float localStrength =
                        0.40 + exposure * 0.48 + wake * 0.22 + nozzle * 0.36;
                    float wakeWave = sin(
                        (dot(roomPx, windDir) / 184.0 -
                         dot(roomPx, crossDir) / 117.0 -
                         _DryCycleFoehnTime * 2.20 + streak.b * 3.7) * 6.2831853);
                    float frontShear = sin(
                        (dot(roomPx, crossDir) / 71.0 -
                         _DryCycleFoehnTime * 1.32 + streak.b * 2.6) * 6.2831853);

                    float alongPulse =
                        sheetEdge * (4.8 + nozzle * 2.2) +
                        (gust - 0.48) * 2.6 +
                        frontShear * gustFront * 2.25 +
                        sin((dot(roomPx, crossDir) / 152.0 -
                             _DryCycleFoehnTime * 1.48) * 6.2831853) *
                            (0.55 + sheet * 0.75);
                    float crossPulse =
                        localFlow.y * 2.9 +
                        sheetEdge * 1.75 +
                        wakeWave * (wake * 3.8 + edgeTurbulence * 1.4) +
                        frontShear * gustTurbulence * 0.85;

                    float distortionDrive =
                        heatDrive *
                        (0.38 + gust * 0.24 + gustFront * 0.48);
                    float2 offsetPx =
                        windDir * alongPulse +
                        crossDir * crossPulse +
                        (flowDir - windDir) * (1.65 + flowSample.a * 1.25);
                    offsetPx *= distortionDrive * localStrength;

                    float secondary = sin(
                        (dot(roomPx, crossDir) / 43.0 +
                         dot(roomPx, windDir) / 520.0 -
                         _DryCycleFoehnTime * 2.73 + streak.b * 4.0) * 6.2831853);
                    offsetPx += crossDir * secondary *
                                (0.30 + 0.92 * sheet) * heatDrive *
                                (0.42 + gustFront * 0.58) *
                                (0.52 + exposure * 0.48);

                    // Previous Foehn allowed ~18.5px sustained displacement. The new
                    // ceiling is ~12.8px and typical non-front air is much lower.
                    offsetPx = ClampMagnitude(offsetPx, 12.8 * heatDrive);

                    float2 displacedUv = grabUv +
                        offsetPx / max(_screenSize, float2(1.0, 1.0));
                    float3 scene = tex2D(_GrabTexture, displacedUv).rgb;

                    float blurPx =
                        heatDrive *
                        saturate(
                            sheet * 0.36 +
                            gust * 0.24 +
                            nozzle * 0.20 +
                            gustFront * 0.56 - 0.39) *
                        (0.56 + exposure * 0.70);
                    float3 blurred = SampleDirectionalBlur(
                        displacedUv,
                        windDir,
                        blurPx * 1.95,
                        scene);
                    scene = lerp(scene, blurred, saturate(blurPx * 0.34));

                    // Dry-hot color treatment remains readable and warm without becoming
                    // an opaque sandstorm filter or bleaching highlights toward white.
                    float luma = dot(scene, float3(0.299, 0.587, 0.114));
                    float midtone = 1.0 - abs(luma * 2.0 - 1.0);
                    float tintAmount = heatDrive *
                        (0.068 + gust * 0.060 + sheet * 0.036 + gustFront * 0.022) *
                        (0.70 + exposure * 0.30);
                    float3 dryScene = scene * float3(1.055, 1.005, 0.885);
                    dryScene += float3(0.115, 0.062, -0.018) * midtone;
                    scene = lerp(scene, dryScene, saturate(tintAmount * 2.05));

                    float focus = clamp(
                        sheetEdge * 0.028 +
                        (nozzle - wake) * 0.016 +
                        gustFront * frontShear * 0.010,
                        -0.036,
                        0.046);
                    scene *= 1.0 + focus * heatDrive;
                    scene = saturate((scene - 0.5) * (1.0 + heatDrive * 0.034) + 0.5);

                    return fixed4(scene, 1.0);
                }
                ENDCG
            }
        }
    }
}
