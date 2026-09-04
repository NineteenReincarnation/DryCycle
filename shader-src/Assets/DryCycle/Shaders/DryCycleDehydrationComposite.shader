Shader "DryCycle/DehydrationComposite"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _DryCycleDehydrationTearFilm ("Tear Film", 2D) = "gray" {}
        _DryCycleDehydrationRetinalNoise ("Retinal Noise", 2D) = "gray" {}
    }

    Category
    {
        Tags { "Queue"="Transparent+100" "IgnoreProjector"="True" "RenderType"="Transparent" }
        ZWrite Off
        Lighting Off
        Cull Off

        SubShader
        {
            GrabPass { "_DryCycleDehydrationGrab" }

            Pass
            {
                Blend One Zero

                CGPROGRAM
                #pragma target 3.0
                #pragma vertex vert
                #pragma fragment frag
                #include "UnityCG.cginc"

                sampler2D _DryCycleDehydrationGrab;
                sampler2D _DryCycleDehydrationTearFilm;
                sampler2D _DryCycleDehydrationRetinalNoise;

                uniform float2 _screenSize;
                uniform float _DryCycleDehydrationMild;
                uniform float _DryCycleDehydrationModerate;
                uniform float _DryCycleDehydrationSevere;
                uniform float _DryCycleDehydrationCollapse;
                uniform float _DryCycleDehydrationDying;
                uniform float _DryCycleDehydrationExertion;
                uniform float _DryCycleDehydrationBlink;
                uniform float _DryCycleDehydrationPulse;
                uniform float _DryCycleDehydrationDeathLock;
                uniform float _DryCycleDehydrationTime;

                struct v2f
                {
                    float4 pos : SV_POSITION;
                    float4 screenPos : TEXCOORD0;
                    float4 grabPos : TEXCOORD1;
                };

                v2f vert(appdata_full v)
                {
                    v2f o;
                    o.pos = UnityObjectToClipPos(v.vertex);
                    o.screenPos = ComputeScreenPos(o.pos);
                    o.grabPos = ComputeGrabScreenPos(o.pos);
                    return o;
                }

                float Smooth01(float value)
                {
                    float t = saturate(value);
                    return t * t * (3.0 - 2.0 * t);
                }

                float2 SafeNormalize(float2 value)
                {
                    float len = length(value);
                    return len > 0.00001 ? value / len : float2(0.0, 1.0);
                }

                float Luma(float3 color)
                {
                    return dot(color, float3(0.299, 0.587, 0.114));
                }

                float4 SampleScene(float2 uv, float2 offsetPx, float blur, float2 radial)
                {
                    float2 pixel = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 baseUv = saturate(uv + offsetPx * pixel);
                    float4 center = tex2D(_DryCycleDehydrationGrab, baseUv);
                    if (blur <= 0.005)
                        return center;

                    float2 tangent = SafeNormalize(float2(-radial.y, radial.x));
                    float2 radialAxis = SafeNormalize(radial);
                    float radius = 0.75 + blur * 2.85;
                    float2 a = tangent * pixel * radius;
                    float2 b = radialAxis * pixel * radius * 0.72;

                    float4 s0 = tex2D(_DryCycleDehydrationGrab, saturate(baseUv - a));
                    float4 s1 = tex2D(_DryCycleDehydrationGrab, saturate(baseUv + a));
                    float4 s2 = tex2D(_DryCycleDehydrationGrab, saturate(baseUv - b));
                    float4 s3 = tex2D(_DryCycleDehydrationGrab, saturate(baseUv + b));
                    float4 s4 = tex2D(_DryCycleDehydrationGrab, saturate(baseUv - a * 2.15));
                    float4 s5 = tex2D(_DryCycleDehydrationGrab, saturate(baseUv + a * 2.15));

                    float4 softened =
                        center * 0.34 +
                        (s0 + s1) * 0.16 +
                        (s2 + s3) * 0.10 +
                        (s4 + s5) * 0.03;
                    return lerp(center, softened, saturate(blur));
                }

                float3 ApplyChromaticFatigue(
                    float3 color,
                    float2 grabUV,
                    float2 radial,
                    float edge,
                    float drive)
                {
                    float amount = edge * drive;
                    if (amount <= 0.004)
                        return color;

                    float2 pixel = 1.0 / max(_screenSize, float2(1.0, 1.0));
                    float2 axis = SafeNormalize(radial) * pixel * (0.45 + amount * 1.65);
                    float red = tex2D(_DryCycleDehydrationGrab, saturate(grabUV + axis)).r;
                    float blue = tex2D(_DryCycleDehydrationGrab, saturate(grabUV - axis)).b;
                    color.r = lerp(color.r, red, amount * 0.28);
                    color.b = lerp(color.b, blue * 0.82, amount * 0.22);
                    return color;
                }

                fixed4 frag(v2f i) : SV_Target
                {
                    float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.00001);
                    float2 grabUV = i.grabPos.xy / max(i.grabPos.w, 0.00001);
                    float2 centered = screenUV - 0.5;
                    float time = _DryCycleDehydrationTime;

                    // Every stage is cumulative. A later state never substitutes for an
                    // earlier one; it adds a new physiological failure layer.
                    float mild = saturate(_DryCycleDehydrationMild);
                    float moderate = saturate(_DryCycleDehydrationModerate);
                    float severe = saturate(_DryCycleDehydrationSevere);
                    float collapse = saturate(_DryCycleDehydrationCollapse);
                    float dying = saturate(_DryCycleDehydrationDying);
                    float exertion = saturate(_DryCycleDehydrationExertion);
                    float blink = saturate(_DryCycleDehydrationBlink);
                    float pulse = saturate(_DryCycleDehydrationPulse);
                    float deathLock = saturate(_DryCycleDehydrationDeathLock);
                    // Once the final dehydration interval begins, the eyes close in
                    // one direction and do not reopen. Death lock completes and holds
                    // the closure instead of leaving a bright central peephole.
                    float terminalClosure = max(
                        Smooth01(saturate((dying - 0.72) / 0.28)),
                        deathLock);

                    float totalDrive = saturate(
                        mild * 0.14 +
                        moderate * 0.20 +
                        severe * 0.24 +
                        collapse * 0.24 +
                        dying * 0.18);

                    // The visual center drifts by less than a pixel-scale fraction. It
                    // communicates failing ocular control without moving the game camera.
                    float2 eyeCenter = float2(
                        sin(time * 0.31 + 0.8) * 0.0025 * collapse,
                        sin(time * 0.23 + 2.1) * 0.0035 * collapse);
                    eyeCenter.y -= deathLock * 0.012;
                    float2 eyeDelta = centered - eyeCenter;

                    float apertureX = lerp(0.56, 0.255, totalDrive);
                    float apertureY = lerp(0.53, 0.185, totalDrive);
                    apertureX *= lerp(1.0, 0.80, blink);
                    apertureY *= lerp(1.0, 0.018, blink);
                    apertureX *= lerp(1.0, 0.84, terminalClosure);
                    apertureY *= lerp(1.0, 0.035, terminalClosure);

                    float2 filmUv0 = screenUV * float2(1.22, 0.86) +
                        float2(time * 0.0021, -time * 0.0013);
                    float2 filmUv1 = screenUV * float2(2.47, 1.71) +
                        float2(-time * 0.0032, time * 0.0017) + 0.317;
                    float4 film0 = tex2D(_DryCycleDehydrationTearFilm, frac(filmUv0));
                    float4 film1 = tex2D(_DryCycleDehydrationTearFilm, frac(filmUv1));
                    float4 retinal = tex2D(
                        _DryCycleDehydrationRetinalNoise,
                        frac(screenUV * float2(1.07, 0.91) + float2(time * 0.0012, 0.0)));

                    float organicEdge =
                        (film0.b - 0.5) * (0.045 * severe + 0.070 * collapse) +
                        (retinal.g - 0.5) * (0.035 * collapse + 0.055 * dying);
                    float ellipse = length(float2(
                        eyeDelta.x / max(apertureX, 0.001),
                        eyeDelta.y / max(apertureY, 0.001)));
                    float edge = smoothstep(0.66 + organicEdge, 1.08 + organicEdge, ellipse);
                    float outerEdge = smoothstep(0.90 + organicEdge, 1.22 + organicEdge, ellipse);

                    // A breaking tear film produces tiny refraction islands. They live
                    // mostly outside the focal center and intensify after exertion.
                    float2 filmNormal =
                        (film0.rg * 2.0 - 1.0) * 0.68 +
                        (film1.rg * 2.0 - 1.0) * 0.32;
                    float filmBreak = Smooth01(saturate(
                        film0.b * 0.58 + film1.b * 0.42 + film0.a * 0.34 - 0.31));
                    float refractDrive =
                        (0.16 * moderate + 0.34 * severe + 0.42 * collapse + 0.32 * dying) *
                        (0.32 + edge * 0.68) *
                        (0.72 + exertion * 0.28);
                    float2 offsetPx = filmNormal * filmBreak * refractDrive * 1.85;

                    // Peripheral focus goes first. Central focus begins to lag only in
                    // collapse/dying stages, with exertion acting as a short amplifier.
                    float peripheralBlur = edge * (
                        0.12 * moderate +
                        0.28 * severe +
                        0.40 * collapse +
                        0.34 * dying);
                    float centralBlur =
                        collapse * 0.055 +
                        dying * 0.13 +
                        exertion * severe * 0.065;
                    float blur = saturate(peripheralBlur + centralBlur + filmBreak * edge * 0.08);
                    float4 scene = SampleScene(grabUV, offsetPx, blur, eyeDelta);
                    float3 color = scene.rgb;

                    // Brief binocular disagreement is restricted to the last stage.
                    // This is a displaced current image, not camera shake.
                    float doubleDrive = dying * (0.035 + pulse * 0.075) * (1.0 - blink);
                    if (doubleDrive > 0.003)
                    {
                        float2 doubleOffset = float2(
                            (1.2 + exertion * 1.6) / max(_screenSize.x, 1.0),
                            (0.35 + pulse * 0.55) / max(_screenSize.y, 1.0));
                        float3 ghost = tex2D(
                            _DryCycleDehydrationGrab,
                            saturate(grabUV + doubleOffset)).rgb;
                        color = lerp(color, max(color, ghost * 0.94), doubleDrive);
                    }

                    float luma = Luma(color);
                    float desaturation = saturate(
                        0.08 * mild +
                        0.16 * moderate +
                        0.20 * severe +
                        0.18 * collapse +
                        0.16 * dying);
                    color = lerp(color, luma.xxx, desaturation);

                    // Dehydration is bleached and salt-dry, not a generic red damage
                    // filter. Highlights chalk toward warm ivory while shadows lose blue.
                    float highlight = smoothstep(0.38, 0.92, luma);
                    float shadow = 1.0 - smoothstep(0.08, 0.42, luma);
                    float3 dryIvory = saturate(
                        color * float3(1.065, 1.015, 0.86) +
                        float3(0.045, 0.030, 0.008));
                    color = lerp(
                        color,
                        dryIvory,
                        totalDrive * (0.20 + highlight * 0.30));
                    color.b *= 1.0 - totalDrive * (0.075 + shadow * 0.065);

                    // Low blood-volume instability appears as restrained exposure loss,
                    // synchronized to a weak pulse rather than a constant strobe.
                    float pressureDip =
                        collapse * (0.025 + pulse * 0.045) +
                        dying * (0.045 + pulse * 0.085);
                    color *= 1.0 - pressureDip;

                    color = ApplyChromaticFatigue(
                        color,
                        grabUV,
                        eyeDelta,
                        edge,
                        severe * 0.16 + collapse * 0.28 + dying * 0.34);

                    // Retinal grain is contrast-aware. It is more visible in midtones and
                    // becomes clumpy, not television-static, near loss of consciousness.
                    float midtone = smoothstep(0.08, 0.38, luma) *
                        (1.0 - smoothstep(0.62, 0.94, luma));
                    float grain = (retinal.r - 0.5) *
                        (0.008 * moderate + 0.016 * severe + 0.028 * collapse + 0.038 * dying);
                    color += grain * (0.42 + midtone * 0.58);

                    float salt = smoothstep(0.79, 0.985, max(film0.a, retinal.b));
                    salt *= (0.18 + edge * 0.82) *
                        (0.05 * moderate + 0.12 * severe + 0.20 * collapse + 0.24 * dying);
                    color = lerp(color, float3(0.94, 0.86, 0.67), salt);

                    // Slow dark islands suggest scotoma/failing attention, reserved for
                    // collapse and dying so they read as a new failure layer.
                    float scotomaField = saturate(
                        retinal.g * 0.62 +
                        film1.b * 0.38 +
                        sin(time * 0.19 + screenUV.x * 7.3 - screenUV.y * 5.1) * 0.08);
                    float scotoma = smoothstep(0.64, 0.91, scotomaField) *
                        (collapse * 0.16 + dying * 0.28) *
                        (0.35 + edge * 0.65);
                    color *= 1.0 - scotoma;

                    // Multiple soft ramps create a deep but readable tunnel. Organic
                    // texture perturbs the boundary so it never resembles a camera lens.
                    float tunnelStrength = saturate(
                        0.13 * mild +
                        0.20 * moderate +
                        0.22 * severe +
                        0.23 * collapse +
                        0.17 * dying);
                    float brownEdge = outerEdge * tunnelStrength;
                    float blackEdge = edge * edge * tunnelStrength;
                    color = lerp(color, float3(0.105, 0.061, 0.026), brownEdge * 0.42);
                    color *= 1.0 - blackEdge * 0.83;

                    // Ordinary weakness blinks compress the existing organic aperture.
                    float lid = smoothstep(0.70, 1.02, ellipse) * blink;

                    // Terminal closure is a pair of actual upper/lower eyelids rather
                    // than another circular vignette. The curved boundary closes the
                    // corners first, narrows into a horizontal slit, then crosses the
                    // center line. At terminalClosure=1 every pixel is behind the lids.
                    float horizontalCurve =
                        pow(saturate(abs(eyeDelta.x) * 1.92), 1.65) *
                        lerp(0.018, 0.115, terminalClosure);
                    float openHalfHeight = lerp(0.64, -0.030, terminalClosure);
                    float eyelidFeather = lerp(0.034, 0.010, terminalClosure);
                    float terminalLidShape = smoothstep(
                        openHalfHeight - eyelidFeather,
                        openHalfHeight + eyelidFeather,
                        abs(eyeDelta.y) + horizontalCurve);
                    float terminalLidOpacity = smoothstep(0.015, 0.16, terminalClosure);
                    float terminalLids = terminalLidShape * terminalLidOpacity;

                    float finalOcclusion = saturate(max(lid * 0.94, terminalLids));
                    finalOcclusion *= 0.90 + retinal.g * 0.10;
                    // Do not let retinal texture leave pinholes after the eyelids meet.
                    finalOcclusion = max(finalOcclusion, step(0.999, terminalClosure));
                    color *= 1.0 - finalOcclusion;

                    return float4(saturate(color), scene.a);
                }
                ENDCG
            }
        }
    }
}
