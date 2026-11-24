// Double precision matches the C# sky atlas, including its premultiplied byte rounding.
__device__ __forceinline__ double clamp1(double a) { return fmax(0.,fmin(1.,a)); }
__device__ __forceinline__ double lattice(int x,int y,unsigned int seed) {
    unsigned int h=(unsigned int)x*1597334677u ^ (unsigned int)y*3812015801u ^ seed;
    h ^= h >> 16; h *= 2246822519u; h ^= h >> 13;
    return (h & 0xffffff) / 16777215.0;
}
__device__ __forceinline__ double noise(double x,double y,unsigned int seed) {
    int ix=(int)floor(x), iy=(int)floor(y);
    double u=x-ix, v=y-iy;
    u*=u*(3.-2.*u); v*=v*(3.-2.*v);
    double a=lattice(ix,iy,seed),b=lattice(ix+1,iy,seed);
    double c=lattice(ix,iy+1,seed),d=lattice(ix+1,iy+1,seed);
    return (a+(b-a)*u)*(1-v)+(c+(d-c)*u)*v;
}
extern "C" __global__ void Ridge(double* ridge, int w,unsigned int seed) {
    int x=blockIdx.x*blockDim.x+threadIdx.x;
    if(x>=w)return;
    double u=(x+.5)*512/w-.5;
    ridge[x]=79+14*sin(u/65)+17*(noise(u/36,1,seed)-.5);
}
extern "C" __global__ void Base(const double* ridge,unsigned char* pixels,int w,int h,unsigned int seed) {
    int x=blockIdx.x*blockDim.x+threadIdx.x,y=blockIdx.y*blockDim.y+threadIdx.y;
    if(x>=w || y>=h)return;
    double u=(x+.5)*512/w-.5, v=(y+.5)*160/h-.5;
    double cloud=0,amp=.52,fx=u/53,fy=v/22;
    for(int i=0;i<5;i++){cloud+=amp*noise(fx,fy,seed+i);fx*=2.03;fy*=2.03;amp*=.5;}
    double dist=v-ridge[x]+(cloud-.46)*42;
    double envelope=exp(-dist*dist/1420.);
    double core=exp(-dist*dist/210.)*clamp1(2.5*cloud-.35);
    double lane0=(dist+4+sin(u/31.)*5)/3.8,lane1=(dist-13-sin(u/40.)*5)/3.4;
    double lane=.55*exp(-lane0*lane0)+.32*exp(-lane1*lane1);
    double edge=clamp1(fmin(fmin(u,511.-u)/35.,fmin(v,159.-v)/16.));
    double filaments=1-fmin(1.,fabs(2*noise(u/9.,v/3.7,seed+41)-1));
    double a=fmin(.275,(.19*envelope+.32*core)*(.18+cloud+.18*filaments)*(1-lane)*edge);
    double warm=clamp1(core*1.06),pink=noise(u/34.,v/27.,seed+98);
    int p=(y*w+x)*4;
    pixels[p]=(unsigned char)((255-41*warm)*a);
    pixels[p+1]=(unsigned char)((112+121*warm)*a);
    pixels[p+2]=(unsigned char)((105+62*pink+(150-62*pink)*warm)*a);
    pixels[p+3]=(unsigned char)(255*a);
}

extern "C" __global__ void Tone(const unsigned char* base, unsigned char* out, int w, int h, unsigned int rgb) {
    const int x = blockIdx.x * blockDim.x + threadIdx.x, y = blockIdx.y * blockDim.y + threadIdx.y;
    if (x >= w || y >= h) return;
    const double u = x / (double)(w-1), weight = (1-u)*185/255.0;
    const int p = (y*w+x)*4, alpha = __double2int_rn(base[p+3]*weight);
    for (int channel=0; channel<3; ++channel) {
        const int color = (rgb >> (8*channel)) & 255;
        out[p+channel] = (unsigned char)__double2int_rn((color+(255-color)*u)*alpha/255.);
    }
    out[p+3] = (unsigned char)alpha;
}
