// writes unity's sun shadow cascades (set while it draws the directional light's shadows) into a small float texture
// for GapCloser's sun shadow test: texel (row, cascade) = row of unity_WorldToShadow[cascade], texel (cascade, 4) = the
// cascade's split sphere, texel (0, 5) _LightSplitsNear, (1, 5) _LightSplitsFar, (2, 5) unity_ShadowSplitSqRadii
Shader "Hidden/GapCloser/ShadowMatrices"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            // one triangle over the whole target
            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 uv = float2((id << 1) & 2, id & 2);
                o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                uint2 p = (uint2)i.pos.xy;
                if (p.y < 4) return unity_WorldToShadow[p.y][p.x];
                if (p.y == 4) return unity_ShadowSplitSpheres[p.x];
                if (p.y == 5)
                {
                    if (p.x == 0) return _LightSplitsNear;
                    if (p.x == 1) return _LightSplitsFar;
                    if (p.x == 2) return unity_ShadowSplitSqRadii;
                    return float4(1, 2, 3, 4);
                }
                return float4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
