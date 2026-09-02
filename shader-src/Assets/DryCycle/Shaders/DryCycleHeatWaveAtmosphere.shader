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

                sampler2D _MainTex;
                sampler2D _GrabTexture;
                sampler2D _LevelTex;
                sampler2D _DryCycleHeatMacroNoise;
                sampler2D _DryCycleHeatMicroNoise;

                uniform float4 _spriteRect;
                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleRoomSizePx;
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
                    float2 uv : TEXCOORD0;
                    float4 screenPos : TEXCOORD1;
                    float4 grabPos : TEXCOORD2;
                };

                struct HeatFieldSample
                {
                    float band;
                    float broad;
                    float fine;
                    float2 offsetPx;
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

                float2 MicroNoise(float2 screenUV, float time)
                {
                    if (_DryCycleHasHeatCustomNoise > 0.5)
                    {
                        float2 scale = max(_screenSize, float2(1.0, 1.0)) / 46.0;
                        float2 p = frac(
                            screenUV * scale +
                            float2(time * 0.103, time * 0.167));
                        float2 q = frac(
                            screenUV * scale * 1.73 +
                            float2(-time * 0.079, time * 0.121) + 0.347);
                        float2 a = tex2D(_DryCycleHeatMicroNoise, p).rg * 2.0 - 1.0;
                        float2 b = tex2D(_DryCycleHeatMicroNoise, q).gb * 2.0 - 1.0;
                        return a * 0.63 + b * 0.37;
                    }

                    float a = sin((screenUV.x * 97.0 + screenUV.y * 71.0 + time * 1.39) * 6.2831853);
                    float b = cos((screenUV.x * 67.0 - screenUV.y * 109.0 - time * 1.71) * 6.2831853);
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
                    float2 screenUV,
                    float depth)
                {
                    HeatFieldSample result;
                    float time = _DryCycleHeatTime;

                    // Broad vertical heat bodies. They drift predominantly upward and
                    // only slowly sideways; this is intentionally unlike a water normal
                    // map that scrolls uniformly across the whole screen.
                    float2 broadUv = roomUV * float2(1.28, 0.62) +
                                     float2(time * 0.0041, -time * 0.0107);
                    float2 mesoUv = roomUV * float2(3.15, 1.34) +
                                    float2(-time * 0.0063, time * 0.0159) + 0.271;
                    float2 breakupUv = roomUV * float2(6.3, 2.15) +
                                       float2(time * 0.0117, -time * 0.0213) + 0.613;

                    float3 n0 = MacroNoise(broadUv);
                    float3 n1 = MacroNoise(mesoUv);
                    float3 n2 = MacroNoise(breakupUv);

                    float broad = saturate(n0.r * 0.58 + n1.g * 0.30 + n2.b * 0.12);
                    float ridge = saturate(abs(n0.b - n1.r) * 1.55 + abs(n1.b - n2.g) * 0.55);
                    float band = smoothstep(0.37, 0.78, broad * 0.78 + ridge * 0.22);
                    band = saturate(band * (0.82 + n1.r * 0.38));

                    float2 gradient = MacroGradient(mesoUv);
                    float2 micro = MicroNoise(screenUV, time);
                    float heat = saturate(max(_DryCycleHeatWaveIntensity, _DryCycleHeatLevelAmount));
                    float depthWeight = lerp(0.62, 1.12, depth);
                    float bandWeight = 0.48 + band * 0.82;

                    // Meso air movement: vertical displacement carries most of the
                    // visual energy. Horizontal motion remains deliberately smaller so
                    // the result reads as rising/refracting hot air rather than water.
                    float2 mesoOffset = float2(
                        gradient.x * 0.38 + (n1.r - 0.5) * 0.28,
                        gradient.y * 0.92 + (broad - 0.5) * 2.05);
                    mesoOffset *= heat * depthWeight * bandWeight;

                    // Fine high-frequency shimmer rides on the larger field. Again Y is
                    // stronger than X, producing compression/stretch and edge jitter.
                    float2 fineOffset = float2(
                        micro.x * 0.31,
                        micro.y * 0.72 +
                        sin((screenUV.x * 17.0 + time * 0.73) * 6.2831853) * 0.14);
                    fineOffset *= heat * lerp(0.52, 1.0, band) * lerp(0.76, 1.05, depth);

                    result.band = band;
                    result.broad = broad;
                    result.fine = saturate(length(fineOffset) / 1.2);
                    result.offsetPx = ClampMagnitude(mesoOffset + fineOffset, 3.35);
                    result.depth = depth;
                    return result;
                }

                float3 ApplyHeatTone(
                    float3 color,
                    HeatFieldSample field,
                    float2 grabUV)
                {
                    float tone = saturate(_DryCycleHeatToneAmount);
                    float solar = saturate(_DryCycleHeatSolarIntensity * _DryCycleHeatWaveIntensity);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.10, 0.31, luma);
                    float mid = smoothstep(0.17, 0.76, luma);
                    float high = smoothstep(0.43, 0.89, luma);

                    // Desert heat reads first as loss of color. Mid/high values bleach
                    // much more strongly than shadows, preserving Rain World's graphic
                    // black shapes instead of covering the whole scene with a pale veil.
                    float desaturation = tone *
                        (0.16 + solar * 0.25 + field.depth * 0.08 + field.band * 0.11) *
                        mid * (1.0 - shadow * 0.94);
                    float gray = dot(color, float3(0.235, 0.705, 0.060));
                    color = lerp(color, gray.xxx, saturate(desaturation));

                    float3 hotWhite = float3(1.0, 0.982, 0.915);
                    float bleach = solar * high *
                        (0.20 + _DryCycleHeatWaveIntensity * 0.27 + field.band * 0.13) *
                        (1.0 - shadow * 0.96);
                    color = lerp(color, hotWhite, saturate(bleach));

                    // Distant hot air loses contrast and trends toward bone-white. This
                    // is depth-weighted and luminance-aware, not a global fog overlay.
                    float distantVeil = tone * solar * field.depth *
                        (0.030 + field.band * 0.026) *
                        (1.0 - shadow * 0.92);
                    color = lerp(color, hotWhite, saturate(distantVeil));

                    // Heat bands subtly change exposure as they pass through the scene,
                    // making the air state readable even over broad low-detail regions.
                    float exposureBreath =
                        (field.band - 0.42) *
                        tone * solar *
                        (1.0 - shadow) * 0.055;
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
                    HeatFieldSample field = EvaluateHeatField(roomUV, screenUV, depth);

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
                        float solar = saturate(_DryCycleHeatSolarIntensity * _DryCycleHeatWaveIntensity);
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
                    color = ApplyHeatTone(color, field, refractedUv);

                    return float4(color, 1.0);
                }
                ENDCG
            }
        }
    }

    Fallback Off
}
