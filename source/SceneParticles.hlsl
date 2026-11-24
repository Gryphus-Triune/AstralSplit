// Native presentation advances the original emitter clock between UI updates.
Buffer<float4> records : register(t0);
Buffer<uint> ink : register(t1);
cbuffer Frame : register(b0) {float4 view;float4 clock;};
struct Pixel {float4 xy:SV_Position;float2 radius:TEXCOORD0;float4 rgba:COLOR;float layerClip:SV_ClipDistance0;float layerEnd:SV_ClipDistance1;};
float2 unitDirection(float2 d){return d*rsqrt(max(dot(d,d),1e-12));}
float2 project(float2 pt,float4 basis,float2 perspective){return float2(dot(pt,basis.xy),dot(pt,basis.zw))/(1+dot(pt,perspective));}
Pixel place(float2 at,float2 along,float2 across,float4 rgba,float layer,uint vertex){
    float2 uv=float2((vertex&1)*2.-1,(vertex>>1)*2.-1);
    float2 screen=(at+uv.x*along+uv.y*across-view.xy)*view.zw;
    Pixel p;p.xy=float4(2*screen.x-1,1-screen.y-layer,0,1);p.radius=uv;p.rgba=rgba;p.layerClip=screen.y;p.layerEnd=1-screen.y;return p;
}
Pixel nativeMoteVS(uint vertex:SV_VertexID,uint instance:SV_InstanceID){
    int k=instance*9;
    float4 a=records.Load(k),b=records.Load(k+1),gate=records.Load(k+2),ageInfo=records.Load(k+3);
    float4 cx=records.Load(k+4),cy=records.Load(k+5),disk=records.Load(k+6),depth=records.Load(k+7),flags=records.Load(k+8);
    float age=ageInfo.x+1000*clock.x*ageInfo.w,approach=ageInfo.y,radius=ageInfo.z;
    float travel=saturate(age/approach),u=saturate((age-approach)/1100.);
    bool inbound=age<=approach;float2 at,dir,raw;
    const float c=.9816271834,s=.1908089954,outer=69.3,inner=22;
    if(inbound){
        float endSpeed=6.2831853*sqrt(inner/outer)*approach/(3*990.*flags.x);
        float t=endSpeed>3?pow(travel,endSpeed):travel*travel*(3-endSpeed+(endSpeed-2)*travel),q=1-t;
        raw=q*q*q*a.xy+3*q*q*t*a.zw+3*q*t*t*b.xy+t*t*t*b.zw;
        float2 tangent=unitDirection(q*q*(a.zw-a.xy)+2*q*t*(b.xy-a.zw)+t*t*(b.zw-b.xy));
        dir=unitDirection(float2(dot(cx.xy,tangent),dot(cy.xy,tangent)));
        at=float2(dot(cx.xy,raw)+cx.z,dot(cy.xy,raw)+cy.z);
    }else{
        float elapsed=age-approach,r,theta,dr,da;
        if(elapsed<990){
            float t=elapsed/990.;r=outer+(inner-outer)*t*t;dr=2*(inner-outer)*t;
            float speed=6.2831853*sqrt(inner/r);theta=gate.w+speed*t;da=speed*(1-t*dr/(2*r));
        }else{float t=saturate((elapsed-990)/110.);r=inner-6*t;dr=-6;theta=gate.w+6.2831853+1.1*t;da=1.1;}
        float sn,cs;sincos(theta,sn,cs);
        float blend=saturate((outer-r)/(outer-48)),mid=3.9+blend,flat=.127+.023*blend;
        float db=r>48 && r<=outer?-dr/(outer-48):0;
        float ex=r*cs,ey=mid+flat*r*sn;float2 loc=float2(c*ex+s*ey,-s*ex+c*ey);
        float dx=dr*cs-r*sn*da,dy=db+(dr*sn+r*cs*da)*flat+r*sn*.023*db;
        float2 tangent=unitDirection(float2(c*dx+s*dy,-s*dx+c*dy));
        raw=gate.xy+gate.z*loc;float2 projected=project(loc,disk,depth.xy);
        at=gate.xy+gate.z*projected;
        dir=unitDirection(project(loc+tangent*(.1/gate.z),disk,depth.xy)-projected);
    }
    float2 v=(raw-gate.xy)/gate.z;
    float side=inbound?0:clamp(-(v.x*c-v.y*s)/35.,-1.,1.);
    float mid=3.9+saturate((outer-abs(v.x*c-v.y*s))/(outer-48));
    bool rear=inbound?flags.y>0:dot(depth.zw,v-float2(s,c)*mid)<0;
    int key=(int)round((.5*travel+.5*u)*47);
    int slot=key<24?key:24+(side<-.28?0:side>.28?2:1)*24+key-24;
    uint pigment=ink.Load((int)flags.z+slot);
    float4 col=float4((pigment>>16)&255,(pigment>>8)&255,pigment&255,(pigment>>24)&255)/255;
    col.a*=min(1.,.77*(1+(side>=0?.25:.30)*side)*saturate(age/100)*(1-saturate((u-.9)/.1)));
    if(age<0 || age>=approach+1100. || col.a<.005)col.a=0;
    return place(at,dir*(2.7*radius*(1+2.4*u*u)),float2(-dir.y,dir.x)*(2.7*radius*(1-.36*u)),col,rear?0:1,vertex);
}
Pixel nativeFieldVS(uint vertex:SV_VertexID,uint instance:SV_InstanceID){
    int k=instance*3;float4 a=records.Load(k),b=records.Load(k+1),d=records.Load(k+2);
    return place(a.xy,a.zw,b.xy,float4(b.z+clock.x*(.075+.23*b.w),b.w,d.x,d.y),d.z,vertex);
}
Pixel nativeDustVS(uint vertex:SV_VertexID,uint instance:SV_InstanceID){
    int k=instance*3;float4 a=records.Load(k),b=records.Load(k+1),d=records.Load(k+2);
    bool lit=b.w>1.5;float2 uv=float2(lerp(-(lit?max(2.65,1+b.z):1)-.32,(lit?2.65:1)+.32,(float)(vertex&1)),
                                       ((vertex>>1)*2.-1)*(lit?3.05:1.28));
    float2 screen=(a.xy+uv.x*a.zw+uv.y*b.xy-view.xy)*view.zw;
    Pixel p;p.xy=float4(2*screen.x-1,1-screen.y-d.z,0,1);p.radius=uv;p.rgba=float4(b.zw,d.xy);p.layerClip=screen.y;p.layerEnd=1-screen.y;return p;
}