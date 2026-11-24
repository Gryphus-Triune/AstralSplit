// Two adjacent camera layers share one render target and one instanced draw.
cbuffer Camera : register(b0) { float4 view; } // world origin, inverse width, inverse height
struct Instance { float2 center:POSITION; float2 along:TEXCOORD0; float2 across:TEXCOORD1; float4 rgba:COLOR0; float front:TEXCOORD2; };
struct Pixel { float4 xy:SV_Position; float2 radius:TEXCOORD0; float4 rgba:COLOR; float3 bounds:SV_ClipDistance0; };
Pixel projectMote(Instance i,float2 uv) {
    float2 screen=(i.center+uv.x*i.along+uv.y*i.across-view.xy)*view.zw;
    Pixel p; p.xy=float4(2*screen.x-1,1-screen.y-i.front,0,1);
    p.radius=uv;p.rgba=i.rgba;p.bounds=float3(screen.y,1-screen.y,1);return p;
}
float2 quadCorner(uint vertex) { return float2((vertex&1)*2.0-1.0,(vertex>>1)*2.0-1.0); }
Pixel moteVS(Instance i,uint vertex:SV_VertexID) { return projectMote(i,quadCorner(vertex)); }
Pixel magneticVS(Instance i,uint vertex:SV_VertexID) {
    Pixel p=projectMote(i,quadCorner(vertex));
    if(i.rgba.b<1.5)p.bounds.z=i.rgba.b<.5?-p.radius.y:p.radius.y;
    return p;
}
float4 shadeField(Pixel p,float a,float logr) {
    float2 q=p.radius;
    float r=max(.01,length(q));
    bool lens=p.rgba.b>1.5;
    float inner=lens?.63:.31, outer=lens?.97:.99;
    float band=smoothstep(inner,inner+.06,r)*(1-smoothstep(outer-.08,outer,r));
    if(lens)band*=smoothstep(.10,.60,abs(q.y));
    // A logarithmic spiral is a constant azimuth minus pitch * log(radius).
    // Four nested filaments per bundle, with broad emission gaps and tapered ends.
    float phase=p.rgba.r,load=p.rgba.g;
    float angle=a-(2.55+3.45*load)*logr+.032*sin(3*a+4*r-phase);
    float families=(angle-phase)*10.185916; // 64 bundles per turn
    float local=frac(families+.5)-.5;
    float aa=max(.025,fwidth(families));
    float leader=1-smoothstep(.045,.085+aa,abs(local));
    float ribs=abs(frac((local+.025)*4+.5)-.5);
    float hair=(1-smoothstep(.05,.13+min(.32,aa*4),ribs))*saturate(1-aa*2);
    float family=floor(families+.5);family-=64*floor(family/64);
    float sector=.55+.45*sin(2*a+1.2*logr-phase+.55*sin(family*1.45));
    float bundles=.64+.36*cos(12*(angle-phase)+1.2*sin(3*a-2*logr));
    float pearl=smoothstep(.16,.87,.5+.5*cos(a+.68+1.3*logr));
    float envelope=smoothstep(.02,.48,sector)*(.40+.60*sector)*(.38+.62*bundles)*(.62+.38*pearl);
    float alpha=band*envelope*(.79*leader+.28*hair)*p.rgba.a*(lens?.95:1);
    float blue=smoothstep(.24,.94,q.x)*(.42+.28*load);
    float3 ink=lerp(float3(1,.69,.36),float3(.57,.84,1),blue);
    ink=lerp(ink,float3(1,.94,.78),leader*.43);
    return float4(ink*alpha,alpha);
}
float4 magneticOptimizedPS(Pixel p):SV_Target {
    return shadeField(p,atan2(p.radius.y,p.radius.x),log(max(.01,length(p.radius))));
}
float2 magneticMap(Pixel p):SV_Target {
    return float2(atan2(p.radius.y,p.radius.x),log(max(.01,length(p.radius))));
}
Texture2DArray<float2> angles : register(t0);
float4 magneticLookup(Pixel p):SV_Target {
    float2 polar=angles.Load(int4(int2(p.xy.xy),p.rgba.b>1.5?1:0,0));
    // A derivative quad can straddle the equator. Its helper lanes fall outside the
    // stored half-image; recover their analytic values before evaluating fwidth.
    if(p.rgba.b<1.5 && abs(p.radius.y)<3*fwidth(p.radius.y)){
        polar=float2(atan2(p.radius.y,p.radius.x),log(max(.01,length(p.radius))));
    }
    return shadeField(p,polar.x,polar.y);
}

