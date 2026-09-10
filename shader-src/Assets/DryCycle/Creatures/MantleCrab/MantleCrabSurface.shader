Shader "DryCycle/MantleCrabSurface"
{
    Properties { _MainTex("Generated pattern and surface atlas",2D)="white"{} }
    SubShader
    {
        Tags {"Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True"}
        ZWrite Off Cull Off Lighting Off
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _PalTex;
            float4 _lightDirAndPixelSize;
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float2 world:TEXCOORD1; float4 env:COLOR; };
            v2f vert(appdata_full v)
            {
                v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.texcoord.xy;
                o.world=mul(unity_ObjectToWorld,v.vertex).xy; o.env=v.color; return o;
            }
            float heightAt(float2 uv, float kind)
            {
                float2 lo=float2((kind*128+.5)/896.0,.5/256.0);
                float2 hi=lo+float2(127.0/896,127.0/256);
                return tex2D(_MainTex,clamp(uv,lo,hi)).a;
            }
            fixed4 frag(v2f i):SV_Target
            {
                float kind=min(6,floor(i.uv.x*7));
                float2 uv=float2((i.uv.x*896-kind*128-.5)/127,(i.uv.y*256-.5)/127);
                float4 pattern=tex2D(_MainTex,i.uv);
                // surface = AO, roughness, anatomical mask, emission
                float4 surface=tex2D(_MainTex,i.uv+float2(0,.5));
                float2 xy=uv*2-1;
                float anatomy=surface.b;

                // Coarse cross-section normals describe the mesh family. Height derivatives below
                // add procedural plate relief on top of this silhouette-aware base normal.
                float3 n;
                if (kind<.5)
                    n=normalize(float3(xy.x*.38,xy.y*.65,.85));
                else if (kind==1)
                    n=normalize(float3(xy.x*.12,xy.y*.68,.88));
                else if (kind==2)
                    n=normalize(float3(xy.x*.34,xy.y*.78,lerp(.82,.60,anatomy)));
                else if (kind==3)
                {
                    float sideBevel=smoothstep(.35,.76,abs(xy.y));
                    float toeBevel=smoothstep(.58,.94,xy.x);
                    n=normalize(float3(toeBevel*.34,sign(xy.y)*sideBevel*.82,1));
                }
                else if (kind==4)
                    n=normalize(float3(xy.x*.16,xy.y*.72,.84));
                else if (kind==5)
                    n=normalize(float3(xy*.8,sqrt(saturate(1-dot(xy,xy)*.45))+.2));
                else
                    n=normalize(float3(xy.x*.08,xy.y*.64,.90));

                float2 grad=float2(
                    heightAt(i.uv+float2(1.0/896,0),kind)-heightAt(i.uv-float2(1.0/896,0),kind),
                    heightAt(i.uv+float2(0,1.0/256),kind)-heightAt(i.uv-float2(0,1.0/256),kind));
                float reliefScale=kind==2?2.4:(kind==3?2.8:3.5);
                n=normalize(n-float3(grad*reliefScale,0));

                // Derive the UV frame from the deformed mesh, including mirrored limbs. This makes
                // the procedural normal follow each bent segment rather than screen-space axes.
                float2 du=ddx(uv), dv=ddy(uv), px=ddx(i.world), py=ddy(i.world);
                float det=du.x*dv.y-du.y*dv.x;
                float invDet=abs(det)>1e-8 ? 1/det : 0;
                float2 t=(px*dv.y-py*du.y)*invDet;
                float2 b=(-px*dv.x+py*du.x)*invDet;
                t*=rsqrt(max(dot(t,t),1e-8));
                b-=t*dot(t,b);
                b*=rsqrt(max(dot(b,b),1e-8));
                float3 normal=normalize(float3(t*n.x+b*n.y,n.z));
                float3 light=normalize(float3(-_lightDirAndPixelSize.xy,.75));
                float diffuse=smoothstep(-.2,.85,dot(normal,light));

                float roughness=surface.g;
                if(kind==3) roughness=lerp(roughness,.98,anatomy); // planted sole
                if(kind==2) roughness=lerp(roughness,.93,anatomy); // soft joint membrane
                if(kind==4) roughness=lerp(roughness,.34,anatomy); // polished pincer cutting edge

                float highlight=pow(saturate(dot(normal,normalize(light+float3(0,0,1)))),lerp(13,3,roughness));
                float highlightScale=1-roughness;
                if(kind==3) highlightScale*=lerp(1,.12,anatomy);
                if(kind==2) highlightScale*=lerp(1,.20,anatomy);
                if(kind==4) highlightScale*=lerp(1,1.45,anatomy);

                float depth=saturate((1-i.env.a)*2);
                float3 albedo=pattern.rgb;
                float luminance=dot(albedo,float3(.299,.587,.114));
                albedo=lerp(albedo,luminance*float3(.8,.98,1.2),depth*.4);

                // UV-edge response is deliberately subtle and quantized later. It helps each
                // exoskeletal plate read as a thin hard shell without turning Rain World into PBR.
                float lateralEdge=smoothstep(.68,.98,abs(xy.y));
                float axialEdge=smoothstep(.76,.98,abs(xy.x));
                float shellRim=lateralEdge*(1-roughness)*.16;
                if(kind==2) shellRim*=1-anatomy*.8;
                if(kind==3) shellRim*=1-anatomy*.9;

                float3 color=albedo*i.env.rgb*(.58+diffuse*.62)*lerp(.35,1,surface.r);
                color+=albedo*highlight*highlightScale*.90*i.env.rgb*(1-depth*.65);
                color+=albedo*shellRim*i.env.rgb;

                // Material-specific anatomical masks now carry actual local structure.
                if(kind==2)
                {
                    // Recess the flexible membrane visually inside the hard joint plate.
                    color*=lerp(1,.72,anatomy*.55);
                }
                else if(kind==3)
                {
                    // Heavy sole: dark, rough, almost no edge glint at the terrain-bearing end.
                    color*=lerp(1,.78,anatomy*.42);
                }
                else if(kind==4)
                {
                    // Cutting edge: small cool hard-chitin glint, strongest toward distal UVs.
                    float cutting=anatomy*(.45+.55*axialEdge);
                    color+=float3(.18,.08,.11)*cutting*highlight*.40*(1-depth*.7);
                }

                float3 black=tex2D(_PalTex,float2(2.5/32,7.5/16)).rgb;
                float3 fog=tex2D(_PalTex,float2(1.5/32,7.5/16)).rgb;
                color=lerp(color,black,.08);
                float fogAmount=1-tex2D(_PalTex,float2(9.5/32,7.5/16)).r;
                color=lerp(color,fog,depth*.38+fogAmount*.06);
                color+=pattern.rgb*surface.a; // emission only; never creates a LightSource

                // Stable anatomical dither, not frame-random noise or transparent edges.
                float2 pixel=floor(uv*128);
                float dither=frac(52.9829189*frac(dot(pixel,float2(.06711056,.00583715))));
                color=floor(saturate(color)*96+dither)/96;
                return fixed4(color,1); // LocalDepth is data, NOT transparency.
            }
            ENDCG
        }
    }
    Fallback Off
}
