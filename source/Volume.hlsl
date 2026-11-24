// The atlas stores premultiplied in-scattered light and 1-transmittance for eight
// depth slabs. Each integrates twelve 3-D density samples toward the sun.
sampler2D field : register(s1);
float2 eye : register(c0);
float drift : register(c1);
float swell : register(c2);
float energy : register(c3);
float4 nebulaPS(float2 uv:TEXCOORD):COLOR {
    float2 warp=float2(swell*(uv.y-.5),drift*(uv.x-.5))*.015;
    float2 stepRay=(eye*float2(.07,.11)+float2(drift*.045,swell*.065))*float2(.25,.5)/7;
    // Leave a guard band even with maximum pointer displacement and both winds.
    float2 p=(float2(.07,.10)+float2(.86,.80)*uv+warp)*float2(.25,.5)-3.5*stepRay;
    float2 stepTile=float2(.25,0)+stepRay;
    float4 cloud=tex2D(field,p);
    p+=stepTile;cloud+=(1-cloud.a)*tex2D(field,p);
    p+=stepTile;cloud+=(1-cloud.a)*tex2D(field,p);
    p+=stepTile;cloud+=(1-cloud.a)*tex2D(field,p);
    p+=stepTile+float2(-1,.5);cloud+=(1-cloud.a)*tex2D(field,p);
    p+=stepTile;cloud+=(1-cloud.a)*tex2D(field,p);
    p+=stepTile;cloud+=(1-cloud.a)*tex2D(field,p);
    p+=stepTile;cloud+=(1-cloud.a)*tex2D(field,p);
    cloud.rgb=min(cloud.aaa,cloud.rgb*lerp(float3(.76,.86,1.05),float3(1.15,1.03,.88),energy));
    return cloud*lerp(.40,.68,energy);
}
