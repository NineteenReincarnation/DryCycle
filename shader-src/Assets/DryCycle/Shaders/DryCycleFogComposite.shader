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
            // This shader is a final world composite rather than a translucent sprite.
            // Use an anonymous GrabPass deliberately: a named GrabPass can be reused by
            // later objects in the same frame, which is incorrect when Jolly/split-screen
            // has multiple RoomCamera composites. Correct per-camera capture wins over
            // the performance optimization here.
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

                    float encoded = fmod(max(0.0, level.r * 255.0 - 1.0), 30.0) / 30.0;
                    return saturate(encoded * 1.35);
                }

                float2 DomainWarpPx(float2 worldPx, float time)
                {
                    float2 p = worldPx / 680.0;
                    float wx = tex2D(
                        _NoiseTex,
                        p * float2(0.83, 1.17) +
                        float2(time * 0.0041, time * -0.0017)).r;
                    float wy = tex2D(
                        _NoiseTex,
                        p.yx * float2(1.31, 0.71) +
                        float2(time * -0.0027, time * 0.0033) + 0.37).r;
                    float2 warp = float2(wx, wy) * 2.0 - 1.0;

                    float wx2 = tex2D(
                        _NoiseTex2,
                        p * 0.43 + float2(time * -0.0011, 0.19)).g;
                    float wy2 = tex2D(
                        _NoiseTex2,
                        p.yx * 0.47 + float2(0.61, time * 0.0015)).b;
                    warp += (float2(wx2, wy2) * 2.0 - 1.0) * 0.34;
                    return warp * 88.0;
                }

                float4 SampleVolume(float3 p)
                {
                    if (_DryCycleHasNoise3D > 0.5)
                        return tex3D(_DryCycleFogNoise3D, frac(p));

                    // Compatibility path: synthesize pseudo-3D structure from Rain
                    // World's global 2D noise textures using Z-dependent offsets.
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

                float VisualFogDensity(
                    float2 roomUV,
                    float2 worldPx,
                    float pseudoDepth,
                    float jitter)
                {
                    const int Steps = 12;
                    float time = _DryCycleFogTime;
                    float2 warpPx = DomainWarpPx(worldPx, time);
                    float accumulated = 0.0;

                    [unroll]
                    for (int s = 0; s < Steps; s++)
                    {
                        float z = (s + jitter) / (float)Steps;
                        float depthParallax =
                            (z - 0.5) * lerp(34.0, 82.0, pseudoDepth);
                        float2 sampleWorld =
                            worldPx + warpPx * lerp(0.35, 1.0, z);
                        sampleWorld += float2(
                            depthParallax * 0.41,
                            depthParallax * -0.17);

                        float2 sampleRoomUv = sampleWorld /
                            max(_DryCycleRoomSizePx, float2(1.0, 1.0));
                        float fluid = tex2D(
                            _DryCycleFogDensityTex,
                            saturate(sampleRoomUv)).r;
                        fluid = lerp(0.68, fluid, _DryCycleHasFluid);

                        // Large coherent mass, high-frequency Worley-style erosion and
                        // a second incommensurate sample create billows instead of a
                        // scrolling 2D texture.
                        float3 macroCoord = float3(
                            sampleWorld / 430.0 +
                            float2(time * 0.0032, time * -0.0011),
                            z * 0.91 + time * 0.0017);
                        float4 macro = SampleVolume(macroCoord);

                        float3 detailCoord = float3(
                            sampleWorld / 137.0 +
                            float2(time * -0.0071, time * 0.0029),
                            z * 2.73 - time * 0.0037);
                        float4 detail = SampleVolume(
                            detailCoord + macro.gbr * 0.19);

                        float body = saturate(
                            macro.r * 1.30 -
                            (1.0 - detail.b) * 0.38 +
                            0.05);
                        body = pow(body, lerp(1.45, 0.82, macro.g));
                        accumulated += body * lerp(0.56, 1.18, fluid);
                    }

                    float density = accumulated / (float)Steps;
                    float wallDistance = tex2D(
                        _DryCycleFogObstacleTex,
                        saturate(roomUV)).g;
                    density += pow(
                        saturate(1.0 - wallDistance),
                        2.0) * 0.075;
                    return saturate(density * 1.42);
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
                    // Lantern is capped at 200 px and LanternMouse at 40 px. Twelve
                    // samples therefore give <= 17 px spacing for the largest allowed
                    // reveal radius, slightly finer than one Rain World tile.
                    const int OcclusionSteps = 12;
                    float blocked = 0.0;
                    [unroll]
                    for (int i = 1; i <= OcclusionSteps; i++)
                    {
                        float t = i / (float)(OcclusionSteps + 1);
                        float2 p = lerp(fromPx, toPx, t);
                        blocked = max(
                            blocked,
                            step(0.5, ObstacleAtWorld(p)));
                    }
                    return 1.0 - blocked;
                }

                void EvaluateFogLights(
                    float2 worldPx,
                    float visualDensity,
                    out float reveal,
                    out float3 coloredScattering)
                {
                    reveal = 0.0;
                    coloredScattering = float3(0.0, 0.0, 0.0);

                    [unroll]
                    for (int i = 0; i < 8; i++)
                    {
                        if (i >= _DryCycleFogLightCount)
                            break;

                        float4 light = _DryCycleFogLights[i];
                        float radius = max(1.0, light.z);
                        float distancePx = distance(worldPx, light.xy);
                        if (distancePx >= radius || light.w <= 0.0001)
                            continue;

                        float radial = Smooth01(
                            1.0 - distancePx / radius);
                        radial = pow(radial, 0.66);
                        float visible = SegmentVisibility(
                            light.xy,
                            worldPx);
                        float localReveal = saturate(
                            radial * light.w * visible);

                        reveal = 1.0 -
                            (1.0 - reveal) *
                            (1.0 - localReveal);
                        coloredScattering +=
                            _DryCycleFogLightColors[i].rgb *
                            localReveal *
                            lerp(0.10, 0.42, visualDensity);
                    }
                }

                float EvaluateAwareness(float2 worldPx)
                {
                    float reveal = 0.0;
                    [unroll]
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

                    float jitter = tex2D(
                        _NoiseTex2,
                        frac(screenUV * (_screenSize / 256.0))).r;
                    float visualDensity = VisualFogDensity(
                        roomUV,
                        worldPx,
                        pseudoDepth,
                        jitter);

                    // Gameplay visibility is deliberately independent from visual fog
                    // texture. DenseFog can billow, tear and flow without ever opening
                    // a random long-distance window through the weather.
                    float depthFactor = lerp(
                        0.86,
                        1.28,
                        pseudoDepth);
                    float extinction =
                        fog * 1.05 +
                        dense * 6.22;
                    float baseTransmittance = exp(
                        -extinction * depthFactor);

                    // At full DenseFog force high-contrast details into the same
                    // ~0.2% regime as the earlier CPU extinction prototype.
                    float denseCeiling = lerp(
                        1.0,
                        0.0022,
                        dense);
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
                    float awarenessReveal = EvaluateAwareness(worldPx);
                    float reveal = 1.0 -
                        (1.0 - lightReveal) *
                        (1.0 - awarenessReveal);

                    float transmittance = lerp(
                        baseTransmittance,
                        0.985,
                        reveal);

                    // Density changes the appearance/internal lighting of fog, not the
                    // gameplay transmittance. Thick lobes are brighter; valleys are
                    // darker and cooler instead of becoming transparent windows.
                    float densityShade = smoothstep(
                        0.16,
                        0.88,
                        visualDensity);
                    float shade = lerp(
                        0.77,
                        1.12,
                        densityShade);
                    float3 fogColor =
                        _DryCycleFogColor.rgb * shade;
                    fogColor = lerp(
                        fogColor,
                        _DryCycleFogColor.rgb * 0.90,
                        (1.0 - visualDensity) * 0.25);

                    float3 scattering =
                        fogColor +
                        lightScattering * (0.48 + dense * 0.22);
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
