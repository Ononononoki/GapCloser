// a soft tint over one rock part for GapCloser's RockSupport: see-through, drawn on the part's own surface pulled toward
// the camera along the view ray (2 cm plus 0.2 % of the distance, so it stays on screen where it was and in front of the
// rock instead of cutting through it), a bit stronger at the edges so the shape reads
Shader "GapCloser/RockTint"
{
    Properties
    {
        _Color ("Color", Color) = (0.8, 0.6, 0.3, 0.35)
        _Rim ("Edge strength", Range(0, 1)) = 0.5
        _Lift ("Pull toward the camera (m)", Float) = 0.02
        _LiftScale ("Pull per meter of distance", Float) = 0.002
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            half _Rim;
            float _Lift, _LiftScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half rim : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 viewPos = UnityObjectToViewPos(v.vertex);
                float d = length(viewPos);
                float lift = min(_Lift + _LiftScale * d, d * 0.5);
                viewPos *= (d - lift) / max(d, 1e-4);
                o.pos = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 view = normalize(WorldSpaceViewDir(v.vertex));
                o.rim = 1.0 - saturate(abs(dot(n, view)));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = _Color;
                c.a *= lerp(1.0, 1.0 + 1.5 * _Rim, i.rim * i.rim);
                c.a = saturate(c.a);
                return c;
            }
            ENDCG
        }
    }
}
