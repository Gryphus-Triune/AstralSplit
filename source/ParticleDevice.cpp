// Hardware instancing for the continuous accretion streams. One shared texture
// contains both camera depths. WPF samples completed frames only.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <dxgi.h>
#include <new>
#include <cstring>
#include <cstdint>

template<class T> struct Ref {
    T* p=nullptr;
    ~Ref(){ reset(); }
    void reset(){ if(p){p->Release();p=nullptr;} }
    T* operator->()const{return p;}
    Ref()=default; Ref(const Ref&)=delete; Ref& operator=(const Ref&)=delete;
};
struct RibbonPaint {
    Ref<ID2D1Factory1> factory;
    Ref<ID2D1Device> device;
    Ref<ID2D1DeviceContext> context;
    Ref<ID2D1StrokeStyle> pen;
    Ref<ID2D1LinearGradientBrush> ink[10];
};
struct Surface {
    Ref<ID2D1Bitmap1> ribbonTarget;
    Ref<ID3D11Texture2D> color; Ref<ID3D11RenderTargetView> target;
    Ref<IDirect3DTexture9> shared; Ref<IDirect3DSurface9> surface;
    void reset(){ribbonTarget.reset();surface.reset();shared.reset();target.reset();color.reset();}
};
struct OrbitPaint {
    Ref<ID3D11VertexShader> vertex,quad;Ref<ID3D11GeometryShader> slices;Ref<ID3D11PixelShader> coverage,ink;
    Ref<ID3D11Buffer> view,orbit,angles;Ref<ID3D11RasterizerState> raster;Ref<ID3D11BlendState> maximum;
    Ref<ID3D11Texture2D> texture;Ref<ID3D11RenderTargetView> target;Ref<ID3D11ShaderResourceView> resource;
    void reset(){resource.reset();target.reset();texture.reset();}
};
struct Batch {
    OrbitPaint orbits;
    Ref<IDirect3D9Ex> d9; Ref<IDirect3DDevice9Ex> bridge;
    Ref<ID3D11Device> device; Ref<ID3D11DeviceContext> commands;
    Ref<ID3D11VertexShader> vertex; Ref<ID3D11PixelShader> pixel;
    Ref<ID3D11InputLayout> layout; Ref<ID3D11BlendState> blend;
    Ref<ID3D11RasterizerState> raster; Ref<ID3D11Buffer> instances,camera;
    Ref<ID3D11Query> fence;
    Ref<ID3D11PixelShader> mapWriter,mapReader;
    Ref<ID3D11Texture2D> mapTexture;Ref<ID3D11ShaderResourceView> mapView;
    Ref<ID3D11RenderTargetView> mapTargets[2];
    float mapKey[22]={};bool mapValid=false,mapUnavailable=false;
    RibbonPaint ribbons;
    Surface frames[3]; int slots=2, selected=-1, shown=-1, pending=-1; unsigned width=0,height=0;
    static const unsigned stride=11*sizeof(float), capacity=1024;
};
#define TRY(x) do{HRESULT hr=(x);if(FAILED(hr))return hr;}while(0)
static HRESULT initDevice(Batch& b,HWND owner,Batch* sibling=nullptr) {
    if(sibling && SUCCEEDED(sibling->device->GetDeviceRemovedReason())){
        b.bridge.p=sibling->bridge.p;b.bridge.p->AddRef();
        b.device.p=sibling->device.p;b.device.p->AddRef();
        b.commands.p=sibling->commands.p;b.commands.p->AddRef();
        return S_OK;
    }
    TRY(Direct3DCreate9Ex(D3D_SDK_VERSION,&b.d9.p));
    UINT adapter=D3DADAPTER_DEFAULT;
    HMONITOR monitor=MonitorFromWindow(owner,MONITOR_DEFAULTTONEAREST);
    for(UINT i=0;i<b.d9->GetAdapterCount();i++)if(b.d9->GetAdapterMonitor(i)==monitor){adapter=i;break;}
    LUID luid={};TRY(b.d9->GetAdapterLUID(adapter,&luid));
    Ref<IDXGIFactory1> factory;TRY(CreateDXGIFactory1(__uuidof(IDXGIFactory1),(void**)&factory.p));
    Ref<IDXGIAdapter1> match;
    for(UINT i=0;;i++){
        HRESULT find=factory->EnumAdapters1(i,&match.p);if(FAILED(find))return find;
        DXGI_ADAPTER_DESC1 desc={};TRY(match->GetDesc1(&desc));
        if(desc.AdapterLuid.LowPart==luid.LowPart && desc.AdapterLuid.HighPart==luid.HighPart && !(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE))break;
        match.reset();
    }
    D3DPRESENT_PARAMETERS pp={};pp.BackBufferWidth=pp.BackBufferHeight=1;pp.BackBufferFormat=D3DFMT_UNKNOWN;
    pp.Windowed=TRUE;pp.SwapEffect=D3DSWAPEFFECT_DISCARD;pp.hDeviceWindow=owner;
    pp.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
    TRY(b.d9->CreateDeviceEx(adapter,D3DDEVTYPE_HAL,owner,D3DCREATE_MULTITHREADED|D3DCREATE_FPU_PRESERVE|D3DCREATE_HARDWARE_VERTEXPROCESSING,&pp,nullptr,&b.bridge.p));
    const D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_0,D3D_FEATURE_LEVEL_10_1,D3D_FEATURE_LEVEL_10_0};
    TRY(D3D11CreateDevice(match.p,D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,levels,3,D3D11_SDK_VERSION,&b.device.p,nullptr,&b.commands.p));
    return S_OK;
}
static HRESULT init(Batch& b,HWND owner,const void* vs,unsigned vsSize,const void* ps,unsigned psSize,Batch* sibling=nullptr) {
    TRY(initDevice(b,owner,sibling));
    TRY(b.device->CreateVertexShader(vs,vsSize,nullptr,&b.vertex.p));
    TRY(b.device->CreatePixelShader(ps,psSize,nullptr,&b.pixel.p));
    const D3D11_INPUT_ELEMENT_DESC fields[]={
        {"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D11_INPUT_PER_INSTANCE_DATA,1},
        {"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,8,D3D11_INPUT_PER_INSTANCE_DATA,1},
        {"TEXCOORD",1,DXGI_FORMAT_R32G32_FLOAT,0,16,D3D11_INPUT_PER_INSTANCE_DATA,1},
        {"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,24,D3D11_INPUT_PER_INSTANCE_DATA,1},
        {"TEXCOORD",2,DXGI_FORMAT_R32_FLOAT,0,40,D3D11_INPUT_PER_INSTANCE_DATA,1}};
    TRY(b.device->CreateInputLayout(fields,5,vs,vsSize,&b.layout.p));
    D3D11_BUFFER_DESC buffer={};buffer.BindFlags=D3D11_BIND_VERTEX_BUFFER;
    buffer.ByteWidth=b.stride*b.capacity;buffer.Usage=D3D11_USAGE_DYNAMIC;buffer.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
    TRY(b.device->CreateBuffer(&buffer,nullptr,&b.instances.p));
    buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;buffer.ByteWidth=16;buffer.CPUAccessFlags=0;buffer.Usage=D3D11_USAGE_DEFAULT;
    TRY(b.device->CreateBuffer(&buffer,nullptr,&b.camera.p));
    D3D11_BLEND_DESC bp={};auto& rt=bp.RenderTarget[0];rt.BlendEnable=TRUE;
    rt.SrcBlend=rt.SrcBlendAlpha=D3D11_BLEND_ONE;rt.DestBlend=rt.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
    rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
    TRY(b.device->CreateBlendState(&bp,&b.blend.p));
    D3D11_RASTERIZER_DESC rd={};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;
    TRY(b.device->CreateRasterizerState(&rd,&b.raster.p));
    D3D11_QUERY_DESC q={D3D11_QUERY_EVENT,0};TRY(b.device->CreateQuery(&q,&b.fence.p));
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT ParticleOpenShared(HWND owner,Batch* sibling,const void* vs,unsigned vsSize,const void* ps,unsigned psSize,Batch** result) {
    *result=nullptr;auto b=new(std::nothrow) Batch();if(!b)return E_OUTOFMEMORY;
    HRESULT hr=init(*b,owner?owner:GetDesktopWindow(),vs,vsSize,ps,psSize,sibling);
    if(FAILED(hr)){delete b;return hr;}*result=b;return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ParticleOpen(HWND owner,const void* vs,unsigned vsSize,const void* ps,unsigned psSize,Batch** result) {
    return ParticleOpenShared(owner,nullptr,vs,vsSize,ps,psSize,result);
}
extern "C" __declspec(dllexport) HRESULT ParticleSlots(Batch* b,int slots){
    if(!b || b->width || slots<2 || slots>3)return E_INVALIDARG;
    b->slots=slots;return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ParticleSelectSlot(Batch* b,int index){
    if(!b || index<0 || index>=b->slots || index==b->shown || b->pending>=0)return E_INVALIDARG;
    b->selected=index;return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ParticleSize(Batch* b,unsigned w,unsigned h){
    if(!b || w==0 || h==0 || w>8192 || h>4096)return E_INVALIDARG;
    b->commands->OMSetRenderTargets(0,nullptr,nullptr);
    b->orbits.reset();b->mapValid=false;b->mapUnavailable=false;b->mapTargets[0].reset();b->mapTargets[1].reset();b->mapView.reset();b->mapTexture.reset();
    b->selected=b->shown=b->pending=-1;b->width=w;b->height=h;
    for(int i=0;i<b->slots;++i){auto& frame=b->frames[i];
        frame.reset();
        D3D11_TEXTURE2D_DESC desc={};desc.Width=w;desc.Height=2*h;desc.MipLevels=desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;desc.MiscFlags=D3D11_RESOURCE_MISC_SHARED;
        TRY(b->device->CreateTexture2D(&desc,nullptr,&frame.color.p));
        TRY(b->device->CreateRenderTargetView(frame.color.p,nullptr,&frame.target.p));
        Ref<IDXGIResource> dxgi;TRY(frame.color->QueryInterface(__uuidof(IDXGIResource),(void**)&dxgi.p));
        HANDLE handle=nullptr;TRY(dxgi->GetSharedHandle(&handle));
        TRY(b->bridge->CreateTexture(w,2*h,1,D3DUSAGE_RENDERTARGET,D3DFMT_A8R8G8B8,D3DPOOL_DEFAULT,&frame.shared.p,&handle));
        TRY(frame.shared->GetSurfaceLevel(0,&frame.surface.p));
    }
    return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ParticleReady(Batch* b,IDirect3DSurface9** surface) {
    *surface=nullptr;if(b->pending<0)return S_FALSE;
    BOOL done=FALSE;HRESULT hr=b->commands->GetData(b->fence.p,&done,sizeof(done),D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if(hr!=S_OK || !done)return hr==S_OK?S_FALSE:hr;
    b->shown=b->pending;b->pending=-1;*surface=b->frames[b->shown].surface.p;return S_OK;
}
#include "FieldCache.inl"
extern "C" __declspec(dllexport) HRESULT ParticleDraw(Batch* b,const float* points,unsigned count,float x,float y,float invW,float invH){
    if(count>b->capacity)return E_INVALIDARG;
    if(b->pending>=0)return S_FALSE;
    int next=b->selected>=0?b->selected:(b->shown==0?1:0);b->selected=-1;auto& frame=b->frames[next];
    if(!frame.target.p)return E_FAIL;
    auto c=b->commands.p;
    const float clear[4]={};c->ClearRenderTargetView(frame.target.p,clear);
    if(count){
        D3D11_MAPPED_SUBRESOURCE map={};TRY(c->Map(b->instances.p,0,D3D11_MAP_WRITE_DISCARD,0,&map));
        std::memcpy(map.pData,points,count*b->stride);c->Unmap(b->instances.p,0);
        const float cam[]={x,y,invW,invH};c->UpdateSubresource(b->camera.p,0,nullptr,cam,0,0);
        const UINT stride=b->stride,offset=0;
        D3D11_VIEWPORT vp={0,0,(float)b->width,(float)(2*b->height),0,1};
        c->RSSetViewports(1,&vp);c->RSSetState(b->raster.p);c->OMSetRenderTargets(1,&frame.target.p,nullptr);
        c->OMSetBlendState(b->blend.p,nullptr,0xffffffff);
        c->IASetInputLayout(b->layout.p);c->IASetVertexBuffers(0,1,&b->instances.p,&stride,&offset);
        c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        c->VSSetShader(b->vertex.p,nullptr,0);c->VSSetConstantBuffers(0,1,&b->camera.p);c->PSSetShader(b->pixel.p,nullptr,0);
        if(b->mapReader.p && count==3 && SUCCEEDED(prepareField(*b,points,cam))){
            c->OMSetRenderTargets(1,&frame.target.p,nullptr);
            c->OMSetBlendState(b->blend.p,nullptr,0xffffffff);
            c->PSSetShader(b->mapReader.p,nullptr,0);c->PSSetShaderResources(0,1,&b->mapView.p);
        }else {
            c->OMSetRenderTargets(1,&frame.target.p,nullptr);c->OMSetBlendState(b->blend.p,nullptr,0xffffffff);
            c->PSSetShader(b->pixel.p,nullptr,0);
        }
        c->DrawInstanced(4,count,0,0);
        if(b->mapReader.p){ID3D11ShaderResourceView* none=nullptr;c->PSSetShaderResources(0,1,&none);}
    }
    c->OMSetRenderTargets(0,nullptr,nullptr);c->End(b->fence.p);c->Flush();b->pending=next;return S_OK;
}
extern "C" __declspec(dllexport) void ParticleClose(Batch* b){delete b;}

#include "RibbonDevice.inl"

#include "CompositionDevice.inl"

#include "OrbitalDevice.inl"

#include "WindowScene.inl"
