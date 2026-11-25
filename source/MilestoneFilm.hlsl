// Shared foreground light and occultation for the native and WPF skies.
static const float filmPI=3.14159265;
static const float filmTAU=6.28318530;

float filmClamp01(float x){ return clamp(x,0.0,1.0); }
float filmGauss(float x){ return exp(-x*x); }
float filmSquared(float x){ return x*x; }

float filmHash(float2 p){
    float3 q=frac(p.xyx*float3(.1031,.11369,.13787));
    q+=dot(q,q.yzx+19.19);
    return frac((q.x+q.y)*q.z);
}

float2 filmHashPair(float2 p){ return float2(filmHash(p),filmHash(p+float2(37.0,11.0))); }

float filmValueNoise(float2 p){
    float2 i=floor(p), f=frac(p);
    float2 w=f*f*(3.0-2.0*f);
    return lerp(lerp(filmHash(i),filmHash(i+float2(1,0)),w.x),
               lerp(filmHash(i+float2(0,1)),filmHash(i+float2(1,1)),w.x),w.y);
}
float2x2 filmRotate(float a){ float c=cos(a),s=sin(a); return float2x2(c,s,-s,c); }

float filmClouds(float2 p){
    float2x2 turn=float2x2(1.62,1.21,-1.21,1.62);
    float f=.51*filmValueNoise(p);
    p=mul(p,turn)+float2(7.1,3.2);
    f+=.26*filmValueNoise(p);
    p=mul(p,turn)+float2(7.1,3.2);
    f+=.14*filmValueNoise(p);
    p=mul(p,turn)+float2(7.1,3.2);
    f+=.06*filmValueNoise(p);
    p=mul(p,turn)+float2(7.1,3.2);
    return f+.03*filmValueNoise(p);
}

float filmGrain(float2 coords, float threshold, float radius){
    float2 tile=floor(coords);
    float2 center=.15+.70*filmHashPair(tile+13.7);
    float bright=filmHash(tile+117.0);
    float core=filmGauss(length(frac(coords)-center)/radius);
    return smoothstep(threshold,1.0,bright)*core;
}

float filmMovingPulse(float seconds, float rate, float width){
    return filmGauss((frac(seconds*rate)-.50)/width);
}

static const float3 filmNearVent=float3(.109,.093,.069);
static const float3 filmNearVentAxis=float3(.739,.533,.412);
static const float3 filmLowerVent=float3(.106,-.097,.040);
static const float3 filmLowerVentAxis=float3(.506,-.758,.411);
static const float3 filmRearVent=float3(.086,.047,-.122);
static const float3 filmRearVentAxis=float3(.486,.461,-.743);

float3 filmPlumeBend(float distance, float seed){
    float drift=distance*distance/(.55+distance);
    return float3(-.31,.14*seed,.09)*drift;
}

float filmNucleusPlume(float3 position,float3 origin,float3 axis,float reach,float seed,float time){
    float3 local=position-origin;
    float along=dot(local,axis);
    if(along<-.041 || along>reach) return 0.0;
    float flow=max(0.0,along);
    float width=.074+.232*flow;
    float3 offAxis=local-axis*flow-filmPlumeBend(flow,seed);
    float3 slip=float3(.016,.048,-.057)*sin(flow*7.8-time*3.1+seed*8.0)*flow;
    float spread=length(offAxis-slip)/width;
    if(spread>3.0) return 0.0;
    float launch=smoothstep(-.041,.081,along);
    float dissipation=1.0-smoothstep(reach*.44,reach,flow);
    float packet=filmMovingPulse(time-flow*1.27+seed*.73,1.04,.153);
    float textureValue=filmValueNoise(float2(flow*7.5-time*3.7+seed*30.0, offAxis.y*15.8+offAxis.z*12.0));
    float filament=filmGauss(spread*1.12)*(.39+textureValue*.77);
    return filament*launch*dissipation*(.55+.84*packet)/(1.0+flow*.86);
}

