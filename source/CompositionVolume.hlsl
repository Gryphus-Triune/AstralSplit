Texture2D density : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Camera : register(b0) { float4 lookWind; float4 energyFlags; };
struct Varying { float4 position:SV_Position; float2 uv:TEXCOORD0; };
Varying volumeVS(uint id:SV_VertexID) {
    Varying v; v.uv=float2((id<<1)&2,id&2);
    v.position=float4(2*v.uv.x-1,1-2*v.uv.y,0,1);return v;
}
float4 volumePixel(Varying v):SV_Target {
    float2 uv=v.uv;
    float2 warp=float2(lookWind.w*(uv.y-.5),lookWind.z*(uv.x-.5))*.015;
    float2 stepRay=(lookWind.xy*float2(.07,.11)+lookWind.zw*float2(.045,.065))*float2(.25,.5)/7;
    float2 p=(float2(.07,.10)+float2(.86,.80)*uv+warp)*float2(.25,.5)-3.5*stepRay;
    float4 c=0;
    [unroll] for(int i=0;i<8;i++) {
        float2 tile=float2((i%4)*.25,(i/4)*.5);
        c+=(1-c.a)*density.SampleLevel(linearClamp,p+tile+i*stepRay,0);
    }
    c.rgb=min(c.aaa,c.rgb*lerp(float3(.76,.86,1.05),float3(1.15,1.03,.88),energyFlags.x));
    float feather=saturate(min(uv.y/.14,(1-uv.y)/.32));
    return c*(lerp(.40,.68,energyFlags.x)*feather);
}
