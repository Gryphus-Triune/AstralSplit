// WPF ps_3_0 optics and ps_2_0 compatibility entries share a premultiplied source.
sampler2D behind : register(s0);
sampler2D profile : register(s1); // precomputed Snell displacement across a circular bevel
sampler2D atlas : register(s2); // shared transmitted sky for tiny scene-sampling controls
float3 shape : register(c0); // width, height, corner radius in DIPs
float2 light : register(c1);
float3 finish : register(c2); // reflection strength, pressed lift, perimeter-only overlay
float3 optics : register(c3); // thickness / width, thickness / height, inverse bevel
float4 viewport : register(c4); // sampled subrectangle in full-surface texture coordinates
float4 aperture : register(c5); // normalized camera origin and size in the sky texture

float4 bevelShape : register(c6); // half extent minus radius, inverse physical sampling extent

float4 transmitted(float2 pos,bool projected) {
    float4 c=tex2D(behind,saturate(pos));
    if(projected)c+=(1-c.a)*tex2D(atlas,aperture.xy+saturate(pos)*aperture.zw);
    return c;
}
float4 optical(float2 uv, bool projected,bool spectral)
{
    float2 p = ((projected ? uv : viewport.xy + uv * viewport.zw) - 0.5) * shape.xy;
    float2 q = abs(p) - bevelShape.xy;
    float2 outside = max(q, 0.0);
    float len = length(outside);
    float distance = shape.z - len - min(max(q.x, q.y), 0.0);
    if(spectral && distance>=max(1.87,1.0/optics.z)){
        float4 flat=transmitted(uv,projected);
        return projected ? flat : flat*(1.0-finish.z);
    }
    float2 normal = sign(p) * outside / max(len, 0.001);
    float sinI = saturate(1.0 - distance * optics.z);
    float bend = tex2D(profile, float2(sinI, 0.5)).r;
    float2 sampleAt = saturate(uv - normal * bend * optics.xy);
    float4 c = transmitted(sampleAt,projected);
    if(spectral){
        // Express the separation in DIPs, even when this material is rendered at 200%.
        float2 delta=normal*bevelShape.zw*(5.8*sinI*sinI)*(.72+.56*abs(normal.x*normal.y));
        float4 red=transmitted(sampleAt-delta,projected),blue=transmitted(sampleAt+delta,projected);
        c=float4(red.r,c.g,blue.b,max(c.a,max(red.a,blue.a)));
    }
    float angle = dot(normal, light);
    float edge = saturate(1.0 - abs(distance - 0.72) / 1.15);
    float key = saturate(angle), bounce = saturate(-angle);
    float spec = (key * key * 0.77 + bounce * bounce * 0.42 + 0.07) * edge;
    spec += (0.07 + finish.y * 0.09) * sinI * key;
    float reflection = saturate(spec * finish.x);
    float seal = edge * (0.13 - 0.09 * abs(angle)) * finish.x;
    c = lerp(c, float4(0.12, 0.22, 0.38, 1.0), seal);
    c = lerp(c, float4(0.97, 0.99, 1.0, 1.0), reflection);
    return projected ? c : c * lerp(1.0, smoothstep(0.0, 0.66, sinI), finish.z);
}
float4 main(float2 uv : TEXCOORD) : COLOR { return optical(uv, false,true); }
float4 projected(float2 uv : TEXCOORD) : COLOR { return optical(uv, true,true); }
float4 main2(float2 uv : TEXCOORD) : COLOR { return optical(uv,false,false); }
float4 projected2(float2 uv : TEXCOORD) : COLOR { return optical(uv,true,false); }

float4 standbyPrimary(float2 uv, float4 region) {
    float2 radial = uv - .5;
    // The active patch can expand around the star during the handoff. Keep
    // the stellar Einstein radius at .4 of its own aperture in either patch.
    float2 p = radial * finish.y;
    float radiusSquared = dot(p, p);
    float edge = 1 - smoothstep(.64, 1, radiusSquared);
    float2 source = uv - radial * (.16 * edge / max(radiusSquared, .034));
    float4 natural = tex2D(behind, region.xy + region.zw * uv);
    float4 image = tex2D(behind, region.xy + region.zw * saturate(source));
    // Keep the innermost field intact beneath the bright photosphere.
    float exposure = saturate((radiusSquared - .030) * 8);
    return lerp(natural, image, finish.x * exposure);
}