float3 filmCometComa(float2 view,float unit,float time,float resolution,out float nucleus){
    float radius=length(view);
    nucleus=0.0;
    if(radius>=2.58) return (float3)(0.0);

    float theta=atan2(view.y,view.x);
    float edge=.171*(1.0+.115*cos(theta*3.0+.8)+.067*sin(theta*7.0-1.4));
    float2 rock=mul(view,filmRotate(-.38))*float2(.91,1.12)/edge;
    float silhouette=length(rock);
    float aa=max(.064,.64/(unit*edge*resolution));
    nucleus=1.0-smoothstep(1.0-aa,1.0+aa,silhouette);
    float surfaceDepth=sqrt(max(0.0,1.0-dot(rock,rock)));

    float3 scatter=(float3)(0.0);
    float transmitted=1.0;
    // Depth slices are denser near the nucleus, and the solid face hides material behind it.
    [loop] for(int i=0;i<12;i++){
        float t=1.0-2.0*(float(i)+.5)/12.0;
        float z=2.3*t*(.25+.75*abs(t));
        float stepSize=2.3*(.25+1.5*abs(t))*(2.0/12.0);
        float visible=1.0-nucleus*(1.0-smoothstep(surfaceDepth*.15-.07,surfaceDepth*.15+.07,z));
        if(visible<.001) continue;

        float3 position=float3(view,z);
        float distance=length(position);
        float3 heading=position/max(.0001,distance);
        float breeze=filmValueNoise(float2(7.1*heading.y+4.1*heading.z,distance*5.36-time*2.39));
        float cells=filmValueNoise(position.xy*4.0+position.z*float2(4.6,-2.7)+float2(time*.49,21.7));
        float gaps=smoothstep(.25,.65,cells*.62+breeze*.38);
        float sunward=smoothstep(-.42,.78,heading.x);
        float3 cloudSpace=position*float3(lerp(.69,1.23,sunward),1.10,1.03);
        float falloff=exp(-2.32*length(cloudSpace));
        float haze=falloff*(.19+1.10*gaps)*(.60+.41*sunward);
        float high=filmNucleusPlume(position,filmNearVent,filmNearVentAxis,1.77,.113,time);
        float low=filmNucleusPlume(position,filmLowerVent,filmLowerVentAxis,1.47,.683,time);
        float rear=filmNucleusPlume(position,filmRearVent,filmRearVentAxis,1.89,.421,time);

        float3 fog=lerp(float3(.036,.36,.65),float3(.097,.68,.69),sunward*.86)*haze*.87;
        float3 jets=float3(.68,1.25,1.34)*high*.86+float3(.11,.74,1.07)*low*.72+float3(.21,.64,.93)*rear*.54;
        float weight=visible*stepSize;
        scatter+=(fog+jets)*weight*transmitted;
        transmitted*=exp(-weight*(haze*.19+(high+low+rear)*.13));
    }

    if(nucleus>.0){
        float3 normal=normalize(float3(rock,surfaceDepth));
        float incidence=max(0.0,dot(normal,float3(.835,.240,.495)));
        float mineral=filmValueNoise(rock*5.2+float2(surfaceDepth*3.41,18.9));
        float3 face=lerp(float3(.027,.034,.042),float3(.74,.64,.54),incidence*(.28+.54*mineral));
        scatter+=face*nucleus*transmitted;
    }
    float upperSpark=filmGauss(length((view-filmNearVent.xy)*float2(1.06,1.28))/.112);
    float lowerSpark=filmGauss(length(view-filmLowerVent.xy)/.104);
    float upperBeat=filmMovingPulse(time+.098,1.04,.155);
    float lowerBeat=filmMovingPulse(time+.57,1.04,.155);
    scatter+=float3(1.19,1.04,.81)*upperSpark*(1.64+.66*upperBeat);
    scatter+=float3(.45,1.14,1.48)*lowerSpark*(.87+.55*lowerBeat);
    float bloom=filmGauss(length((view-float2(.081,.027))*float2(1.07,1.1))/.365);
    scatter+=float3(.22,.68,.69)*bloom*(.40+.20*upperBeat);

    // The motes leave individual vents and disperse at different depths.
    [loop] for(int i=0;i<15;i++){
        float index=float(i);
        float2 random=filmHashPair(float2(index,109.7));
        float age=frac(time*(.57+.39*random.x)+random.y);
        bool upper=fmod(index,3.0)<1.0;
        bool back=fmod(index,3.0)>1.0;
        float3 origin=upper?filmNearVent:back?filmRearVent:filmLowerVent;
        float3 axis=upper?filmNearVentAxis:back?filmRearVentAxis:filmLowerVentAxis;
        float distance=age*(.98+.62*random.x);
        float fan=(random.y-.5)*.44;
        float3 ray=axis+float3(-fan*.25,fan,-fan*.38);
        float3 particle=origin+ray*distance+filmPlumeBend(distance,(random.x-.5)*2.0);
        float2 delta=view-particle.xy;
        float2 projection=normalize(ray.xy);
        float width=.013+.017*random.x+(back?.0:.009);
        float lengthwise=dot(delta,projection)/(width*(1.2+.68*age));
        float crosswise=dot(delta,float2(-projection.y,projection.x))/width;
        float light=filmGauss(length(float2(lengthwise,crosswise)));
        float life=smoothstep(.045,.16,age)*(1.0-smoothstep(.52,.98,age));
        float depth=back?(.49*(1.0-nucleus)):.92;
        float3 pigment=upper?float3(1.08,.83,.53):float3(.31,.80,1.22);
        scatter+=pigment*light*life*depth*(.30+.54*random.y);
    }
    return scatter*(1.0-smoothstep(2.02,2.58,radius));
}

