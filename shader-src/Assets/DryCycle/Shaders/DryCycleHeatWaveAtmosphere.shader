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

                struct LatticeField
                {
                    float2 offsetPx;
                    float2 dOffsetDx;
                    float2 dOffsetDy;
                };

                struct HeatFieldSample
                {
                    float band;
                    float mirage;
                    float sheet;
                    float sheetEdge;
                    float compression;
                    float expansion;
                    float shear;
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

                float Smooth01(float value)
                {
                    float t = saturate(value);
                    return t * t * (3.0 - 2.0 * t);
                }

                float2 Smooth01(float2 value)
                {
                    float2 t = saturate(value);
                    return t * t * (3.0 - 2.0 * t);
                }

                float PhaseBlend(float phase)
                {
                    return abs(0.5 - phase) * 2.0;
                }

                float Hash21(float2 p)
                {
                    p = frac(p * float2(123.34, 345.45));
                    p += dot(p, p + 34.345);
                    return frac(p.x * p.y);
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
                    out float sheetCurvature,
                    out float groundSheet)
                {
                    float groundCore = pow(saturate(ground), 1.55);

                    float macroCenter = SampleThermalSheetScalar(
                        roomPx, flow, phaseSeed, 930.0, 72.0, 0.043, 0.11);
                    float macroUp = SampleThermalSheetScalar(
                        roomPx + float2(0.0, 5.5), flow, phaseSeed, 930.0, 72.0, 0.043, 0.11);
                    float macroDown = SampleThermalSheetScalar(
                        roomPx - float2(0.0, 5.5), flow, phaseSeed, 930.0, 72.0, 0.043, 0.11);

                    float fineSeed = phaseSeed * 1.31 + 0.17;
                    float fineCenter = SampleThermalSheetScalar(
                        roomPx, flow, fineSeed, 470.0, 36.0, 0.092, 0.43);
                    float fineUp = SampleThermalSheetScalar(
                        roomPx + float2(0.0, 3.2), flow, fineSeed, 470.0, 36.0, 0.092, 0.43);
                    float fineDown = SampleThermalSheetScalar(
                        roomPx - float2(0.0, 3.2), flow, fineSeed, 470.0, 36.0, 0.092, 0.43);

                    float denseSeed = phaseSeed * 1.73 + 0.31;
                    float denseCenter = SampleThermalSheetScalar(
                        roomPx, flow, denseSeed, 320.0, 23.0, 0.138, 0.71);
                    float denseUp = SampleThermalSheetScalar(
                        roomPx + float2(0.0, 2.2), flow, denseSeed, 320.0, 23.0, 0.138, 0.71);
                    float denseDown = SampleThermalSheetScalar(
                        roomPx - float2(0.0, 2.2), flow, denseSeed, 320.0, 23.0, 0.138, 0.71);

                    groundSheet = groundCore * denseCenter;

                    float center = saturate(
                        macroCenter * 0.82 +
                        fineCenter * 0.63 +
                        groundSheet * 0.95);
                    float up = saturate(
                        macroUp * 0.82 +
                        fineUp * 0.63 +
                        groundCore * denseUp * 0.95);
                    float down = saturate(
                        macroDown * 0.82 +
                        fineDown * 0.63 +
                        groundCore * denseDown * 0.95);

                    sheetBody = saturate(center * lerp(0.90, 1.08, evolution));
                    sheetEdge = clamp((up - down) * 2.70, -1.0, 1.0);
                    sheetCurvature = clamp((up + down - center * 2.0) * 3.20, -1.0, 1.0);
                }

                float2 EvaluateLatticeNode(
                    float2 node,
                    float cellSize,
                    float2 flow,
                    float phaseSeed,
                    float drive,
                    float groundDrive,
                    float amplitude,
                    float speed,
                    float seed)
                {
                    float h0 = Hash21(node + float2(seed * 19.31, seed * 7.17));
                    float h1 = Hash21(node.yx + float2(seed * 11.73, seed * 23.41));
                    float worldX = (node.x + 0.5) * cellSize;
                    float worldY = (node.y + 0.5) * cellSize;
                    float time = _DryCycleHeatTime;

                    float travellingPhase =
                        worldY / (cellSize * 6.3) -
                        time * speed +
                        h0 +
                        sin(worldX / 630.0 + h1 * 6.2831853) * 0.085;
                    float updraft = sin(travellingPhase * 6.2831853);
                    float breathing = sin(
                        time * speed * 0.47 +
                        h1 * 6.2831853 +
                        node.x * 0.23 -
                        node.y * 0.11);
                    float meander = sin(
                        time * speed * 0.63 +
                        h0 * 6.2831853 +
                        node.y * 0.31 +
                        node.x * 0.07);
                    float groundBoil = sin(
                        time * speed * 1.71 +
                        h1 * 10.73 +
                        node.x * 0.49 -
                        node.y * 0.37);

                    float relaxedDrive = Smooth01(saturate((drive - 0.08) / 0.92));
                    float groundLift = saturate(groundDrive);
                    float vertical =
                        updraft * 0.68 +
                        breathing * 0.24 +
                        groundBoil * groundLift * 0.56;
                    float lateral =
                        meander * 0.52 +
                        flow.x * 0.31 +
                        updraft * flow.x * 0.17 +
                        groundBoil * groundLift * 0.14;

                    float localAmplitude =
                        amplitude *
                        relaxedDrive *
                        (1.0 + groundLift * 0.32);
                    return float2(lateral, vertical) * localAmplitude;
                }

                LatticeField SampleOpticalLatticeScale(
                    float2 roomPx,
                    float cellSize,
                    float2 flow,
                    float phaseSeed,
                    float drive,
                    float groundDrive,
                    float amplitude,
                    float speed,
                    float seed)
                {
                    LatticeField result;
                    float2 grid = roomPx / cellSize;
                    float2 baseCell = floor(grid);
                    float2 local = frac(grid);
                    float2 smoothLocal = Smooth01(local);
                    float2 smoothDerivative =
                        6.0 * local * (1.0 - local) / cellSize;

                    float2 n00 = EvaluateLatticeNode(
                        baseCell + float2(0.0, 0.0), cellSize, flow, phaseSeed,
                        drive, groundDrive, amplitude, speed, seed);
                    float2 n10 = EvaluateLatticeNode(
                        baseCell + float2(1.0, 0.0), cellSize, flow, phaseSeed,
                        drive, groundDrive, amplitude, speed, seed);
                    float2 n01 = EvaluateLatticeNode(
                        baseCell + float2(0.0, 1.0), cellSize, flow, phaseSeed,
                        drive, groundDrive, amplitude, speed, seed);
                    float2 n11 = EvaluateLatticeNode(
                        baseCell + float2(1.0, 1.0), cellSize, flow, phaseSeed,
                        drive, groundDrive, amplitude, speed, seed);

                    float2 row0 = lerp(n00, n10, smoothLocal.x);
                    float2 row1 = lerp(n01, n11, smoothLocal.x);
                    result.offsetPx = lerp(row0, row1, smoothLocal.y);
                    result.dOffsetDx =
                        lerp(n10 - n00, n11 - n01, smoothLocal.y) *
                        smoothDerivative.x;
                    result.dOffsetDy =
                        lerp(n01 - n00, n11 - n10, smoothLocal.x) *
                        smoothDerivative.y;
                    return result;
                }

                void EvaluateOpticalLattice(
                    float2 roomPx,
                    float2 flow,
                    float phaseSeed,
                    float band,
                    float sheet,
                    float ground,
                    float groundSheet,
                    out float2 latticeOffset,
                    out float latticeCompression,
                    out float latticeExpansion,
                    out float latticeShear,
                    out float latticeBend)
                {
                    float macroDrive = saturate(
                        band * 0.72 +
                        sheet * 0.26 +
                        ground * 0.18);
                    float thermalDrive = saturate(
                        sheet * 0.86 +
                        groundSheet * 0.78 +
                        band * 0.24);

                    LatticeField macro = SampleOpticalLatticeScale(
                        roomPx,
                        64.0,
                        flow,
                        phaseSeed,
                        macroDrive,
                        ground * 0.42,
                        4.75,
                        0.19,
                        0.17);
                    LatticeField thermal = SampleOpticalLatticeScale(
                        roomPx,
                        28.0,
                        flow,
                        phaseSeed * 1.43 + 0.29,
                        thermalDrive,
                        max(ground, groundSheet),
                        4.65,
                        0.46,
                        0.61);

                    latticeOffset = macro.offsetPx + thermal.offsetPx;
                    float2 dX = macro.dOffsetDx + thermal.dOffsetDx;
                    float2 dY = macro.dOffsetDy + thermal.dOffsetDy;

                    float j00 = 1.0 + dX.x;
                    float j01 = dY.x;
                    float j10 = dX.y;
                    float j11 = 1.0 + dY.y;
                    float determinant = j00 * j11 - j01 * j10;

                    latticeCompression = saturate((1.0 - determinant) * 3.4);
                    latticeExpansion = saturate((determinant - 1.0) * 2.8);
                    latticeShear = saturate((abs(j01) + abs(j10)) * 2.55);
                    latticeBend = saturate((length(dX) + length(dY)) * 1.75);
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
                    float groundCore = pow(saturate(ground), 1.48);
                    float evolution = EvaluateEvolution(
                        roomPx,
                        frac(flowData.a * 0.71 + surfaceData.a * 0.29),
                        strength);

                    float sheet;
                    float sheetEdge;
                    float sheetCurvature;
                    float groundSheet;
                    SampleThermalSheets(
                        roomPx,
                        flow,
                        frac(flowData.a * 0.66 + surfaceData.a * 0.34),
                        ground,
                        evolution,
                        sheet,
                        sheetEdge,
                        sheetCurvature,
                        groundSheet);

                    float band = saturate(
                        strength * 0.39 +
                        mirageBand * 0.43 +
                        sheet * 0.52 +
                        groundSheet * 0.24);
                    float body = smoothstep(0.14, 0.78, band);
                    float sheetBody = smoothstep(0.10, 0.83, sheet);

                    float2 latticeOffset;
                    float latticeCompression;
                    float latticeExpansion;
                    float latticeShear;
                    float latticeBend;
                    EvaluateOpticalLattice(
                        roomPx,
                        flow,
                        frac(flowData.a * 0.59 + surfaceData.a * 0.41),
                        band,
                        sheet,
                        groundCore,
                        groundSheet,
                        latticeOffset,
                        latticeCompression,
                        latticeExpansion,
                        latticeShear,
                        latticeBend);

                    float2 baseOffset = float2(
                        baseNormal.x * 1.10,
                        baseNormal.y * 2.25) *
                        lerp(0.25, 0.82, body) *
                        evolution;
                    float2 detailOffset = float2(
                        detailNormal.x * 0.48,
                        detailNormal.y * 0.92) *
                        lerp(0.32, 0.90, max(body, sheetBody));
                    float2 gradientOffset = float2(
                        densityGradient.x * 0.92,
                        densityGradient.y * 1.95) *
                        smoothstep(0.16, 0.88, strength);

                    float mirageY =
                        mirageStretch *
                        lerp(1.20, 5.10, mirageBand) *
                        lerp(0.25, 0.90, body) *
                        evolution;

                    float sheetGate =
                        heat *
                        lerp(0.44, 1.0, sheetBody) *
                        lerp(0.90, 1.10, evolution);
                    float sheetY =
                        (sheetEdge * lerp(3.70, 8.70, sheetBody) +
                         sheetCurvature * lerp(0.90, 3.15, sheetBody)) *
                        sheetGate;
                    float sheetX =
                        (flow.x * sheetEdge * 1.42 +
                         detailNormal.x * sheetBody * 0.46) *
                        sheetGate;

                    float groundBand = saturate(
                        mirageBand * 0.32 +
                        sheet * 0.62 +
                        groundSheet * 0.58 +
                        strength * 0.18);
                    float groundStrength =
                        groundCore *
                        smoothstep(0.04, 0.75, heat) *
                        lerp(0.80, 1.13, evolution);
                    float groundY =
                        (sheetEdge * 1.10 +
                         sheetCurvature * 0.54 +
                         mirageStretch * 0.44 +
                         detailNormal.y * 0.10) *
                        groundStrength *
                        lerp(2.20, 7.75, groundBand);
                    float groundX =
                        (flow.x * sheetEdge * 0.46 +
                         baseNormal.x * 0.33 +
                         detailNormal.x * 0.24) *
                        groundStrength *
                        lerp(0.38, 2.10, groundBand);

                    float2 offset =
                        latticeOffset +
                        baseOffset +
                        detailOffset +
                        gradientOffset +
                        float2(0.0, mirageY) +
                        float2(sheetX, sheetY) +
                        float2(groundX, groundY);
                    offset *= heat;
                    offset = ClampMagnitude(offset, 15.8);

                    float signedLatticeFocus =
                        latticeCompression - latticeExpansion;
                    float combinedCompression = clamp(
                        sheetCurvature * 0.72 +
                        signedLatticeFocus * 0.88,
                        -1.0,
                        1.0);
                    float focusRaw =
                        signedLatticeFocus * 0.78 +
                        sheetCurvature * 0.52 +
                        sheetEdge * 0.10 +
                        mirageStretch * 0.22 -
                        densityGradient.y * 0.028;
                    float focus = clamp(focusRaw, -1.0, 1.0) *
                        heat *
                        lerp(0.28, 1.0, max(body, sheetBody)) *
                        lerp(0.91, 1.13, evolution) *
                        lerp(0.95, 1.19, groundCore);

                    float2 layeringOffset = float2(
                        latticeOffset.x * 0.18 +
                        flow.x * sheetEdge * 1.06 +
                        detailNormal.x * 0.54,
                        latticeOffset.y * 0.16 +
                        sheetEdge * lerp(1.95, 5.80, sheetBody) +
                        combinedCompression * lerp(0.85, 3.55, sheetBody) +
                        groundCore * baseNormal.y * 1.25);
                    layeringOffset *= heat * lerp(0.48, 1.0, max(body, sheetBody));
                    layeringOffset = ClampMagnitude(layeringOffset, 7.2);

                    result.band = band;
                    result.mirage = mirageBand;
                    result.sheet = sheet;
                    result.sheetEdge = sheetEdge;
                    result.compression = combinedCompression;
                    result.expansion = latticeExpansion;
                    result.shear = latticeShear;
                    result.blur = saturate(
                        mirageData.b * 0.40 +
                        body * 0.12 +
                        sheetBody * 0.24 +
                        groundCore * 0.24 +
                        latticeExpansion * 0.30 +
                        latticeShear * 0.16 +
                        latticeBend * 0.12);
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

                float3 ApplyThermalCompression(
                    float3 center,
                    float2 grabUV,
                    HeatFieldSample field,
                    float sceneEdge)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float sheet = smoothstep(0.10, 0.86, field.sheet);
                    float edgeMagnitude = saturate(abs(field.sheetEdge));
                    float compressionMagnitude = saturate(abs(field.compression));
                    float amount =
                        heat *
                        max(sheet, smoothstep(0.10, 0.72, compressionMagnitude)) *
                        (0.052 +
                         edgeMagnitude * 0.094 +
                         compressionMagnitude * 0.118 +
                         field.ground * 0.068 +
                         field.shear * 0.060 +
                         sceneEdge * 0.115);

                    if (amount <= 0.008)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float span =
                        0.68 +
                        edgeMagnitude * 1.95 +
                        compressionMagnitude * 2.55 +
                        field.ground * 1.15 +
                        field.shear * 0.85;
                    float signedDirection =
                        abs(field.sheetEdge) > 0.04
                            ? sign(field.sheetEdge)
                            : sign(field.compression + 0.0001);
                    float2 verticalStep = float2(0.0, signedDirection * span) * px;
                    float2 shearStep = float2(field.shear * 0.85, 0.0) * px;

                    float3 primary = tex2D(_GrabTexture, grabUV + verticalStep + shearStep).rgb;
                    float3 counter = tex2D(_GrabTexture, grabUV - verticalStep * 0.72 - shearStep * 0.55).rgb;
                    float3 compressed = lerp(
                        primary,
                        counter,
                        saturate(0.33 + compressionMagnitude * 0.28));
                    return lerp(center, compressed, saturate(amount));
                }

                float3 ApplyMirageLayering(
                    float3 center,
                    float2 grabUV,
                    HeatFieldSample field,
                    float sceneEdge)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float body = smoothstep(0.34, 0.89, field.band);
                    float sheet = smoothstep(0.13, 0.84, field.sheet);
                    float focusMagnitude = saturate(abs(field.focus));
                    float layerAmount =
                        heat *
                        max(body, sheet) *
                        (0.030 +
                         field.ground * 0.088 +
                         sheet * 0.070 +
                         focusMagnitude * 0.064 +
                         field.shear * 0.058 +
                         sceneEdge * sheet * 0.102);

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
                        max(focusMagnitude, abs(field.compression) * 0.70) *
                        (0.040 + sceneEdge * 0.042);
                    if (counterAmount > 0.006)
                    {
                        float2 counterOffset = float2(
                            -field.layeringOffsetPx.x * 0.42,
                            -field.layeringOffsetPx.y * 0.60);
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
                    HeatFieldSample field,
                    float sceneEdge)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float magnitude = length(offsetPx);
                    float sheet = smoothstep(0.14, 0.86, field.sheet);
                    float coherentHeat = max(sheet, smoothstep(0.22, 0.82, field.band));

                    float baseSoften =
                        smoothstep(3.20, 14.6, magnitude) *
                        field.blur *
                        heat;
                    float latticeSoften =
                        heat *
                        coherentHeat *
                        (field.expansion * 0.24 +
                         field.shear * 0.13);
                    float silhouetteSoften =
                        heat *
                        coherentHeat *
                        sceneEdge *
                        (0.075 + field.blur * 0.17 + field.ground * 0.055);

                    float soften = saturate(
                        baseSoften +
                        latticeSoften +
                        silhouetteSoften);

                    if (soften <= 0.010)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 direction = SafeNormalize(
                        offsetPx +
                        float2(field.shear * 1.15, field.compression * 0.75));
                    float radius = lerp(0.48, 2.95, soften);
                    float2 stepUv = direction * px * radius;

                    float3 blur = center * 0.38;
                    blur += tex2D(_GrabTexture, grabUV + stepUv).rgb * 0.22;
                    blur += tex2D(_GrabTexture, grabUV - stepUv).rgb * 0.22;
                    blur += tex2D(_GrabTexture, grabUV + stepUv * 1.95).rgb * 0.09;
                    blur += tex2D(_GrabTexture, grabUV - stepUv * 1.95).rgb * 0.09;

                    return lerp(center, blur, soften * 0.67);
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
                        (0.050 +
                         field.ground * 0.028 +
                         field.band * 0.015 +
                         field.sheet * 0.030 +
                         field.shear * 0.014) *
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
                        heat * 0.72));
                    float solar = saturate(_DryCycleHeatSolarIntensity * heat);

                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.060, 0.28, luma);
                    float lit = 1.0 - shadow;
                    float midRise = smoothstep(0.095, 0.41, luma);
                    float midFall = 1.0 - smoothstep(0.72, 0.965, luma);
                    float midBand = midRise * midFall;
                    float redMidBand =
                        smoothstep(0.14, 0.40, luma) *
                        (1.0 - smoothstep(0.68, 0.94, luma));
                    float high = smoothstep(0.42, 0.88, luma);
                    float highlightHeadroom = 1.0 - smoothstep(0.76, 0.985, luma);

                    // Hard dry contrast first. The lower pivot keeps the room from
                    // drifting back toward pale gray while deep silhouettes remain black.
                    float contrast = tone * (0.118 + solar * 0.108);
                    color = (color - 0.392) * (1.0 + contrast) + 0.392;
                    color = saturate(color);

                    float gradedLuma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float maxChannel = max(color.r, max(color.g, color.b));
                    float minChannel = min(color.r, min(color.g, color.b));
                    float chroma = maxChannel - minChannel;
                    // Keep strongly saturated emissive colors recognizable. Neutral
                    // concrete, dust and terrain receive the full scorched treatment.
                    float neutralResponse = lerp(
                        0.48,
                        1.0,
                        1.0 - smoothstep(0.26, 0.70, chroma));

                    float bandHeat = saturate((field.band - 0.20) / 0.80);
                    float sheetHeat = smoothstep(0.14, 0.88, field.sheet);
                    float lensHeat = saturate(
                        max(field.focus, 0.0) * 0.56 +
                        field.ground * 0.46);
                    float localHeat = saturate(max(
                        bandHeat * 0.78,
                        max(sheetHeat, lensHeat)));

                    // Broad amber grade: red rises, blue is heavily suppressed, green
                    // is held below red so the result reads as sun-baked ochre rather
                    // than lemon yellow.
                    float3 amberShifted = saturate(
                        color * float3(1.155, 0.965, 0.615) +
                        gradedLuma * float3(0.112, 0.038, 0.000));
                    float amberAmount =
                        tone *
                        (0.300 +
                         solar * 0.165 +
                         localHeat * 0.090) *
                        lit *
                        (0.42 + midBand * 0.58) *
                        neutralResponse;
                    color = lerp(color, amberShifted, saturate(amberAmount));

                    // Scorched ochre lives mainly in the middle values. A slight
                    // luminance pull-down stops the grade from reading as bright cream.
                    float scorchMask =
                        midBand *
                        (1.0 - smoothstep(0.86, 0.99, gradedLuma));
                    float dryDown =
                        tone *
                        (0.022 + localHeat * 0.014) *
                        scorchMask *
                        lit;
                    color *= 1.0 - dryDown;

                    float3 burntOchre = saturate(
                        color * float3(1.125, 0.915, 0.565) +
                        gradedLuma * float3(0.102, 0.028, 0.000));
                    float scorchAmount =
                        tone *
                        (0.135 +
                         solar * 0.100 +
                         localHeat * 0.112) *
                        scorchMask *
                        lit *
                        neutralResponse;
                    color = lerp(color, burntOchre, saturate(scorchAmount));

                    // A restrained red/amber cast in low-mid and mid values gives the
                    // room the dim, baked dusk-red quality of extreme dry desert heat.
                    float3 duskRed = saturate(
                        color * float3(1.100, 0.910, 0.700) +
                        gradedLuma * float3(0.068, 0.010, 0.000));
                    float redAmount =
                        tone *
                        (0.082 +
                         solar * 0.045 +
                         localHeat * 0.090) *
                        redMidBand *
                        lit *
                        neutralResponse;
                    color = lerp(color, duskRed, saturate(redAmount));

                    // Hot highlights become amber-orange, never white. Even near-white
                    // source values retain some warm pull instead of escaping the grade.
                    float3 hotAmber = float3(1.0, 0.715, 0.190);
                    float amberHighlight =
                        high *
                        lit *
                        (tone * 0.122 +
                         solar * 0.205 +
                         localHeat * 0.112) *
                        (0.42 + highlightHeadroom * 0.58) *
                        neutralResponse;
                    color = lerp(color, hotAmber, saturate(amberHighlight));

                    // Strong thermal sheets and ground lenses receive an extra local
                    // burnt-orange response that stays tied to the optical heat field.
                    float3 localScorch = saturate(
                        color * float3(1.070, 0.935, 0.670) +
                        gradedLuma * float3(0.048, 0.012, 0.000));
                    float localScorchAmount =
                        localHeat *
                        tone *
                        (0.082 + solar * 0.060) *
                        lit *
                        neutralResponse;
                    color = lerp(color, localScorch, saturate(localScorchAmount));

                    // Keep the optical breathing subtle so heat bands change hue and
                    // density without washing the scene back toward pale exposure.
                    float exposureBreath =
                        ((field.band - 0.34) * 0.55 +
                         (field.sheet - 0.34) * 0.45) *
                        tone *
                        (0.022 + solar * 0.020) *
                        lit *
                        highlightHeadroom;
                    exposureBreath +=
                        field.focus *
                        tone *
                        0.014 *
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

                    float2 resolvedOffset = field.offsetPx;
                    float coherentHeat = max(field.band, field.sheet);
                    float silhouetteBoost =
                        1.0 +
                        sceneEdge *
                        smoothstep(0.14, 0.84, coherentHeat) *
                        _DryCycleHeatWaveIntensity *
                        0.24;
                    resolvedOffset *= silhouetteBoost;
                    resolvedOffset = ClampMagnitude(resolvedOffset, 15.8);

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
                        float2 v = clamp(resolvedOffset / 15.8, -1.0, 1.0);
                        float magnitude = saturate(length(resolvedOffset) / 15.8);
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

                    if (_DryCycleHeatDebugMode == 7)
                    {
                        float compression = saturate(max(field.compression, 0.0));
                        return float4(
                            compression,
                            saturate(field.expansion),
                            saturate(field.shear),
                            1.0);
                    }

                    float2 pxToUv = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 edgeMargin = pxToUv * 3.2;
                    float2 refractedUv = clamp(
                        grabUV + resolvedOffset * pxToUv,
                        edgeMargin,
                        1.0 - edgeMargin);

                    float3 color = tex2D(_GrabTexture, refractedUv).rgb;
                    color = ApplyThermalCompression(
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
                        field,
                        sceneEdge);
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