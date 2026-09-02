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
                uniform float _DryCycleHeatTime;
                uniform float _DryCycleHasHeatSimulation;
                uniform float _DryCycleHasHeatCustomNoise;
                uniform int _DryCycleHeatDebugMode;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float2 uv : TEXCOORD0;
                    float4 screenPos : TEXCOORD1;
                    float4 grabPos : TEXCOORD2;
                };

                v2f vert(appdata_full v)
                {
                    v2f o;
                    o.pos = UnityObjectToClipPos(v.vertex);
                    o.uv = v.texcoord.xy;
                    o.screenPos = ComputeScreenPos(o.pos);
                    o.grabPos = ComputeGrabScreenPos(o.pos);
                    return o;
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
                        return 0.60;

                    float encoded = fmod(
                        max(0.0, level.r * 255.0 - 1.0),
                        30.0) / 30.0;
                    return saturate(encoded * 1.34);
                }

                float4 SampleTerrain(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatTerrainTex, saturate(roomUV));
                }

                float4 SampleThermal(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatThermalTex, saturate(roomUV));
                }

                float4 SampleOptical(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatOpticalTex, saturate(roomUV));
                }

                float MacroValue(float2 uv)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                        return tex2D(_DryCycleHeatMacroNoise, frac(uv)).r;

                    float a = sin((uv.x * 2.7 + uv.y * 1.3) * 6.2831853);
                    float b = cos((uv.x * 1.1 - uv.y * 3.4 + 0.37) * 6.2831853);
                    return saturate(0.5 + a * 0.23 + b * 0.17);
                }

                float2 MacroVector(float2 roomUV, float time)
                {
                    float2 p = roomUV * float2(2.4, 1.55) +
                        float2(time * 0.0032, -time * 0.0017);

                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 texel = float2(1.0 / 256.0, 1.0 / 256.0);
                        float xP = tex2D(_DryCycleHeatMacroNoise, frac(p + float2(texel.x, 0.0))).r;
                        float xM = tex2D(_DryCycleHeatMacroNoise, frac(p - float2(texel.x, 0.0))).r;
                        float yP = tex2D(_DryCycleHeatMacroNoise, frac(p + float2(0.0, texel.y))).g;
                        float yM = tex2D(_DryCycleHeatMacroNoise, frac(p - float2(0.0, texel.y))).g;
                        return float2(xP - xM, yP - yM) * 3.1;
                    }

                    return float2(
                        sin((p.x + p.y * 0.41) * 6.2831853),
                        cos((p.y - p.x * 0.37) * 6.2831853)) * 0.42;
                }

                float2 MicroVector(float2 screenUV, float time)
                {
                    float2 pixelScale = max(_screenSize, float2(1.0, 1.0)) / 72.0;
                    float2 p = frac(
                        screenUV * pixelScale +
                        float2(time * 0.074, -time * 0.111));
                    float2 q = frac(
                        screenUV * pixelScale * 1.67 +
                        float2(-time * 0.093, time * 0.061) + 0.43);

                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 a = tex2D(_DryCycleHeatMicroNoise, p).rg * 2.0 - 1.0;
                        float2 b = tex2D(_DryCycleHeatMicroNoise, q).gb * 2.0 - 1.0;
                        return a * 0.66 + b * 0.34;
                    }

                    return float2(
                        sin((p.x * 17.0 + p.y * 11.0) * 6.2831853),
                        cos((q.x * 13.0 - q.y * 19.0) * 6.2831853)) * 0.58;
                }

                float BoundaryBelow(float2 roomUV, float tilesDown)
                {
                    float tileY = 20.0 / max(_DryCycleRoomSizePx.y, 1.0);
                    float distance = tileY * tilesDown;
                    float valid = step(distance, roomUV.y);
                    return SampleTerrain(roomUV - float2(0.0, distance)).b * valid;
                }

                void BuildHeatMasks(
                    float2 roomUV,
                    float time,
                    out float groundLayer,
                    out float plumeLayer)
                {
                    // Ground shimmer lives in a thin 0-40px boundary layer. The old
                    // renderer let the entire simulated field distort the screen; this
                    // mask is deliberately geometric and local.
                    float b0 = BoundaryBelow(roomUV, 0.0);
                    float b1 = BoundaryBelow(roomUV, 0.75);
                    float b2 = BoundaryBelow(roomUV, 1.50);
                    float groundSource = max(b0, max(b1 * 0.92, b2 * 0.48));
                    groundLayer = saturate(groundSource);

                    // Automatic desert plumes extend farther above sun-baked surfaces,
                    // but are broken into rising pockets instead of forming a solid
                    // vertical distortion curtain.
                    float p2 = BoundaryBelow(roomUV, 2.0) * 0.92;
                    float p3 = BoundaryBelow(roomUV, 3.0) * 0.82;
                    float p4 = BoundaryBelow(roomUV, 4.0) * 0.68;
                    float p5 = BoundaryBelow(roomUV, 5.0) * 0.50;
                    float p6 = BoundaryBelow(roomUV, 6.0) * 0.34;
                    float p7 = BoundaryBelow(roomUV, 7.0) * 0.20;
                    float plumeSource = max(
                        max(p2, p3),
                        max(max(p4, p5), max(p6, p7)));

                    float riseA = MacroValue(
                        roomUV * float2(4.3, 1.35) +
                        float2(time * 0.006, -time * 0.041));
                    float riseB = MacroValue(
                        roomUV * float2(7.1, 2.05) +
                        float2(-time * 0.004, -time * 0.057) + 0.37);
                    float breakup = smoothstep(0.30, 0.78, riseA * 0.68 + riseB * 0.32);

                    float thermal = SampleThermal(roomUV).r;
                    float thermalDriver = lerp(
                        0.58,
                        0.58 + smoothstep(0.08, 0.62, thermal) * 0.52,
                        _DryCycleHasHeatSimulation);
                    plumeLayer = saturate(
                        plumeSource *
                        (0.30 + breakup * 0.82) *
                        thermalDriver);
                }

                float VisualSkyTransmission(float2 roomUV)
                {
                    float4 terrain = SampleTerrain(roomUV);
                    float tileY = 20.0 / max(_DryCycleRoomSizePx.y, 1.0);
                    float above = SampleTerrain(roomUV + float2(0.0, tileY)).a;
                    float twoAbove = SampleTerrain(roomUV + float2(0.0, tileY * 2.0)).a;
                    return saturate(max(terrain.a, max(above * 0.94, twoAbove * 0.70)));
                }

                float3 ApplyWhiteHeat(
                    float3 color,
                    float amount,
                    float solar,
                    float skyTransmission,
                    float depth)
                {
                    amount = saturate(amount);
                    float localSun = saturate(solar * skyTransmission);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadows = 1.0 - smoothstep(0.10, 0.36, luma);
                    float mids = smoothstep(0.20, 0.66, luma) *
                                 (1.0 - smoothstep(0.74, 0.96, luma));
                    float highs = smoothstep(0.48, 0.92, luma);

                    // Dry noon heat removes color from exposed mids/highs while deep
                    // shade remains dark. This is intentionally the opposite of the old
                    // grey full-screen veil.
                    float desat = amount * localSun *
                        (mids * 0.24 + highs * 0.36) *
                        (1.0 - shadows * 0.96);
                    color = lerp(color, luma.xxx, saturate(desat));

                    float bleach = amount * localSun * highs *
                        (0.18 + localSun * 0.24) *
                        (1.0 - shadows);
                    color = lerp(color, float3(1.0, 0.995, 0.965), saturate(bleach));

                    // Very distant atmosphere loses contrast and picks up a faint warm
                    // veil. Near gameplay art never receives this term.
                    float farAir = amount * solar *
                        smoothstep(0.58, 1.0, depth) * 0.075;
                    color = lerp(color, float3(0.965, 0.955, 0.905), farAir);

                    // Tiny warm response on already-lit surfaces; never an orange tint.
                    float warm = amount * localSun * (mids + highs) * 0.035;
                    color *= lerp(1.0.xxx, float3(1.018, 1.006, 0.970), warm);
                    return saturate(color);
                }

                float3 HeatMap(float value)
                {
                    value = saturate(value);
                    float3 cold = float3(0.015, 0.025, 0.055);
                    float3 warm = float3(0.95, 0.16, 0.025);
                    float3 hot = float3(1.0, 0.88, 0.10);
                    return lerp(
                        lerp(cold, warm, smoothstep(0.0, 0.52, value)),
                        hot,
                        smoothstep(0.50, 1.0, value));
                }

                float4 DebugOutput(float2 roomUV, float2 screenUV)
                {
                    float4 thermal = SampleThermal(roomUV);
                    float4 optical = SampleOptical(roomUV);
                    float4 terrain = SampleTerrain(roomUV);
                    float2 velocity = tex2D(_DryCycleHeatVelocityTex, saturate(roomUV)).xy;

                    if (_DryCycleHeatDebugMode == 1)
                        return float4(HeatMap(thermal.r), 1.0);

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
                        float groundLayer;
                        float plumeLayer;
                        BuildHeatMasks(roomUV, _DryCycleHeatTime, groundLayer, plumeLayer);
                        float depth = DecodePseudoDepth(LevelUV(screenUV));
                        return float4(groundLayer, plumeLayer, depth * 0.65, 1.0);
                    }

                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                float4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.0001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);

                    if (_DryCycleHeatDebugMode > 0)
                        return DebugOutput(roomUV, screenUV);

                    float intensity = saturate(_DryCycleHeatWaveIntensity);
                    float whiteHeat = saturate(_DryCycleWhiteHeat);
                    float solar = saturate(_DryCycleHeatSolarIntensity);
                    float4 original = tex2D(_GrabTexture, grabUV);
                    if (intensity <= 0.0001 && whiteHeat <= 0.0001)
                        return original;

                    float groundLayer;
                    float plumeLayer;
                    BuildHeatMasks(roomUV, _DryCycleHeatTime, groundLayer, plumeLayer);

                    float depth = DecodePseudoDepth(LevelUV(screenUV));
                    float sky = VisualSkyTransmission(roomUV);
                    float thermal = SampleThermal(roomUV).r;
                    float thermalMod = lerp(
                        1.0,
                        0.78 + smoothstep(0.06, 0.72, thermal) * 0.42,
                        _DryCycleHasHeatSimulation);

                    // Far scenery only wanders by a fraction of a pixel. It should be
                    // noticed over seconds, not read as a visible water-wave filter.
                    float farMask = smoothstep(0.56, 1.0, depth) *
                        lerp(0.46, 1.0, sky);
                    float2 macro = MacroVector(roomUV, _DryCycleHeatTime);
                    float2 micro = MicroVector(screenUV, _DryCycleHeatTime);

                    float2 farOffsetPx = macro *
                        (0.12 + intensity * 0.20) *
                        farMask;

                    // Thin hot boundary layer: fast sub-pixel shimmer with slightly
                    // stronger vertical instability, never room-height distortion.
                    float groundDrive = intensity *
                        lerp(0.60, 1.0, solar) *
                        groundLayer * thermalMod;
                    float2 groundOffsetPx = float2(
                        micro.x * 0.78 + macro.x * 0.12,
                        micro.y * 0.46 + macro.y * 0.10) *
                        groundDrive;

                    // Rising automatic plumes are sparse pockets above exposed ground.
                    // They move more slowly and coherently than the ground shimmer.
                    float plumeDrive = intensity * plumeLayer * thermalMod;
                    float2 plumeOffsetPx = float2(
                        macro.x * 0.88 + micro.x * 0.16,
                        macro.y * 0.42 + micro.y * 0.24) *
                        (0.52 + thermal * 0.42) *
                        plumeDrive;

                    float2 offsetPx = farOffsetPx + groundOffsetPx + plumeOffsetPx;
                    float2 screenSize = max(_screenSize, float2(1.0, 1.0));
                    float2 grabOffset = float2(
                        offsetPx.x / screenSize.x,
                        offsetPx.y / screenSize.y * _ProjectionParams.x);

                    float3 scene = tex2D(
                        _GrabTexture,
                        saturate(grabUV + grabOffset)).rgb;

                    // A tiny two-tap optical softness only inside strong local hot air.
                    // Outside those masks pixel art remains a single sharp sample.
                    float localAir = saturate(groundLayer * 0.72 + plumeLayer * 0.58) * intensity;
                    if (localAir > 0.16)
                    {
                        float2 softPx = float2(
                            micro.x * 0.32,
                            micro.y * 0.18) / screenSize;
                        softPx.y *= _ProjectionParams.x;
                        float3 a = tex2D(_GrabTexture, saturate(grabUV + grabOffset + softPx)).rgb;
                        float3 b = tex2D(_GrabTexture, saturate(grabUV + grabOffset - softPx)).rgb;
                        scene = lerp(scene, (a + scene * 2.0 + b) * 0.25, localAir * 0.10);
                    }

                    scene = ApplyWhiteHeat(
                        scene,
                        whiteHeat,
                        solar,
                        sky,
                        depth);

                    return float4(scene, 1.0);
                }
                ENDCG
            }
        }
    }
}