float3 filmComet(float2 p,float4 frame,out float cover){
    float time=frame.x,aspect=frame.y,resolution=frame.z;
    cover=0;
    float travel=filmClamp01(time/6.6);
    float sweep=.23*travel+.77*smoothstep(.065,.945,travel);
    float closePass=filmGauss((time-3.73)/1.11);
    float2 head=float2(lerp(-.555,.560,sweep)*aspect,
                   (lerp(-.43,.43,travel)+.05*sin(filmPI*travel))*frame.w);
    float2 offset=p-head;
    // The ion tail stays pointed away from the same illuminator as the filmComet travels.
    float2 lightDir=normalize(float2(1.06*aspect,.235*frame.w)-head);
    float2 side=float2(-lightDir.y,lightDir.x);
    float s=-dot(offset,lightDir);
    float across=dot(offset,side);
    float flow=max(0.0,s);
    float active=smoothstep(.0,.62,time)*(1.0-smoothstep(5.90,6.60,time));
    float comaScale=.79+.57*closePass;
    float radius=length(offset);
    float3 outLight=(float3)(0.0);
    if(s>-.075 && s<1.57 && abs(across)<.67){
        float gate=smoothstep(-.033,.040,s);
        float reach=lerp(.27,1.48,smoothstep(.04,2.86,time));
        float draft=1.0-smoothstep(reach*.54,reach,s);
        float swept=.036*smoothstep(.07,.94,flow)*sin(flow*8.8-time*2.1);
        float dustCurve=-.238*pow(flow,1.51)+swept;
        float dustWidth=.016+(.092+.062*closePass)*pow(flow,.82);
        float dy=(across-dustCurve)/dustWidth;
        float dustProfile=filmGauss(dy*1.38);
        float2 dustCoords=float2(flow*6.7-time*.74,dy*3.09+flow*.41);
        float density=filmClouds(dustCoords+float2(13.4,4.2));
        float fibre=filmValueNoise(float2(s*20.5-time*2.8,dy*19.6+density*1.35));
        float dust=dustProfile*draft*gate*(.19+pow(density,2.4)*2.55)*(.48+fibre*.77);
        float puffs=filmMovingPulse(time-flow/.63+.28,.76,.23);
        outLight+=dust*float3(.83,.357,.117)*(.173+.098*puffs);
        float granules=filmGrain(float2(s*87.0-time*27.0,(across-dustCurve)*151.0)+41.0,.53,.153);
        float glints=filmGrain(float2(s*42.0-time*14.2,dy*45.0)+float2(11.6,31.4),.72,.165);
        outLight+=((granules*.29+glints*.50)*filmGauss(dy*.88)*draft*gate)*float3(1.32,.81,.37);
        float spread=smoothstep(.03,.86,flow);
        float eddy=.025*spread*sin(flow*14.0-4.9*time)+.010*spread*sin(flow*32.0-5.8*time);
        float width=.0069+.008*flow+.002*closePass;
        float hot=filmGauss((across-eddy)/width);
        float upper=filmGauss((across-(.007+.053*flow+eddy*.68))/(width*.78+flow*.009));
        float lower=filmGauss((across-(-.008-.039*flow+eddy*1.24))/(width*.92+flow*.009));
        float ribbon=filmClouds(float2(s*10.8-time*3.65,across*95.0+.82*sin(time*1.27)));
        float turbulence=smoothstep(.22,.69,ribbon);
        float wake=filmGauss((across-eddy)/(.029+flow*.037));
        // Packets light up downstream according to their travel time from the nucleus.
        float pulseA=filmMovingPulse(time-flow/1.14+.28,1.37,.128);
        float pulseB=filmMovingPulse(time-flow/.82+.51,1.11,.159);
        float fine=pow(.5+.5*sin(s*78.0-time*22.5+ribbon*14.0),4.0);
        float ions=gate*draft*(hot*(.41+.95*pulseA)+upper*(.20+.65*pulseB)
                   +lower*(.21+.48*pulseA)+wake*.088)*(.29+turbulence*.71)*(.82+fine*.21);
        outLight+=ions*float3(.112,.52,1.74);
        outLight+=float3(.26,.91,1.76)*(hot*.79+upper*.21)*pulseA*filmGauss((flow-.19)/.39)*draft*gate*.30;
    }
    float comaUnit=.0522*comaScale;
    if(radius<2.58*comaUnit && active>.001){
        float nucleus;
        float2 view=float2(dot(offset,lightDir),across)/comaUnit;
        float3 coma=filmCometComa(view,comaUnit,time,resolution,nucleus);
        cover=nucleus*.95*active;
        outLight=outLight*(1.0-.94*nucleus)+coma;
    }
    return outLight*active;
}