float4 standbyImages(float2 uv, float4 region) {
    float4 primary = standbyPrimary(uv, region);
    float2 radial = uv - .5;
    float2 p = radial * finish.y;
    float radiusSquared = dot(p, p);
    float inverseRadius = rsqrt(max(radiusSquared, .0001));
    // Sample the opposite azimuth in a narrow band around the star.
    float dist = radiusSquared * inverseRadius - (22.44 / 90.0);
    float band = saturate(1 - abs(dist) * 46);
    float2 source = .5 - radial * (dist * 7.4 * inverseRadius);
    float4 inner = tex2D(behind, region.xy + region.zw * saturate(source));
    float brightness = max(inner.r, max(inner.g, inner.b));
    float gain = .24 * finish.x * band * saturate((brightness - .14) * 2);
    return saturate(primary + inner * gain * (1 - primary));
}

float4 stellarStandby(float2 uv:TEXCOORD):COLOR {
    return standbyImages(uv, float4(0,0,1,1));
}

// A point lens maps the observed angle back to the source plane. The same radius
// applies to every wavelength, including soft star halos and subpixel sources.
float4 primaryLens(float2 uv,float4 region) {
    float2 p = uv * 2 - 1;
    float r = max(length(p), .0005);
    // The Einstein radius scales as sqrt(mass). Move the stellar images
    // continuously back to their source pixels as the horizon evaporates.
    float mass = saturate(light.x);
    float relativeRadius = r * rsqrt(max(.00000001, mass));
    float edge = 1 - smoothstep(.77, 1, relativeRadius);
    float2 src = .5 + .5 * p * (1 - edge * mass * (36.0/90.0)*(36.0/90.0) / (r*r));
    float visible = smoothstep(.20*mass,.227*mass+.00001,r);
    return tex2D(behind,region.xy+region.zw*saturate(src)) * visible * step(0,min(src.x,src.y)) * step(max(src.x,src.y),1);
}
float4 opticalLens(float2 uv,float4 region) {
    float4 primary=primaryLens(uv,region);
    float2 p=uv*2-1; float r=max(.0001,length(p)); float2 n=p/r;
    float m=saturate(light.x),dist=r-(22.44/90.0)*m;
    float width=max(.002,.65*fwidth(r));
    float band=1-smoothstep(width,3.2*width,abs(dist));
    // Odd parity across the critical curve. A weak quadrupole resolves an aligned source into four highlights.
    float2 src=.5-n*(dist/max(.12,m))*7.4+float2(n.x,-n.y)*.014*m;
    float4 image=tex2D(behind,region.xy+region.zw*saturate(src));
    float lum=max(image.r,max(image.g,image.b));
    float axes=pow(abs(n.x*n.x-n.y*n.y),12);
    float gain=band*m*smoothstep(.075,.55,lum)*(.46+.54*axes);
    return saturate(primary+image*gain*(1-primary));
}
float4 stellarFull2(float2 uv:TEXCOORD):COLOR {return primaryLens(uv,float4(0,0,1,1));}
float4 stellarFull(float2 uv:TEXCOORD):COLOR {return opticalLens(uv,float4(0,0,1,1));}
float4 sceneLens(float2 uv:TEXCOORD):COLOR {
    float2 local=(uv-viewport.xy)/viewport.zw;
    if(min(local.x,local.y)<0 || max(local.x,local.y)>1)return tex2D(behind,uv);
    float4 source=finish.x>0 ? standbyImages(local,viewport) : tex2D(behind,uv);
    if(shape.x<=0)return source;
    return lerp(source,opticalLens(local,viewport),shape.x);
}
float4 stellarLens2(float2 uv : TEXCOORD) : COLOR {
    // The ps_2_0 handoff keeps both geometric images from the primary ray.
    return lerp(standbyPrimary(uv,float4(0,0,1,1)),stellarFull2(uv),shape.x);
}
float4 stellarLens(float2 uv : TEXCOORD) : COLOR {
    return lerp(stellarStandby(uv),stellarFull(uv),shape.x);
}

// The input of each optical strip is the live sky behind that bevel, never UI text.
float4 bevelField(float2 uv:TEXCOORD):COLOR {
    float2 p=uv*shape.xy,d=p-light;
    float dist=length(d),v=(dist-optics.x)/max(1,optics.z);
    float wave=optics.y*exp(-v*v)*cos(v*1.8);
    float2 src=saturate(uv-d/max(1,dist)*wave*32.0/shape.xy);
    float4 c=tex2D(behind,src);
    float4 glint=float4(.68,.81,1,1)*(.21*abs(wave));
    return (c*(1-glint.a)+glint)*(saturate(1.8*abs(wave)));
}


