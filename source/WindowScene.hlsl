// A quad may sample an uploaded tile or a previous pass. All inputs use premultiplied BGRA.
Texture2D behind : register(t0);
Texture2D profile : register(t1);
Texture2D atlas : register(t2);
SamplerState sampleLinear : register(s0);
struct DrawRecord {
    float4 posX,posY,uvMode;
    float4 opticsData[7];
    float4 metadata,padding[5];
};
cbuffer Draw : register(b0){DrawRecord bank[256];};
static uint drawIndex;
// MILESTONE_FUNCTIONS
#define originX bank[drawIndex].posX
#define axisYuv bank[drawIndex].posY
#define spanMode bank[drawIndex].uvMode
#define params bank[drawIndex].opticsData
#define clockInfo bank[drawIndex].metadata
#define shape params[0].xyz
#define light params[1].xy
#define finish params[2].xyz
#define optics params[3].xyz
#define viewport params[4]
#define aperture params[5]
#define bevelShape params[6]
#define tex2D(tex,uv) tex.Sample(sampleLinear,uv)
cbuffer FrameClock : register(b1) { float4 sincePublish; };
struct Varying {float4 position:SV_Position;float2 uv:TEXCOORD0;nointerpolation uint index:TEXCOORD1;};
Varying windowSceneVS(uint id:SV_VertexID,uint instance:SV_InstanceID){
    drawIndex=instance;
    Varying v;float2 corner=float2(id&1,id>>1);
    v.position=float4(originX.xy+corner.x*originX.zw+corner.y*axisYuv.xy,0,1);
    // The surface contains the realized rows beyond both viewport edges. The
    // scissors stay in the viewport while the display clock moves its contents.
    int mode=(int)clockInfo.w;
    if((mode==22 || mode==23) && sincePublish.z==params[1].x){
        float pos=clamp(mode==22?sincePublish.y:sincePublish.w,params[0].y,params[0].z);
        v.position.y+=(pos-params[0].x)*params[0].w;
    }
    if((int)clockInfo.w==17){
        float2 local=(corner-.5)*float2(240,188);
        float w=1+dot(params[2].xy,local);float2 uv=.5+float2(dot(params[0].xy,local),dot(params[1].xy,local))/(w*float2(240,188));
        v.position=float4((originX.xy+uv.x*originX.zw+uv.y*axisYuv.xy)*w,0,w);
    }
    v.uv=corner;v.index=instance;return v;
}
#ifdef MILESTONE_PASS
float4 sceneFilmPS(Varying v):SV_Target{
    drawIndex=v.index;
    float4 frame=params[1];
    frame.x+=sincePublish.x*params[2].y;
    return milestoneFilm(params[0].xy+v.uv*params[0].zw,frame,params[2].x)*spanMode.z;
}
#endif
// OPTICAL_FUNCTIONS
float4 windowScenePS(Varying v):SV_Target{
    drawIndex=v.index;
    float2 uv=v.uv;
    int mode=(int)clockInfo.w;
    if(mode==21){
        float loop=params[0].z,phase=params[0].x+sincePublish.x*params[0].y;
        float pulse=sin(3.14159265*frac(phase+(loop==0?.20:loop==1?.68:.49)));pulse*=pulse;
        float opacity=pulse<.002?0:pulse*(loop==0?1:loop==1?.91:.43);
        return behind.Sample(sampleLinear,uv)*(spanMode.z*opacity);
    }
    if(mode==1)return params[0]*spanMode.z;
    if(mode==2){
        if(shape.x>=1)return stellarFull(uv)*spanMode.z;
        float4 source=finish.x>0 ? stellarStandby(uv) : behind.Sample(sampleLinear,uv);
        if(shape.x<=0)return source*spanMode.z;
        return lerp(source,opticalLens(uv,float4(0,0,1,1)),shape.x)*spanMode.z;
    }
    if(mode==15 || mode==16){
        float2 d=params[0].xy;
        if(mode==15)return (4*behind.Sample(sampleLinear,uv)+behind.Sample(sampleLinear,uv+d)+behind.Sample(sampleLinear,uv-d)
            +behind.Sample(sampleLinear,uv+float2(d.x,-d.y))+behind.Sample(sampleLinear,uv+float2(-d.x,d.y)))*(.125*spanMode.z);
        float4 c=behind.Sample(sampleLinear,uv+float2(2*d.x,0))+behind.Sample(sampleLinear,uv-float2(2*d.x,0))
            +behind.Sample(sampleLinear,uv+float2(0,2*d.y))+behind.Sample(sampleLinear,uv-float2(0,2*d.y));
        c+=2*(behind.Sample(sampleLinear,uv+d)+behind.Sample(sampleLinear,uv-d)+
             behind.Sample(sampleLinear,uv+float2(d.x,-d.y))+behind.Sample(sampleLinear,uv+float2(-d.x,d.y)));
        return c*(spanMode.z/12);
    }
    if(mode==17)return behind.Sample(sampleLinear,uv)*(spanMode.z*(params[3].x>0?profile.Sample(sampleLinear,uv).a:1));
    if(mode==18){
        float4 color=0;float sum=0;
        [unroll] for(int n=-7;n<=7;n++){float t=n/7.;float weight=exp(-4.5*t*t);color+=behind.SampleLevel(sampleLinear,uv+t*params[0].xy,0)*weight;sum+=weight;}
        return color*(spanMode.z/sum);
    }
    if(mode==19){float a=behind.Sample(sampleLinear,uv).a*params[0].a*spanMode.z;return float4(params[0].rgb*a,a);}
    if(mode==14)return behind.Sample(sampleLinear,uv)*saturate(min(uv.y/.14,(1-uv.y)/.32))*spanMode.z;
    if(mode==12){
        float2 at=axisYuv.zw+spanMode.xy*uv;
        float2 mirrored=1-abs(frac(at*.5)*2-1);
        return behind.SampleLevel(sampleLinear,lerp(frac(at),mirrored,params[0].xy),0)*spanMode.z;
    }
    if(mode==9)return sceneLens(uv)*spanMode.z;
    if(mode==10){
        float2 n=(uv-.5)*shape.xy;float r=length(n),radius=min(shape.x,shape.y)*.5,bevel=clamp(shape.y*.29,2.2,8);
        float si=saturate(1-(radius-r)/bevel),st=si/1.48,ci=sqrt(saturate(1-si*si)),ct=sqrt(saturate(1-st*st));
        float bend=(1.9/1.1)*bevel*(si*ct-ci*st)/max(.00001,ci*ct+si*st);
        return profile.Sample(sampleLinear,params[1].xy+params[1].zw*saturate(uv-n*(bend/max(.001,r))/shape.xy))*spanMode.z;
    }
    if(mode==11){
        float4 m=behind.Sample(sampleLinear,uv),rim=atlas.Sample(sampleLinear,uv);
        float2 loc=uv*float2(198,158)-float2(99,79);
        float2 fraction=float2(m.g,rim.g),indices=float2(m.r,rim.r)*255+fraction;
        float2 shifted=frac((float2(m.b,rim.b)*255+fraction+20)/40)*40;
        float2 angle=lerp(indices,shifted,step(0,dot(loc,float2(.9816271834,-.1908089954))));
        float e=profile.Sample(sampleLinear,float2((1.5+angle.x)/42,.25)).a;
        float o=profile.Sample(sampleLinear,float2((1.5+angle.y)/42,.75)).a;
        return lerp(e,o,smoothstep(7,14,abs(dot(loc,float2(.1908089954,.9816271834))-4.25))).xxxx*spanMode.z;
    }
    if(mode==3 || mode==4)return optical(uv,mode==4,true)*spanMode.z;
    if(mode==5)return bevelField(uv)*spanMode.z;
    if(mode==6){
        float3 ray=float3((uv-.5)*float2(240,188),1);
        float2 src=.5+float2(dot(ray,params[0].xyz),dot(ray,params[1].xyz))/dot(ray,params[2].xyz)/float2(240,188);
        if(any(src<0)||any(src>1))return 0;
        return behind.Sample(sampleLinear,src)*(spanMode.z*(params[3].x>0?profile.Sample(sampleLinear,src).a:1));
    }
    if(mode==7){
        float2 wind=-cos(float2(params[0].zw+sincePublish.x*params[1].y)*float2(3.14159265/19,3.14159265/26));
        float2 warp=wind.yx*(uv.yx-.5)*.015;
        float2 stepRay=(params[0].xy*float2(.07,.11)+wind*float2(.045,.065))*float2(.25,.5)/7;
        float2 p=(float2(.07,.10)+float2(.86,.80)*uv+warp)*float2(.25,.5)-3.5*stepRay;
        float4 c=0;
        [unroll] for(int i=0;i<8;i++){
            float2 tile=float2((i%4)*.25,(i/4)*.5);
            c+=(1-c.a)*behind.SampleLevel(sampleLinear,p+tile+i*stepRay,0);
        }
        c.rgb=min(c.aaa,c.rgb*lerp(float3(.76,.86,1.05),float3(1.15,1.03,.88),params[1].x));
        return c*lerp(.40,.68,params[1].x)*spanMode.z;
    }
    if(abs(spanMode.w)>.00001){
        float angle=-spanMode.w*sincePublish.x;float sine,cosine;sincos(angle,sine,cosine);float2 p=uv-.5;
        uv=.5+float2(p.x*cosine-p.y*sine,p.x*sine+p.y*cosine);
        if(any(uv<0)||any(uv>1))return 0;
    }
    float4 ink=behind.Sample(sampleLinear,axisYuv.zw+spanMode.xy*uv);
    if(mode==8)ink*=profile.Sample(sampleLinear,uv).a;
    return ink*spanMode.z;
}
