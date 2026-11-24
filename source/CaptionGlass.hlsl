sampler2D atlas : register(s1);
float2 size : register(c0);
float4 region : register(c1);
float4 captionGlass(float2 uv:TEXCOORD0):COLOR0 {
    float2 n=(uv-.5)*size;
    float r=length(n), radius=min(size.x,size.y)*.5, bevel=clamp(size.y*.29,2.2,8);
    float si=saturate(1-(radius-r)/bevel), st=si/1.48;
    float ci=sqrt(saturate(1-si*si)),ct=sqrt(saturate(1-st*st));
    float bend=(1.9/1.1)*bevel*(si*ct-ci*st)/max(.00001,ci*ct+si*st);
    return tex2D(atlas,region.xy+region.zw*saturate(uv-n*(bend/max(.001,r))/size));
}
