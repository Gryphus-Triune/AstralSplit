// Two adjacent camera layers share one render target and one instanced draw.
cbuffer Camera : register(b0) { float4 view; } // world origin, inverse width, inverse height
struct Instance { float2 center:POSITION; float2 along:TEXCOORD0; float2 across:TEXCOORD1; float4 rgba:COLOR0; float front:TEXCOORD2; };
struct Pixel { float4 xy:SV_Position; float2 radius:TEXCOORD0; float4 rgba:COLOR; float layerClip:SV_ClipDistance0; float layerEnd:SV_ClipDistance1; };
Pixel moteVS(Instance i,uint vertex:SV_VertexID) {
    float2 uv=float2((vertex&1)*2.0-1.0,(vertex>>1)*2.0-1.0);
    float2 screen=(i.center+uv.x*i.along+uv.y*i.across-view.xy)*view.zw;
    Pixel p; p.xy=float4(2*screen.x-1,1-screen.y-i.front,0,1);
    p.radius=uv;p.rgba=i.rgba;p.layerClip=screen.y;p.layerEnd=1-screen.y;return p;
}
float4 motePS(Pixel p):SV_Target {
    float r=length(p.radius);
    float alpha=(r<.25?lerp(1,.32,4*r):.32*saturate((1-r)/.75))*p.rgba.a;
    return float4(p.rgba.rgb*alpha,alpha);
}
// The formation palette and the quadratic trail use the same shapes and order as
// the retained vector fallback. Each grain needs one quad even with a halo and tail.
Pixel dustVS(Instance i,uint vertex:SV_VertexID) {
    bool lit=i.rgba.g>1.5;
    float left=lit?max(2.65,1+i.rgba.r):1;
    float right=lit?2.65:1;
    float2 uv=float2(lerp(-left-.32,right+.32,(float)(vertex&1)),
                     ((vertex>>1)*2.0-1.0)*(lit?3.05:1.28));
    float2 screen=(i.center+uv.x*i.along+uv.y*i.across-view.xy)*view.zw;
    Pixel p;p.xy=float4(2*screen.x-1,1-screen.y-i.front,0,1);
    p.radius=uv;p.rgba=i.rgba;p.layerClip=screen.y;p.layerEnd=1-screen.y;return p;
}
float4 magneticPS(Pixel p):SV_Target {
    float2 q=p.radius;
    float r=max(.01,length(q)),a=atan2(q.y,q.x);
    bool lens=p.rgba.b>1.5;
    if(!lens && ((q.y<0)!=(p.rgba.b<.5))) discard;
    float inner=lens?.63:.31, outer=lens?.97:.99;
    float band=smoothstep(inner,inner+.06,r)*(1-smoothstep(outer-.08,outer,r));
    if(lens)band*=smoothstep(.10,.60,abs(q.y));
    // A logarithmic spiral is a constant azimuth minus pitch * log(radius).
    // Four nested filaments per bundle, with broad emission gaps and tapered ends.
    float phase=p.rgba.r,load=p.rgba.g;
    float angle=a-(2.55+3.45*load)*log(r)+.032*sin(3*a+4*r-phase);
    float families=(angle-phase)*10.185916; // 64 bundles per turn
    float local=frac(families+.5)-.5;
    float aa=max(.025,fwidth(families));
    float leader=1-smoothstep(.045,.085+aa,abs(local));
    float ribs=abs(frac((local+.025)*4+.5)-.5);
    float hair=(1-smoothstep(.05,.13+min(.32,aa*4),ribs))*saturate(1-aa*2);
    float family=floor(families+.5);family-=64*floor(family/64);
    float sector=.55+.45*sin(2*a+1.2*log(r)-phase+.55*sin(family*1.45));
    float bundles=.64+.36*cos(12*(angle-phase)+1.2*sin(3*a-2*log(r)));
    float pearl=smoothstep(.16,.87,.5+.5*cos(a+.68+1.3*log(r)));
    float envelope=smoothstep(.02,.48,sector)*(.40+.60*sector)*(.38+.62*bundles)*(.62+.38*pearl);
    float alpha=band*envelope*(.79*leader+.28*hair)*p.rgba.a*(lens?.95:1);
    float blue=smoothstep(.24,.94,q.x)*(.42+.28*load);
    float3 ink=lerp(float3(1,.69,.36),float3(.57,.84,1),blue);
    ink=lerp(ink,float3(1,.94,.78),leader*.43);
    return float4(ink*alpha,alpha);
}
float bead(float2 p,float2 radii) {
    float d=length(p/radii)-1;
    return saturate(.5-d/max(fwidth(d),.025));
}
float4 inkOver(float4 under,float4 ink,float coverage){
    float a=ink.a*coverage;
    return under*(1-a)+float4(ink.rgb*a,a);
}
float4 dustPS(Pixel p):SV_Target {
    const float4 pigment[5]={float4(224,165,119,164)/255,float4(210,192,196,176)/255,
        float4(255,205,144,219)/255,float4(255,237,204,255)/255,float4(220,235,255,255)/255};
    float2 uv=p.radius;int kind=(int)(p.rgba.g+.5);
    float4 dust=0;
    if(kind>=2){
        dust=inkOver(dust,float4(255,187,122,31)/255,bead(uv,float2(2.65,2.6625)));
        float len=1+p.rgba.r, q=saturate(-uv.x/len), b=.26*p.rgba.r/len;
        float t=q/max(.00001,b+sqrt(b*b+(1-2*b)*q));
        float sd=max(max(uv.x,-len-uv.x),abs(uv.y)-.68*(1-t*t));
        float trail=saturate(.5-sd/max(fwidth(sd),.025));
        dust=inkOver(dust,kind==4?float4(200,216,244,77)/255:float4(255,198,139,83)/255,trail);
    }
    // Match the integrated light of the original subpixel vector grains.
    return .93*inkOver(dust,pigment[min(4,kind)],bead(uv,float2(1,.9525)));
}