float filmDet2(float2 x,float2 y){ return x.x*y.y-x.y*y.x; }

float3 filmPulsar(float2 p,float4 frame){
    float time=frame.x;
    const float period=1.337;
    const float tilt=.82030475;
    const float inclination=.69813170;
    const float azimuth=.71;
    float3 eye=float3(0.0,0.0,1.0);
    float3 spinAxis=float3(sin(inclination)*cos(azimuth),sin(inclination)*sin(azimuth),cos(inclination));
    float3 equatorX=normalize(eye-dot(eye,spinAxis)*spinAxis);
    float3 equatorY=cross(spinAxis,equatorX);
    float phase=filmTAU*time/period-.84;
    float3 magnetic=cos(tilt)*spinAxis+sin(tilt)*(cos(phase)*equatorX+sin(phase)*equatorY);
    float projection=max(.02,length(magnetic.xy));
    float2 axis=magnetic.xy/projection;
    float2 normal=float2(-axis.y,axis.x);
    float2 r=p-float2(-.018,-.008);
    float radius=length(r);
    float emerge=smoothstep(0.0,.50,time);
    float align=filmGauss(acos(clamp(magnetic.z,-1.0,1.0))/.206);
    float side=dot(r,axis);
    float travelled=abs(side)/projection;
    float across=dot(r,normal);
    float visiblePole=sign(side)*magnetic.z;
    float3 light=(float3)(0.0);
    float beamRange=max(1.11,frame.y*1.16);
    if(travelled<beamRange && abs(across)<.042+.245*travelled){
        // A beam pointing toward the viewer foreshortens with its projected 3D axis.
        float cone=.004+.082*travelled;
        float ridge=across/cone;
        float wake=filmClouds(float2(travelled*16.4-time*5.9,ridge*2.75+sign(side)*5.3));
        float pulse=filmMovingPulse(time-travelled*.94+.29,1.0/period,.15);
        float reach=exp(-1.45*min(travelled,1.0)-.84*travelled/beamRange)
                   *(1.0-smoothstep(.81,1.0,travelled/beamRange));
        float emergeBeam=smoothstep(.021,.056,abs(side));
        float core=filmGauss(ridge*2.9)*(.67+.78*pulse);
        float wings=filmGauss(ridge*.94)*(.20+.45*pulse+.17*wake);
        float scatter=(core+wings)*emergeBeam*reach*(.32+wake*.81);
        float3 spectrum=lerp(float3(.39,.24,1.08),float3(.14,.61,1.65),smoothstep(-.65,.68,visiblePole));
        light+=spectrum*scatter*(.88+.39*visiblePole);
        float filament=filmGauss((ridge-.53*sin(travelled*7.6-time*6.7))/.11);
        float twin=filmGauss((ridge+.48*sin(travelled*8.9-time*5.2+.73))/.135);
        light+=(float3(.39,.31,1.03)*filament*.65+float3(.21,.55,1.05)*twin*.35)
               *reach*emergeBeam*(.17+.34*pulse);
        float mote=filmGrain(float2(travelled*87.0-time*29.0,across*151.0)+sign(side)*19.0,.63,.152);
        light+=mote*filmGauss(ridge*.84)*reach*float3(.50,.79,1.53)*.49;
    }

    float starSize=.038;
    if(radius>starSize*.94 && radius<.35){
        float3 longitudeX=normalize(spinAxis-dot(spinAxis,magnetic)*magnetic);
        float3 longitudeY=cross(magnetic,longitudeX);
        float3 flux=(float3)(0.0);
        float structure=filmValueNoise(r*98.5+float2(time*.91,14.7));
        [loop] for(int i=0;i<6;i++){
            float layer=float(i);
            float longitude=.34+filmPI*(layer/6.0);
            float3 meridian=cos(longitude)*longitudeX+sin(longitude)*longitudeY;
            // Invert the projected dipole meridian to evaluate r = L sin²(theta).
            float determinant=filmDet2(magnetic.xy,meridian.xy);
            float visible=smoothstep(.06,.23,abs(determinant));
            if(visible>.0){
                float2 position=float2(filmDet2(r,meridian.xy),filmDet2(magnetic.xy,r))/determinant;
                float distance=length(position);
                float depth=magnetic.z*position.x+meridian.z*position.y;
                float fieldLine=distance*distance*distance/(position.y*position.y+.000001);
                float thickness=.0027+.026*radius;
                float contour=filmGauss((fieldLine-(.094+layer*.0079))/thickness);
                contour+=.57*filmGauss((fieldLine-(.166+layer*.0185))/(thickness*1.18));
                float passing=pow(.5+.5*sin(position.x*43.0-time*7.0+layer*.67),3.0);
                float3 hue=lerp(float3(.30,.23,.88),float3(.14,.60,1.37),smoothstep(-.10,.14,depth));
                flux+=hue*contour*visible*(.22+.45*passing+.25*structure)*(.72+.28*sign(depth));
            }
        }
        light+=flux*smoothstep(starSize*.96,starSize*1.23,radius)*(1.0-smoothstep(.18,.35,radius))*.31;
    }
    float2 disc=r/starSize;
    float z=sqrt(max(0.0,1.0-dot(disc,disc)));
    if(radius<starSize+.0035){
        float3 surface=float3(disc,z);
        float longitude=atan2(dot(surface,equatorY),dot(surface,equatorX))-phase;
        float latitude=asin(clamp(dot(surface,spinAxis),-1.0,1.0));
        float2 marble=float2(cos(longitude)*(6.5+latitude*1.53),sin(longitude)*6.5+latitude*9.4);
        float textureValue=filmClouds(marble+float2(8.0,23.8));
        float crest=smoothstep(.24,.75,textureValue);
        float limb=pow(z,.64);
        float spot=filmGauss(acos(clamp(dot(surface,magnetic),-1.0,1.0))/.235);
        float farSpot=filmGauss(acos(clamp(-dot(surface,magnetic),-1.0,1.0))/.22);
        float cut=1.0-smoothstep(starSize-.0011,starSize+.0023,radius);
        light+=cut*float3(.28,.63,1.28)*(.12+limb*(.58+1.30*crest)+spot*(1.0+align*1.68)+farSpot*.82);
    }
    light+=float3(.22,.52,1.5)*filmGauss(radius/.062)*(.25+.65*align);
    light+=float3(.74,.88,1.37)*exp(-abs(r.x)/(.035+.078*align))*filmGauss(r.y/.0023)*(.15+.91*align);
    light+=float3(.77,.95,1.19)*filmGauss(length(r-magnetic.xy*.031)/.010)*(.15+align*3.0);
    return light*emerge;
}

