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
                sampler2D _DryCycleHeatSurfaceField;

                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float2 _DryCycleHeatRoomSizePx;
                uniform float _DryCycleHeatWaveIntensity;
                uniform float _DryCycleHeatSolarIntensity;
                uniform float _DryCycleHeatToneAmount;
                uniform float _DryCycleHeatLevelAmount;
                uniform float _DryCycleHeatTime;
                uniform float _DryCycleHasHeatTextures;
                uniform float _DryCycleHasHeatSurfaceField;
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
                    float sheet;
                    float sheetEdge;
                    float compression;
                    float blur;
                    float focus;
                    float ground;
                    float surface;
                    float dryAir;
                    float evolution;
                    float2 flow;
                    float2 offsetPx;
                    float2 layeringOffsetPx;
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
                        float p0 = phase;
                        float p1 = frac(phase + 0.5);
                        float blend = PhaseBlend(p0);

                        float2 macroUv = roomPx / float2(1040.0, 238.0);
                        float2 macroTravel = float2(flow.x * 0.118, flow.y * 0.066);
                        float4 macroA = tex2D(
                            _DryCycleHeatMirageField,
                            frac(macroUv - macroTravel * p0));
                        float4 macroB = tex2D(
                            _DryCycleHeatMirageField,
                            frac(macroUv - macroTravel * p1 + 0.371));
                        float4 macro = lerp(macroA, macroB, blend);

                        float finePhase = frac(phase * 1.371 + _DryCycleHeatTime * 0.043 + 0.173);
                        float finePhaseB = frac(finePhase + 0.5);
                        float fineBlend = PhaseBlend(finePhase);
                        float2 fineUv = roomPx / float2(430.0, 104.0);
                        float2 fineTravel = float2(flow.x * 0.176, flow.y * 0.105);
                        float4 fineA = tex2D(
                            _DryCycleHeatMirageField,
                            frac(fineUv - fineTravel * finePhase + 0.219));
                        float4 fineB = tex2D(
                            _DryCycleHeatMirageField,
                            frac(fineUv - fineTravel * finePhaseB + 0.641));
                        float4 fine = lerp(fineA, fineB, fineBlend);

                        float macroStretch = macro.g * 2.0 - 1.0;
                        float fineStretch = fine.g * 2.0 - 1.0;
                        float band = saturate(macro.r * 0.67 + fine.r * 0.48 - 0.04);
                        float stretch = clamp(
                            macroStretch * 0.70 + fineStretch * 0.46,
                            -1.0,
                            1.0);
                        float blur = saturate(max(macro.b * 0.76, fine.b * 0.72));
                        float mixedPhase = frac(macro.a * 0.61 + fine.a * 0.39);
                        return float4(band, stretch * 0.5 + 0.5, blur, mixedPhase);
                    }

                    float broad = sin(
                        (roomPx.y / 84.0 + roomPx.x / 1040.0 - _DryCycleHeatTime * 0.083) *
                        6.2831853);
                    float fineWave = sin(
                        (roomPx.y / 37.0 - roomPx.x / 610.0 - _DryCycleHeatTime * 0.154) *
                        6.2831853);
                    float band = smoothstep(0.02, 0.83, broad * 0.5 + 0.5);
                    float stretch = clamp(broad * 0.68 + fineWave * 0.34, -1.0, 1.0);
                    return float4(
                        band,
                        stretch * 0.5 + 0.5,
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
                            sin((roomPx.x / 43.0 + roomPx.y / 61.0 + time * 0.31) * 6.2831853) * 0.55,
                            cos((roomPx.x / 57.0 - roomPx.y / 37.0 - time * 0.37) * 6.2831853) * 0.62);
                        return;
                    }

                    float basePhase = frac(_DryCycleHeatTime * 0.064 + phaseSeed);
                    float basePhaseB = frac(basePhase + 0.5);
                    float baseBlend = PhaseBlend(basePhase);
                    float2 baseUv = roomPx / float2(250.0, 425.0);
                    float2 baseTravel = float2(flow.x * 0.132, flow.y * 0.091);

                    float4 baseA = tex2D(
                        _DryCycleHeatNormalField,
                        frac(baseUv - baseTravel * basePhase));
                    float4 baseB = tex2D(
                        _DryCycleHeatNormalField,
                        frac(baseUv - baseTravel * basePhaseB + 0.317));
                    baseNormal =
                        (lerp(baseA.rg, baseB.rg, baseBlend) * 2.0 - 1.0);

                    float detailPhase = frac(_DryCycleHeatTime * 0.235 + phaseSeed * 0.731 + 0.193);
                    float detailPhaseB = frac(detailPhase + 0.5);
                    float detailBlend = PhaseBlend(detailPhase);
                    float2 detailUv = roomPx / float2(46.0, 59.0);
                    float2 detailTravel = float2(flow.x * 0.198, flow.y * 0.144);

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
                    return float2(r - l, u - d) * 8.35;
                }

                float4 SampleSurface(float2 roomPx)
                {
                    if (_DryCycleHasHeatSurfaceField > 0.5)
                    {
                        float2 room01 = saturate(
                            roomPx / max(_DryCycleHeatRoomSizePx, float2(1.0, 1.0)));
                        return tex2D(_DryCycleHeatSurfaceField, room01);
                    }

                    float phase = frac(roomPx.x / 701.0 + roomPx.y / 911.0);
                    return float4(0.0, 0.0, 1.0, phase);
                }

                float EvaluateEvolution(float2 roomPx, float phaseSeed, float strength)
                {
                    float a = sin(
                        _DryCycleHeatTime * 0.61 +
                        phaseSeed * 6.2831853 +
                        roomPx.x / 1160.0 * 6.2831853);
                    float b = sin(
                        _DryCycleHeatTime * 0.27 +
                        phaseSeed * 11.173 -
                        roomPx.y / 970.0 * 6.2831853 +
                        strength * 2.3);
                    return clamp(0.90 + a * 0.105 + b * 0.075, 0.72, 1.08);
                }

                float SampleThermalSheetScalar(
                    float2 roomPx,
                    float2 flow,
                    float phaseSeed,
                    float horizontalScale,
                    float verticalScale,
                    float speed,
                    float seed)
                {
                    float warp;
                    float breakup;

                    if (_DryCycleHasHeatTextures > 0.5)
                    {
                        float2 noiseUv = frac(
                            roomPx / float2(horizontalScale * 0.61, verticalScale * 4.2) +
                            float2(seed * 0.173, phaseSeed * 0.217));
                        float4 noise = tex2D(_DryCycleHeatMirageField, noiseUv);
                        warp =
                            (noise.r - 0.5) * 0.52 +
                            (noise.g - 0.5) * 0.34 +
                            (noise.a - 0.5) * 0.22;
                        breakup = smoothstep(0.16, 0.82, noise.b * 0.72 + noise.r * 0.28);
                    }
                    else
                    {
                        warp =
                            sin((roomPx.x / 510.0 + roomPx.y / 310.0 + seed) * 6.2831853) * 0.22;
                        breakup = 0.72 +
                            sin((roomPx.x / 370.0 - roomPx.y / 590.0 + seed) * 6.2831853) * 0.18;
                    }

                    float lateralMeander =
                        sin(
                            roomPx.x / horizontalScale * 6.2831853 +
                            phaseSeed * 8.13 +
                            seed * 3.7) * 0.065;
                    float phase = frac(
                        roomPx.y / verticalScale +
                        roomPx.x / horizontalScale +
                        warp * 0.46 +
                        lateralMeander +
                        flow.x * 0.055 -
                        _DryCycleHeatTime * speed +
                        phaseSeed * 0.23 +
                        seed);

                    float ridge = 1.0 - abs(phase * 2.0 - 1.0);
                    float sheet = smoothstep(0.64, 0.95, ridge);
                    return saturate(sheet * breakup);
                }

                void SampleThermalSheets(
                    float2 roomPx,
                    float2 flow,
                    float phaseSeed,
                    float ground,
                    float evolution,
                    out float sheetBody,
                    out float sheetEdge,
                    out float compression,
                    out float groundSheet)
                {
                    float groundCore = pow(saturate(ground), 1.55);

                    float macroCenter = SampleThermalSheetScalar(
                        roomPx,
                        flow,
                        phaseSeed,
                        930.0,
                        72.0,
                        0.043,
                        0.11);
                    float macroUp = SampleThermalSheetScalar(
                        roomPx + float2(0.0, 5.5),
                        flow,
                        phaseSeed,
                        930.0,
                        72.0,
                        0.043,
                        0.11);
                    float macroDown = SampleThermalSheetScalar(
                        roomPx - float2(0.0, 5.5),
                        flow,
                        phaseSeed,
                        930.0,
                        72.0,
                        0.043,
                        0.11);

                    float fineCenter = SampleThermalSheetScalar(
                        roomPx,
                        flow,
                        phaseSeed * 1.31 + 0.17,
                        470.0,
                        36.0,
                        0.092,
                        0.43);
                    float fineUp = SampleThermalSheetScalar(
                        roomPx + float2(0.0, 3.2),
                        flow,
                        phaseSeed * 1.31 + 0.17,
                        470.0,
                        36.0,
                        0.092,
                        0.43);
                    float fineDown = SampleThermalSheetScalar(
                        roomPx - float2(0.0, 3.2),
                        flow,
                        phaseSeed * 1.31 + 0.17,
                        470.0,
                        36.0,
                        0.092,
                        0.43);

                    float denseCenter = SampleThermalSheetScalar(
                        roomPx,
                        flow,
                        phaseSeed * 1.73 + 0.31,
                        320.0,
                        23.0,
                        0.138,
                        0.71);
                    float denseUp = SampleThermalSheetScalar(
                        roomPx + float2(0.0, 2.2),
                        flow,
                        phaseSeed * 1.73 + 0.31,
                        320.0,
                        23.0,
                        0.138,
                        0.71);
                    float denseDown = SampleThermalSheetScalar(
                        roomPx - float2(0.0, 2.2),
                        flow,
                        phaseSeed * 1.73 + 0.31,
                        320.0,
                        23.0,
                        0.138,
                        0.71);

                    groundSheet = groundCore * denseCenter;

                    float center = saturate(
                        macroCenter * 0.83 +
                        fineCenter * 0.63 +
                        groundSheet * 0.92);
                    float up = saturate(
                        macroUp * 0.83 +
                        fineUp * 0.63 +
                        groundCore * denseUp * 0.92);
                    float down = saturate(
                        macroDown * 0.83 +
                        fineDown * 0.63 +
                        groundCore * denseDown * 0.92);

                    sheetBody = saturate(center * lerp(0.90, 1.08, evolution));
                    sheetEdge = clamp((up - down) * 2.65, -1.0, 1.0);
                    compression = clamp((up + down - center * 2.0) * 3.15, -1.0, 1.0);
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
                    float4 surfaceData = SampleSurface(roomPx);
                    float dryAir = surfaceData.b;
                    float ground = surfaceData.r * dryAir;
                    float surface = surfaceData.g * dryAir;
                    float evolution = EvaluateEvolution(
                        roomPx,
                        frac(flowData.a * 0.71 + surfaceData.a * 0.29),
                        strength);

                    float sheet;
                    float sheetEdge;
                    float compression;
                    float groundSheet;
                    SampleThermalSheets(
                        roomPx,
                        flow,
                        frac(flowData.a * 0.66 + surfaceData.a * 0.34),
                        ground,
                        evolution,
                        sheet,
                        sheetEdge,
                        compression,
                        groundSheet);

                    float band = saturate(
                        strength * 0.43 +
                        mirageBand * 0.49 +
                        sheet * 0.44 +
                        groundSheet * 0.18);
                    float body = smoothstep(0.16, 0.79, band);
                    float sheetBody = smoothstep(0.12, 0.84, sheet);
                    float surfaceLift = 1.0 + surface * 0.18;
                    float groundCore = pow(saturate(ground), 1.45);

                    float2 baseOffset = float2(
                        baseNormal.x * 1.72,
                        baseNormal.y * 3.82) *
                        lerp(0.30, 0.92, body) *
                        evolution *
                        surfaceLift;

                    float2 detailOffset = float2(
                        detailNormal.x * 0.68,
                        detailNormal.y * 1.30) *
                        lerp(0.42, 0.92, body) *
                        lerp(0.92, 1.09, evolution);

                    float2 gradientOffset = float2(
                        densityGradient.x * 1.22,
                        densityGradient.y * 2.72) *
                        smoothstep(0.14, 0.88, strength) *
                        lerp(0.91, 1.10, evolution);

                    float mirageY =
                        mirageStretch *
                        lerp(1.45, 6.60, mirageBand) *
                        lerp(0.30, 0.92, body) *
                        evolution;

                    // Thermal sheets use the first and second vertical derivatives of
                    // a thin anisotropic heat-layer field. Opposite signs at the upper
                    // and lower sheet boundaries bend silhouettes in opposite directions,
                    // while curvature produces local optical compression/expansion.
                    float sheetGate =
                        heat *
                        lerp(0.46, 1.0, sheetBody) *
                        lerp(0.90, 1.10, evolution);
                    float sheetY =
                        (sheetEdge * lerp(4.10, 9.80, sheetBody) +
                         compression * lerp(1.10, 3.85, sheetBody)) *
                        sheetGate;
                    float sheetX =
                        (flow.x * sheetEdge * 1.55 +
                         detailNormal.x * sheetBody * 0.58) *
                        sheetGate;

                    float groundBand = saturate(
                        mirageBand * 0.42 +
                        strength * 0.22 +
                        sheet * 0.58 +
                        groundSheet * 0.44);
                    float groundStrength =
                        groundCore *
                        smoothstep(0.05, 0.78, heat) *
                        lerp(0.78, 1.12, evolution);
                    float groundY =
                        (mirageStretch * 0.58 +
                         sheetEdge * 1.22 +
                         compression * 0.48 +
                         baseNormal.y * 0.13 +
                         detailNormal.y * 0.10) *
                        groundStrength *
                        lerp(2.25, 8.40, groundBand);
                    float groundX =
                        (baseNormal.x * 0.42 +
                         detailNormal.x * 0.35 +
                         flow.x * sheetEdge * 0.52 +
                         densityGradient.x * 0.026) *
                        groundStrength *
                        lerp(0.42, 2.35, groundBand);

                    float2 offset =
                        baseOffset +
                        detailOffset +
                        gradientOffset +
                        float2(0.0, mirageY) +
                        float2(sheetX, sheetY) +
                        float2(groundX, groundY);

                    offset *= heat;
                    offset = ClampMagnitude(offset, 14.5);

                    float focusRaw =
                        mirageStretch * 0.40 +
                        compression * 0.82 +
                        sheetEdge * 0.15 -
                        densityGradient.y * 0.041 +
                        baseNormal.y * 0.09 +
                        detailNormal.y * 0.038;
                    float focus = clamp(focusRaw, -1.0, 1.0) *
                        heat *
                        lerp(0.30, 1.0, max(body, sheetBody)) *
                        lerp(0.88, 1.10, evolution) *
                        lerp(0.94, 1.22, groundCore);

                    float2 layeringOffset = float2(
                        -offset.x * 0.12 +
                        detailNormal.x * 0.68 +
                        flow.x * sheetEdge * 1.18,
                        sheetEdge * lerp(2.25, 6.40, sheetBody) +
                        compression * lerp(1.10, 4.20, sheetBody) +
                        mirageStretch * lerp(0.70, 2.90, saturate(abs(focus))) +
                        groundCore * baseNormal.y * 1.65);
                    layeringOffset *= heat * lerp(0.54, 1.0, max(body, sheetBody));
                    layeringOffset = ClampMagnitude(layeringOffset, 7.0);

                    result.band = band;
                    result.mirage = mirageBand;
                    result.sheet = sheet;
                    result.sheetEdge = sheetEdge;
                    result.compression = compression;
                    result.blur = saturate(
                        mirageData.b * 0.48 +
                        body * 0.15 +
                        sheetBody * 0.28 +
                        groundCore * 0.28);
                    result.focus = focus;
                    result.ground = groundCore;
                    result.surface = surface;
                    result.dryAir = dryAir;
                    result.evolution = evolution;
                    result.flow = flow;
                    result.offsetPx = offset;
                    result.layeringOffsetPx = layeringOffset;
                    return result;
                }

                float EvaluateSceneEdge(float2 grabUV)
                {
                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 dx = float2(px.x * 1.35, 0.0);
                    float2 dy = float2(0.0, px.y * 1.35);
                    float l = dot(tex2D(_GrabTexture, grabUV - dx).rgb, float3(0.2126, 0.7152, 0.0722));
                    float r = dot(tex2D(_GrabTexture, grabUV + dx).rgb, float3(0.2126, 0.7152, 0.0722));
                    float d = dot(tex2D(_GrabTexture, grabUV - dy).rgb, float3(0.2126, 0.7152, 0.0722));
                    float u = dot(tex2D(_GrabTexture, grabUV + dy).rgb, float3(0.2126, 0.7152, 0.0722));
                    return saturate((abs(r - l) + abs(u - d)) * 2.35);
                }

                float3 ApplyThermalSheetCompression(
                    float3 center,
                    float2 grabUV,
                    HeatFieldSample field,
                    float sceneEdge)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float sheet = smoothstep(0.12, 0.88, field.sheet);
                    float edgeMagnitude = saturate(abs(field.sheetEdge));
                    float compressionMagnitude = saturate(abs(field.compression));
                    float amount =
                        heat *
                        sheet *
                        (0.060 +
                         edgeMagnitude * 0.105 +
                         compressionMagnitude * 0.115 +
                         field.ground * 0.075 +
                         sceneEdge * 0.115);

                    if (amount <= 0.008)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float span =
                        0.70 +
                        edgeMagnitude * 2.15 +
                        compressionMagnitude * 2.65 +
                        field.ground * 1.25;
                    float signedDirection =
                        abs(field.sheetEdge) > 0.04
                            ? sign(field.sheetEdge)
                            : sign(field.compression + 0.0001);
                    float2 verticalStep = float2(0.0, signedDirection * span) * px;

                    float3 primary = tex2D(_GrabTexture, grabUV + verticalStep).rgb;
                    float3 counter = tex2D(_GrabTexture, grabUV - verticalStep * 0.72).rgb;
                    float3 compressed = lerp(primary, counter, saturate(0.34 + compressionMagnitude * 0.26));
                    return lerp(center, compressed, saturate(amount));
                }

                float3 ApplyMirageLayering(
                    float3 center,
                    float2 grabUV,
                    HeatFieldSample field,
                    float sceneEdge)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float body = smoothstep(0.37, 0.90, field.band);
                    float sheet = smoothstep(0.16, 0.86, field.sheet);
                    float focusMagnitude = saturate(abs(field.focus));
                    float layerAmount =
                        heat *
                        max(body, sheet) *
                        (0.034 +
                         field.ground * 0.095 +
                         sheet * 0.075 +
                         focusMagnitude * 0.070 +
                         sceneEdge * sheet * 0.105);

                    if (layerAmount <= 0.008)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 firstUv = grabUV + field.layeringOffsetPx * px;
                    float3 first = tex2D(_GrabTexture, firstUv).rgb;
                    float3 layered = lerp(center, first, saturate(layerAmount));

                    float counterAmount =
                        heat *
                        max(field.ground, sheet * 0.62) *
                        max(body, sheet) *
                        max(focusMagnitude, abs(field.compression) * 0.72) *
                        (0.044 + sceneEdge * 0.040);
                    if (counterAmount > 0.006)
                    {
                        float2 counterOffset = float2(
                            -field.layeringOffsetPx.x * 0.44,
                            -field.layeringOffsetPx.y * 0.61);
                        float3 counter = tex2D(
                            _GrabTexture,
                            grabUV + counterOffset * px).rgb;
                        layered = lerp(layered, counter, saturate(counterAmount));
                    }

                    return layered;
                }

                float3 ApplyDirectionalSoftening(
                    float3 center,
                    float2 grabUV,
                    float2 offsetPx,
                    float blurMask,
                    float sheet)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float magnitude = length(offsetPx);
                    float soften =
                        smoothstep(4.25, 13.8, magnitude) *
                        blurMask *
                        heat *
                        lerp(0.74, 1.0, smoothstep(0.20, 0.88, sheet));

                    if (soften <= 0.012)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 direction = SafeNormalize(offsetPx);
                    float radius = lerp(0.52, 2.35, soften);
                    float2 stepUv = direction * px * radius;

                    float3 blur = center * 0.42;
                    blur += tex2D(_GrabTexture, grabUV + stepUv).rgb * 0.21;
                    blur += tex2D(_GrabTexture, grabUV - stepUv).rgb * 0.21;
                    blur += tex2D(_GrabTexture, grabUV + stepUv * 1.95).rgb * 0.08;
                    blur += tex2D(_GrabTexture, grabUV - stepUv * 1.95).rgb * 0.08;

                    return lerp(center, blur, soften * 0.60);
                }

                float3 ApplyOpticalFocus(
                    float3 color,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadowGuard = smoothstep(0.050, 0.22, luma);
                    float highlightHeadroom = 1.0 - smoothstep(0.82, 0.975, luma);
                    float focusGain =
                        field.focus *
                        heat *
                        (0.052 +
                         field.ground * 0.030 +
                         field.band * 0.017 +
                         field.sheet * 0.034) *
                        shadowGuard *
                        highlightHeadroom;
                    color *= 1.0 + focusGain;
                    return saturate(color);
                }

                float3 ApplyHeatTone(
                    float3 color,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float tone = saturate(max(
                        _DryCycleHeatToneAmount,
                        heat * 0.68));
                    float solar = saturate(_DryCycleHeatSolarIntensity * heat);

                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.070, 0.29, luma);
                    float lit = 1.0 - shadow;
                    float midRise = smoothstep(0.105, 0.45, luma);
                    float midFall = 1.0 - smoothstep(0.74, 0.97, luma);
                    float midBand = midRise * midFall;
                    float high = smoothstep(0.44, 0.90, luma);
                    float highlightHeadroom = 1.0 - smoothstep(0.78, 0.98, luma);

                    // HeatWave increases dry contrast and pushes illuminated palette
                    // values toward sand/hot-yellow. It never targets white.
                    float contrast = tone * (0.092 + solar * 0.112);
                    color = (color - 0.415) * (1.0 + contrast) + 0.415;
                    color = saturate(color);

                    float3 yellowShifted = saturate(
                        color * float3(1.105, 1.044, 0.735) +
                        luma * float3(0.094, 0.060, 0.000));
                    float yellowAmount =
                        tone *
                        (0.305 +
                         solar * 0.245 +
                         field.band * 0.075 +
                         field.sheet * 0.090 +
                         field.ground * 0.050) *
                        midBand *
                        lit;
                    color = lerp(color, yellowShifted, saturate(yellowAmount));

                    float dryLuma = dot(color, float3(0.260, 0.682, 0.058));
                    float3 dryYellow = dryLuma * float3(1.17, 1.05, 0.65);
                    float dryAmount =
                        tone *
                        (0.068 +
                         solar * 0.118 +
                         field.band * 0.038 +
                         field.sheet * 0.035) *
                        high *
                        lit;
                    color = lerp(color, dryYellow, saturate(dryAmount));

                    float3 hotYellow = float3(1.0, 0.820, 0.395);
                    float yellowHighlight =
                        high *
                        lit *
                        (tone * 0.145 +
                         solar * 0.285 +
                         solar * field.band * 0.095 +
                         field.sheet * tone * 0.050 +
                         field.ground * tone * 0.035);
                    color = lerp(color, hotYellow, saturate(yellowHighlight));

                    float bandHeat = saturate((field.band - 0.25) / 0.75);
                    float sheetHeat = smoothstep(0.18, 0.90, field.sheet);
                    float3 bandYellow = saturate(
                        color * float3(1.052, 1.019, 0.870) +
                        luma * float3(0.032, 0.019, 0.0));
                    float bandColorAmount =
                        max(bandHeat * 0.72, sheetHeat) *
                        tone *
                        (0.062 +
                         solar * 0.060 +
                         max(field.focus, 0.0) * 0.030) *
                        lit;
                    color = lerp(color, bandYellow, saturate(bandColorAmount));

                    float exposureBreath =
                        ((field.band - 0.34) * 0.55 +
                         (field.sheet - 0.34) * 0.45) *
                        tone *
                        (0.038 + solar * 0.042) *
                        lit *
                        highlightHeadroom;
                    exposureBreath +=
                        field.focus *
                        tone *
                        0.022 *
                        lit *
                        highlightHeadroom;
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
                    float sceneEdge = EvaluateSceneEdge(grabUV);

                    // A curved short-path resolve is strongest in coherent thermal
                    // sheets and hot ground layers. The secondary field lookup changes
                    // the bend direction rather than recursively distorting the screen.
                    float2 resolvedOffset = field.offsetPx;
                    float coherentHeat = max(field.band, field.sheet);
                    if (coherentHeat > 0.40 && _DryCycleHeatWaveIntensity > 0.16)
                    {
                        HeatFieldSample nextField = EvaluateHeatField(
                            roomPx +
                            field.offsetPx * 1.85 +
                            field.layeringOffsetPx * 0.72 +
                            field.flow * 3.0);
                        float pathBlend = smoothstep(0.40, 0.92, coherentHeat);
                        resolvedOffset = lerp(
                            field.offsetPx,
                            field.offsetPx * 0.49 + nextField.offsetPx * 0.51,
                            pathBlend);
                        field.blur = max(
                            field.blur,
                            nextField.blur * pathBlend);
                        field.focus = lerp(
                            field.focus,
                            (field.focus + nextField.focus) * 0.5,
                            pathBlend * 0.74);
                        field.layeringOffsetPx = lerp(
                            field.layeringOffsetPx,
                            nextField.layeringOffsetPx,
                            pathBlend * 0.58);
                    }

                    // High-contrast silhouettes reveal heat haze most clearly. Give them
                    // a modest extra response inside thermal sheets without affecting flat
                    // regions or turning the entire frame into a wobbling lens.
                    float silhouetteBoost =
                        1.0 +
                        sceneEdge *
                        smoothstep(0.18, 0.86, field.sheet) *
                        _DryCycleHeatWaveIntensity *
                        0.22;
                    resolvedOffset *= silhouetteBoost;
                    resolvedOffset = ClampMagnitude(resolvedOffset, 14.5);

                    if (_DryCycleHeatDebugMode == 1)
                    {
                        return float4(
                            saturate(field.band),
                            saturate(field.sheet),
                            saturate(field.ground),
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 2)
                    {
                        float2 v = clamp(resolvedOffset / 14.5, -1.0, 1.0);
                        float magnitude = saturate(length(resolvedOffset) / 14.5);
                        return float4(v * 0.5 + 0.5, magnitude, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 3)
                    {
                        float heat = saturate(_DryCycleHeatWaveIntensity);
                        float tone = saturate(max(_DryCycleHeatToneAmount, heat * 0.68));
                        float solar = saturate(_DryCycleHeatSolarIntensity * heat);
                        return float4(tone, solar, field.sheet, 1.0);
                    }

                    if (_DryCycleHeatDebugMode == 4)
                    {
                        return float4(
                            field.flow * 0.5 + 0.5,
                            field.mirage,
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 5)
                    {
                        return float4(
                            saturate(field.ground),
                            saturate(field.surface),
                            saturate(field.dryAir),
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 6)
                    {
                        float signedFocus = saturate(field.focus * 0.5 + 0.5);
                        return float4(
                            signedFocus,
                            saturate(abs(field.compression)),
                            saturate(abs(field.sheetEdge)),
                            1.0);
                    }

                    float2 pxToUv = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 edgeMargin = pxToUv * 3.0;
                    float2 refractedUv = clamp(
                        grabUV + resolvedOffset * pxToUv,
                        edgeMargin,
                        1.0 - edgeMargin);

                    float3 color = tex2D(_GrabTexture, refractedUv).rgb;
                    color = ApplyThermalSheetCompression(
                        color,
                        refractedUv,
                        field,
                        sceneEdge);
                    color = ApplyMirageLayering(
                        color,
                        refractedUv,
                        field,
                        sceneEdge);
                    color = ApplyDirectionalSoftening(
                        color,
                        refractedUv,
                        resolvedOffset,
                        field.blur,
                        field.sheet);
                    color = ApplyOpticalFocus(color, field);
                    color = ApplyHeatTone(color, field);

                    return float4(color, 1.0);
                }
                ENDCG
            }
        }
    }

    Fallback Off
}
