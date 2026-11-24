// The same 101 authored samples and the same ten Doppler bands are used by the vector renderer.
cbuffer View : register(b0) {
 float4 plane, depth, originScale, physical, gradient;
 float4 colors[30];float4 widths[3];
};
cbuffer Samples:register(b1){float4 orbit[101];}
cbuffer Stars:register(b2){float4 spin[128];}
static const float cs=.981627183447664, sn=.190808995376545;
float2 localAt(float4 orbital,float2 rot){
    float x=dot(orbital.xy,float2(rot.x,-rot.y)),z=dot(orbital.xy,rot.yx);
    float y=orbital.z+orbital.w*z;return float2(cs*x+sn*y,-sn*x+cs*y);
}
float distanceToPlane(float2 p){
    float x=p.x*cs-p.y*sn;
    float mid=lerp(3.9,4.9,saturate((69.3-abs(x))/(69.3-48)));
    return dot(p-float2(sn,cs)*mid,depth.zw);
}
float2 project(float2 p){return float2(dot(p,plane.xy),dot(p,plane.zw))/(1+dot(p,depth.xy));}
uint thermalBand(uint k){return (k>15)+(k>30)+(k>45)+(k>60)+(k>73)+(k>82)+(k>90)+(k>94)+(k>97);}
struct Edge {float4 pos:SV_Position; nointerpolation float4 endpoints:TEXCOORD0; nointerpolation float radius:TEXCOORD1; nointerpolation uint band:TEXCOORD2;};
struct Slice {float4 pos:SV_Position; nointerpolation float4 endpoints:TEXCOORD0; nointerpolation float radius:TEXCOORD1; uint target:SV_RenderTargetArrayIndex;};
Edge orbitVS(uint id:SV_VertexID,uint instance:SV_InstanceID){
    uint halfIndex=instance%2, k=(instance/2)%100+1, star=instance/200;
    float2 a=localAt(orbit[k-1],spin[star].xy),b=localAt(orbit[k],spin[star].xy);
    float da=distanceToPlane(a),db=distanceToPlane(b);
    bool first=(da>=0)==(halfIndex==1),last=(db>=0)==(halfIndex==1);
    Edge o;o.band=thermalBand(k);float width=widths[o.band/4][o.band%4];
    o.radius=width*originScale.z*.5;
    if(!first && !last){o.pos=float4(2,2,0,1);o.endpoints=0;return o;}
    if(first!=last){float2 split=lerp(a,b,da/(da-db));if(!first)a=split;else b=split;}
    float2 x=(project(a)-originScale.xy)*originScale.zw,y=(project(b)-originScale.xy)*originScale.zw;
    x.y+=halfIndex*physical.y;y.y+=halfIndex*physical.y;
    o.endpoints=float4(x,y);
    float2 uv=float2(id==1||id==4||id==5,id==2||id==3||id==5);
    float pad=o.radius+1.0;
    float2 pt=lerp(min(x,y)-pad,max(x,y)+pad,uv);
    o.pos=float4(pt.x*2/physical.x-1,1-pt.y/physical.y,0,1);return o;
}
[maxvertexcount(3)] void orbitGS(triangle Edge tri[3],inout TriangleStream<Slice> output){
    [unroll] for(int i=0;i<3;i++){
        Slice v;v.pos=tri[i].pos;v.endpoints=tri[i].endpoints;v.radius=tri[i].radius;v.target=tri[i].band;output.Append(v);
    }
}
float orbitMask(Slice p):SV_Target{
    float2 axis=p.endpoints.zw-p.endpoints.xy;float inv=1/max(.000001,dot(axis,axis));float sum=0;
    [unroll] for(int y=0;y<4;y++)[unroll] for(int x=0;x<4;x++){
        float2 d=p.pos.xy+float2((x+.5)*.25-.5,(y+.5)*.25-.5)-p.endpoints.xy;
        float2 off=d-axis*saturate(dot(d,axis)*inv);sum+=saturate(.5+(p.radius-length(off))*4);
    }
    return sum*(1.0/16);
}
Texture2DArray<float> coverage:register(t0);
struct Quad{float4 pos:SV_Position;};
Quad orbitQuad(uint vertex:SV_VertexID){
    Quad o;float2 uv=float2((vertex<<1)&2,vertex&2);o.pos=float4(2*uv.x-1,1-2*uv.y,0,1);return o;
}
float4 orbitInk(Quad q):SV_Target{
    int2 texel=int2(q.pos.xy);float2 local=q.pos.xy;
    if(local.y>=physical.y)local.y-=physical.y;
    local=originScale.xy+local/originScale.zw;
    float2 axis=gradient.zw-gradient.xy;float t=2*saturate(dot(local-gradient.xy,axis)/max(.000001,dot(axis,axis)));
    float4 outColor=0;
    [unroll] for(int i=0;i<10;i++){
        float4 ink=t<1?lerp(colors[i*3],colors[i*3+1],t):lerp(colors[i*3+1],colors[i*3+2],t-1);
        float a=ink.a*coverage.Load(int4(texel,i,0));outColor=float4(ink.rgb*a,a)+(1-a)*outColor;
    }
    return outColor;
}
