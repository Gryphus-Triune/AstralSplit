// Each strip follows the field-line and breath equations in StellarProminence.cs.
// Geometry and time advance on the composition device, even between UI packets.
cbuffer SolarDraw : register(b0) {float4 originX, axisY, span, phaseData, pixelData;};
cbuffer FrameClock : register(b1) {float4 elapsed;};
static const float pi=3.14159265359;
static const float3 ice=float3(142,224,255)/255.,pearl=float3(248,252,255)/255.,gold=float3(255,199,131)/255.;
struct Sample {float4 position:SV_Position;float2 uv:TEXCOORD0;float4 ink:COLOR0;nointerpolation float halo:TEXCOORD1;};
float2 direction(float a){float s,c;sincos(a,s,c);return float2(c,s);}
float2 normalOf(float2 along){return float2(-along.y,along.x)/max(.0001,length(along));}
float2 fieldLine(float progress,float u,float depth,int loop) {
    float wave=sin(pi*progress),cs=cos(pi*progress),energy=sin(pi*u);
    float amp=(loop==0?45:loop==1?34:17)*pow(max(0,energy),1.32);
    float angle=(loop==0?-.79:loop==1?2.40:-2.25)+.09*energy+.04*depth;
    float twist=sin(2*pi*progress+.72*depth+u*3.1);
    float tang=-cs*(8+5*depth+.23*amp*wave)+wave*wave*(7*energy+2.1*twist);
    float outwards=8+wave*(4+amp*(.68+.33*depth))+.90*wave*twist;
    float2 axis=direction(angle);return axis*outwards+float2(-axis.y,axis.x)*tang;
}
Sample solarVS(uint vertex:SV_VertexID,uint instance:SV_InstanceID) {
    Sample o=(Sample)0;
    float phase=frac(phaseData.x+elapsed.x*phaseData.y),angle=2*pi*phase,breath=.5-.5*cos(angle);
    int section=(int)phaseData.z;
    float2 pos=0,rad=0,along=float2(1,0);
    float width=1,alpha=1,side=(vertex&1)?1:-1,progress=(vertex>>1)/32.;
    float3 color=ice;bool isHalo=false;
    if(section==-1 && instance==0){isHalo=true;rad=float2(59+16*breath,49+14*breath);alpha=.13+.15*breath;}
    else if(section==3){isHalo=true;color=pearl;rad=float2(13+4*breath,12+3*breath);alpha=.53+.20*breath;}
    else if(section==-1){
        uint i=instance-1;float a=2.399963*i+.15*sin(angle+i*.84);
        float fan=.5+.5*cos(2*(a+.66)),extent=13+(7+16*fan)*(1+.16*sin(angle+i*1.7));
        float turn=.18*sin(i+angle);
        float2 start=5*direction(a),tip=extent*direction(a+turn),mid=(extent+5)*.5*direction(a+turn*.5);
        float2 n=normalOf(tip-start),ctrl=(start+tip)*.5+n*(2*dot(mid-start,n));
        pos=lerp(lerp(start,ctrl,progress),lerp(ctrl,tip,progress),progress);
        along=lerp(ctrl-start,tip-ctrl,progress);
        width=.65+(i%4)*.17;color=(i%6==0)?gold:ice;
        alpha=(73./255.)*(1-progress)*(.45+.55*(.5+.5*cos(angle+i*.83)));
    }else{
        float u=frac(phase+(section==0?.20:section==1?.68:.49));
        float3 pigment=section==1?gold:ice;
        if(instance==28){isHalo=true;color=pigment;rad=float2(10,8);alpha=.16;pos=fieldLine(.54,u,.83,section);}
        else{
            uint filament=instance/4,kind=instance%4;
            float depth=filament/6.,head=min(.999,frac(u*1.07+(filament==2?.14:0)));
            if(kind>=2 && filament!=2 && filament!=6 || kind==0 && filament%3!=0)alpha=0;
            else if(kind==3){isHalo=true;color=pearl;rad=float2(3,2.1);alpha=.57*sin(pi*head);pos=fieldLine(head,u,depth,section);}
            else{
                float t=kind==2?lerp(max(0,head-.11),min(1,head+.05),progress):progress;
                pos=fieldLine(t,u,depth,section);
                along=fieldLine(min(1,t+.001),u,depth,section)-fieldLine(max(0,t-.001),u,depth,section);
                if(kind==0){width=4.4;color=pigment;alpha=.11;}
                if(kind==1){width=.48+.38*depth;color=lerp(pigment,pearl,.15+.18*depth);alpha=.15+.38*pow(depth,1.5);}
                if(kind==2){width=1.06;color=lerp(pigment,pearl,.61);alpha=.79*sin(pi*head);}
            }
        }
    }
    if(isHalo){
        uint corner=min(vertex,3u);o.uv=float2(corner&1,corner>>1)*2-1;
        pos+=o.uv*rad;o.halo=1;
    }else{
        float fringe=pixelData.x;
        float extent=.5*width+fringe;
        pos+=normalOf(along)*side*extent;o.uv=float2(side*extent/ max(.0001,fringe),.5*width/max(.0001,fringe));o.halo=0;
    }
    float2 loc=(pos+80)/160;
    o.position=float4(originX.xy+loc.x*originX.zw+loc.y*axisY.xy,0,1);
    o.ink=float4(color,alpha*span.z);return o;
}
float4 solarPS(Sample s):SV_Target {
    float a;
    if(s.halo>.5){
        float r=length(s.uv);
        if(r<.18)a=lerp(1,193./255.,r/.18);
        else if(r<.42)a=lerp(193./255.,86./255.,(r-.18)/.24);
        else if(r<.74)a=lerp(86./255.,14./255.,(r-.42)/.32);
        else a=(14./255.)*saturate((1-r)/.26);
    }else a=saturate(s.uv.y+.5-abs(s.uv.x));
    a*=s.ink.a;return float4(s.ink.rgb*a,a);
}