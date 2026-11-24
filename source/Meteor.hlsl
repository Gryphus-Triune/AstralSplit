// Flight geometry uses physical desktop pixels, including on mixed-DPI monitors.
cbuffer Flight : register(b0) {
    float2 viewport, center;
    float2 body; float unit, phase;
    float layer, outgoing; float2 target;
};
Buffer<float4> grains : register(t0);
Texture2D scene : register(t1);
SamplerState linearClamp : register(s0);
struct Vertex {
    float4 pos:SV_POSITION; float2 uv:TEXCOORD0;
    nointerpolation float4 ends:TEXCOORD1;
    nointerpolation float4 ink:TEXCOORD2;
    nointerpolation float size:TEXCOORD3;
};
float2 along(float t){
    float p=saturate(t/.89), q=saturate((p-.7)/.3);
    float join=pow(.7,2.3), tangent=2.3*pow(.7,1.3)*.3;
    float e=p<=.7?pow(p,2.3):join+(1-join)*q*q*(3-2*q)+tangent*q*(1-q)*(1-q);
    if(outgoing<.5)e=pow(1-saturate(t/.46),2.4);
    float2 control=float2(target.x+sign(target.x-center.x)*32,min(center.y-48,(center.y+target.y)/2));
    return lerp(lerp(center,control,e),lerp(control,target,e),e);
}
Vertex meteorVS(uint v:SV_VertexID,uint id:SV_InstanceID){
    Vertex o=(Vertex)0;o.uv=float2(v&1,v>>1);
    bool hide=outgoing>.5;
    float u=saturate((phase-.4)/.6)-1;
    float scale=hide?pow(1-saturate(phase/.62),1.7):max(0,1+2.3*u*u*u+1.3*u*u);
    float strength=hide?saturate((1-scale)*1.5):saturate((.62-phase)/.16);
    float glow=sin(3.14159265*phase);
    float3 gold=float3(255,184,108)/255.,white=float3(255,248,234)/255.,ice=float3(154,205,255)/255.;
    float2 at=center, extent=0;
    if(layer<.5){
        at=hide?along(phase):center;extent=.5*scale*body;o.ink.a=saturate((scale-.025)/.30);
    }else if(layer<1.5){
        float q=(id+1)/24.;float2 a=along(phase-.19+.19*id/24.),b=along(phase-.19+.19*q);
        o.ends=float4(a,b);o.size=.25+2*q;at=(a+b)*.5;extent=abs(b-a)*.5+o.size+1;
        o.ink=float4(float3(1,(170+77*q)/255.,(97+136*q)/255.),strength*.839*q*q);
    }else if(layer<2.5){
        at=along(phase);extent=(id==0?30:7)*unit;o.ink=float4(id==0?gold:white,strength);
    }else if(layer<3.5){
        uint i=id/2;float3 seed=grains[3*i].xyz;float a=seed.x+(1-phase)*1.5;
        at=center+float2(cos(a),sin(a))*pow(1-phase,1.58)*seed.y*body;
        extent=seed.z*unit*((id%2)==0?3.6:1);
        o.ink=float4((id%2)==1?white:(i%4==0?ice:gold),glow);
    }else if(layer<4.5){
        extent=(id==0?42:10)*glow*unit;o.ink=float4(id==0?gold:white,glow);
    }else{
        float wave=saturate((phase-.46)/.54);o.size=(6+130*wave)*unit;extent=o.size+2;
        o.ink=float4(white,.65*(1-wave)*(phase>.46));
    }
    float2 p=at+(2*o.uv-1)*extent;o.pos=float4(p/viewport*float2(2,-2)+float2(-1,1),0,1);
    return o;
}
float4 meteorPS(Vertex i):SV_TARGET {
    if(layer<.5){
        float2 q=abs((i.uv-.5)*body)-(.5*body-10);
        float edge=length(max(q,0))+min(max(q.x,q.y),0)-10;
        return scene.Sample(linearClamp,i.uv)*i.ink.a*saturate(.5-edge/max(fwidth(edge),.01));
    }
    float alpha;
    if(layer<1.5){
        float2 ab=i.ends.zw-i.ends.xy,ap=i.pos.xy-i.ends.xy;
        alpha=saturate(i.size+.5-length(ap-ab*saturate(dot(ap,ab)/max(.00001,dot(ab,ab)))));
    }else if(layer>4.5)alpha=saturate(1-abs(length(i.pos.xy-center)-i.size));
    else{
        float r=length(i.uv*2-1);alpha=r<.23?lerp(1,.439,r/.23):.439*saturate((1-r)/.77);
    }
    alpha*=i.ink.a;return float4(i.ink.rgb*alpha,alpha);
}
