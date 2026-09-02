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
                sampler2D _DryCycleHeatPlumeTex;
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
                uniform float _DryCycleHasHeatPlumes;
                uniform float _DryCycleHasHeatCustomNoise;
                uniform int _DryCycleHeatDebugMode;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float2 uv : TEXCOORD0;
                    float4 screenPos : TEXCOORD1;
                    float4 grabPos : TEXCOORD2;
                };

                struct DistortionSample
                {
                    float2 offsetPx;
                    float ground;
                    float plume;
                    float depth;
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
                        return 0.64;

                    float encoded = fmod(
                        max(0.0, level.r * 255.0 - 1.0),
                        30.0) / 30.0;
                    return saturate(encoded * 1.28);
                }

                float4 TerrainAt(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatTerrainTex, saturate(roomUV));
                }

                float4 ThermalAt(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatThermalTex, saturate(roomUV));
                }

                float4 PlumeAt(float2 roomUV)
                {
                    return tex2D(_DryCycleHeatPlumeTex, saturate(roomUV));
                }

                float VisualSkyTransmission(float2 roomUV)
                {
                    float2 tile = 20.0 / max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                    float4 here = TerrainAt(roomUV);
                    float above1 = TerrainAt(roomUV + float2(0.0, tile.y)).a;
                    float above2 = TerrainAt(roomUV + float2(0.0, tile.y * 2.0)).a;
                    return saturate(max(here.a, max(above1 * 0.94, above2 * 0.70)));
                }

                float GroundBoundaryMask(float2 roomUV)
                {
                    // Search downward through only the first ~46 px of air. This keeps
                    // heat boil glued to hot ground/platforms instead of filling rooms.
                    float invH = 1.0 / max(_DryCycleRoomSizePx.y, 1.0);
                    float g = TerrainAt(roomUV).b;
                    g = max(g, TerrainAt(roomUV - float2(0.0, 6.0 * invH)).b * 0.98);
                    g = max(g, TerrainAt(roomUV - float2(0.0, 12.0 * invH)).b * 0.91);
                    g = max(g, TerrainAt(roomUV - float2(0.0, 19.0 * invH)).b * 0.77);
                    g = max(g, TerrainAt(roomUV - float2(0.0, 27.0 * invH)).b * 0.56);
                    g = max(g, TerrainAt(roomUV - float2(0.0, 36.0 * invH)).b * 0.31);
                    g = max(g, TerrainAt(roomUV - float2(0.0, 46.0 * invH)).b * 0.10);
                    return saturate(g * _DryCycleHeatWaveIntensity);
                }

                float2 MacroGradient(float2 uv)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 texel = float2(1.0 / 256.0, 1.0 / 256.0);
                        float aR = tex2D(_DryCycleHeatMacroNoise, frac(uv + float2(texel.x, 0.0))).r;
                        float aL = tex2D(_DryCycleHeatMacroNoise, frac(uv - float2(texel.x, 0.0))).r;
                        float aU = tex2D(_DryCycleHeatMacroNoise, frac(uv + float2(0.0, texel.y))).g;
                        float aD = tex2D(_DryCycleHeatMacroNoise, frac(uv - float2(0.0, texel.y))).g;
                        return float2(aR - aL, aU - aD) * 18.0;
                    }

                    return float2(
                        sin((uv.x * 2.31 + uv.y * 1.17) * 6.2831853),
                        cos((uv.y * 2.07 - uv.x * 0.83) * 6.2831853)) * 0.08;
                }

                float2 MicroPhase(float2 screenUV, float time)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 pixelScale = max(_screenSize, float2(1.0, 1.0)) / 58.0;
                        float2 p = frac(
                            screenUV * pixelScale +
                            float2(time * 0.118, time * 0.071));
                        float2 q = frac(
                            screenUV * pixelScale * 1.67 +
                            float2(-time * 0.083, time * 0.133) + 0.37);
                        float2 a = tex2D(_DryCycleHeatMicroNoise, p).rg * 2.0 - 1.0;
                        float2 b = tex2D(_DryCycleHeatMicroNoise, q).gb * 2.0 - 1.0;
                        return a * 0.64 + b * 0.36;
                    }

                    float a = sin((screenUV.x * 101.0 + screenUV.y * 73.0 + time * 1.73) * 6.2831853);
                    float b = cos((screenUV.x * 79.0 - screenUV.y * 119.0 - time * 1.31) * 6.2831853);
                    return float2(a, b);
                }

                float2 PlumeGradient(float2 roomUV)
                {
                    // Plume simulation uses four cells per 20px tile => ~5px per texel.
                    float2 texel = 5.0 / max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                    float left = PlumeAt(roomUV - float2(texel.x, 0.0)).r;
                    float right = PlumeAt(roomUV + float2(texel.x, 0.0)).r;
                    float down = PlumeAt(roomUV - float2(0.0, texel.y)).r;
                    float up = PlumeAt(roomUV + float2(0.0, texel.y)).r;
                    return float2(right - left, up - down);
                }

                float2 ClampMagnitude(float2 value, float maximum)
                {
                    float len = length(value);
                    if (len <= maximum || len <= 0.00001)
                        return value;
                    return value * (maximum / len);
                }

                DistortionSample EvaluateDistortion(
                    float2 roomUV,
                    float2 screenUV,
                    float depth)
                {
                    DistortionSample result;
                    result.ground = GroundBoundaryMask(roomUV);
                    result.depth = depth;

                    float4 plumeData = PlumeAt(roomUV);
                    float plumeDensity = plumeData.r * _DryCycleHasHeatPlumes;
                    float plumeCore = plumeData.g * _DryCycleHasHeatPlumes;
                    float plumeAge = plumeData.b;
                    result.plume = smoothstep(0.035, 0.54, plumeDensity) *
                                   lerp(0.32, 1.0, _DryCycleHeatWaveIntensity);

                    float2 micro = MicroPhase(screenUV, _DryCycleHeatTime);

                    // Ground air boils fast, anisotropically and by very few pixels.
                    // Vertical displacement is deliberately stronger than horizontal so
                    // rods/feet compress and stretch instead of reading as water waves.
                    float2 groundOffset = float2(
                        micro.x * 0.58,
                        micro.y * 1.08 +
                        sin((_DryCycleHeatTime * 2.35 + screenUV.x * 13.0) * 6.2831853) * 0.24);
                    groundOffset *= result.ground *
                                    lerp(0.45, 1.18, _DryCycleHeatWaveIntensity);

                    // Coherent rising plumes get the strongest local refraction. Their
                    // density gradient bends silhouettes; thermal velocity only biases
                    // the direction so a plume visibly flows upward instead of wobbling.
                    float2 plumeGrad = PlumeGradient(roomUV);
                    float2 velocityUv = tex2D(
                        _DryCycleHeatVelocityTex,
                        saturate(roomUV)).xy * _DryCycleHasHeatSimulation;
                    float2 velocityPx = velocityUv * _DryCycleRoomSizePx;
                    float velocityLen = length(velocityPx);
                    float2 velocityDir = velocityLen > 0.01
                        ? velocityPx / velocityLen
                        : float2(0.0, 1.0);

                    float edgeStrength = saturate(length(plumeGrad) * 3.8);
                    float2 plumeOffset = plumeGrad *
                        lerp(3.2, 7.4, saturate(plumeCore * 1.20));
                    plumeOffset += velocityDir *
                        (0.34 + plumeCore * 0.62) *
                        (0.35 + edgeStrength * 0.65);
                    plumeOffset += float2(
                        micro.x * 0.62,
                        micro.y * 0.42) *
                        (0.35 + plumeAge * 0.65);
                    plumeOffset = ClampMagnitude(plumeOffset, 4.6);
                    plumeOffset *= result.plume;

                    // Distant atmosphere never becomes a visible wave. It is a slow,
                    // sub-pixel image wander meant to be noticed only after staring.
                    float farMask = smoothstep(0.52, 0.91, depth) *
                                    _DryCycleHeatWaveIntensity;
                    float2 macroUv = roomUV * float2(1.73, 1.17) +
                                     float2(_DryCycleHeatTime * 0.0017,
                                            _DryCycleHeatTime * 0.0011);
                    float2 farOffset = MacroGradient(macroUv) *
                                       farMask * 0.54;
                    farOffset = ClampMagnitude(farOffset, 0.62);

                    result.offsetPx = groundOffset + plumeOffset + farOffset;
                    return result;
                }

                float3 WhiteHeatTone(
                    float3 color,
                    float amount,
                    float skyTransmission,
                    float depth,
                    float plume)
                {
                    amount = saturate(amount);
                    float sun = saturate(
                        _DryCycleHeatSolarIntensity *
                        lerp(0.36, 1.0, skyTransmission));
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.12, 0.34, luma);
                    float mid = smoothstep(0.18, 0.70, luma) *
                                (1.0 - smoothstep(0.78, 0.98, luma));
                    float high = smoothstep(0.52, 0.93, luma);

                    // Mid/high chroma is burned away first; deep shadow is protected.
                    float desaturation = amount * sun *
                        (mid * 0.30 + high * 0.55) *
                        (1.0 - shadow * 0.96);
                    float warmLuma = dot(color, float3(0.235, 0.705, 0.060));
                    color = lerp(color, warmLuma.xxx, saturate(desaturation));

                    // Exposed highlights approach warm white with a soft shoulder. This
                    // raises sun-baked surfaces without turning creatures/shadows emissive.
                    float bleach = amount * sun * high *
                        (0.18 + sun * 0.26 + depth * 0.07) *
                        (1.0 - shadow);
                    float3 hotWhite = float3(1.0, 0.986, 0.925);
                    color = lerp(color, hotWhite, saturate(bleach));

                    // A plume slightly lowers local contrast like a refractive air lens;
                    // this is not fog and remains almost invisible without motion.
                    float airVeil = plume * amount * sun * 0.018 *
                                    (1.0 - shadow * 0.92);
                    color = lerp(color, hotWhite, airVeil);

                    // Far sunlit scenery loses a little contrast in the dry glare.
                    float distantVeil = depth * amount * sun * 0.020 *
                                        (1.0 - shadow);
                    color = lerp(color, hotWhite, distantVeil);
                    return saturate(color);
                }

                float3 HeatMap(float value)
                {
                    value = saturate(value);
                    float3 cold = float3(0.01, 0.02, 0.06);
                    float3 warm = float3(0.95, 0.13, 0.025);
                    float3 hot = float3(1.0, 0.83, 0.08);
                    return lerp(
                        lerp(cold, warm, smoothstep(0.0, 0.55, value)),
                        hot,
                        smoothstep(0.48, 1.0, value));
                }

                float4 DebugOutput(float2 roomUV, float2 screenUV, float depth)
                {
                    float4 thermal = ThermalAt(roomUV);
                    float4 optical = tex2D(_DryCycleHeatOpticalTex, saturate(roomUV));
                    float4 terrain = TerrainAt(roomUV);
                    float4 plume = PlumeAt(roomUV);
                    float2 velocity = tex2D(_DryCycleHeatVelocityTex, saturate(roomUV)).xy;

                    if (_DryCycleHeatDebugMode == 1)
                    {
                        return float4(HeatMap(thermal.r), 1.0);
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
                        float3 color = terrain.g.xxx * 0.20;
                        color += float3(0.85, 0.05, 0.03) * step(0.5, terrain.r);
                        color += float3(1.00, 0.62, 0.02) * terrain.b * 0.92;
                        color += float3(0.04, 0.30, 0.98) * terrain.a * 0.68;
                        return float4(saturate(color), 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 5)
                    {
                        float ground = GroundBoundaryMask(roomUV);
                        return float4(
                            saturate(plume.r * 1.15),
                            ground,
                            depth,
                            1.0);
                    }

                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                float4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.0001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);
                    float depth = DecodePseudoDepth(LevelUV(screenUV));

                    if (_DryCycleHeatDebugMode > 0)
                        return DebugOutput(roomUV, screenUV, depth);

                    float intensity = saturate(_DryCycleHeatWaveIntensity);
                    float plumeDensity = PlumeAt(roomUV).r * _DryCycleHasHeatPlumes;
                    float residualPlume = smoothstep(0.05, 0.46, plumeDensity) * 0.32;
                    float opticalPresence = max(intensity, residualPlume);
                    float toneAmount = saturate(_DryCycleWhiteHeat);

                    float4 original = tex2D(_GrabTexture, grabUV);
                    if (opticalPresence <= 0.0001 && toneAmount <= 0.0001)
                        return original;

                    DistortionSample first = EvaluateDistortion(roomUV, screenUV, depth);

                    // Only plume refraction gets a second field sample. This produces a
                    // continuous bent air lens without recursively distorting SceneColor.
                    float2 roomStep = first.offsetPx /
                        max(_DryCycleRoomSizePx, float2(1.0, 1.0)) * 0.42;
                    float2 secondRoomUV = saturate(roomUV + roomStep);
                    DistortionSample second = EvaluateDistortion(
                        secondRoomUV,
                        screenUV + first.offsetPx / max(_screenSize, float2(1.0, 1.0)) * 0.42,
                        depth);
                    float plumeTrace = saturate(max(first.plume, second.plume));
                    float2 offsetPx = lerp(
                        first.offsetPx,
                        (first.offsetPx + second.offsetPx) * 0.5,
                        plumeTrace * 0.72);
                    offsetPx = ClampMagnitude(offsetPx, 5.1);

                    float2 offsetUV = offsetPx / max(_screenSize, float2(1.0, 1.0));
                    float2 distortedUV = grabUV + offsetUV;
                    float4 color = tex2D(_GrabTexture, distortedUV);

                    // Directional source blur exists only inside strong plume cores.
                    // Ground shimmer and ordinary pixels retain Rain World's sharp edge.
                    float streak = smoothstep(0.42, 0.92, plumeDensity) * 0.16;
                    if (streak > 0.001)
                    {
                        float3 a = tex2D(_GrabTexture, distortedUV - offsetUV * 0.42).rgb;
                        float3 b = tex2D(_GrabTexture, distortedUV + offsetUV * 0.31).rgb;
                        color.rgb = lerp(color.rgb, (color.rgb * 2.0 + a + b) * 0.25, streak);
                    }

                    float sky = VisualSkyTransmission(roomUV);
                    color.rgb = WhiteHeatTone(
                        color.rgb,
                        toneAmount,
                        sky,
                        depth,
                        first.plume);
                    return color;
                }
                ENDCG
            }
        }
    }
}
