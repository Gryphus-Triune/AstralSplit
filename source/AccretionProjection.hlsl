sampler2D image : register(s0);
float3 inverseX : register(c0);
float3 inverseY : register(c1);
float3 divisor : register(c2);

float2 address(float2 uv) {
    float2 size=float2(240,188);
    float3 ray=float3((uv-.5)*size,1);
    float2 local=float2(dot(ray,inverseX),dot(ray,inverseY))/dot(ray,divisor);
    return .5+local/size;
}
float4 accretionProjection(float2 uv : TEXCOORD) : COLOR {
    float2 at=address(uv);
    return tex2D(image,saturate(at))*step(0,min(at.x,at.y))*step(max(at.x,at.y),1);
}

sampler2D exposure : register(s1);
float4 maskedProjection(float2 uv:TEXCOORD):COLOR {
    float2 at=address(uv);float2 src=saturate(at);
    float edge=step(0,min(at.x,at.y))*step(max(at.x,at.y),1);
    return tex2D(image,src)*(edge*tex2D(exposure,src).a);
}
