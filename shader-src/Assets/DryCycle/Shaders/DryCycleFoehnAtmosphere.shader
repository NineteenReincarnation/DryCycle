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
                sampler2D _DryCycleFoehnDustField;
                sampler2D _DryCycleFoehnTerrainField;

                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleFoehnRoomSizePx;
                uniform float _DryCycleFoehnIntensity;
                uniform float _DryCycleFoehnTime;
                uniform float2 _DryCycleFoehnWindDir;
                uniform float _DryCycleFoehnGustSeed;
                uniform float _DryCycleHasFoehnTextures;
                uniform float _DryCycleHasFoehnDustField;
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

                float4 SampleDust(float2 roomPx, float2 windDir, float2 crossDir)
                {
                    float along = dot(roomPx, windDir);
                    float across = dot(roomPx, crossDir);

                    if (_DryCycleHasFoehnDustField > 0.5)
                    {
                        float phaseA = frac(_DryCycleFoehnTime * 0.185 + _DryCycleFoehnGustSeed * 0.13);
                        float phaseB = frac(phaseA + 0.5);
                        float blendA = PhaseBlend(phaseA);
                        float2 uvA = float2(along / 980.0, across / 470.0);
                        float4 a = tex2D(
                            _DryCycleFoehnDustField,
                            frac(uvA - float2(phaseA * 0.94, phaseA * 0.035)));
                        float4 b = tex2D(
                            _DryCycleFoehnDustField,
                            frac(uvA - float2(phaseB * 0.94, phaseB * 0.035) + float2(0.347, 0.211)));
                        float4 broad = lerp(a, b, blendA);

                        float phaseC = frac(_DryCycleFoehnTime * 0.315 + 0.37);
                        float phaseD = frac(phaseC + 0.5);
                        float blendB = PhaseBlend(phaseC);
                        float2 uvB = float2(along / 430.0, across / 225.0);
                        float4 c = tex2D(
                            _DryCycleFoehnDustField,
                            frac(uvB - float2(phaseC * 1.14, -phaseC * 0.045) + float2(0.193, 0.417)));
                        float4 d = tex2D(
                            _DryCycleFoehnDustField,
                            frac(uvB - float2(phaseD * 1.14, -phaseD * 0.045) + float2(0.661, 0.083)));
                        float4 detail = lerp(c, d, blendB);

                        return float4(
                            saturate(broad.r * 0.74 + detail.r * 0.34),
                            saturate(broad.g * 0.60 + detail.g * 0.52),
                            saturate(broad.b * 0.38 + detail.b * 0.72),
                            frac(broad.a * 0.63 + detail.a * 0.57));
                    }

                    float broadWave = sin(
                        (along / 610.0 - across / 430.0 - _DryCycleFoehnTime * 0.78) * 6.2831853);
                    float clumpWave = sin(
                        (along / 260.0 + across / 170.0 - _DryCycleFoehnTime * 1.34) * 6.2831853);
                    return float4(
                        saturate(broadWave * 0.30 + 0.50),
                        saturate(clumpWave * 0.36 + 0.48),
                        saturate(abs(clumpWave) * 0.72),
                        frac(along / 719.0 + across / 331.0));
                }

                // x = broad gust body, y = narrow leading front, z = turbulence.
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

                // x = signed compression/rebound wave, y = pressure-front envelope.
                // A narrow oscillatory derivative-like profile reads as an air-pressure
                // front sweeping through the scene instead of a persistent water wobble.
                float2 SamplePressureWave(
                    float2 roomPx,
                    float2 windDir,
                    float2 crossDir,
                    float intensity)
                {
                    float along = dot(roomPx, windDir);
                    float across = dot(roomPx, crossDir);
                    float speed = lerp(178.0, 286.0, pow(saturate(intensity), 0.72));

                    float warpA =
                        sin(across / 244.0 + _DryCycleFoehnGustSeed * 6.2831853) * 54.0 +
                        sin(across / 91.0 - _DryCycleFoehnTime * 0.29 +
                            _DryCycleFoehnGustSeed * 13.7) * 18.0;
                    float coordA =
                        along - _DryCycleFoehnTime * speed + warpA +
                        _DryCycleFoehnGustSeed * 1080.0 * 3.17;
                    float phaseA = frac(coordA / 1080.0) * 1080.0;
                    float signedA = phaseA > 540.0 ? phaseA - 1080.0 : phaseA;
                    float envA = 1.0 - smoothstep(18.0, 142.0, abs(signedA));
                    float waveA = sin(signedA / 18.0 * 3.14159265) * envA * envA;

                    float warpB =
                        sin(across / 137.0 + _DryCycleFoehnTime * 0.18 +
                            _DryCycleFoehnGustSeed * 21.1) * 27.0;
                    float coordB =
                        along - _DryCycleFoehnTime * speed * 1.09 + warpB +
                        _DryCycleFoehnGustSeed * 640.0 * 7.41;
                    float phaseB = frac(coordB / 640.0) * 640.0;
                    float signedB = phaseB > 320.0 ? phaseB - 640.0 : phaseB;
                    float envB = 1.0 - smoothstep(14.0, 104.0, abs(signedB));
                    float waveB = sin(signedB / 14.0 * 3.14159265) * envB * envB;

                    return float2(
                        waveA * 0.86 + waveB * 0.36,
                        saturate(max(envA, envB * 0.64)));
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
                    float3 a = tex2D(_GrabTexture, grabUv - stepUv * 1.55).rgb;
                    float3 b = tex2D(_GrabTexture, grabUv - stepUv * 0.68).rgb;
                    float3 c = tex2D(_GrabTexture, grabUv + stepUv * 0.68).rgb;
                    float3 d = tex2D(_GrabTexture, grabUv + stepUv * 1.55).rgb;
                    return center * 0.42 + (b + c) * 0.19 + (a + d) * 0.10;
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
                        windDir * localFlow.x + crossDir * localFlow.y * 0.78);

                    float4 streak = SampleStreak(roomPx, windDir, crossDir, flowSample.a * 0.37);
                    float4 streakUp = SampleStreak(
                        roomPx + crossDir * 8.0,
                        windDir,
                        crossDir,
                        flowSample.a * 0.37);
                    float4 streakDown = SampleStreak(
                        roomPx - crossDir * 8.0,
                        windDir,
                        crossDir,
                        flowSample.a * 0.37);
                    float4 dust = SampleDust(roomPx, windDir, crossDir);

                    float sheet = saturate(streak.r * 0.72 + streak.g * 0.36);
                    float sheetEdge = (streakUp.r - streakDown.r) * 1.16 +
                                      (streakUp.g - streakDown.g) * 0.42;
                    float3 sharedGust = SampleSharedGust(
                        roomPx,
                        windDir,
                        crossDir,
                        intensity);
                    float2 pressureWave = SamplePressureWave(
                        roomPx,
                        windDir,
                        crossDir,
                        intensity);
                    float gust = saturate(max(
                        sharedGust.x,
                        flowSample.b * 0.42 + sheet * 0.30 + streak.a * 0.10));
                    float gustFront = sharedGust.y;
                    float gustTurbulence = sharedGust.z;

                    int debugMode = (int)floor(_DryCycleFoehnDebugMode + 0.5);
                    if (debugMode == 1)
                    {
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
                        return fixed4(dust.r, dust.g, max(dust.b, pressureWave.y), 1.0);
                    }

                    if (intensity <= 0.0001)
                        return tex2D(_GrabTexture, grabUv);

                    float localStrength =
                        0.42 + exposure * 0.44 + wake * 0.16 + nozzle * 0.30;
                    float wakeWave = sin(
                        (dot(roomPx, windDir) / 184.0 -
                         dot(roomPx, crossDir) / 117.0 -
                         _DryCycleFoehnTime * 2.20 + streak.b * 3.7) * 6.2831853);
                    float frontShear = sin(
                        (dot(roomPx, crossDir) / 79.0 -
                         _DryCycleFoehnTime * 1.26 + dust.a * 3.1) * 6.2831853);

                    // Low continuous shear. This stays much weaker than the moving
                    // pressure front so the room no longer looks permanently liquefied.
                    float ambientAlong =
                        sheetEdge * (2.75 + nozzle * 1.45) +
                        (gust - 0.50) * 1.55 +
                        sin((dot(roomPx, crossDir) / 168.0 -
                             _DryCycleFoehnTime * 1.34) * 6.2831853) *
                            (0.30 + sheet * 0.46);
                    float ambientCross =
                        localFlow.y * 1.85 +
                        sheetEdge * 0.92 +
                        wakeWave * (wake * 2.75 + edgeTurbulence * 0.92);

                    float ambientDrive = heatDrive * (0.24 + gust * 0.18);
                    float2 offsetPx =
                        (windDir * ambientAlong +
                         crossDir * ambientCross +
                         (flowDir - windDir) * (0.92 + flowSample.a * 0.72)) *
                        ambientDrive * localStrength;

                    // Pressure-wave displacement is intentionally stronger than the
                    // ambient heat refraction: geometry compresses, overshoots and
                    // rebounds as the gust front crosses it, like a visible air shock.
                    float shockStrength =
                        heatDrive *
                        (6.8 + gustFront * 3.2 + nozzle * 1.7) *
                        (0.62 + exposure * 0.38);
                    offsetPx += windDir * pressureWave.x * shockStrength;
                    offsetPx += crossDir *
                                frontShear *
                                pressureWave.y *
                                heatDrive *
                                (0.55 + wake * 1.65 + edgeTurbulence * 0.62);

                    offsetPx = ClampMagnitude(offsetPx, 14.6 * heatDrive);

                    float2 displacedUv = grabUv +
                        offsetPx / max(_screenSize, float2(1.0, 1.0));
                    float3 scene = tex2D(_GrabTexture, displacedUv).rgb;

                    float blurPx =
                        heatDrive *
                        saturate(
                            sheet * 0.20 +
                            gust * 0.16 +
                            nozzle * 0.14 +
                            pressureWave.y * 0.68 - 0.30) *
                        (0.46 + exposure * 0.58);
                    float3 blurred = SampleDirectionalBlur(
                        displacedUv,
                        windDir,
                        blurPx * 1.82,
                        scene);
                    scene = lerp(scene, blurred, saturate(blurPx * 0.30));

                    // Thin Sandstorm-inspired suspended dust layer. Unlike Watcher's
                    // storm this never owns visibility: it is advected, terrain-aware,
                    // gust-reactive mineral air that sits underneath the point grains.
                    float roomHeight01 = saturate(
                        roomPx.y / max(_DryCycleFoehnRoomSizePx.y, 1.0));
                    float lowerAir = lerp(1.24, 0.72, roomHeight01);
                    float dustDensity = saturate(
                        dust.r * 0.58 +
                        dust.g * 0.34 +
                        dust.b * 0.12 +
                        streak.a * 0.12);
                    float terrainDust =
                        exposure * 0.72 +
                        nozzle * 0.24 +
                        wake * 0.20 +
                        edgeTurbulence * 0.08;
                    float dustAmount = saturate(
                        dustDensity *
                        heatDrive *
                        lowerAir *
                        terrainDust *
                        (0.040 + gust * 0.055 + gustFront * 0.045));
                    dustAmount += saturate(
                        dust.g * wake * heatDrive * (0.018 + gustTurbulence * 0.026));
                    dustAmount = min(dustAmount, 0.145);

                    float3 dustDark = float3(0.30, 0.225, 0.105);
                    float3 dustGold = float3(0.66, 0.485, 0.205);
                    float3 dustColor = lerp(dustDark, dustGold, saturate(dust.r * 0.68 + dust.g * 0.32));
                    scene = lerp(scene, dustColor, dustAmount);

                    // Deeper, duskier ochre grade: lower overall luminance, reduce blue,
                    // retain hot amber midtones, and keep shadows heavy rather than gray.
                    float luma = dot(scene, float3(0.299, 0.587, 0.114));
                    float midtone = 1.0 - abs(luma * 2.0 - 1.0);
                    float3 deepScene = scene * (0.945 - heatDrive * 0.040);
                    float3 ochreScene =
                        deepScene * float3(1.105, 0.982, 0.705) +
                        luma * float3(0.082, 0.030, -0.012) * midtone;
                    float gradeAmount = heatDrive *
                        (0.285 + gust * 0.070 + pressureWave.y * 0.035 + dustAmount * 0.70);
                    scene = lerp(deepScene, ochreScene, saturate(gradeAmount));

                    float shadow = 1.0 - smoothstep(0.10, 0.40, luma);
                    scene *= 1.0 - shadow * heatDrive * 0.055;
                    scene += float3(0.022, 0.010, -0.010) *
                             shadow * heatDrive * 0.34;

                    // Fine dust breakup is a tiny luminance flutter, not another line
                    // layer. It helps the suspended dust feel particulate in motion.
                    float microDust = (dust.b - 0.5) *
                                      dustAmount *
                                      (0.045 + gustTurbulence * 0.025);
                    scene += microDust * float3(0.78, 0.58, 0.27);

                    float focus = clamp(
                        sheetEdge * 0.018 +
                        (nozzle - wake) * 0.012 +
                        pressureWave.x * pressureWave.y * 0.020,
                        -0.032,
                        0.038);
                    scene *= 1.0 + focus * heatDrive;
                    scene = saturate((scene - 0.40) * (1.0 + heatDrive * 0.072) + 0.40);

                    return fixed4(saturate(scene), 1.0);
                }
                ENDCG
            }
        }
    }
}