float3 filmRemnant(float2 local,float age){
    float radius=length(local);
    if(radius>1.62) return (float3)(0.0);
    float signAge=min(age,6.5);
    float2 p=mul(local,filmRotate(-.20))*float2(.97,1.127);
    float a=atan2(p.y,p.x);
    float3 gas=(float3)(0.0);
    [loop] for(int i=0;i<5;i++){
        float plane=float(i);
        float z=plane*.31-.62;
        float2 onPlane=p+float2(-z*.09,z*.138);
        float broad=filmClouds(onPlane*2.5+float2(16.3+z*3.7,22.1-z*4.1));
        float2 distorted=onPlane+float2(broad-.46,filmValueNoise(onPlane*4.1+31.3+z*2.42)-.5)*.26;
        float distance=length(float3(distorted,z*.89));
        float fold=.91+(broad-.48)*.54+.086*sin(3.0*a+z*5.0)+.079*cos(a*5.0-z*1.9);
        float membrane=filmGauss((distance-fold)/.090);
        float lip=filmGauss((distance-fold+.047)/.027);
        float plasma=filmClouds(distorted*8.9+float2(63.0+z*4.9,5.7+z*5.5));
        float shear=filmValueNoise(distorted*20.0+plasma*1.3+float2(z*11.0,plane*.81));
        float folds=filmGauss((plasma-.54)/.062);
        float smallThread=filmGauss((shear-.60)/.111);
        float dense=pow(plasma,.88)*smoothstep(.335,.66,broad);
        float metals=filmClouds(onPlane*float2(2.7,3.1)+float2(4.3,12.7+z));
        float3 outer=lerp(float3(.008,.40,.51),float3(.16,.075,.48),smoothstep(.30,.66,broad+metals*.09));
        float3 alloy=lerp(float3(.94,.23,.089),float3(.22,.49,.48),smoothstep(.40,.67,plasma));
        gas+=outer*(membrane*.41+lip*.20)*dense*1.75;
        gas+=alloy*membrane*(.12+folds*.68)*smallThread*dense*.83;
        gas+=float3(1.13,.49,.20)*lip*folds*pow(filmClamp01(shear),3.3)*smoothstep(.37,.68,plasma)*.66;
    }
    float strand=filmClouds(p*11.5+float2(3.8,14.7));
    float interior=filmGauss((radius-.54)/.40)*filmGauss((strand-.55)/.043);
    float hotPlasma=smoothstep(.44,.74,filmClouds(p*float2(3.3,5.0)+12.8));
    gas+=lerp(float3(.32,.086,.29),float3(.48,.25,.16),smoothstep(.47,.76,hotPlasma))*interior*(.20+hotPlasma*.48);
    float embers=filmGrain(local*float2(42.0,45.0)+float2(-signAge*.81,signAge*.46),.80,.165);
    gas+=float3(.98,.50,.29)*embers*filmGauss((radius-.78)/.33)*.53;
    return gas*(1.0-smoothstep(1.20,1.61,radius));
}

