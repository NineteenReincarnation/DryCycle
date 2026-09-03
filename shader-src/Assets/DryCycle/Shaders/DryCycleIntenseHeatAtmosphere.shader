Shader "DryCycle/IntenseHeatAtmosphere"
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
                sampler2D _DryCycleIntenseFlowField;
                sampler2D _DryCycleIntenseNormalField;
                sampler2D _DryCycleIntenseMirageField;
                sampler2D _DryCycleIntenseSurfaceField;
                sampler2D _DryCycleIntenseSolarField;

                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleIntenseRoomSizePx;
                uniform float _DryCycleIntenseHeatIntensity;
                uniform float _DryCycleIntenseSolarIntensity;
                uniform float _DryCycleIntenseHeatTime;
                uniform float _DryCycleIntenseHasOpticalTextures;
                uniform float _DryCycleIntenseHasSurfaceField;
                uniform float _DryCycleIntenseHasSolarField;
                uniform int _DryCycleIntenseDebugMode;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float4 screenPos : TEXCOORD0;
                    float4 grabPos : TEXCOORD1;
                };

                struct HeatSample
                {
                    float2 offsetPx;
                    float boil;
                    float sheet;
                    float ground;
                    float directSun;
                    float penumbra;
                    float sky;
                    float shimmer;
                    float blur;
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
                    return len > 0.00001 ? value / len : float2(0.0, 1.0);
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

                float Hash21(float2 p)
                {
                    p = frac(p * float2(123.34, 345.45));
                    p += dot(p, p + 34.345);
                    return frac(p.x * p.y);
                }

                float2 RoomUV(float2 screenUV)
                {
                    return _camInRoomRect.xy + screenUV * _camInRoomRect.zw;
                }

                float4 SampleFlow(float2 roomPx)
                {
                    if (_DryCycleIntenseHasOpticalTextures > 0.5)
                    {
                        return tex2D(
                            _DryCycleIntenseFlowField,
                            frac(roomPx / float2(520.0, 760.0)));
                    }

                    float t = _DryCycleIntenseHeatTime;
                    float sx = sin((roomPx.x / 430.0 + roomPx.y / 890.0 + t * 0.041) * 6.2831853);
                    float sy = cos((roomPx.x / 670.0 - roomPx.y / 710.0 - t * 0.033) * 6.2831853);
                    float2 flow = SafeNormalize(float2(sx * 0.32, 0.88 + sy * 0.12));
                    return float4(flow * 0.5 + 0.5, saturate(0.65 + sx * 0.20), frac(sx + sy));
                }

                float4 SampleSurface(float2 roomUV)
                {
                    if (_DryCycleIntenseHasSurfaceField > 0.5)
                        return tex2D(_DryCycleIntenseSurfaceField, saturate(roomUV));
                    return float4(0.45, 0.25, 1.0, 0.5);
                }

                float4 SampleSolar(float2 roomUV)
                {
                    if (_DryCycleIntenseHasSolarField > 0.5)
                        return tex2D(_DryCycleIntenseSolarField, saturate(roomUV));
                    return float4(1.0, 0.0, 1.0, 0.5);
                }

                float4 SampleDualPhase(
                    sampler2D source,
                    float2 baseUv,
                    float2 travel,
                    float phase,
                    float seed)
                {
                    float p0 = frac(phase);
                    float p1 = frac(phase + 0.5);
                    float blend = abs(0.5 - p0) * 2.0;
                    float4 a = tex2D(source, frac(baseUv - travel * p0 + seed));
                    float4 b = tex2D(source, frac(baseUv - travel * p1 + seed + 0.417));
                    return lerp(a, b, blend);
                }

                HeatSample EvaluateHeat(float2 screenUV, float2 roomUV, float2 roomPx)
                {
                    HeatSample h;
                    float intensity = saturate(_DryCycleIntenseHeatIntensity);
                    float solar = saturate(_DryCycleIntenseSolarIntensity);
                    float4 flowTex = SampleFlow(roomPx);
                    float2 flow = SafeNormalize(flowTex.rg * 2.0 - 1.0);
                    float flowHeat = saturate(flowTex.b);
                    float phase = frac(flowTex.a + _DryCycleIntenseHeatTime * 0.084);
                    float4 surface = SampleSurface(roomUV);
                    float4 sun = SampleSolar(roomUV);

                    h.ground = saturate(surface.r * surface.b);
                    h.directSun = saturate(sun.r * solar);
                    h.penumbra = sun.g;
                    h.sky = sun.b;

                    float largeA = sin(
                        roomPx.x * 0.0068 +
                        roomPx.y * 0.0037 -
                        _DryCycleIntenseHeatTime * 1.42 +
                        flowTex.a * 4.1);
                    float largeB = sin(
                        roomPx.x * 0.0032 -
                        roomPx.y * 0.0074 +
                        _DryCycleIntenseHeatTime * 1.08 + 1.9);
                    float largeC = cos(
                        roomPx.x * 0.0108 +
                        roomPx.y * 0.0021 -
                        _DryCycleIntenseHeatTime * 1.83 + 0.8);
                    h.boil = Smooth01(
                        saturate(0.54 + largeA * 0.24 + largeB * 0.16 + largeC * 0.10));

                    float4 normalA;
                    float4 normalB;
                    float4 mirage;
                    if (_DryCycleIntenseHasOpticalTextures > 0.5)
                    {
                        normalA = SampleDualPhase(
                            _DryCycleIntenseNormalField,
                            roomPx / float2(335.0, 520.0),
                            float2(flow.x * 0.31, flow.y * 0.44),
                            phase,
                            0.137);
                        normalB = SampleDualPhase(
                            _DryCycleIntenseNormalField,
                            roomPx / float2(155.0, 245.0),
                            float2(-flow.x * 0.21, flow.y * 0.68),
                            phase * 1.47 + 0.23,
                            0.613);
                        mirage = SampleDualPhase(
                            _DryCycleIntenseMirageField,
                            roomPx / float2(520.0, 92.0),
                            float2(flow.x * 0.18, 0.46),
                            phase * 1.21,
                            0.319);
                    }
                    else
                    {
                        float n0 = sin(roomPx.x * 0.021 + _DryCycleIntenseHeatTime * 2.7);
                        float n1 = cos(roomPx.y * 0.034 - _DryCycleIntenseHeatTime * 3.2);
                        normalA = float4(n0 * 0.5 + 0.5, n1 * 0.5 + 0.5, 0.5, 0.5);
                        normalB = normalA.bgra;
                        mirage = float4(n0 * 0.5 + 0.5, n1 * 0.5 + 0.5, 0.5, 0.5);
                    }

                    float2 baseNormal = normalA.rg * 2.0 - 1.0;
                    float2 detailNormal = normalB.ba * 2.0 - 1.0;
                    float mirageBand = saturate(mirage.r * 0.64 + mirage.g * 0.36);
                    h.sheet = Smooth01(saturate(
                        flowHeat * 0.36 +
                        mirageBand * 0.48 +
                        h.boil * 0.42 - 0.18));

                    float exposureDrive = saturate(
                        0.32 +
                        h.directSun * 0.74 +
                        h.penumbra * 0.22);
                    float heatDrive = intensity * exposureDrive;

                    // The dominant displacement remains vertical. Horizontal offset is
                    // coherent and slower, so extreme heat reads as refractive air rather
                    // than water-like isotropic wobble.
                    float2 macroOffset = float2(
                        (largeA * 0.56 + largeB * 0.24) * 5.2,
                        (largeA * 0.72 - largeB * 0.44 + largeC * 0.20) * 9.4);
                    macroOffset *= h.boil * heatDrive;

                    float2 normalOffset = float2(
                        baseNormal.x * 4.4 + detailNormal.x * 2.0,
                        baseNormal.y * 8.6 + detailNormal.y * 4.2);
                    normalOffset *= heatDrive * (0.58 + h.sheet * 0.84);

                    float groundMirage = h.ground * h.directSun * intensity;
                    float2 mirageOffset = float2(
                        detailNormal.x * 3.8,
                        (mirage.g * 2.0 - 1.0) * 17.2 + baseNormal.y * 6.8);
                    mirageOffset *= groundMirage * (0.48 + mirageBand * 0.92);

                    h.shimmer = saturate(
                        length(detailNormal) * 0.32 +
                        h.sheet * 0.52 +
                        h.penumbra * 0.28);

                    float2 micro = float2(
                        detailNormal.x * 1.7,
                        detailNormal.y * 3.1) *
                        intensity *
                        (0.36 + h.directSun * 0.84);

                    h.offsetPx = ClampMagnitude(
                        macroOffset + normalOffset + mirageOffset + micro,
                        lerp(12.0, 25.5, intensity * (0.48 + h.directSun * 0.52)));
                    h.blur = saturate(
                        intensity *
                        (h.sheet * 0.48 + h.boil * 0.24 + groundMirage * 0.52));
                    return h;
                }

                float4 SampleScene(float2 grabUV, float2 offsetPx, float blur, float2 flow)
                {
                    float2 pixel = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 uv = grabUV + offsetPx * pixel;
                    float4 center = tex2D(_GrabTexture, uv);
                    if (blur <= 0.025)
                        return center;

                    float2 axis = SafeNormalize(float2(flow.x * 0.44, 1.0)) * pixel * (1.1 + blur * 2.7);
                    float4 a = tex2D(_GrabTexture, uv - axis);
                    float4 b = tex2D(_GrabTexture, uv + axis);
                    float4 c = tex2D(_GrabTexture, uv - axis * 2.15);
                    float4 d = tex2D(_GrabTexture, uv + axis * 2.15);
                    return lerp(center, center * 0.42 + (a + b) * 0.19 + (c + d) * 0.10, blur * 0.72);
                }

                float3 ApplyDisasterGrade(float3 color, HeatSample h, float2 screenUV)
                {
                    float intensity = saturate(_DryCycleIntenseHeatIntensity);
                    float solar = saturate(_DryCycleIntenseSolarIntensity);
                    float luma = dot(color, float3(0.299, 0.587, 0.114));

                    // Collapse the scene into a narrow dry-hot range. Blue/cyan survive
                    // only as dirty gray-brown traces while deep shadows stay readable.
                    float3 dryBase = saturate(
                        color * float3(1.17, 0.91, 0.49) +
                        luma * float3(0.15, 0.048, 0.000));
                    float gradeAmount = intensity * (0.64 + h.directSun * 0.25 + solar * 0.08);
                    color = lerp(color, dryBase, saturate(gradeAmount));

                    float gradedLuma = dot(color, float3(0.299, 0.587, 0.114));
                    float shadow = 1.0 - smoothstep(0.10, 0.39, gradedLuma);
                    float mid = smoothstep(0.11, 0.44, gradedLuma) *
                                (1.0 - smoothstep(0.67, 0.92, gradedLuma));

                    float3 caramel = saturate(
                        color * float3(0.91, 0.73, 0.42) +
                        float3(0.075, 0.030, 0.000));
                    color = lerp(color, caramel, intensity * shadow * 0.46);

                    float3 ochre = saturate(
                        color * float3(1.11, 0.86, 0.54) +
                        gradedLuma * float3(0.105, 0.036, 0.000));
                    color = lerp(color, ochre, intensity * mid * (0.35 + h.directSun * 0.38));

                    float direct = intensity * h.directSun;
                    float hotMask = smoothstep(0.40, 0.92, gradedLuma) * direct;
                    float3 burningAmber = float3(1.0, 0.60, 0.075);
                    color = lerp(color, burningAmber, hotMask * 0.24);

                    // Direct sun raises warm luminance but never targets white.
                    color += float3(0.070, 0.028, 0.000) * direct *
                             (0.42 + h.sky * 0.38 + h.boil * 0.20);

                    // A soft solar wash from the upper-left makes the room read as being
                    // under a giant directional sun without drawing a literal sun disk.
                    float solarWash = saturate(1.0 - length((screenUV - float2(0.16, 0.88)) * float2(0.82, 1.08)));
                    solarWash = solarWash * solarWash * intensity * solar * 0.12;
                    color = lerp(color, float3(1.0, 0.55, 0.055), solarWash);

                    return saturate(color);
                }

                float3 ApplyPeripheralHeat(float3 color, float2 grabUV, float2 screenUV)
                {
                    float intensity = saturate(_DryCycleIntenseHeatIntensity);
                    float2 edgeAxis = abs(screenUV - 0.5) * 2.0;
                    float edge = saturate(max(edgeAxis.x, edgeAxis.y));
                    edge = smoothstep(0.53, 1.0, edge);
                    edge = edge * edge;

                    float pulse = 0.88 + 0.12 * sin(_DryCycleIntenseHeatTime * 0.77 + screenUV.y * 5.1);
                    float amount = edge * intensity * pulse;
                    float3 peripheral = float3(0.94, 0.29, 0.025);
                    color = lerp(color, peripheral, amount * 0.24);

                    // Very small edge-only spectral separation: an eye-overload cue,
                    // not the central visual effect.
                    float2 radial = SafeNormalize(screenUV - 0.5);
                    float2 pixel = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float shift = amount * 0.72;
                    float r = tex2D(_GrabTexture, grabUV + radial * pixel * shift).r;
                    float b = tex2D(_GrabTexture, grabUV - radial * pixel * shift).b;
                    color.r = lerp(color.r, max(color.r, r), amount * 0.12);
                    color.b = lerp(color.b, b * 0.72, amount * 0.08);
                    return saturate(color);
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.00001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.00001);
                    float2 roomUV = RoomUV(screenUV);
                    float2 roomPx = roomUV * max(_DryCycleIntenseRoomSizePx, float2(1.0, 1.0));

                    HeatSample h = EvaluateHeat(screenUV, roomUV, roomPx);
                    float4 flowTex = SampleFlow(roomPx);
                    float2 flow = SafeNormalize(flowTex.rg * 2.0 - 1.0);

                    if (_DryCycleIntenseDebugMode == 1)
                    {
                        return float4(h.directSun, h.penumbra, h.sky, 1.0);
                    }
                    if (_DryCycleIntenseDebugMode == 2)
                    {
                        return float4(
                            saturate(length(h.offsetPx) / 25.5),
                            h.boil,
                            h.ground,
                            1.0);
                    }
                    if (_DryCycleIntenseDebugMode == 3)
                    {
                        return float4(h.directSun, h.sheet, h.boil, 1.0);
                    }

                    float4 scene = SampleScene(grabUV, h.offsetPx, h.blur, flow);
                    float3 color = ApplyDisasterGrade(scene.rgb, h, screenUV);
                    color = ApplyPeripheralHeat(color, grabUV, screenUV);

                    if (_DryCycleIntenseDebugMode == 4)
                    {
                        float2 edgeAxis = abs(screenUV - 0.5) * 2.0;
                        float edge = smoothstep(0.53, 1.0, saturate(max(edgeAxis.x, edgeAxis.y)));
                        return float4(edge, edge * 0.30, 0.0, 1.0);
                    }

                    return float4(color, scene.a);
                }
                ENDCG
            }
        }
    }
}
