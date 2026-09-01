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
                sampler2D _LevelTex;
                sampler2D _DryCycleHeatOpticalTex;
                sampler2D _DryCycleHeatThermalTex;
                sampler2D _DryCycleHeatVelocityTex;
                sampler2D _DryCycleHeatTerrainTex;
                sampler2D _DryCycleHeatMacroNoise;
                sampler2D _DryCycleHeatMicroNoise;

                uniform float4 _spriteRect;
                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleRoomSizePx;
                uniform float _DryCycleHeatWaveIntensity;
                uniform float _DryCycleWhiteHeat;
                uniform float _DryCycleHeatSolarIntensity;
                uniform float _DryCycleHeatBurst;
                uniform float _DryCycleHeatBurstKick;
                uniform float _DryCycleHeatStillness;
                uniform float _DryCycleHeatTime;
                uniform float _DryCycleHasHeatSimulation;
                uniform float _DryCycleHasHeatCustomNoise;
                uniform float _DryCycleHeatLayerOpticalScale;
                uniform float _DryCycleHeatLayerMacroScale;
                uniform float _DryCycleHeatLayerMicroScale;
                uniform float _DryCycleHeatLayerStreakScale;
                uniform float _DryCycleHeatLayerToneWeight;
                uniform int _DryCycleHeatDebugMode;

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
                    float2 span = max(
                        abs(_spriteRect.zw - _spriteRect.xy),
                        float2(0.0001, 0.0001));
                    return (screenUV - _spriteRect.xy) / span;
                }

                float DecodePseudoDepth(float2 levelUV)
                {
                    float4 level = tex2D(_LevelTex, saturate(levelUV));
                    if (level.r >= 0.999 && level.g >= 0.999 && level.b >= 0.999)
                        return 0.55;

                    float encoded = fmod(
                        max(0.0, level.r * 255.0 - 1.0),
                        30.0) / 30.0;
                    return saturate(encoded * 1.34);
                }

                float4 SampleOptical(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatOpticalTex, saturate(roomUV));
                }

                float4 SampleThermal(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatThermalTex, saturate(roomUV));
                }

                float4 SampleTerrain(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatTerrainTex, saturate(roomUV));
                }

                float2 MacroGradientCustom(float2 uv)
                {
                    float2 texel = float2(1.0 / 256.0, 1.0 / 256.0);
                    float2 xP = tex2D(_DryCycleHeatMacroNoise, frac(uv + float2(texel.x, 0.0))).rg;
                    float2 xM = tex2D(_DryCycleHeatMacroNoise, frac(uv - float2(texel.x, 0.0))).rg;
                    float2 yP = tex2D(_DryCycleHeatMacroNoise, frac(uv + float2(0.0, texel.y))).gb;
                    float2 yM = tex2D(_DryCycleHeatMacroNoise, frac(uv - float2(0.0, texel.y))).gb;
                    return float2(
                        (xP.x - xM.x) + (xP.y - xM.y) * 0.42,
                        (yP.x - yM.x) + (yP.y - yM.y) * 0.42);
                }

                float2 MacroPhase(float2 roomUV, float time)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 p0 = roomUV * float2(2.05, 1.32) +
                            float2(time * 0.0046, time * 0.0020);
                        float2 p1 = roomUV * float2(3.55, 2.38) +
                            float2(-time * 0.0027, time * 0.0035) + 0.37;
                        return MacroGradientCustom(p0) * 0.72 +
                               MacroGradientCustom(p1) * 0.38;
                    }

                    // Analytic fallback if runtime phase texture generation failed.
                    float a = sin((roomUV.y * 2.3 + roomUV.x * 0.7 + time * 0.0061) * 6.2831853);
                    float b = cos((roomUV.x * 3.1 - roomUV.y * 1.2 - time * 0.0042) * 6.2831853);
                    float c = sin((roomUV.x * 1.4 + roomUV.y * 3.7 + time * 0.0032) * 6.2831853);
                    return float2(a + c * 0.36, b - c * 0.28) * 0.052;
                }

                float2 MicroPhase(float2 screenUV, float time)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 pixelScale = max(_screenSize, float2(1.0, 1.0)) / 84.0;
                        float2 p = frac(
                            screenUV * pixelScale +
                            float2(time * 0.087, -time * 0.069));
                        float2 q = frac(
                            screenUV * pixelScale * 1.71 +
                            float2(-time * 0.057, time * 0.097) + 0.41);
                        float2 a = tex2D(_DryCycleHeatMicroNoise, p).rg * 2.0 - 1.0;
                        float2 b = tex2D(_DryCycleHeatMicroNoise, q).gb * 2.0 - 1.0;
                        return a * 0.64 + b * 0.36;
                    }

                    float a = sin((screenUV.x * 113.0 + screenUV.y * 71.0 + time * 1.37) * 6.2831853);
                    float b = cos((screenUV.x * 83.0 - screenUV.y * 127.0 - time * 1.09) * 6.2831853);
                    return float2(a, b) * 0.36;
                }

                float VisualSkyTransmission(float2 roomUV)
                {
                    float4 terrain = SampleTerrain(roomUV);
                    float2 oneTile = 20.0 / max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                    float above = SampleTerrain(roomUV + float2(0.0, oneTile.y)).a;
                    float twoAbove = SampleTerrain(roomUV + float2(0.0, oneTile.y * 2.0)).a;

                    // Solid pixels encode no sky themselves. Sampling the air directly
                    // above lets sun-baked floors/platforms bleach without making a
                    // shaded creature or wall glow merely because it is bright-colored.
                    return saturate(max(terrain.a, max(above * 0.94, twoAbove * 0.72)));
                }

                float3 HeatMap(float value)
                {
                    value = saturate(value);
                    float3 cold = float3(0.015, 0.025, 0.055);
                    float3 warm = float3(0.95, 0.16, 0.025);
                    float3 hot = float3(1.0, 0.88, 0.10);
                    float3 white = float3(1.0, 1.0, 1.0);
                    float3 a = lerp(cold, warm, smoothstep(0.0, 0.48, value));
                    float3 b = lerp(hot, white, smoothstep(0.72, 1.0, value));
                    return lerp(a, b, smoothstep(0.42, 0.78, value));
                }

                float4 DebugOutput(float2 roomUV)
                {
                    float4 thermal = SampleThermal(roomUV);
                    float4 optical = SampleOptical(roomUV);
                    float4 terrain = SampleTerrain(roomUV);
                    float2 velocity = tex2D(_DryCycleHeatVelocityTex, saturate(roomUV)).xy;

                    if (_DryCycleHeatDebugMode == 1)
                    {
                        float3 heat = HeatMap(thermal.r);
                        heat = lerp(heat, float3(0.40, 0.10, 0.95), saturate(thermal.g) * 0.34);
                        return float4(heat, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 2)
                    {
                        float2 pxVelocity = velocity * _DryCycleRoomSizePx;
                        float magnitude = saturate(length(pxVelocity) / 150.0);
                        float2 direction = length(pxVelocity) > 0.001
                            ? normalize(pxVelocity)
                            : float2(0.0, 0.0);
                        return float4(
                            0.5 + direction.x * 0.45 * magnitude,
                            0.5 + direction.y * 0.45 * magnitude,
                            magnitude,
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 3)
                    {
                        float magnitude = saturate(length(optical.rg) * 420.0);
                        return float4(
                            saturate(0.5 + optical.r * 42.0),
                            saturate(0.5 + optical.g * 42.0),
                            magnitude,
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 4)
                    {
                        float3 color = terrain.g.xxx * 0.25;
                        color += float3(0.85, 0.06, 0.03) * step(0.5, terrain.r);
                        color += float3(0.95, 0.62, 0.05) * terrain.b * 0.85;
                        color += float3(0.05, 0.28, 0.95) * terrain.a * 0.72;
                        return float4(saturate(color), 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 5)
                    {
                        float boundary = saturate(optical.a);
                        float retained = saturate(thermal.g);
                        return float4(
                            boundary,
                            retained * 0.58 + boundary * 0.42,
                            retained,
                            1.0);
                    }

                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                float3 WhiteHeatTone(
                    float3 color,
                    float amount,
                    float skyTransmission,
                    float thermal)
                {
                    amount = saturate(amount);
                    float localSun = saturate(
                        skyTransmission *
                        _DryCycleHeatSolarIntensity);
                    float exposure = saturate(localSun * 0.92 + thermal * 0.12);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadowProtect = 1.0 - smoothstep(0.11, 0.39, luma);
                    float mid = smoothstep(0.17, 0.72, luma) *
                                (1.0 - smoothstep(0.76, 0.98, luma));
                    float high = smoothstep(0.48, 0.94, luma);

                    // Sunlight progressively destroys chroma and contrast in exposed
                    // mid/high values. Deep shadows are intentionally protected.
                    float desaturate =
                        amount *
                        exposure *
                        (mid * 0.42 + high * 0.66) *
                        (1.0 - shadowProtect * 0.94);
                    color = lerp(color, luma.xxx, saturate(desaturate));

                    float contrastLoss =
                        amount *
                        exposure *
                        (0.05 + high * 0.16) *
                        (1.0 - shadowProtect);
                    color = lerp(color, lerp(color, luma.xxx, 0.65), contrastLoss);

                    float bleach =
                        amount *
                        high *
                        exposure *
                        (0.31 + localSun * 0.39 + thermal * 0.12);
                    color = lerp(color, 1.0.xxx, saturate(bleach));

                    // Veiling glare belongs to exposed atmosphere, not emissive
                    // surfaces. It stays very low amplitude and never lifts deep shade.
                    float veil =
                        amount *
                        localSun *
                        (0.015 + thermal * 0.021) *
                        (1.0 - shadowProtect * 0.96);
                    color += veil;
                    return saturate(color);
                }

                float4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.0001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);

                    if (_DryCycleHeatDebugMode > 0)
                        return DebugOutput(roomUV);

                    float intensity = saturate(_DryCycleHeatWaveIntensity);
                    float4 centerOptical = SampleOptical(roomUV);
                    float residualThermal = centerOptical.b * _DryCycleHasHeatSimulation;
                    float opticalPresence = saturate(max(
                        intensity,
                        max(
                            _DryCycleHeatBurst * 0.48,
                            max(_DryCycleHeatBurstKick * 0.58, residualThermal * 0.36))));

                    float4 original = tex2D(_GrabTexture, grabUV);
                    float toneAmount =
                        saturate(_DryCycleWhiteHeat) *
                        max(0.0, _DryCycleHeatLayerToneWeight);
                    if (opticalPresence <= 0.0001 && toneAmount <= 0.0001)
                        return original;

                    // Real path length is now supplied by the ordered Far/Mid/Near
                    // capture slices. _LevelTex only gives a tiny local refinement so
                    // decorative depth cannot dominate the optical hierarchy.
                    float levelDepth = DecodePseudoDepth(LevelUV(screenUV));
                    float depthGain = lerp(0.96, 1.06, levelDepth);
                    float2 pxToRoom = 1.0 / max(
                        _DryCycleRoomSizePx,
                        float2(1.0, 1.0));
                    float2 accumulatedRoom = float2(0.0, 0.0);
                    float accumulatedThermal = 0.0;
                    float accumulatedBoundary = 0.0;

                    [unroll]
                    for (int stepIndex = 0; stepIndex < 6; stepIndex++)
                    {
                        float traceT = (stepIndex + 0.5) / 6.0;
                        float2 traceRoomUV = saturate(
                            roomUV +
                            accumulatedRoom * (0.40 + traceT * 0.31));
                        float4 optical = SampleOptical(traceRoomUV);
                        float4 terrain = SampleTerrain(traceRoomUV);
                        float thermal = lerp(
                            0.20 * intensity,
                            optical.b,
                            _DryCycleHasHeatSimulation);
                        float boundary = lerp(
                            terrain.b * intensity * 0.46,
                            optical.a,
                            _DryCycleHasHeatSimulation);

                        float2 meso =
                            optical.rg *
                            _DryCycleHasHeatSimulation *
                            (0.74 + thermal * 0.98);
                        float2 macro =
                            MacroPhase(traceRoomUV, _DryCycleHeatTime) *
                            pxToRoom *
                            (1.05 + thermal * 2.15) *
                            _DryCycleHeatLayerMacroScale;
                        float2 micro =
                            MicroPhase(screenUV + accumulatedRoom, _DryCycleHeatTime) *
                            pxToRoom *
                            (0.30 + thermal * 1.30 + boundary * 1.08) *
                            _DryCycleHeatLayerMicroScale;

                        float stillness = saturate(_DryCycleHeatStillness);
                        meso *= lerp(1.0, 0.70, stillness);
                        macro *= lerp(1.0, 0.82, stillness);
                        micro *= lerp(1.0, 0.22, stillness);

                        float burstScale =
                            1.0 +
                            saturate(_DryCycleHeatBurst) * 1.62 +
                            saturate(_DryCycleHeatBurstKick) * 2.15;
                        float2 stepOffset =
                            meso * _DryCycleHeatLayerOpticalScale +
                            macro +
                            micro;

                        // Near hot surfaces the vertical refractive gradient dominates,
                        // creating local compression/stretch and small inferior-mirage
                        // folds without drawing a fake water puddle.
                        stepOffset.y *=
                            1.0 +
                            boundary *
                            (0.74 + intensity * 0.68 + _DryCycleHeatBurstKick * 0.45);
                        stepOffset *=
                            opticalPresence *
                            depthGain *
                            burstScale /
                            6.0;
                        accumulatedRoom += stepOffset;
                        accumulatedThermal += thermal / 6.0;
                        accumulatedBoundary += boundary / 6.0;
                    }

                    float2 screenOffset = accumulatedRoom /
                        max(abs(_camInRoomRect.zw), float2(0.0001, 0.0001));
                    float2 grabOffset = float2(
                        screenOffset.x,
                        screenOffset.y * _ProjectionParams.x);

                    float streakAmount = saturate(
                        (accumulatedThermal * 0.50 +
                         accumulatedBoundary * 0.38 +
                         _DryCycleHeatBurst * 0.42 +
                         _DryCycleHeatBurstKick * 0.62) *
                        _DryCycleHeatLayerStreakScale);
                    float2 streak = grabOffset * (0.35 + streakAmount * 0.68);
                    float3 scene = tex2D(
                        _GrabTexture,
                        saturate(grabUV + grabOffset)).rgb;

                    // Directional optical smear exists only where the integrated path
                    // is energetic. Ordinary pixel art stays a single sharp sample.
                    if (streakAmount > 0.055)
                    {
                        float3 a = tex2D(
                            _GrabTexture,
                            saturate(grabUV + grabOffset - streak * 0.58)).rgb;
                        float3 b = tex2D(
                            _GrabTexture,
                            saturate(grabUV + grabOffset + streak * 0.58)).rgb;
                        scene = lerp(
                            scene,
                            (a + scene * 2.0 + b) * 0.25,
                            streakAmount * 0.36);
                    }

                    float skyTransmission = VisualSkyTransmission(roomUV);
                    scene = WhiteHeatTone(
                        scene,
                        toneAmount,
                        skyTransmission,
                        accumulatedThermal);

                    return float4(scene, 1.0);
                }
                ENDCG
            }
        }
    }
}