float3 filmSupernova(float2 p,float4 frame){
    float time=frame.x;
    float2 core=p-float2(-.062,.012);
    float radius=length(core);
    float ignition=1.16;
    float age=max(0.0,time-ignition);
    float a=atan2(core.y,core.x);
    float explode=step(ignition,time);
    float launch=smoothstep(.0,.42,age);
    float seedSize=lerp(.047,.013,smoothstep(.1,1.16,time));
    float tremor=.90+.095*sin(time*11.4)+.045*cos(time*23.0);
    float3 light=float3(.55,.65,1.30)*filmGauss(radius/(seedSize*tremor))*(1.0-explode)*1.32;
    light+=float3(1.15,.80,.53)*filmGauss(radius/(seedSize*.36))*(1.0-explode)*1.3;
    float burst=explode*exp(-age*5.4)*smoothstep(-.014,.065,age);
    light+=burst*(float3(1.5,.91,.53)*filmGauss(radius/.126)*2.4
                  +float3(1.18,1.08,.94)*filmGauss(radius/.041)*8.5);
    light+=burst*float3(1.15,.87,.57)*exp(-abs(core.x)*4.8)*filmGauss(core.y/.0039)*1.65;
    light+=burst*float3(.79,1.0,1.45)*exp(-abs(core.y)*8.8)*filmGauss(core.x/.0022)*.96;
    if(explode>.5){
        // Distances stay isotropic. The slowest part of the wave clears even the farthest corner.
        float travel=age/5.8;
        float waveR=.035+frame.y*1.08*travel*(1.15-.15*saturate(travel));
        float waveOffset=radius-waveR*(1.0+.017*sin(a*5.0)+.024*sin(a*2.0+.37));
        float edge=filmGauss(waveOffset/(.0051+.0037*age));
        float compression=filmGauss((radius-waveR*.967)/(.024+.008*age));
        float shockFade=exp(-age*.19)*smoothstep(.025,.28,age)*(1.0-smoothstep(6.3,8.1,age));
        float gradients=.45;
        if(edge+compression>.00012)gradients=filmClouds(core*9.5+float2(age*.3,2.8));
        float3 sheath=lerp(float3(.19,.71,1.42),float3(.88,.39,1.21),smoothstep(.24,.69,gradients));
        light+=(edge*(.44+gradients*.68)+compression*.13)*sheath*shockFade*1.44;
        float wave2=filmGauss((radius-(waveR*.76-.012))/(.008+.002*age));
        light+=float3(.55,.30,.69)*wave2*shockFade*smoothstep(.22,.68,age)*.34;
        float scale=.026+.302*(1.0-exp(-pow(age*.60,.81)));
        float2 local=core/scale;
        if(radius<1.63*scale){
            light+=filmRemnant(local,age)*launch*(.89+.59*exp(-age*.40))*1.42;
        }
        if(age<7.9 && radius<waveR*1.25 && radius>.041){
            float shellFade=smoothstep(.10,.38,age)*(1.0-smoothstep(3.6,7.9,age))
                          *(1.0-smoothstep(1.00,1.25,radius/waveR));
            float2 spray=float2(radius/max(.022,waveR*.73),a/filmTAU);
            // Sample the same ejecta for the whole flight. Limit the angular size of distant grains.
            float grainWidth=.145*rsqrt(1.0+.68*waveR);
            float jets=filmGrain(float2(spray.x*24.7,spray.y*186.0)+29.4,.74,grainWidth);
            float sheet=filmGauss((spray.x-.94)/.56);
            light+=float3(1.8,.75,.29)*jets*sheet*shellFade*.67;
        }
        light+=filmGauss(radius/.013)*float3(.43,.65,1.04)*(.18+exp(-age*.65)*.32);
    }
    return light;
}

