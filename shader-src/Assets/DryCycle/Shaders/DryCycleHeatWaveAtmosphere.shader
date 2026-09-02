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
                sampler2D _LevelTex;
                sampler2D _DryCycleHeatMacroNoise;
                sampler2D _DryCycleHeatMicroNoise;

                uniform float4 _spriteRect;
                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleHeatRoomSizePx;
                uniform float _DryCycleHeatWaveIntensity;
                uniform float _DryCycleHeatSolarIntensity;
                uniform float _DryCycleHeatToneAmount;
                uniform float _DryCycleHeatLevelAmount;
                uniform float _DryCycleHeatTime;
                uniform float _DryCycleHasHeatCustomNoise;
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
                    float fine;
                    float2 offsetPx;
                    float depth;
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
                        return 0.72;

                    float encoded = fmod(
                        max(0.0, level.r * 255.0 - 1.0),
                        30.0) / 30.0;
                    return saturate(encoded * 1.24);
                }

                float3 MacroNoise(float2 uv)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                        return tex2D(_DryCycleHeatMacroNoise, frac(uv)).rgb;

                    float a = sin((uv.x * 2.17 + uv.y * 0.73) * 6.2831853) * 0.5 + 0.5;
                    float b = cos((uv.x * 1.31 - uv.y * 1.87 + 0.37) * 6.2831853) * 0.5 + 0.5;
                    float c = sin((uv.x * 3.11 + uv.y * 1.29 + 0.19) * 6.2831853) * 0.5 + 0.5;
                    return float3(a, b, c);
                }

                float2 MicroNoise(float2 roomPx, float time)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 p = frac(
                            roomPx / 46.0 +
                            float2(time * 0.103, time * 0.167));
                        float2 q = frac(
                            roomPx / 27.0 +
                            float2(-time * 0.079, time * 0.121) + 0.347);
                        float2 a = tex2D(_DryCycleHeatMicroNoise, p).rg * 2.0 - 1.0;
                        float2 b = tex2D(_DryCycleHeatMicroNoise, q).gb * 2.0 - 1.0;
                        return a * 0.63 + b * 0.37;
                    }

                    float a = sin((roomPx.x / 43.0 + roomPx.y / 61.0 + time * 0.22) * 6.2831853);
                    float b = cos((roomPx.x / 57.0 - roomPx.y / 37.0 - time * 0.27) * 6.2831853);
                    return float2(a, b);
                }

                float2 MacroGradient(float2 uv)
                {
                    float2 texel = float2(1.0 / 256.0, 1.0 / 256.0);
                    float r = MacroNoise(uv + float2(texel.x, 0.0)).g;
                    float l = MacroNoise(uv - float2(texel.x, 0.0)).g;
                    float u = MacroNoise(uv + float2(0.0, texel.y)).b;
                    float d = MacroNoise(uv - float2(0.0, texel.y)).b;
                    return float2(r - l, u - d) * 12.0;
                }

                float2 ClampMagnitude(float2 value, float maximum)
                {
                    float len = length(value);
                    if (len <= maximum || len <= 0.00001)
                        return value;
                    return value * (maximum / len);
                }

                HeatFieldSample EvaluateHeatField(
                    float2 roomUV,
                    float depth)
                {
                    HeatFieldSample result;
                    float time = _DryCycleHeatTime;
                    float2 roomPx = roomUV * max(
                        _DryCycleHeatRoomSizePx,
                        float2(1.0, 1.0));

                    // Heat structures live in room-world pixel space. Their periods are
                    // therefore consistent between tiny rooms, huge rooms and camera
                    // positions instead of stretching to whatever fraction of a room is
                    // currently on screen.
                    float2 broadUv = roomPx / float2(720.0, 1120.0) +
                                     float2(time * 0.0041, -time * 0.0107);
                    float2 mesoUv = roomPx / float2(310.0, 560.0) +
                                    float2(-time * 0.0063, time * 0.0159) + 0.271;
                    float2 breakupUv = roomPx / float2(145.0, 285.0) +
                                       float2(time * 0.0117, -time * 0.0213) + 0.613;

                    float3 n0 = MacroNoise(broadUv);
                    float3 n1 = MacroNoise(mesoUv);
                    float3 n2 = MacroNoise(breakupUv);

                    float broad = saturate(n0.r * 0.58 + n1.g * 0.30 + n2.b * 0.12);
                    float ridge = saturate(
                        abs(n0.b - n1.r) * 1.55 +
                        abs(n1.b - n2.g) * 0.55);
                    float band = smoothstep(
                        0.37,
                        0.78,
                        broad * 0.78 + ridge * 0.22);
                    band = saturate(band * (0.82 + n1.r * 0.38));

                    float2 gradient = MacroGradient(mesoUv);
                    float2 micro = MicroNoise(roomPx, time);
                    float heat = saturate(max(
                        _DryCycleHeatWaveIntensity,
                        _DryCycleHeatLevelAmount));
                    float depthWeight = lerp(0.62, 1.12, depth);
                    float bandWeight = 0.48 + band * 0.82;

                    // Whole-air meso motion: the vertical component is intentionally
                    // dominant. Large coherent regions stretch/compress upward while X
                    // only wanders slightly, preventing the classic underwater look.
                    float2 mesoOffset = float2(
                        gradient.x * 0.38 + (n1.r - 0.5) * 0.28,
                        gradient.y * 0.92 + (broad - 0.5) * 2.05);
                    mesoOffset *= heat * depthWeight * bandWeight;

                    // Fine shimmer belongs to the same world-space air mass. It moves
                    // faster than the broad bands but stays sub-pixel to ~1 px in most
                    // regions, vibrating edges instead of translating whole objects.
                    float2 fineOffset = float2(
                        micro.x * 0.31,
                        micro.y * 0.72 +
                        sin((roomPx.x / 185.0 + time * 0.73) * 6.2831853) * 0.14);
                    fineOffset *= heat *
                                  lerp(0.52, 1.0, band) *
                                  lerp(0.76, 1.05, depth);

                    result.band = band;
                    result.fine = saturate(length(fineOffset) / 1.2);
                    result.offsetPx = ClampMagnitude(mesoOffset + fineOffset, 3.35);
                    result.depth = depth;
                    return result;
                }

                float3 ApplyHeatTone(
                    float3 color,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float tone = saturate(_DryCycleHeatToneAmount);
                    float solar = saturate(_DryCycleHeatSolarIntensity * heat);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.10, 0.31, luma);
                    float mid = smoothstep(0.17, 0.76, luma);
                    float high = smoothstep(0.43, 0.89, luma);

                    // Heat first removes color. Mid/high values bleach substantially,
                    // while Rain World's deep graphic shadows remain nearly untouched.
                    float desaturation = tone *
                        (0.18 + solar * 0.27 + field.depth * 0.08 + field.band * 0.12) *
                        mid * (1.0 - shadow * 0.94);
                    float gray = dot(color, float3(0.235, 0.705, 0.060));
                    color = lerp(color, gray.xxx, saturate(desaturation));

                    float3 hotWhite = float3(1.0, 0.982, 0.915);

                    // Even residual extreme heat slightly dries bright values; direct
                    // desert sun then provides the much stronger bone-white overheat
                    // response. This keeps HeatWave readable in a static screenshot
                    // without turning sheltered darkness into luminous fog.
                    float residualBleach = tone * high * 0.075 *
                                           (1.0 - shadow * 0.97);
                    float solarBleach = solar * high *
                        (0.22 + heat * 0.30 + field.band * 0.14) *
                        (1.0 - shadow * 0.96);
                    color = lerp(
                        color,
                        hotWhite,
                        saturate(residualBleach + solarBleach));

                    // Distant hot air loses a little contrast and trends warm-white.
                    // This is deliberately modest; it is atmospheric compression, not
                    // a fog layer.
                    float distantVeil = tone *
                        lerp(0.35, 1.0, solar) *
                        field.depth *
                        (0.022 + field.band * 0.028) *
                        (1.0 - shadow * 0.92);
                    color = lerp(color, hotWhite, saturate(distantVeil));

                    // Broad heat bodies have a tiny exposure modulation. This makes hot
                    // air visible over low-detail expanses where UV refraction alone
                    // would sample almost the same flat color.
                    float exposureBreath =
                        (field.band - 0.42) *
                        tone *
                        lerp(0.30, 1.0, solar) *
                        (1.0 - shadow) * 0.058;
                    color *= 1.0 + exposureBreath;

                    return saturate(color);
                }

                float3 ApplyLocalHeatSoftening(
                    float3 center,
                    float2 grabUV,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float soften = heat * field.depth *
                        smoothstep(0.28, 0.92, field.band) * 0.14;
                    if (soften <= 0.001)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float radius = lerp(0.45, 0.90, field.band);
                    float3 blur =
                        tex2D(_GrabTexture, grabUV + float2(px.x * radius, 0.0)).rgb +
                        tex2D(_GrabTexture, grabUV - float2(px.x * radius, 0.0)).rgb +
                        tex2D(_GrabTexture, grabUV + float2(0.0, px.y * radius)).rgb +
                        tex2D(_GrabTexture, grabUV - float2(0.0, px.y * radius)).rgb;
                    blur *= 0.25;
                    return lerp(center, blur, saturate(soften));
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.0001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);
                    float depth = DecodePseudoDepth(LevelUV(screenUV));
                    HeatFieldSample field = EvaluateHeatField(roomUV, depth);

                    if (_DryCycleHeatDebugMode == 1)
                    {
                        float3 debugBand = lerp(
                            float3(0.05, 0.01, 0.0),
                            float3(1.0, 0.19, 0.02),
                            field.band);
                        return float4(debugBand, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 2)
                    {
                        float2 v = clamp(field.offsetPx / 3.35, -1.0, 1.0);
                        return float4(v * 0.5 + 0.5, field.fine, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 3)
                    {
                        float solar = saturate(
                            _DryCycleHeatSolarIntensity *
                            _DryCycleHeatWaveIntensity);
                        return float4(
                            saturate(_DryCycleHeatToneAmount),
                            solar,
                            field.band,
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 4)
                    {
                        return float4(depth.xxx, 1.0);
                    }

                    float2 pxToUv = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 refractedUv = grabUV + field.offsetPx * pxToUv;
                    float3 color = tex2D(_GrabTexture, refractedUv).rgb;
                    color = ApplyLocalHeatSoftening(color, refractedUv, field);
                    color = ApplyHeatTone(color, field);

                    return float4(color, 1.0);
                }
                ENDCG
            }
        }
    }

    Fallback Off
}
