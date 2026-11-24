// Projected orbital angles are encoded into the red/green lanes of a fixed
// texture. Two guard texels wrap each row of the sector exposure table.
sampler2D lookup : register(s0);
sampler2D sectors : register(s1);
sampler2D rimLookup : register(s2);
float4 depositPS(float2 uv:TEXCOORD):COLOR {
    float4 map=tex2D(lookup,uv);
    float4 rim=tex2D(rimLookup,uv);
    float2 loc=uv*float2(198,158)-float2(99,79);
    float x=dot(loc,float2(.9816271834,-.1908089954));
    float v=dot(loc,float2(.1908089954,.9816271834));
    float2 fraction=float2(map.g,rim.g);
    float2 normal=float2(map.r,rim.r)*255+fraction;
    float2 shifted=frac((float2(map.b,rim.b)*255+fraction+20)/40)*40;
    float2 angle=lerp(normal,shifted,step(0,x));
    float equator=tex2D(sectors,float2((1.5+angle.x)/42.0,.25)).a;
    float optical=tex2D(sectors,float2((1.5+angle.y)/42.0,.75)).a;
    return lerp(equator,optical,smoothstep(7,14,abs(v-4.25))).xxxx;
}