float filmWrappedAngle(float a){ return atan2(sin(a),cos(a)); }

// Coordinates are centered on the emitting body; +Y points up. The shadow plate
// uses a -11 degree tilt in screen coordinates and compresses its lower half by 1.19.
// A film unit is four shadow-art units. The half-opacity limb in the plate is 1.0105.
float filmHorizonRadius(float2 outward){
    float lower=max(0.0,dot(outward,float2(.190809,-.981627)));
    return (1.0105/4.0)*rsqrt(1.0+(1.19*1.19-1.0)*lower*lower);
}

float3 filmCorona(float2 p,float4 frame){
    float time=frame.x;
    float distance=length(p);
    float2 normal=distance>1e-6?p/distance:float2(1,0);
    float angle=atan2(normal.y,normal.x);
    float onHorizon=frame.w;
    float limb=onHorizon*filmHorizonRadius(normal);
    float aa=min(.025,.65/max(1.0,frame.z));
    float height=distance-limb;
    if(height < -aa)return (float3)0;

    float ray=max(0.0,height);
    // Only the extended wind uses the window scale. Facets, footpoints and the inner corona
    // stay in source space through both stellar breathing and horizon formation.
    float ratio=ray/max(1.0,frame.y);
    float az=angle+.083*sin(angle*3.0+ratio*4.4)+.043*sin(ratio*9.1-time*.71);
    float fan=pow(.5+.5*cos(az*5.0-.52),6.0);
    float thread=pow(.5+.5*cos(az*37.0+ratio*6.5-time*.30),8.0);
    float windLight=.078*smoothstep(.10,.68,ray)*fan*(.23+.77*thread)
                  / (1.0+.55*ray) * (1.0-smoothstep(.90,1.12,ratio));
    float3 outerLight=windLight*lerp(float3(.55,.74,1.07),float3(1.16,.81,.54),.5+.5*cos(az*2.0));
    if(distance>=.99)return outerLight;
    float seed=filmClouds(normal*5.1+float2(12.4,8.3));
    float outer=smoothstep(.004,.39,ray);
    // The wind bends the ends of the streamers. Their footpoints stay on the source.
    float wind=outer*(.098*sin(2.12*time-17.4*ray+angle*2.0)
                     +.063*sin(3.2*time-24.5*ray-angle*3.0));
    float projected=angle-ray*.73*sin(angle*2.0+.43)+wind;
    float fans=1.12*filmGauss(filmWrappedAngle(projected+2.50)/.43)
              +.81*filmGauss(filmWrappedAngle(projected-.16)/.46)
              +.58*filmGauss(filmWrappedAngle(projected-1.87)/.31)
              +.48*filmGauss(filmWrappedAngle(projected+1.04)/.31);
    float twist=ray*(1.33+1.80*sin(angle*2.0-.30))+wind;
    float2 curled=mul(normal,filmRotate(twist));
    float contour=filmClouds(curled*(13.0+ray*10.7)+float2(5.0,19.0)+float2(4.5*ray,-3.0*ray));
    float strands=filmClouds(mul(normal,filmRotate(-twist*.22))*46.5+float2(ray*18.7-time*1.40,ray*4.5+time*.47));
    float2 streamline=float2(cos(projected),sin(projected));
    float currents=filmClouds(streamline*19.7+float2(contour*3.4,ray*40.0-time*6.5));
    float power=(.26+seed*seed*.88)*(.23+fans*.98);
    float taper=exp(-ray/(.033+power*.279));
    float filaments=filmGauss((contour-.526)/.125)*(.24+.84*pow(strands,2.06));
    float branches=pow(strands,3.7)*exp(-ray/(.048+power*.37))*(.22+fans*.78);
    float root=filmGauss(ray/(.022+.040*power));
    float current=pow(.5+.5*cos(ray*57.4-time*6.7+sin(projected*3.0)*2.9+contour*5.0),3.5);
    float flowLight=.31+currents*.93+current*.55;
    float glow=(taper*(.024+filaments*.44)*(.32+power*1.47)+branches*.39)*flowLight+root*.19;
    float3 tone=lerp(float3(.52,.70,1.21),float3(1.35,.88,.49),smoothstep(.30,.76,seed));
    float3 light=tone*glow;

    if(onHorizon>.5){
        if(height<.10){
            // The two bevels meet on a continuous edge. Each fixed kite-shaped facet
            // reflects the travelling light at a slightly different angle.
            float cell=frac(angle*(21.0/filmTAU)+.13*sin(3.0*angle)+.5)-.5;
            float kite=1.0-2.0*abs(cell);
            float width=max(.0082,1.65*aa);
            float outerEdge=width*(.72+.72*kite);
            float bevel=saturate(ray/outerEdge);
            float cut=2.0*cell+.70*(2.0*bevel-1.0);
            float reflection=.5+.5*cos(2.0*angle-.67*time+.36*sin(3.0*angle));
            float face=filmGauss((cut-.57*sin(angle*3.0-.83*time))/.50);
            float band=1.0-smoothstep(outerEdge-aa,outerEdge+aa,ray);
            float glint=face*reflection*reflection;
            float3 ice=float3(.36,.73,1.52), pearl=float3(1.49,1.20,.86);
            float3 platinum=lerp(ice,pearl,.26+.61*smoothstep(-.8,.8,cut+.43*cos(2.0*angle)));
            float edge=filmGauss((ray-outerEdge*.55)/max(aa,.0028));
            light+=platinum*(band*(.38+.48*kite+1.9*glint)+edge*(.37+2.0*glint)
                           +filmGauss(ray/(2.7*width))*(.035+.28*glint));
        }
        light*=smoothstep(-aa,aa,height);
    }
    return light*(1.0-smoothstep(.79,.99,distance))+outerLight;
}

