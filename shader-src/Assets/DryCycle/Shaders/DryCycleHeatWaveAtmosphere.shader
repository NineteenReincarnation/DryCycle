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
                    // Two half-cycle-shifted advected samples crossfade around their
                    // reset points. This prevents the familiar endlessly scrolling
                    // normal-map look and makes the air evolve in place.
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

                        // Macro strata are long, slow optical lenses. Fine strata ride
                        // inside them and break up their edges. Keeping both strongly
                        // anisotropic is what separates heat haze from water ripples.
                        float2 macroUv = roomPx / float2(980.0, 252.0);
                        float2 macroTravel = flow * 0.102;
                        float4 macroA = tex2D(
                            _DryCycleHeatMirageField,
                            frac(macroUv - macroTravel * p0));
                        float4 macroB = tex2D(
                            _DryCycleHeatMirageField,
                            frac(macroUv - macroTravel * p1 + 0.371));
                        float4 macro = lerp(macroA, macroB, blend);

                        float finePhase = frac(phase * 1.371 + _DryCycleHeatTime * 0.031 + 0.173);
                        float finePhaseB = frac(finePhase + 0.5);
                        float fineBlend = PhaseBlend(finePhase);
                        float2 fineUv = roomPx / float2(420.0, 116.0);
                        float2 fineTravel = flow * 0.158;
                        float4 fineA = tex2D(
                            _DryCycleHeatMirageField,
                            frac(fineUv - fineTravel * finePhase + 0.219));
                        float4 fineB = tex2D(
                            _DryCycleHeatMirageField,
                            frac(fineUv - fineTravel * finePhaseB + 0.641));
                        float4 fine = lerp(fineA, fineB, fineBlend);

                        float macroStretch = macro.g * 2.0 - 1.0;
                        float fineStretch = fine.g * 2.0 - 1.0;
                        float band = saturate(macro.r * 0.70 + fine.r * 0.42 - 0.05);
                        float stretch = clamp(
                            macroStretch * 0.72 + fineStretch * 0.42,
                            -1.0,
                            1.0);
                        float blur = saturate(max(macro.b * 0.78, fine.b * 0.68));
                        float mixedPhase = frac(macro.a * 0.63 + fine.a * 0.37);
                        return float4(band, stretch * 0.5 + 0.5, blur, mixedPhase);
                    }

                    float broad = sin(
                        (roomPx.y / 88.0 + roomPx.x / 1060.0 - _DryCycleHeatTime * 0.075) *
                        6.2831853);
                    float fineWave = sin(
                        (roomPx.y / 41.0 - roomPx.x / 680.0 - _DryCycleHeatTime * 0.121) *
                        6.2831853);
                    float band = smoothstep(0.04, 0.82, broad * 0.5 + 0.5);
                    float stretch = clamp(broad * 0.72 + fineWave * 0.28, -1.0, 1.0);
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
                    return float2(r - l, u - d) * 8.15;
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
                        _DryCycleHeatTime * 0.67 +
                        phaseSeed * 6.2831853 +
                        roomPx.x / 1160.0 * 6.2831853);
                    float b = sin(
                        _DryCycleHeatTime * 0.29 +
                        phaseSeed * 11.173 -
                        roomPx.y / 970.0 * 6.2831853 +
                        strength * 2.3);
                    return clamp(0.88 + a * 0.10 + b * 0.08, 0.70, 1.06);
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

                    float band = saturate(
                        strength * 0.52 +
                        mirageBand * 0.61 +
                        ground * mirageBand * 0.15);
                    float body = smoothstep(0.18, 0.84, band);
                    float surfaceLift = 1.0 + surface * 0.16;

                    // Three optical scales coexist: broad refractive bodies, small
                    // turbulent detail and scalar-density boundaries. Vertical bending
                    // is intentionally dominant so the effect reads as rising hot air.
                    float2 baseOffset = float2(
                        baseNormal.x * 1.78,
                        baseNormal.y * 4.10) *
                        lerp(0.36, 1.05, body) *
                        evolution *
                        surfaceLift;

                    float2 detailOffset = float2(
                        detailNormal.x * 0.78,
                        detailNormal.y * 1.62) *
                        lerp(0.44, 1.0, body) *
                        lerp(0.92, 1.08, evolution);

                    float2 gradientOffset = float2(
                        densityGradient.x * 1.34,
                        densityGradient.y * 3.08) *
                        smoothstep(0.16, 0.90, strength) *
                        lerp(0.90, 1.10, evolution);

                    // Mirage is a dedicated vertical compression/stretch layer, not a
                    // generic 2D normal map. Long strata can therefore fold silhouettes
                    // without making the room sway like it is underwater.
                    float mirageY =
                        mirageStretch *
                        lerp(1.65, 7.55, mirageBand) *
                        lerp(0.34, 1.0, body) *
                        evolution;

                    // Terrain proximity contributes a separate ground-hugging lens. It
                    // is strongest directly above floors/ledges and rapidly disappears
                    // with height, reproducing the dense inferior-mirage zone seen over
                    // sun-baked ground without hard-coding any particular room.
                    float groundBand = saturate(
                        mirageBand * 0.68 +
                        strength * 0.32);
                    float groundStrength =
                        ground *
                        smoothstep(0.08, 0.86, heat) *
                        lerp(0.72, 1.08, evolution);
                    float groundY =
                        (mirageStretch * 0.78 +
                         baseNormal.y * 0.17 +
                         detailNormal.y * 0.10) *
                        groundStrength *
                        lerp(2.10, 7.20, groundBand);
                    float groundX =
                        (baseNormal.x * 0.58 +
                         detailNormal.x * 0.31 +
                         densityGradient.x * 0.035) *
                        groundStrength *
                        lerp(0.48, 2.10, groundBand);

                    float2 offset =
                        baseOffset +
                        detailOffset +
                        gradientOffset +
                        float2(0.0, mirageY) +
                        float2(groundX, groundY);

                    offset *= heat;
                    offset = ClampMagnitude(offset, 12.0);

                    // Refraction gradients do more than move pixels: converging and
                    // diverging lenses also create narrow bright/dark compression bands.
                    // This focus term drives that modulation later without adding a
                    // water-like caustic texture.
                    float focusRaw =
                        mirageStretch * 0.64 -
                        densityGradient.y * 0.050 +
                        baseNormal.y * 0.13 +
                        detailNormal.y * 0.045;
                    float focus = clamp(focusRaw, -1.0, 1.0) *
                        heat *
                        lerp(0.34, 1.0, body) *
                        lerp(0.88, 1.08, evolution) *
                        lerp(0.92, 1.14, ground);

                    // A small secondary refraction vector produces local silhouette
                    // doubling/compression in the hottest bands. It is not a screen-wide
                    // ghost layer and is strongest in ground mirage.
                    float2 layeringOffset = float2(
                        -offset.x * 0.18 + detailNormal.x * 0.92,
                        mirageStretch * lerp(1.25, 4.65, saturate(abs(focus))) +
                        ground * baseNormal.y * 2.35);
                    layeringOffset *= heat * lerp(0.58, 1.0, body);
                    layeringOffset = ClampMagnitude(layeringOffset, 5.5);

                    result.band = band;
                    result.mirage = mirageBand;
                    result.blur = saturate(
                        mirageData.b * 0.61 +
                        body * 0.21 +
                        ground * 0.24);
                    result.focus = focus;
                    result.ground = ground;
                    result.surface = surface;
                    result.dryAir = dryAir;
                    result.evolution = evolution;
                    result.flow = flow;
                    result.offsetPx = offset;
                    result.layeringOffsetPx = layeringOffset;
                    return result;
                }

                float3 ApplyMirageLayering(
                    float3 center,
                    float2 grabUV,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float body = smoothstep(0.46, 0.92, field.band);
                    float focusMagnitude = saturate(abs(field.focus));
                    float layerAmount =
                        heat *
                        body *
                        (0.045 +
                         field.ground * 0.105 +
                         focusMagnitude * 0.075);

                    if (layerAmount <= 0.008)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 firstUv = grabUV + field.layeringOffsetPx * px;
                    float3 first = tex2D(_GrabTexture, firstUv).rgb;
                    float3 layered = lerp(center, first, saturate(layerAmount));

                    // Ground mirage gets one weak counter-sample. This creates the
                    // compressed/doubled contour associated with very hot air while the
                    // low blend prevents a visible double-exposure filter.
                    float counterAmount =
                        heat *
                        field.ground *
                        body *
                        focusMagnitude *
                        0.055;
                    if (counterAmount > 0.006)
                    {
                        float2 counterOffset = float2(
                            -field.layeringOffsetPx.x * 0.46,
                            -field.layeringOffsetPx.y * 0.58);
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
                    float blurMask)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float magnitude = length(offsetPx);

                    // Keep moderate shimmer crisp so silhouettes visibly bend. Blur only
                    // the strongest refractive streaks, where real hot air smears detail.
                    float soften =
                        smoothstep(3.15, 11.6, magnitude) *
                        blurMask *
                        heat;

                    if (soften <= 0.012)
                        return center;

                    float2 px = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 direction = SafeNormalize(offsetPx);
                    float radius = lerp(0.55, 2.20, soften);
                    float2 stepUv = direction * px * radius;

                    float3 blur = center * 0.40;
                    blur += tex2D(_GrabTexture, grabUV + stepUv).rgb * 0.22;
                    blur += tex2D(_GrabTexture, grabUV - stepUv).rgb * 0.22;
                    blur += tex2D(_GrabTexture, grabUV + stepUv * 1.95).rgb * 0.08;
                    blur += tex2D(_GrabTexture, grabUV - stepUv * 1.95).rgb * 0.08;

                    return lerp(center, blur, soften * 0.62);
                }

                float3 ApplyOpticalFocus(
                    float3 color,
                    HeatFieldSample field)
                {
                    float heat = saturate(_DryCycleHeatWaveIntensity);
                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadowGuard = smoothstep(0.055, 0.24, luma);
                    float highlightHeadroom = 1.0 - smoothstep(0.84, 0.985, luma);
                    float focusGain =
                        field.focus *
                        heat *
                        (0.040 + field.ground * 0.025 + field.band * 0.018) *
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

                    // HeatWave owns a room-wide dry-hot color state even away from direct
                    // sunlight. Solar exposure strengthens it but does not gate it.
                    float tone = saturate(max(
                        _DryCycleHeatToneAmount,
                        heat * 0.62));
                    float solar = saturate(_DryCycleHeatSolarIntensity * heat);

                    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
                    float shadow = 1.0 - smoothstep(0.075, 0.30, luma);
                    float lit = 1.0 - shadow;
                    float midRise = smoothstep(0.12, 0.48, luma);
                    float midFall = 1.0 - smoothstep(0.72, 0.96, luma);
                    float midBand = midRise * midFall;
                    float high = smoothstep(0.46, 0.90, luma);
                    float highlightHeadroom = 1.0 - smoothstep(0.78, 0.98, luma);

                    // Dry desert sun increases separation between deep graphic shadows
                    // and exposed surfaces. The heat state should feel harsh, not foggy.
                    float contrast = tone * (0.078 + solar * 0.098);
                    color = (color - 0.425) * (1.0 + contrast) + 0.425;
                    color = saturate(color);

                    // Midtones carry the strongest desert-yellow identity. Rather than
                    // overlaying a yellow sheet, preserve the Rain World palette and bend
                    // exposed values toward a sand/sun spectrum according to luminance.
                    float3 yellowShifted = saturate(
                        color * float3(1.092, 1.039, 0.775) +
                        luma * float3(0.086, 0.056, 0.000));
                    float yellowAmount =
                        tone *
                        (0.255 +
                         solar * 0.235 +
                         field.band * 0.090 +
                         field.ground * 0.040) *
                        midBand *
                        lit;
                    color = lerp(color, yellowShifted, saturate(yellowAmount));

                    // High values dry toward hot yellow instead of being bleached toward
                    // white. Naturally white source art can remain white, but HeatWave
                    // never uses white as its target color.
                    float dryLuma = dot(color, float3(0.255, 0.685, 0.060));
                    float3 dryYellow = dryLuma * float3(1.15, 1.045, 0.70);
                    float dryAmount =
                        tone *
                        (0.060 + solar * 0.115 + field.band * 0.045) *
                        high *
                        lit;
                    color = lerp(color, dryYellow, saturate(dryAmount));

                    float3 hotYellow = float3(1.0, 0.855, 0.455);
                    float yellowHighlight =
                        high *
                        lit *
                        (tone * 0.135 +
                         solar * 0.295 +
                         solar * field.band * 0.120 +
                         field.ground * tone * 0.030);
                    color = lerp(color, hotYellow, saturate(yellowHighlight));

                    // Optical focus and heat-band motion share the same color response.
                    // Converging bands warm slightly; diverging bands can darken a little.
                    // The gain is deliberately small so this reads as lens compression,
                    // not a painted animated caustic texture.
                    float bandHeat = saturate((field.band - 0.27) / 0.73);
                    float3 bandYellow = saturate(
                        color * float3(1.050, 1.018, 0.885) +
                        luma * float3(0.030, 0.018, 0.0));
                    float bandColorAmount =
                        bandHeat *
                        tone *
                        (0.065 + solar * 0.070 + max(field.focus, 0.0) * 0.030) *
                        lit;
                    color = lerp(color, bandYellow, saturate(bandColorAmount));

                    float exposureBreath =
                        (field.band - 0.33) *
                        tone *
                        (0.048 + solar * 0.052) *
                        lit *
                        highlightHeadroom;
                    exposureBreath +=
                        field.focus *
                        tone *
                        0.020 *
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

                    // A second field lookup only happens inside coherent hot bodies. It
                    // approximates a short curved optical path: the ray is bent once,
                    // then samples the field again where that bend carried it.
                    float2 resolvedOffset = field.offsetPx;
                    if (field.band > 0.46 && _DryCycleHeatWaveIntensity > 0.18)
                    {
                        HeatFieldSample nextField = EvaluateHeatField(
                            roomPx + field.offsetPx * 2.10 + field.flow * 2.4);
                        float pathBlend = smoothstep(0.46, 0.94, field.band);
                        resolvedOffset = lerp(
                            field.offsetPx,
                            field.offsetPx * 0.54 + nextField.offsetPx * 0.46,
                            pathBlend);
                        field.blur = max(
                            field.blur,
                            nextField.blur * pathBlend);
                        field.focus = lerp(
                            field.focus,
                            (field.focus + nextField.focus) * 0.5,
                            pathBlend * 0.72);
                        field.layeringOffsetPx = lerp(
                            field.layeringOffsetPx,
                            nextField.layeringOffsetPx,
                            pathBlend * 0.55);
                    }
                    resolvedOffset = ClampMagnitude(resolvedOffset, 12.0);

                    if (_DryCycleHeatDebugMode == 1)
                    {
                        return float4(
                            saturate(field.band),
                            saturate(field.mirage * 0.72 + field.band * 0.28),
                            saturate(field.ground),
                            1.0);
                    }

                    if (_DryCycleHeatDebugMode == 2)
                    {
                        float2 v = clamp(resolvedOffset / 12.0, -1.0, 1.0);
                        float magnitude = saturate(length(resolvedOffset) / 12.0);
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
                            saturate(abs(field.focus)),
                            saturate(field.ground * 0.85 + field.band * 0.15),
                            1.0);
                    }

                    float2 pxToUv = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 edgeMargin = pxToUv * 2.5;
                    float2 refractedUv = clamp(
                        grabUV + resolvedOffset * pxToUv,
                        edgeMargin,
                        1.0 - edgeMargin);

                    float3 color = tex2D(_GrabTexture, refractedUv).rgb;
                    color = ApplyMirageLayering(color, refractedUv, field);
                    color = ApplyDirectionalSoftening(
                        color,
                        refractedUv,
                        resolvedOffset,
                        field.blur);
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
