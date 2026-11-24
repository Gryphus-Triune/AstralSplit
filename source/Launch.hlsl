// Physical monitor pixels. A single instanced draw follows the same trajectories,
// depth layers and palette as StellarSingularity, independent of WPF's dispatcher.
cbuffer Flight : register(b0) {
    float2 viewport, origin;
    float2 bodySize; float scale, phase;
    float layer, diagnostic, waveRadius, reserved;
};
Buffer<float4> seeds : register(t0);
Texture2D home : register(t1);
SamplerState linearClamp : register(s0);
struct Vertex {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    nointerpolation float4 ends : TEXCOORD1;
    nointerpolation float4 ink : TEXCOORD2;
    nointerpolation float2 pen : TEXCOORD3;
};
static const float3 amber=float3(255,177,102)/255., pearl=float3(255,237,200)/255., ice=float3(163,210,246)/255.;
float2 path(float4 g,float u) {
    u+=g.w*u*(1-u);
    float radius=1-u*(.72+u*(.65-.37*u)), near=1-radius;
    float s,c; sincos(g.z*near*(.32+.68*near),s,c);
    return origin+radius*float2(dot(g.xy,float2(c,-s)),dot(g.xy,float2(s,c)));
}
Vertex launchVS(uint v:SV_VertexID,uint instance:SV_InstanceID) {
    Vertex o=(Vertex)0; float2 uv=float2(v&1,v>>1); o.uv=uv;
    float2 low,high;
    if(layer<.5) {
        float u=saturate((phase-.555)/.445), rem=1-u;
        float2 halfBody=.5*bodySize*(1-(1+4*u)*rem*rem*rem*rem);
        low=origin-halfBody; high=origin+halfBody;
    } else if(layer<1.5) {
        uint i=instance/4, part=instance%4;
        float4 g=seeds[3*i],life=seeds[3*i+1],kind=seeds[3*i+2];
        int depth=(int)kind.y;
        if(phase<=life.x || phase>=life.y || (depth==0 && part!=0) ||
            (depth==1 && (part==1 || part==2))) {o.position=float4(-2,-2,0,1);return o;}
        float u=(phase-life.x)/(life.y-life.x);
        float tail=life.z*(1-smoothstep(.90,1,u));
        float2 head=path(g,u), prior=path(g,max(0,u-tail));
        float2 neck=depth==2 ? path(g,max(0,u-tail*.38)):head;
        float2 a=part==0 ? prior : (part==1 ? neck : head);
        float2 b=part==0 ? neck : head;
        float strength=depth==0?.46:depth==1?.72:.97;
        bool streak=depth==0 || part==1;
        float r=part>=2 ? life.w*(part==2?4:1) : .5*scale*(streak?.80+.30*depth:.50+.27*depth);
        float3 color=kind.x<.5?amber:kind.x<1.5?pearl:ice;
        o.ink=float4(part==3?lerp(color,1,.27):color,strength*(part==3?1:part==2?.58:streak?.77:.39));
        o.pen=float2(r,part);o.ends=float4(a,b);
        low=min(a,b)-r-1;high=max(a,b)+r+1;
    } else if(layer<2.5) {
        float extent=max(75*scale,waveRadius+9*scale)+2;
        low=origin-extent;high=origin+extent;
    } else {low=float2(24,24);high=low+float2(96,6);}
    float2 p=lerp(low,high,uv);
    o.position=float4(p/viewport*float2(2,-2)+float2(-1,1),0,1);return o;
}
float4 rgba(float3 c,float a) {return float4(c*a,a);}
float4 over(float4 upper,float4 lower) {return upper+lower*(1-upper.a);}
float halo(float x) {
    return x<.17?lerp(1,.7,x/.17):x<.48?lerp(.7,.14,(x-.17)/.31):.14*saturate((1-x)/.52);
}
float4 wave(float2 p,float u,float reach,float aspect,float3 color,float strength,float width) {
    float dist=abs(length(p/float2(1,aspect))-(7*scale+reach*(1-(1-u)*(1-u))));
    float a=strength*(1-u)*(1-u)*smoothstep(0,.1,u)*saturate(.5+.5*width*scale-dist);
    return rgba(color,a*(u>0 && u<1));
}
float4 launchPS(Vertex i):SV_TARGET {
    if(layer<.5) {
        float2 d=abs((i.uv-.5)*bodySize)-(.5*bodySize-10);
        float edge=length(max(d,0))+min(max(d.x,d.y),0)-10;
        return home.Sample(linearClamp,i.uv)*smoothstep(.555,.735,phase)*saturate(.5-edge/max(fwidth(edge),.01));
    }
    if(layer<1.5) {
        float2 a=i.ends.xy, delta=i.ends.zw-a;
        float2 p=i.position.xy-a;
        float distance=length(p-delta*saturate(dot(p,delta)/max(.00001,dot(delta,delta))));
        float a0=i.pen.y==2?halo(distance/i.pen.x):saturate(i.pen.x+.5-distance);
        return rgba(i.ink.rgb,i.ink.a*a0);
    }
    if(layer>2.5) {
        uint id=(uint)round(phase*4094),bits=9u|(id<<4)|((4095u^id)<<16)|(6u<<28);
        float on=(float)((bits>>(uint(min(i.uv.x*32,31))))&1u);
        return float4(on,on,on,1);
    }
    float2 p=i.position.xy-origin;
    float charge=smoothstep(.105,.527,phase),energy=charge*(1-smoothstep(.56,.735,phase));
    float size=scale*(2+5.6*charge); float2 q=p/size;float r=length(q);
    float4 outColor=rgba(amber,.80*halo(r/6.8));
    outColor=over(rgba(ice,.5*halo(length(q/float2(4.3,.52)))),outColor);
    float3 inner=float3(36,28,43)/255.,mid=float3(46,33,40)/255., ivory=float3(255,250,240)/255.;
    float3 ring=r<.64?lerp(inner,mid,r/.64):r<.79?lerp(mid,amber,(r-.64)/.15):
        r<.88?lerp(amber,ivory,(r-.79)/.09):lerp(ivory,pearl,saturate((r-.88)/.12));
    outColor=over(rgba(ring,saturate((1-r)/.12)),outColor);
    float4 spark=over(rgba(pearl,.98*halo(r/1.5)),rgba(pearl,.98*halo(length(q/float2(9,.44)))));
    outColor=over(spark*smoothstep(.505,.57,phase),outColor)*energy;
    outColor=over(wave(p,(phase-.555)/.24,waveRadius,1,pearl,.62,.95),outColor);
    return over(wave(p,(phase-.603)/.28,waveRadius*.80,.92,ice,.49,.7),outColor);
}
