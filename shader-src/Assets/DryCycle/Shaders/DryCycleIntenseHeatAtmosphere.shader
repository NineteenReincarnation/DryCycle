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

                    // Keep the air violently hot without making the whole camera feel
                    // seasick. Large-scale displacement is reduced; smaller refraction
                    // layers still provide dense boiling detail.
                    float2 macroOffset = float2(
                        (largeA * 0.56 + largeB * 0.24) * 3.8,
                        (largeA * 0.72 - largeB * 0.44 + largeC * 0.20) * 6.8);
                    macroOffset *= h.boil * heatDrive;

                    float2 normalOffset = float2(
                        baseNormal.x * 3.5 + detailNormal.x * 1.55,
                        baseNormal.y * 6.7 + detailNormal.y * 3.15);
                    normalOffset *= heatDrive * (0.56 + h.sheet * 0.76);

                    float groundMirage = h.ground * h.directSun * intensity;
                    float2 mirageOffset = float2(
                        detailNormal.x * 3.0,
                        (mirage.g * 2.0 - 1.0) * 13.0 + baseNormal.y * 5.0);
                    mirageOffset *= groundMirage * (0.44 + mirageBand * 0.82);

                    h.shimmer = saturate(
                        length(detailNormal) * 0.32 +
                        h.sheet * 0.52 +
                        h.penumbra * 0.28);

                    float2 micro = float2(
                        detailNormal.x * 1.25,
                        detailNormal.y * 2.20) *
                        intensity *
                        (0.34 + h.directSun * 0.76);

                    h.offsetPx = ClampMagnitude(
                        macroOffset + normalOffset + mirageOffset + micro,
                        lerp(9.5, 19.0, intensity * (0.48 + h.directSun * 0.52)));
                    h.blur = saturate(
                        intensity *
                        (h.sheet * 0.42 + h.boil * 0.19 + groundMirage * 0.43));
                    return h;
                }

                float4 SampleScene(float2 grabUV, float2 offsetPx, float blur, float2 flow)
                {
                    float2 pixel = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 uv = grabUV + offsetPx * pixel;
                    float4 center = tex2D(_GrabTexture, uv);
                    if (blur <= 0.025)
                        return center;

                    float2 axis = SafeNormalize(float2(flow.x * 0.44, 1.0)) * pixel * (1.0 + blur * 2.1);
                    float4 a = tex2D(_GrabTexture, uv - axis);
                    float4 b = tex2D(_GrabTexture, uv + axis);
                    float4 c = tex2D(_GrabTexture, uv - axis * 2.05);
                    float4 d = tex2D(_GrabTexture, uv + axis * 2.05);
                    return lerp(center, center * 0.46 + (a + b) * 0.18 + (c + d) * 0.09, blur * 0.58);
                }

                float SolarBlotchField(float2 roomPx)
                {
                    float t = _DryCycleIntenseHeatTime;

                    // Large soft hot-light islands drift diagonally through world space.
                    // Two scales are multiplied so the result looks like irregular
                    // sunlight breaking through turbulent dry air rather than water caustics.
                    float2 p0 = roomPx / float2(430.0, 310.0) + float2(t * 0.030, -t * 0.014);
                    float2 p1 = roomPx / float2(265.0, 205.0) + float2(-t * 0.021, t * 0.019);
                    float broad =
                        sin(p0.x * 6.2831853) * 0.46 +
                        sin(p0.y * 6.2831853 + 1.37) * 0.34 +
                        sin((p0.x + p0.y) * 6.2831853 + 2.21) * 0.20;
                    float detail =
                        sin(p1.x * 6.2831853 + 0.74) * 0.42 +
                        cos(p1.y * 6.2831853 + 2.03) * 0.34 +
                        sin((p1.x - p1.y) * 6.2831853 + 4.12) * 0.24;

                    float islands = smoothstep(0.27, 0.79, broad * 0.70 + detail * 0.30);
                    float breakup = 0.72 + 0.28 * smoothstep(
                        -0.20,
                        0.62,
                        sin((p0.x * 1.7 - p0.y * 1.1) * 6.2831853 + t * 0.11));
                    return saturate(islands * breakup);
                }

                float3 ApplyDisasterGrade(float3 color, HeatSample h, float2 screenUV, float2 roomPx)
                {
                    float intensity = saturate(_DryCycleIntenseHeatIntensity);
                    float solar = saturate(_DryCycleIntenseSolarIntensity);
                    float luma = dot(color, float3(0.299, 0.587, 0.114));

                    // Keep the whole room unmistakably hot, but preserve enough of the
                    // source palette that the image does not collapse into one flat orange.
                    float3 warmBase = saturate(
                        color * float3(1.10, 0.965, 0.70) +
                        luma * float3(0.065, 0.018, -0.010));
                    float gradeAmount = intensity *
                        (0.43 + solar * 0.15 + h.directSun * 0.12);
                    color = lerp(color, warmBase, saturate(gradeAmount));

                    float gradedLuma = dot(color, float3(0.299, 0.587, 0.114));
                    float shadow = 1.0 - smoothstep(0.10, 0.37, gradedLuma);
                    float lowMid = smoothstep(0.16, 0.40, gradedLuma) *
                                   (1.0 - smoothstep(0.48, 0.68, gradedLuma));
                    float mid = smoothstep(0.12, 0.42, gradedLuma) *
                                (1.0 - smoothstep(0.68, 0.91, gradedLuma));
                    float highlight = smoothstep(0.52, 0.91, gradedLuma);

                    // More red is carried by the shadows and low mids. This creates a
                    // burnt red-brown floor under the gold sunlight instead of flattening
                    // the whole room into one orange-yellow hue.
                    float3 burntShadow = saturate(
                        color * float3(0.76, 0.49, 0.38) +
                        float3(0.036, 0.005, 0.001));
                    color = lerp(color, burntShadow, intensity * shadow * 0.62);

                    float3 rustRed = saturate(
                        color * float3(1.06, 0.61, 0.43) +
                        float3(0.085, 0.010, 0.001));
                    color = lerp(color, rustRed, intensity * lowMid * 0.16);

                    float heatVariation = saturate(
                        0.42 + h.boil * 0.34 + h.sheet * 0.24);
                    float3 ochre = saturate(
                        color * float3(1.075, 0.91, 0.64) +
                        gradedLuma * float3(0.070, 0.025, 0.000));
                    float3 oldGold = saturate(
                        color * float3(1.13, 0.99, 0.68) +
                        float3(0.060, 0.032, 0.002));
                    float3 midTone = lerp(ochre, oldGold, heatVariation * 0.62);
                    color = lerp(
                        color,
                        midTone,
                        intensity * mid * (0.24 + h.directSun * 0.19));

                    float direct = intensity * h.directSun;
                    float3 solarGold = float3(1.0, 0.72, 0.18);
                    color = lerp(color, solarGold, highlight * direct * 0.15);

                    // Dense boiling pockets now lean red-orange before they reach the
                    // hottest gold highlights, widening the heat palette.
                    float hotPocket = intensity * h.boil * h.sheet *
                                      smoothstep(0.26, 0.82, gradedLuma);
                    float3 emberPocket = float3(0.94, 0.285, 0.028);
                    color = lerp(color, emberPocket, hotPocket * 0.12);

                    float verticalSun = smoothstep(0.04, 0.96, screenUV.y);
                    float solarWash = intensity * solar *
                        (0.030 + verticalSun * 0.075);
                    color = lerp(color, float3(1.0, 0.76, 0.24), solarWash);

                    // Moving solar blotches: soft, world-space hot-light patches whose
                    // cores are gold and whose shoulders carry orange-red. They brighten
                    // by color shift rather than by pushing the scene toward white.
                    float blotch = SolarBlotchField(roomPx) * intensity * solar;
                    float blotchCore = smoothstep(0.28, 0.90, blotch) * h.directSun;
                    float blotchRim = smoothstep(0.10, 0.66, blotch) * (1.0 - blotchCore * 0.55);
                    float3 blotchRed = float3(0.88, 0.205, 0.028);
                    float3 blotchGold = float3(1.0, 0.61, 0.095);
                    color = lerp(color, blotchRed, blotchRim * 0.075);
                    color = lerp(color, blotchGold, blotchCore * 0.105);

                    float thermalContrast = (h.boil - 0.5) * intensity;
                    color += float3(0.038, 0.006, -0.014) * max(0.0, thermalContrast);
                    color *= 1.0 - max(0.0, -thermalContrast) * float3(0.045, 0.030, 0.010);

                    return saturate(color);
                }

                float3 ApplyPeripheralHeat(float3 color, float2 grabUV, float2 screenUV)
                {
                    float intensity = saturate(_DryCycleIntenseHeatIntensity);
                    float2 edgeAxis = abs(screenUV - 0.5) * 2.0;
                    float edge = saturate(max(edgeAxis.x, edgeAxis.y));
                    edge = smoothstep(0.53, 1.0, edge);
                    edge = edge * edge;

                    float pulse = 0.88 + 0.12 * sin(
                        _DryCycleIntenseHeatTime * 0.77 + screenUV.y * 5.1);
                    float amount = edge * intensity * pulse;

                    float corner = saturate(edgeAxis.x * edgeAxis.y);
                    float lowerEdge = saturate(1.0 - screenUV.y);
                    float3 edgeAmber = float3(0.90, 0.235, 0.025);
                    float3 edgeBurnt = float3(0.43, 0.045, 0.012);
                    float edgeDepth = saturate(corner * 0.68 + lowerEdge * 0.18);
                    float3 peripheral = lerp(edgeAmber, edgeBurnt, edgeDepth);
                    color = lerp(color, peripheral, amount * 0.24);

                    float2 radial = SafeNormalize(screenUV - 0.5);
                    float2 pixel = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float shift = amount * 0.62;
                    float r = tex2D(_GrabTexture, grabUV + radial * pixel * shift).r;
                    float b = tex2D(_GrabTexture, grabUV - radial * pixel * shift).b;
                    color.r = lerp(color.r, max(color.r, r), amount * 0.10);
                    color.b = lerp(color.b, b * 0.72, amount * 0.07);
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
                            saturate(length(h.offsetPx) / 19.0),
                            h.boil,
                            h.ground,
                            1.0);
                    }
                    if (_DryCycleIntenseDebugMode == 3)
                    {
                        return float4(h.directSun, h.sheet, h.boil, 1.0);
                    }

                    float4 scene = SampleScene(grabUV, h.offsetPx, h.blur, flow);
                    float3 color = ApplyDisasterGrade(scene.rgb, h, screenUV, roomPx);
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