float4 milestoneFilm(float2 p,float4 frame,float kind)
{
    float cover=0.0;
    float3 light=0.0;
    [branch] if(kind<0.5)light=filmComet(p,frame,cover);
    else if(kind<1.5)light=filmPulsar(p,frame);
    else if(kind<2.5)light=filmSupernova(p,frame);
    else light=filmCorona(p,frame);
    float3 rgb=pow(saturate(1.0-exp(-1.12*max(light,0.0))),.477);
    float alpha=max(rgb.r,max(rgb.g,rgb.b));
    // Only the body of the comet occludes the sky. Light stays premultiplied.
    rgb+=float3(.039,.054,.083)*cover*(1.0-alpha);
    return float4(rgb,alpha+(1.0-alpha)*cover);
}

// WPF entries
float4 filmBasis : register(c0);
float4 filmFrame : register(c1);
float4 filmCometPS(float2 uv:TEXCOORD0):COLOR {
    return milestoneFilm(filmBasis.xy+uv*filmBasis.zw, filmFrame, 0);
}
float4 filmPulsarPS(float2 uv:TEXCOORD0):COLOR {
    return milestoneFilm(filmBasis.xy+uv*filmBasis.zw, filmFrame, 1);
}
float4 filmNovaPS(float2 uv:TEXCOORD0):COLOR {
    return milestoneFilm(filmBasis.xy+uv*filmBasis.zw, filmFrame, 2);
}
float4 filmDiamondPS(float2 uv:TEXCOORD0):COLOR {
    return milestoneFilm(filmBasis.xy+uv*filmBasis.zw, filmFrame, 3);
}
