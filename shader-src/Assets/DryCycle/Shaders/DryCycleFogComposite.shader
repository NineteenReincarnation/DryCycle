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
            // Anonymous GrabPass is intentional. A named grab can be reused by later
            // RoomCamera composites in the same frame; split-screen/Jolly cameras need
            // their own current scene capture. ComputeGrabScreenPos handles Unity's
            // inverted render-texture projection correctly.
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
                    // Rain World publishes camera origin and visible span normalized to
                    // the current room. Using this instead of screen-space UV anchors fog
                    // in the world when the camera pans or changes camera position.
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

                    // Compatibility path when compute/3D textures are unavailable.
                    // Z-dependent offsets synthesize pseudo-volume structure from Rain
                    // World's global 2D noise textures instead of flat texture scrolling.
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

                float SampleFluid(float2 roomUV)
                {
                    float d = tex2D(
                        _DryCycleFogDensityTex,
                        saturate(roomUV)).r;
                    return lerp(0.68, d, _DryCycleHasFluid);
                }

                float SampleBlastClear(float2 roomUV)
                {
                    // R can be thinned by ordinary fluid motion and player wakes. G is
                    // authored only by recognized ExplosiveSpear/ScavengerBomb blasts,
                    // so only G is allowed to relax gameplay extinction below.
                    float clear = tex2D(
                        _DryCycleFogDensityTex,
                        saturate(roomUV)).g;
                    return saturate(clear * _DryCycleHasFluid);
                }

                float2 FluidGradient(float2 roomUV)
                {
                    // World-space stencil so the same room shape produces comparable
                    // curl at different simulation resolutions.
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

                    // Bend detail along the actual low-frequency fluid density gradient.
                    // The perpendicular vector acts like a stable curl direction, making
                    // detailed billows roll around simulated masses and room geometry.
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

                    // Pseudo-depth controls how much virtual volume the pixel integrates.
                    // Foreground geometry therefore carries less atmospheric thickness
                    // while distant bands accumulate much more, despite Rain World not
                    // having a conventional depth buffer.
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

                        // Macro: huge, slow coherent fog masses.
                        float3 macroCoord = float3(
                            sampleWorld / 500.0 +
                            float2(time * 0.0025, time * -0.0008),
                            z * 0.83 + time * 0.0012);
                        float4 macro = SampleVolume(macroCoord);

                        // Mid: opposite drift and macro-warped sampling produces rolling
                        // lobes rather than two obvious scrolling noise layers.
                        float3 midCoord = float3(
                            sampleWorld / 176.0 +
                            float2(time * -0.0053, time * 0.0021),
                            z * 2.19 - time * 0.0027);
                        float4 mid = SampleVolume(
                            midCoord + macro.gbr * 0.23);

                        // Fine: erosion and wisps. Its weight stays low so the weather
                        // reads as broad humid fog rather than particle smoke.
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

                        // Fluid dictates placement; volume noise only gives that mass a
                        // rich internal shape. A recognized blast is different from an
                        // ordinary thin patch: its G field explicitly removes the medium
                        // as well as R density, producing a true temporary cavity.
                        float body =
                            macroBody * 0.68 +
                            midBody * 0.24 +
                            wisps * 0.08;
                        body *= lerp(0.48, 1.30, fluid);
                        body *= lerp(1.0, 0.05, blastClear);

                        // Middle-depth weighting prevents the integral becoming a flat
                        // chalk field while preserving large opaque-looking billows.
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
                    density += pow(
                        saturate(1.0 - wallDistance),
                        2.1) * 0.065 *
                        lerp(1.0, 0.08, surfaceBlastClear);

                    // Variance later adds local colour richness. Random visual-density
                    // valleys still cannot reveal gameplay; only the explicit G channel
                    // sampled in frag below has that authority.
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
                    // Player awareness deliberately keeps the original strict occlusion
                    // behaviour. The softer volumetric treatment below is for real fog
                    // illuminators only, so this change cannot alter awareness gameplay.
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
                    // Fog illumination should not behave like a binary line-of-sight
                    // mask. Integrate solid coverage as optical thickness so anti-aliased
                    // tile edges and thin walls attenuate continuously rather than cutting
                    // a perfectly hard geometric wedge through the fog.
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

                        // When the receiver itself is a solid tile, ignore only the last
                        // ~18px of that ray. The wall surface can therefore have its fog
                        // driven away, while an earlier tile in a thick wall still blocks
                        // the light. This avoids the old outlined-wall / visibility-mod look.
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

                    // A light illuminating suspended fog behaves more like a finite area
                    // source than a mathematical point. Five weighted source offsets make
                    // wall shadows develop a broad penumbra instead of razor-straight
                    // triangles. The width grows with distance but stays below one tile.
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

                        // Use a long, soft shoulder rather than a crisp circular reveal.
                        // A tiny world-anchored noise warp breaks the last visible perfect
                        // circle without inheriting the native LightSource radius flicker.
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

                        // Light can still illuminate suspended fog around a corner even
                        // when geometry strongly limits scene transmittance. Keeping a
                        // diffuse fraction of the halo prevents wall silhouettes from
                        // reading like a binary visibility polygon, without exposing the
                        // scene behind the wall at the same strength.
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

                    // Spatial jitter plus a very slow temporal phase removes visible
                    // ray-march layers without introducing sparkling/flickering fog.
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
                    float blastClear = SampleBlastClear(roomUV);

                    // Remap the physical G field into a broad full-clear core plus a
                    // soft shoulder. G itself remains obstacle-aware, advected and
                    // decaying with the fluid; this only fixes the final visibility
                    // response so a real evacuated cavity can become as clear as a
                    // LanternMouse instead of being capped around 85-90% transmittance.
                    float blastReveal = smoothstep(
                        0.18,
                        0.55,
                        blastClear);

                    // Normal fluid thinning is visual only. The explicit G channel is
                    // different: it represents fog physically expelled by a recognized
                    // explosion, so it is permitted to reduce local gameplay extinction.
                    float depthFactor = lerp(
                        0.84,
                        1.31,
                        pseudoDepth);
                    float extinction =
                        fog * 1.08 +
                        dense * 6.32;
                    float blastExtinctionScale = lerp(
                        1.0,
                        0.02,
                        blastClear);
                    float baseTransmittance = exp(
                        -extinction * blastExtinctionScale * depthFactor);

                    // DenseFog normally enforces the ~0.2% contrast floor. A blast only
                    // relaxes that floor where its advected G field still exists; random
                    // R-density valleys and player wakes cannot affect this term.
                    float effectiveDense = dense * (1.0 - blastClear);
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

                    // Awareness is NOT a physical clearing source. Lights and an
                    // explosion-cleared cavity are: both are allowed to reach the same
                    // 0.985 near-clear target, while the blast adds no coloured halo.
                    float awarenessTarget = max(
                        baseTransmittance,
                        lerp(0.075, 0.145, dense));
                    float transmittance = lerp(
                        baseTransmittance,
                        awarenessTarget,
                        awarenessReveal);
                    float physicalReveal = 1.0 -
                        (1.0 - lightReveal) *
                        (1.0 - blastReveal);
                    transmittance = lerp(
                        transmittance,
                        0.985,
                        physicalReveal);

                    // Visual density changes the medium's look, not gameplay visibility.
                    // Turbulent regions gain micro-contrast and cooler valleys, removing
                    // the previous flat white-cloth appearance.
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

                    // Small density-correlated ambient scattering gives thick masses
                    // body without whitening the whole frame uniformly.
                    scattering *= 1.0 +
                        visualDensity *
                        (0.025 + dense * 0.025);

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
