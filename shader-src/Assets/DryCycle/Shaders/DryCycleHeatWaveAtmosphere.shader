Shader "DryCycle/HeatWaveAtmosphere"
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
                sampler2D _DryCycleHeatFlowField;
                sampler2D _DryCycleHeatNormalField;
                sampler2D _DryCycleHeatMirageField;

                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleHeatRoomSizePx;
                uniform float _DryCycleHeatWaveIntensity;
                uniform float _DryCycleHeatSolarIntensity;
                uniform float _DryCycleHeatToneAmount;
                uniform float _DryCycleHeatLevelAmount;
                uniform float _DryCycleHeatTime;
                uniform float _DryCycleHasHeatTextures;
                uniform int _DryCycleHeatDebugMode;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float4 screenPos : TEXCOORD0;
                    float4 grabPos : TEXCOORD1;
                };

                struct HeatFieldSample
                {
                    float band;
                    float mirage;
                    float blur;
                    float2 flow;
                    float2 offsetPx;
                };

                v2f vert(appdata_full v)
                {
                    v2f o;
                    o.pos = UnityObjectToClipPos(v.vertex);
                    o.screenPos = ComputeScreenPos(o.pos);
                    o.grabPos = ComputeGrabScreenPos(o.pos);
                    return o;
                }

                float2 RoomUV(float2 screenUV)
                {
                    return _camInRoomRect.xy + screenUV * _camInRoomRect.zw;
                }

                float2 SafeNormalize(float2 value)
                {
                    float len = length(value);
                    return len > 0.00001 ? value / len : float2(0.0, 1.0);
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
                    // At the end of one advected phase the other phase is near its
                    // undeformed state. This hides flow-map stretching and makes the
                    // texture evolve rather than visibly slide forever.
                    return abs(0.5 - phase) * 2.0;
                }

                float4 SampleFlow(float2 roomPx)
                {
                    if (_DryCycleHasHeatTextures > 0.5)
                    {
                        float2 uv = frac(roomPx / float2(470.0, 760.0));
                        return tex2D(_DryCycleHeatFlowField, uv);
                    }

                    float time = _DryCycleHeatTime;
                    float sx = sin((roomPx.x / 390.0 + roomPx.y / 830.0 + time * 0.013) * 6.2831853);
                    float sy = cos((roomPx.x / 610.0 - roomPx.y / 720.0 - time * 0.009) * 6.2831853);
                    float2 flow = SafeNormalize(float2(sx * 0.28, 0.82 + sy * 0.14));
                    float strength = saturate(0.56 + sx * 0.22 + sy * 0.12);
                    float phase = frac(roomPx.x / 517.0 + roomPx.y / 683.0);
                    return float4(flow * 0.5 + 0.5, strength, phase);
                }

                float4 SampleMirage(float2 roomPx, float2 flow, float phase)
                {
                    if (_DryCycleHasHeatTextures > 0.5)
                    {
                        float2 baseUv = roomPx / float2(760.0, 215.0);
                        float p0 = phase;
                        float p1 = frac(phase + 0.5);
                        float blend = PhaseBlend(p0);
                        float2 travel = flow * 0.115;

                        float4 a = tex2D(
                            _DryCycleHeatMirageField,
                            frac(baseUv - travel * p0));
                        float4 b = tex2D(
                            _DryCycleHeatMirageField,
                            frac(baseUv - travel * p1 + 0.371));
                        return lerp(a, b, blend);
                    }

                    float wave = sin(
                        (roomPx.y / 78.0 + roomPx.x / 930.0 - _DryCycleHeatTime * 0.083) *
                        6.2831853);
                    float band = smoothstep(0.04, 0.82, wave * 0.5 + 0.5);
                    return float4(
                        band,
                        wave * 0.5 + 0.5,
                        band * 0.72,
                        phase);
                }

                void SampleAdvectedNormals(
                    float2 roomPx,
                    float2 flow,
                    float phaseSeed,
                    out float2 baseNormal,
                    out float2 detailNormal)
                {
                    if (_DryCycleHasHeatTextures <= 0.5)
                    {
                        float time = _DryCycleHeatTime;
                        baseNormal = float2(
                            sin((roomPx.x / 176.0 + roomPx.y / 337.0 + time * 0.037) * 6.2831853) * 0.42,
                            cos((roomPx.x / 281.0 - roomPx.y / 229.0 - time * 0.043) * 6.2831853) * 0.58);
                        detailNormal = float2(
                            sin((roomPx.x / 43.0 + roomPx.y / 61.0 + time * 0.19) * 6.2831853) * 0.55,
                            cos((roomPx.x / 57.0 - roomPx.y / 37.0 - time * 0.23) * 6.2831853) * 0.62);
                        return;
                    }

                    float basePhase = frac(_DryCycleHeatTime * 0.061 + phaseSeed);
                    float basePhaseB = frac(basePhase + 0.5);
                    float baseBlend = PhaseBlend(basePhase);
                    float2 baseUv = roomPx / float2(245.0, 430.0);
                    float2 baseTravel = flow * 0.125;

                    float4 baseA = tex2D(
                        _DryCycleHeatNormalField,
                        frac(baseUv - baseTravel * basePhase));
                    float4 baseB = tex2D(
                        _DryCycleHeatNormalField,
                        frac(baseUv - baseTravel * basePhaseB + 0.317));
                    baseNormal =
                        (lerp(baseA.rg, baseB.rg, baseBlend) * 2.0 - 1.0);

                    float detailPhase = frac(_DryCycleHeatTime * 0.147 + phaseSeed * 0.731 + 0.193);
                    float detailPhaseB = frac(detailPhase + 0.5);
                    float detailBlend = PhaseBlend(detailPhase);
                    float2 detailUv = roomPx / float2(49.0, 67.0);
                    float2 detailTravel = flow * 0.165;

                    float4 detailA = tex2D(
                        _DryCycleHeatNormalField,
                        frac(detailUv - detailTravel * detailPhase + 0.173));
                    float4 detailB = tex2D(
                        _DryCycleHeatNormalField,
                        frac(detailUv - detailTravel * detailPhaseB + 0.629));
                    detailNormal =
                        (lerp(detailA.ba, detailB.ba, detailBlend) * 2.0 - 1.0);
                }

                float2 SampleHeatGradient(float2 roomPx)
                {
                    if (_DryCycleHasHeatTextures <= 0.5)
                        return float2(0.0, 0.0);

                    float2 uv = frac(roomPx / float2(470.0, 760.0));
                    float2 texel = float2(1.0 / 256.0, 1.0 / 256.0);
                    float r = tex2D(_DryCycleHeatFlowField, frac(uv + float2(texel.x, 0.0))).b;
                    float l = tex2D(_DryCycleHeatFlowField, frac(uv - float2(texel.x, 0.0))).b;
                    float u = tex2D(_DryCycleHeatFlowField, frac(uv + float2(0.0, texel.y))).b;
                    float d = tex2D(_DryCycleHeatFlowField, frac(uv - float2(0.0, texel.y))).b;
                    return float2(r - l, u - d) * 7.5;
                }

                HeatFieldSample EvaluateHeatField(float2 roomPx)
                {
                    HeatFieldSample result;
                    float heat = saturate(max(
                        _DryCycleHeatWaveIntensity,
                        _DryCycleHeatLevelAmount));

                    float4 flowData = SampleFlow(roomPx);
                    float2 flow = SafeNormalize(flowData.rg * 2.0 - 1.0);
                    float strength = flowData.b;
                    float phase = frac(flowData.a + _DryCycleHeatTime * 0.047);
                    float4 mirageData = SampleMirage(roomPx, flow, phase);
                    float mirageBand = mirageData.r;
                    float mirageStretch = mirageData.g * 2.0 - 1.0;

                    float2 baseNormal;
                    float2 detailNormal;
                    SampleAdvectedNormals(
                        roomPx,
                        flow,
                        flowData.a,
                        baseNormal,
                        detailNormal);

                    float2 densityGradient = SampleHeatGradient(roomPx);
                    float band = saturate(
                        strength * 0.61 +
                        mirageBand * 0.54);
                    float body = smoothstep(0.20, 0.82, band);

                    // Base refraction is always present at low amplitude, but strong heat
                    // bodies expand its coverage and especially its vertical component.
                    float2 baseOffset = float2(
                        baseNormal.x * 1.55,
                        baseNormal.y * 3.55) *
                        lerp(0.34, 1.0, body);

                    float2 detailOffset = float2(
                        detailNormal.x * 0.74,
                        detailNormal.y * 1.46) *
                        lerp(0.46, 1.0, body);

                    // The scalar heat-body boundary contributes the strongest coherent
                    // optical bending. Velocity transports the texture but does not
                    // directly dictate where light bends.
                    float2 gradientOffset = float2(
                        densityGradient.x * 1.18,
                        densityGradient.y * 2.76) *
                        smoothstep(0.18, 0.88, strength);

                    // Mirage is an independent Y remap: local compression/stretch can be
                    // strong without turning the whole screen into a water normal map.
                    float mirageY =
                        mirageStretch *
                        lerp(1.35, 6.65, mirageBand) *
                        lerp(0.34, 1.0, body);

                    float2 offset =
                        baseOffset +
                        detailOffset +
                        gradientOffset +
                        float2(0.0, mirageY);

                    offset *= heat;
                    offset = ClampMagnitude(offset, 9.5);

                    result.band = band;
                    result.mirage = mirageBand;
                    result.blur = saturate(
                        mirageData.b * 0.72 +
                        body * 0.28);
                    result.flow = flow;
                    result.offsetPx = offset;
                    return result;
                }

                float3 ApplyDirectionalSoftening(
                    float3 center,
                    float2 grabUV,
                    float2 offsetPx,
                    float blurMask)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float magnitude = length(offsetPx);
                    float soften =
                        smoothstep(1.25, 8.6, magnitude) *
                        blurMask *
                        heat;

                    if (soften <= 0.012)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 direction = SafeNormalize(offsetPx);
                    float radius = lerp(0.42, 1.55, soften);
                    float2 stepUv = direction * px * radius;

                    float3 blur = center * 0.42;
                    blur += tex2D(_GrabTexture, grabUV + stepUv).rgb * 0.22;
                    blur += tex2D(_GrabTexture, grabUV - stepUv).rgb * 0.22;
                    blur += tex2D(_GrabTexture, grabUV + stepUv * 1.9).rgb * 0.07;
                    blur += tex2D(_GrabTexture, grabUV - stepUv * 1.9).rgb * 0.07;

                    return lerp(center, blur, soften * 0.72);
                }

                float3 ApplyHeatTone(
                    float3 color,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);

                    // HeatWave must alter the room palette even in shade. Direct sun
                    // amplifies the effect but no longer acts as a gate.
                    float tone = saturate(max(
                        _DryCycleHeatToneAmount,
                        heat * 0.62));
                    float solar = saturate(_DryCycleHeatSolarIntensity * heat);

                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.075, 0.30, luma);
                    float lit = 1.0 - shadow;
                    float mid = smoothstep(0.14, 0.72, luma);
                    float high = smoothstep(0.42, 0.90, luma);

                    // Hot desert light increases local contrast instead of laying pale
                    // fog over the scene. Rain World's graphic blacks stay dense.
                    float contrast = tone * (0.075 + solar * 0.085);
                    color = (color - 0.43) * (1.0 + contrast) + 0.43;
                    color = saturate(color);

                    // Shift exposed colors toward dry warm stone/bone. This is a room
                    // color state, not a uniform orange overlay.
                    float3 warmed = color * float3(1.105, 1.005, 0.835);
                    float warmAmount =
                        tone *
                        (0.27 + solar * 0.19 + field.band * 0.055) *
                        lit;
                    color = lerp(color, warmed, saturate(warmAmount));

                    // Only modest desaturation: the old implementation over-desaturated
                    // broad areas and looked like fog. Here color loss belongs mostly to
                    // bright surfaces being scorched by the heat state.
                    float dryGray = dot(color, float3(0.255, 0.685, 0.060));
                    float desaturation =
                        tone *
                        (0.075 + solar * 0.12 + field.band * 0.045) *
                        mid * lit;
                    color = lerp(
                        color,
                        dryGray * float3(1.075, 1.015, 0.86),
                        saturate(desaturation));

                    float3 hotWhite = float3(1.0, 0.955, 0.805);
                    float bleach =
                        high * lit *
                        (tone * 0.115 +
                         solar * 0.285 +
                         solar * field.band * 0.095);
                    color = lerp(color, hotWhite, saturate(bleach));

                    // Heat bodies also carry a small, warm exposure pulse. This keeps
                    // them visible across flat regions without using a gray atmospheric
                    // veil and ties color motion to the same field as the optics.
                    float exposureBreath =
                        (field.band - 0.34) *
                        tone *
                        (0.052 + solar * 0.055) *
                        lit;
                    color *= 1.0 + exposureBreath;

                    return saturate(color);
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.0001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);
                    float2 roomPx = roomUV * max(
                        _DryCycleHeatRoomSizePx,
                        float2(1.0, 1.0));

                    HeatFieldSample field = EvaluateHeatField(roomPx);

                    // A second field lookup only matters inside coherent hot bodies.
                    // It approximates a short curved optical path without running a
                    // costly multi-step integrator over every pixel in the room.
                    float2 resolvedOffset = field.offsetPx;
                    if (field.band > 0.44 && _DryCycleHeatWaveIntensity > 0.18)
                    {
                        HeatFieldSample nextField = EvaluateHeatField(
                            roomPx + field.offsetPx * 2.35);
                        float pathBlend = smoothstep(0.44, 0.92, field.band);
                        resolvedOffset = lerp(
                            field.offsetPx,
                            (field.offsetPx + nextField.offsetPx) * 0.5,
                            pathBlend);
                        field.blur = max(
                            field.blur,
                            nextField.blur * pathBlend);
                    }
                    resolvedOffset = ClampMagnitude(resolvedOffset, 9.5);

                    if (_DryCycleHeatDebugMode == 1)
                    {
                        return float4(
                            saturate(field.band),
                            saturate(field.mirage * 0.72 + field.band * 0.28),
                            0.02,
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 2)
                    {
                        float2 v = clamp(resolvedOffset / 9.5, -1.0, 1.0);
                        float magnitude = saturate(length(resolvedOffset) / 9.5);
                        return float4(v * 0.5 + 0.5, magnitude, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 3)
                    {
                        float heat = saturate(_DryCycleHeatWaveIntensity);
                        float tone = saturate(max(_DryCycleHeatToneAmount, heat * 0.62));
                        float solar = saturate(_DryCycleHeatSolarIntensity * heat);
                        return float4(tone, solar, field.band, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 4)
                    {
                        return float4(
                            field.flow * 0.5 + 0.5,
                            field.mirage,
                            1.0);
                    }

                    float2 pxToUv = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 refractedUv = grabUV + resolvedOffset * pxToUv;
                    float3 color = tex2D(_GrabTexture, refractedUv).rgb;
                    color = ApplyDirectionalSoftening(
                        color,
                        refractedUv,
                        resolvedOffset,
                        field.blur);
                    color = ApplyHeatTone(color, field);

                    return float4(color, 1.0);
                }
                ENDCG
            }
        }
    }

    Fallback Off
}
