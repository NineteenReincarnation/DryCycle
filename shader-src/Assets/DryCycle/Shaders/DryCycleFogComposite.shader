Shader "DryCycle/FogComposite"
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
                sampler2D _NoiseTex;
                sampler2D _NoiseTex2;
                sampler2D _DryCycleFogDensityTex;
                sampler2D _DryCycleFogObstacleTex;
                sampler3D _DryCycleFogNoise3D;

                uniform float4 _spriteRect;
                uniform float4 _camInRoomRect;
                uniform float2 _screenSize;
                uniform float _RAIN;

                uniform float4 _DryCycleFogColor;
                uniform float2 _DryCycleRoomSizePx;
                uniform float _DryCycleFogIntensity;
                uniform float _DryCycleDenseFogIntensity;
                uniform float _DryCycleFogTime;
                uniform float _DryCycleHasFluid;
                uniform float _DryCycleHasNoise3D;

                uniform int _DryCycleFogLightCount;
                uniform float4 _DryCycleFogLights[8];
                uniform float4 _DryCycleFogLightColors[8];

                uniform int _DryCycleAwarenessCount;
                uniform float4 _DryCycleAwareness[4];

                static const float BlastWaveDistanceScale = 1024.0;
                static const float BlastWaveLifetime = 0.62;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float2 uv : TEXCOORD0;
                    float4 screenPos : TEXCOORD1;
                    float4 grabPos : TEXCOORD2;
                    float4 color : COLOR;
                };

                v2f vert(appdata_full v)
                {
                    v2f o;
                    o.pos = UnityObjectToClipPos(v.vertex);
                    o.uv = v.texcoord.xy;
                    o.screenPos = ComputeScreenPos(o.pos);
                    o.grabPos = ComputeGrabScreenPos(o.pos);
                    o.color = v.color;
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
                        return 1.0;

                    float encoded = fmod(
                        max(0.0, level.r * 255.0 - 1.0),
                        30.0) / 30.0;
                    return saturate(encoded * 1.34);
                }

                float4 SampleVolume(float3 p)
                {
                    if (_DryCycleHasNoise3D > 0.5)
                        return tex3D(_DryCycleFogNoise3D, frac(p));

                    float2 aUv = p.xy + float2(p.z * 0.173, p.z * -0.119);
                    float2 bUv = p.yz + float2(p.x * -0.137, p.x * 0.091);
                    float a = tex2D(_NoiseTex, frac(aUv)).r;
                    float b = tex2D(_NoiseTex2, frac(bUv)).g;
                    float c = tex2D(_NoiseTex, frac(p.xz + p.y * 0.161)).r;
                    return float4(
                        saturate(a * 0.62 + b * 0.38),
                        b,
                        saturate(b * 0.48 + c * 0.52),
                        c);
                }

                float4 SampleFogState(float2 roomUV)
                {
                    return tex2D(
                        _DryCycleFogDensityTex,
                        saturate(roomUV));
                }

                float SampleFluid(float2 roomUV)
                {
                    float d = SampleFogState(roomUV).r;
                    return lerp(0.68, d, _DryCycleHasFluid);
                }

                float SampleBlastClear(float2 roomUV)
                {
                    float clear = SampleFogState(roomUV).g;
                    return saturate(clear * _DryCycleHasFluid);
                }

                float2 OffsetRoomUV(float2 roomUV, float2 offsetPx)
                {
                    return saturate(
                        roomUV +
                        offsetPx /
                        max(_DryCycleRoomSizePx, float2(1.0, 1.0)));
                }

                float4 BlastClearTap(float2 roomUV, float2 offsetPx)
                {
                    // The explosion-clear field is an atmospheric screen/room field, not
                    // momentum. It is intentionally sampled through terrain so a blast can
                    // clear mist drawn over a wall and continue onto the other side.
                    return SampleFogState(OffsetRoomUV(roomUV, offsetPx));
                }

                float BlastAirDensityTap(
                    float2 roomUV,
                    float2 offsetPx,
                    float centerDensity,
                    float centerSolid)
                {
                    float2 sampleUv = OffsetRoomUV(roomUV, offsetPx);
                    float2 midUv = OffsetRoomUV(roomUV, offsetPx * 0.5);
                    float sampleSolid = tex2D(
                        _DryCycleFogObstacleTex,
                        sampleUv).r;
                    float midSolid = tex2D(
                        _DryCycleFogObstacleTex,
                        midUv).r;

                    // R is actual fluid mass and therefore still cannot be reconstructed
                    // through a wall. This restriction applies only to R, never to G.
                    float blocked = max(
                        step(0.5, sampleSolid),
                        step(0.5, midSolid));
                    blocked = max(
                        blocked,
                        step(0.40, abs(sampleSolid - centerSolid)));

                    float sampleDensity = SampleFogState(sampleUv).r;
                    return lerp(sampleDensity, centerDensity, blocked);
                }

                float4 EvaluateBlastBoundary(
                    float2 roomUV,
                    float2 worldPx)
                {
                    if (_DryCycleHasFluid <= 0.5)
                        return 0.0;

                    float4 center = SampleFogState(roomUV);
                    float centerSolid = tex2D(
                        _DryCycleFogObstacleTex,
                        saturate(roomUV)).r;
                    float solidMask = step(0.5, centerSolid);

                    // G uses an obstacle-independent reconstruction. This is what makes
                    // wall surfaces and open air share one continuous explosion clearing
                    // shape instead of being cut apart at tile boundaries.
                    float4 gxP = BlastClearTap(roomUV, float2(8.0, 0.0));
                    float4 gxM = BlastClearTap(roomUV, float2(-8.0, 0.0));
                    float4 gyP = BlastClearTap(roomUV, float2(0.0, 8.0));
                    float4 gyM = BlastClearTap(roomUV, float2(0.0, -8.0));

                    float2 clearGradient = float2(
                        gxP.g - gxM.g,
                        gyP.g - gyM.g) * 0.5;
                    float clearGradientLength = length(clearGradient);

                    // R obtains its own terrain-aware normal for actual fluid entrainment.
                    float dxP = BlastAirDensityTap(
                        roomUV, float2(8.0, 0.0), center.r, centerSolid);
                    float dxM = BlastAirDensityTap(
                        roomUV, float2(-8.0, 0.0), center.r, centerSolid);
                    float dyP = BlastAirDensityTap(
                        roomUV, float2(0.0, 8.0), center.r, centerSolid);
                    float dyM = BlastAirDensityTap(
                        roomUV, float2(0.0, -8.0), center.r, centerSolid);

                    float nearDensity =
                        (dxP + dxM + dyP + dyM) * 0.25;
                    float2 densityGradient = float2(
                        dxP - dxM,
                        dyP - dyM) * 0.5;
                    float densityGradientLength = length(densityGradient);

                    float directionNoise = tex2D(
                        _NoiseTex,
                        frac(worldPx / 310.0 + 0.173)).r * 6.2831853;
                    float2 fallbackNormal = float2(
                        cos(directionNoise),
                        sin(directionNoise));

                    float2 preferredGradient = lerp(
                        densityGradient,
                        clearGradient,
                        solidMask);
                    float preferredLength = length(preferredGradient);
                    float2 normal = preferredLength > 0.0005
                        ? preferredGradient / preferredLength
                        : fallbackNormal;
                    float2 tangent = float2(-normal.y, normal.x);

                    // Unblocked G taps at several scales create a smooth wall crossing and
                    // also expose the diffusion-driven recovery of the wall clear field.
                    float4 gnP = BlastClearTap(roomUV, normal * 22.0);
                    float4 gnM = BlastClearTap(roomUV, normal * -22.0);
                    float4 gtP = BlastClearTap(roomUV, tangent * 16.0);
                    float4 gtM = BlastClearTap(roomUV, tangent * -16.0);
                    float4 gwP = BlastClearTap(roomUV, normal * 38.0);
                    float4 gwM = BlastClearTap(roomUV, normal * -38.0);

                    float permissionRaw =
                        center.g * 0.22 +
                        (gxP.g + gxM.g + gyP.g + gyM.g) * 0.09 +
                        (gnP.g + gnM.g + gtP.g + gtM.g) * 0.07 +
                        (gwP.g + gwM.g) * 0.07;
                    permissionRaw = saturate(permissionRaw);

                    // Actual air density is reconstructed only along unobstructed paths.
                    float dnP = BlastAirDensityTap(
                        roomUV, normal * 22.0, center.r, centerSolid);
                    float dnM = BlastAirDensityTap(
                        roomUV, normal * -22.0, center.r, centerSolid);
                    float dtP = BlastAirDensityTap(
                        roomUV, tangent * 16.0, center.r, centerSolid);
                    float dtM = BlastAirDensityTap(
                        roomUV, tangent * -16.0, center.r, centerSolid);

                    float normalMean = (dnP + dnM) * 0.5;
                    float tangentMean = (dtP + dtM) * 0.5;
                    float farDensity =
                        normalMean * 0.66 +
                        tangentMean * 0.34;
                    float reconstructedDensity =
                        center.r * 0.44 +
                        nearDensity * 0.34 +
                        farDensity * 0.22;

                    float broadNoise = tex2D(
                        _NoiseTex,
                        frac(worldPx / 250.0 +
                        float2(
                            _DryCycleFogTime * 0.0014,
                            _DryCycleFogTime * -0.0007))).r;
                    float midNoise = tex2D(
                        _NoiseTex2,
                        frac(worldPx.yx / 118.0 +
                        float2(
                            _DryCycleFogTime * -0.0018,
                            _DryCycleFogTime * 0.0011) + 0.37)).g;

                    float densityContrast = saturate(
                        densityGradientLength * 3.0 +
                        abs(farDensity - center.r) * 1.75 +
                        abs(normalMean - tangentMean) * 0.85);
                    float clearContrast = saturate(
                        clearGradientLength * 4.6 +
                        abs((gnP.g + gnM.g) * 0.5 - center.g) * 1.8 +
                        abs(gwP.g - gwM.g) * 0.55);
                    float edgeContrast = lerp(
                        densityContrast,
                        clearContrast,
                        solidMask);

                    reconstructedDensity +=
                        ((broadNoise - 0.5) * 0.11 +
                         (midNoise - 0.5) * 0.045) *
                        lerp(0.18, 1.0, edgeContrast);

                    float permission = smoothstep(
                        0.025,
                        0.20,
                        permissionRaw);
                    float coreDeficit = 1.0 - smoothstep(
                        0.07,
                        0.43,
                        center.r);
                    float bodyDeficit = 1.0 - smoothstep(
                        0.13,
                        0.79,
                        reconstructedDensity);
                    float airCavity = permission * saturate(max(
                        bodyDeficit,
                        coreDeficit * 0.94));

                    // Terrain has R=0 by definition, so using R there would incorrectly
                    // turn every cleared wall into a permanent hard vacuum edge. Wall fog
                    // instead follows the diffusing G field itself. The sub-linear power
                    // preserves a clear centre while giving the perimeter a wide shoulder.
                    float surfaceCavity = pow(
                        permissionRaw,
                        0.72);
                    float cavity = lerp(
                        airCavity,
                        surfaceCavity,
                        solidMask);

                    // Slow, broad modulation affects only the transition zone. On walls it
                    // follows the G gradient, so as neighbour diffusion erodes the clear
                    // patch the rendered fog visibly spreads inward/outward instead of
                    // fading as a static polygon.
                    float boundaryNoise = lerp(
                        0.88,
                        1.12,
                        broadNoise * 0.68 + midNoise * 0.32);
                    float transitionBand = saturate(
                        1.0 - abs(cavity * 2.0 - 1.0));
                    transitionBand = Smooth01(transitionBand);
                    float mixingLayer = saturate(
                        transitionBand *
                        (0.42 + edgeContrast * 0.96) *
                        boundaryNoise *
                        lerp(permission, saturate(permissionRaw * 1.35), solidMask));

                    return float4(
                        saturate(cavity),
                        mixingLayer,
                        saturate(permissionRaw),
                        saturate(reconstructedDensity));
                }

                float BlastWaveRadiusPx(float ageSeconds)
                {
                    float age = max(0.0, ageSeconds);
                    return (1250.0 * age) / (1.0 + age * 0.85);
                }

                float SampleBlastWaveReveal(float2 roomUV)
                {
                    if (_DryCycleHasFluid <= 0.5)
                        return 0.0;

                    float4 state = SampleFogState(roomUV);
                    float waveLife = state.a;
                    if (waveLife <= 0.0001)
                        return 0.0;

                    float waveAge = max(
                        0.0,
                        BlastWaveLifetime - waveLife);
                    float waveRadiusPx = BlastWaveRadiusPx(waveAge);
                    float waveDistancePx =
                        state.b * BlastWaveDistanceScale;

                    float frontWidth = lerp(
                        18.0,
                        34.0,
                        saturate(waveAge / 0.42));
                    float front = 1.0 - smoothstep(
                        waveRadiusPx - frontWidth,
                        waveRadiusPx + frontWidth * 1.12,
                        waveDistancePx);

                    float handoff = smoothstep(
                        0.02,
                        0.14,
                        waveLife);
                    return saturate(front * handoff);
                }

                float2 FluidGradient(float2 roomUV)
                {
                    float2 texel24 = 24.0 /
                        max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                    float l = SampleFluid(roomUV - float2(texel24.x, 0.0));
                    float r = SampleFluid(roomUV + float2(texel24.x, 0.0));
                    float b = SampleFluid(roomUV - float2(0.0, texel24.y));
                    float t = SampleFluid(roomUV + float2(0.0, texel24.y));
                    return float2(r - l, t - b);
                }

                float2 DomainWarpPx(
                    float2 roomUV,
                    float2 worldPx,
                    float time)
                {
                    float2 p = worldPx / 720.0;

                    float wx = tex2D(
                        _NoiseTex,
                        p * float2(0.81, 1.19) +
                        float2(time * 0.0037, time * -0.0015)).r;
                    float wy = tex2D(
                        _NoiseTex,
                        p.yx * float2(1.27, 0.73) +
                        float2(time * -0.0023, time * 0.0030) + 0.37).r;
                    float2 warp = float2(wx, wy) * 2.0 - 1.0;

                    float wx2 = tex2D(
                        _NoiseTex2,
                        p * 0.41 + float2(time * -0.0010, 0.19)).g;
                    float wy2 = tex2D(
                        _NoiseTex2,
                        p.yx * 0.46 + float2(0.61, time * 0.0013)).b;
                    warp += (float2(wx2, wy2) * 2.0 - 1.0) * 0.31;

                    float2 gradient = FluidGradient(roomUV);
                    float gradientLength = length(gradient);
                    if (gradientLength > 0.0001)
                    {
                        float2 tangent =
                            float2(-gradient.y, gradient.x) / gradientLength;
                        warp += tangent *
                            saturate(gradientLength * 5.0) * 0.72;
                    }

                    return warp * 102.0;
                }

                float2 VisualFogDensity(
                    float2 roomUV,
                    float2 worldPx,
                    float pseudoDepth,
                    float jitter)
                {
                    const int Steps = 24;
                    float time = _DryCycleFogTime;
                    float2 warpPx = DomainWarpPx(roomUV, worldPx, time);
                    float accumulated = 0.0;
                    float accumulatedSq = 0.0;

                    float zExtent = lerp(0.48, 1.0, pseudoDepth);

                    [loop]
                    for (int s = 0; s < Steps; s++)
                    {
                        float z01 = (s + jitter) / (float)Steps;
                        float z = z01 * zExtent;
                        float depthParallax =
                            (z - 0.5 * zExtent) *
                            lerp(42.0, 104.0, pseudoDepth);

                        float2 sampleWorld = worldPx;
                        sampleWorld += warpPx * lerp(0.24, 1.08, z01);
                        sampleWorld += float2(
                            depthParallax * 0.43,
                            depthParallax * -0.16);

                        float2 sampleRoomUv = sampleWorld /
                            max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                        float fluid = SampleFluid(sampleRoomUv);
                        float blastClear = SampleBlastClear(sampleRoomUv);

                        float3 macroCoord = float3(
                            sampleWorld / 500.0 +
                            float2(time * 0.0025, time * -0.0008),
                            z * 0.83 + time * 0.0012);
                        float4 macro = SampleVolume(macroCoord);

                        float3 midCoord = float3(
                            sampleWorld / 176.0 +
                            float2(time * -0.0053, time * 0.0021),
                            z * 2.19 - time * 0.0027);
                        float4 mid = SampleVolume(
                            midCoord + macro.gbr * 0.23);

                        float3 fineCoord = float3(
                            sampleWorld / 71.0 +
                            float2(time * 0.0091, time * 0.0037),
                            z * 4.73 + time * 0.0041);
                        float4 fine = SampleVolume(
                            fineCoord +
                            mid.brg * 0.17 +
                            macro.arg * 0.09);

                        float macroBody = saturate(
                            macro.r * 1.36 -
                            (1.0 - mid.g) * 0.31 +
                            0.035);
                        macroBody = pow(
                            macroBody,
                            lerp(1.55, 0.76, macro.g));

                        float midBody = saturate(
                            mid.r * 1.18 -
                            (1.0 - fine.b) * 0.29 +
                            0.02);
                        float wisps =
                            smoothstep(0.36, 0.79, fine.r) *
                            lerp(0.72, 1.18, fine.a);

                        float body =
                            macroBody * 0.68 +
                            midBody * 0.24 +
                            wisps * 0.08;
                        body *= lerp(0.48, 1.30, fluid);

                        float densityDeficit = 1.0 - smoothstep(
                            0.10,
                            0.78,
                            fluid);
                        float blastPermission = smoothstep(
                            0.025,
                            0.22,
                            blastClear);
                        float blastMediumClear =
                            blastPermission *
                            pow(saturate(densityDeficit), 0.86);
                        body *= lerp(
                            1.0,
                            0.08,
                            blastMediumClear);

                        float slab = 0.76 +
                            0.24 * sin(saturate(z01) * 3.14159265);
                        body *= slab;

                        accumulated += body;
                        accumulatedSq += body * body;
                    }

                    float density = accumulated / (float)Steps;
                    float secondMoment = accumulatedSq / (float)Steps;
                    float variance = max(
                        0.0,
                        secondMoment - density * density);

                    float wallDistance = tex2D(
                        _DryCycleFogObstacleTex,
                        saturate(roomUV)).g;
                    float surfaceBlastClear = SampleBlastClear(roomUV);
                    float surfaceFluid = SampleFluid(roomUV);
                    float surfaceDeficit = 1.0 - smoothstep(
                        0.10,
                        0.78,
                        surfaceFluid);
                    float surfacePermission = pow(
                        saturate(surfaceBlastClear),
                        0.78);
                    float surfaceClear =
                        surfacePermission * surfaceDeficit;
                    density += pow(
                        saturate(1.0 - wallDistance),
                        2.1) * 0.065 *
                        lerp(1.0, 0.08, surfaceClear);

                    return float2(
                        saturate(density * 1.48),
                        saturate(variance * 7.0));
                }

                float ObstacleAtWorld(float2 worldPx)
                {
                    float2 uv = worldPx /
                        max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                    if (uv.x <= 0.0 || uv.y <= 0.0 ||
                        uv.x >= 1.0 || uv.y >= 1.0)
                    {
                        return 1.0;
                    }
                    return tex2D(_DryCycleFogObstacleTex, uv).r;
                }

                float SegmentVisibility(float2 fromPx, float2 toPx)
                {
                    const int OcclusionSteps = 24;
                    float blocked = 0.0;

                    [loop]
                    for (int i = 1; i <= OcclusionSteps; i++)
                    {
                        float t = i / (float)(OcclusionSteps + 1);
                        float2 p = lerp(fromPx, toPx, t);
                        blocked = max(
                            blocked,
                            step(0.5, ObstacleAtWorld(p)));
                        if (blocked > 0.5)
                            break;
                    }
                    return 1.0 - blocked;
                }

                float FogLightRayTransmittance(
                    float2 fromPx,
                    float2 toPx,
                    float targetSolid)
                {
                    const int FogOcclusionSteps = 20;
                    float rayLength = max(1.0, distance(fromPx, toPx));
                    float opticalDepth = 0.0;

                    [loop]
                    for (int i = 1; i <= FogOcclusionSteps; i++)
                    {
                        float t = i / (float)(FogOcclusionSteps + 1);
                        float2 p = lerp(fromPx, toPx, t);
                        float solid = smoothstep(
                            0.18,
                            0.82,
                            ObstacleAtWorld(p));

                        if (targetSolid > 0.001)
                        {
                            float remainingPx = rayLength * (1.0 - t);
                            float receiverWeight = smoothstep(
                                4.0,
                                18.0,
                                remainingPx);
                            solid *= lerp(
                                1.0,
                                receiverWeight,
                                targetSolid);
                        }

                        opticalDepth += solid;
                    }

                    return exp(-opticalDepth * 1.55);
                }

                float FogLightSoftVisibility(
                    float2 fromPx,
                    float2 toPx,
                    float radius)
                {
                    float2 delta = toPx - fromPx;
                    float rayLength = max(1.0, length(delta));
                    float2 normal = float2(-delta.y, delta.x) / rayLength;
                    float targetSolid = smoothstep(
                        0.25,
                        0.75,
                        ObstacleAtWorld(toPx));

                    float penumbra = lerp(
                        4.0,
                        18.0,
                        saturate(rayLength / max(1.0, radius)));

                    float visibility =
                        FogLightRayTransmittance(
                            fromPx,
                            toPx,
                            targetSolid) * 0.36;
                    visibility += FogLightRayTransmittance(
                        fromPx + normal * (penumbra * 0.50),
                        toPx,
                        targetSolid) * 0.22;
                    visibility += FogLightRayTransmittance(
                        fromPx - normal * (penumbra * 0.50),
                        toPx,
                        targetSolid) * 0.22;
                    visibility += FogLightRayTransmittance(
                        fromPx + normal * penumbra,
                        toPx,
                        targetSolid) * 0.10;
                    visibility += FogLightRayTransmittance(
                        fromPx - normal * penumbra,
                        toPx,
                        targetSolid) * 0.10;

                    return saturate(visibility);
                }

                void EvaluateFogLights(
                    float2 worldPx,
                    float visualDensity,
                    out float reveal,
                    out float3 coloredScattering)
                {
                    reveal = 0.0;
                    coloredScattering = float3(0.0, 0.0, 0.0);

                    [loop]
                    for (int i = 0; i < 8; i++)
                    {
                        if (i >= _DryCycleFogLightCount)
                            break;

                        float4 light = _DryCycleFogLights[i];
                        float radius = max(1.0, light.z);
                        float distancePx = distance(worldPx, light.xy);
                        if (distancePx >= radius || light.w <= 0.0001)
                            continue;

                        float edgeNoise = tex2D(
                            _NoiseTex,
                            frac(worldPx / 260.0 + light.xy / 1900.0)).r;
                        float normalizedDistance =
                            distancePx /
                            (radius * lerp(0.97, 1.03, edgeNoise));
                        float radial = 1.0 - smoothstep(
                            0.16,
                            1.0,
                            normalizedDistance);
                        radial = pow(saturate(radial), 1.12);

                        float visible = FogLightSoftVisibility(
                            light.xy,
                            worldPx,
                            radius);
                        float localReveal = saturate(
                            radial * light.w * visible);

                        reveal = 1.0 -
                            (1.0 - reveal) *
                            (1.0 - localReveal);

                        float fogIllumination =
                            radial * light.w *
                            lerp(0.36, 1.0, visible);
                        float halo = fogIllumination *
                            lerp(0.08, 0.50, visualDensity) *
                            (0.55 + radial * 0.45);
                        coloredScattering +=
                            _DryCycleFogLightColors[i].rgb * halo;
                    }
                }

                float EvaluateAwareness(float2 worldPx)
                {
                    float reveal = 0.0;
                    [loop]
                    for (int i = 0; i < 4; i++)
                    {
                        if (i >= _DryCycleAwarenessCount)
                            break;

                        float4 awareness = _DryCycleAwareness[i];
                        float distancePx = distance(
                            worldPx,
                            awareness.xy);
                        if (distancePx >= awareness.z)
                            continue;

                        float radial = Smooth01(
                            1.0 - distancePx /
                            max(1.0, awareness.z));
                        float visible = SegmentVisibility(
                            awareness.xy,
                            worldPx);
                        float localReveal =
                            radial * awareness.w * visible;
                        reveal = 1.0 -
                            (1.0 - reveal) *
                            (1.0 - localReveal);
                    }
                    return saturate(reveal);
                }

                half4 frag(v2f i) : SV_Target
                {
                    float2 screenUV =
                        i.screenPos.xy /
                        max(i.screenPos.w, 0.0001);
                    float2 roomUV = RoomUV(screenUV);
                    float2 worldPx =
                        roomUV * _DryCycleRoomSizePx;
                    float pseudoDepth =
                        DecodePseudoDepth(LevelUV(screenUV));

                    float4 scene = tex2Dproj(
                        _GrabTexture,
                        UNITY_PROJ_COORD(i.grabPos));

                    float fog = saturate(_DryCycleFogIntensity);
                    float dense = saturate(_DryCycleDenseFogIntensity);
                    float presence = saturate(max(fog, dense));
                    if (presence <= 0.0001)
                        return scene;

                    float2 jitterUv = frac(
                        screenUV * (_screenSize / 256.0) +
                        float2(
                            frac(_DryCycleFogTime * 0.0137),
                            frac(_DryCycleFogTime * 0.0089)));
                    float jitter = tex2D(_NoiseTex2, jitterUv).r;

                    float2 densityData = VisualFogDensity(
                        roomUV,
                        worldPx,
                        pseudoDepth,
                        jitter);
                    float visualDensity = densityData.x;
                    float turbulence = densityData.y;

                    float4 blastBoundary = EvaluateBlastBoundary(
                        roomUV,
                        worldPx);
                    float physicalBlastClear = blastBoundary.x;
                    float edgeMixing = blastBoundary.y;

                    visualDensity = saturate(
                        visualDensity +
                        edgeMixing * (0.10 + dense * 0.055));
                    turbulence = saturate(
                        turbulence + edgeMixing * 0.30);

                    float depthFactor = lerp(
                        0.84,
                        1.31,
                        pseudoDepth);
                    float extinction =
                        fog * 1.08 +
                        dense * 6.32;
                    float blastExtinctionScale = exp(
                        -3.90 * physicalBlastClear);
                    float baseTransmittance = exp(
                        -extinction * blastExtinctionScale * depthFactor);

                    float denseClear = pow(
                        saturate(physicalBlastClear),
                        1.05);
                    float effectiveDense =
                        dense * (1.0 - denseClear);
                    float denseCeiling = lerp(
                        1.0,
                        0.0020,
                        effectiveDense);
                    baseTransmittance = min(
                        baseTransmittance,
                        denseCeiling);

                    float lightReveal;
                    float3 lightScattering;
                    EvaluateFogLights(
                        worldPx,
                        visualDensity,
                        lightReveal,
                        lightScattering);
                    float awarenessReveal =
                        EvaluateAwareness(worldPx);

                    float awarenessTarget = max(
                        baseTransmittance,
                        lerp(0.075, 0.145, dense));
                    float transmittance = lerp(
                        baseTransmittance,
                        awarenessTarget,
                        awarenessReveal);

                    float blastReveal = pow(
                        saturate(physicalBlastClear),
                        1.10);
                    // B/A only drive the R/G evacuation front in compute. They are not
                    // rendered as a second reveal layer, which avoids a visible circular
                    // pressure-wave line while preserving the expanding clear cavity.
                    float physicalReveal = 1.0 -
                        (1.0 - lightReveal) *
                        (1.0 - blastReveal);
                    transmittance = lerp(
                        transmittance,
                        0.985,
                        physicalReveal);

                    float body = smoothstep(
                        0.13,
                        0.90,
                        visualDensity);
                    float shade = lerp(
                        0.69,
                        1.11,
                        body);
                    shade *= lerp(
                        0.95,
                        1.07,
                        turbulence);

                    float3 fogColor =
                        _DryCycleFogColor.rgb * shade;
                    float3 coolValley =
                        _DryCycleFogColor.rgb *
                        float3(0.91, 0.94, 0.98);
                    fogColor = lerp(
                        coolValley,
                        fogColor,
                        smoothstep(0.20, 0.72, visualDensity));

                    float3 scattering = fogColor;
                    scattering += lightScattering *
                        (0.46 + dense * 0.25);

                    scattering *= 1.0 +
                        visualDensity *
                        (0.025 + dense * 0.025) +
                        edgeMixing * 0.075;

                    float3 finalColor =
                        scene.rgb * transmittance +
                        scattering * (1.0 - transmittance);
                    return float4(finalColor, 1.0);
                }
                ENDCG
            }
        }
    }
}