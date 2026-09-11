Shader "DryCycle/MenuTextFlow"
{
    Properties
    {
        _MainTex ("Text Alpha", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _FlowPhase ("Flow Phase", Float) = 0
        _FlowStrength ("Flow Strength", Range(0,1)) = 0.65
        _DarkStrength ("Dark Notch Strength", Range(0,0.4)) = 0.16
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent+200"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _BaseColor;
            float _FlowPhase;
            float _FlowStrength;
            float _DarkStrength;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float4 screenPos : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _BaseColor;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float ReferenceGradient(float t)
            {
                t = frac(t);
                const float baseValue = 163.0 / 255.0;

                // Reconstructed from the 100x1 textgradient.png embedded in RainWorldRender.exe.
                // The narrow dark notches and the three white bands are intentional: they are what
                // make the title read as moving polished material rather than a generic sine glow.
                if (t < 0.23) return baseValue;
                if (t < 0.27) return lerp(baseValue, 129.0 / 255.0, saturate((t - 0.23) / 0.03));
                if (t < 0.31) return lerp(baseValue, 167.0 / 255.0, saturate((t - 0.27) / 0.04));
                if (t < 0.32) return 133.0 / 255.0;
                if (t < 0.72) return lerp(133.0 / 255.0, 1.0, saturate((t - 0.32) / 0.39));
                if (t < 0.78) return baseValue;
                if (t < 0.82) return 1.0;
                if (t < 0.85) return baseValue;
                if (t < 0.86) return 1.0;
                if (t < 0.99) return lerp(1.0, baseValue, saturate((t - 0.86) / 0.13));
                return baseValue;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 glyph = tex2D(_MainTex, i.uv);
                float2 screenUV = i.screenPos.xy / max(i.screenPos.w, 0.00001);

                // Same diagonal mapping recovered from the reference title effect. _FlowPhase is
                // normally advanced at -0.125 units/second, so one complete profile takes ~8 s.
                float coordinate = 0.70 * screenUV.x - 0.19 * screenUV.y + _FlowPhase;
                float sampleValue = ReferenceGradient(coordinate) * 255.0;
                float bright = saturate((sampleValue - 163.0) / (255.0 - 163.0));
                float dark = saturate((163.0 - sampleValue) / (163.0 - 129.0));

                float3 baseRgb = i.color.rgb * (1.0 - dark * _DarkStrength);
                float3 flowed = lerp(baseRgb, float3(1.0, 1.0, 1.0), bright * _FlowStrength);
                return fixed4(flowed, glyph.a * i.color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
