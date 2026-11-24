// A transparent swap chain sits between two redirected WPF layers. Only the render
// worker touches its D3D11 immediate context after initialization.
#include <dxgi1_3.h>
#include <dcomp.h>
#include <avrt.h>
#include <cmath>
struct ObservatoryDevice {
    Ref<ID3D11Device> gpu;Ref<ID3D11DeviceContext> dc;
    Ref<IDXGISwapChain2> chain;Ref<ID3D11RenderTargetView> target;
    Ref<ID3D11Texture2D> atlas;Ref<ID3D11ShaderResourceView> field;
    Ref<ID3D11VertexShader> vs;Ref<ID3D11PixelShader> ps;
    Ref<ID3D11SamplerState> sampler;Ref<ID3D11Buffer> uniforms;
    Ref<IDCompositionDevice> composition;Ref<IDCompositionTarget> host;
    Ref<IDCompositionVisual> root,base,wind,foreground;
    Ref<IDCompositionScaleTransform> size;
    HANDLE stop=nullptr,thread=nullptr,latency=nullptr;
    SRWLOCK state=SRWLOCK_INIT;
    struct View {float eyeX=0,eyeY=0,energy=.12f;double first=0,second=0;LONGLONG stamp=0;bool moving=false;} camera;
    unsigned w=0,h=0;LARGE_INTEGER freq={};LONG error=S_OK,frames=0;
    ~ObservatoryDevice(){
        if(stop)SetEvent(stop);
        if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
        if(host.p){host->SetRoot(nullptr);if(composition.p)composition->Commit();}
        if(latency)CloseHandle(latency);
        if(stop)CloseHandle(stop);
    }
    static DWORD WINAPI Run(void* self){return static_cast<ObservatoryDevice*>(self)->Work();}
    DWORD Work(){
        DWORD task=0;HANDLE priority=AvSetMmThreadCharacteristicsW(L"Games",&task);
        HANDLE waits[]={stop,latency};
        for(;;){
            DWORD signal=WaitForMultipleObjects(2,waits,FALSE,400);
            if(signal==WAIT_OBJECT_0)break;
            if(signal==WAIT_TIMEOUT)continue;
            if(signal!=WAIT_OBJECT_0+1){InterlockedExchange(&error,E_FAIL);break;}
            AcquireSRWLockShared(&state);auto v=camera;ReleaseSRWLockShared(&state);
            LARGE_INTEGER now;QueryPerformanceCounter(&now);
            double dt=v.moving?double(now.QuadPart-v.stamp)/freq.QuadPart:0;
            const double pi=3.141592653589793;
            float data[]={v.eyeX,v.eyeY,float(-std::cos((v.first+dt)*pi/19)),float(-std::cos((v.second+dt)*pi/26)),v.energy,0,0,0};
            dc->UpdateSubresource(uniforms.p,0,nullptr,data,0,0);
            dc->OMSetRenderTargets(1,&target.p,nullptr);
            D3D11_VIEWPORT viewport={0,0,float(w),float(h),0,1};dc->RSSetViewports(1,&viewport);
            dc->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            dc->VSSetShader(vs.p,nullptr,0);dc->PSSetShader(ps.p,nullptr,0);
            dc->PSSetConstantBuffers(0,1,&uniforms.p);dc->PSSetShaderResources(0,1,&field.p);dc->PSSetSamplers(0,1,&sampler.p);
            dc->Draw(3,0);
            HRESULT hr=chain->Present(1,0);
            if(FAILED(hr)){InterlockedExchange(&error,hr);break;}
            InterlockedIncrement(&frames);
        }
        if(priority)AvRevertMmThreadCharacteristics(priority);return 0;
    }
    HRESULT Initialize(HWND window,HWND below,HWND above,unsigned width,unsigned height,const unsigned char* pixels,
                       unsigned aw,unsigned ah,const void* vertex,unsigned vl,const void* pixel,unsigned pl) {
        w=width;h=height;QueryPerformanceFrequency(&freq);LARGE_INTEGER now;QueryPerformanceCounter(&now);camera.stamp=now.QuadPart;
        Ref<IDXGIFactory2> factory;TRY(CreateDXGIFactory1(__uuidof(IDXGIFactory2),(void**)&factory.p));
        HMONITOR monitor=MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST);Ref<IDXGIAdapter1> at;
        for(UINT i=0;;i++){
            HRESULT hr=factory->EnumAdapters1(i,&at.p);if(FAILED(hr))return hr;
            bool found=false;
            for(UINT j=0;;j++){
                Ref<IDXGIOutput> out;if(FAILED(at->EnumOutputs(j,&out.p)))break;
                DXGI_OUTPUT_DESC desc={};if(SUCCEEDED(out->GetDesc(&desc)) && desc.Monitor==monitor){found=true;break;}
            }
            if(found)break;at.reset();
        }
        TRY(D3D11CreateDevice(at.p,D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&gpu.p,nullptr,&dc.p));
        DXGI_SWAP_CHAIN_DESC1 desc={};desc.Width=w;desc.Height=h;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;
        desc.Scaling=DXGI_SCALING_STRETCH;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;desc.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        Ref<IDXGISwapChain1> first;TRY(factory->CreateSwapChainForComposition(gpu.p,&desc,nullptr,&first.p));
        TRY(first->QueryInterface(__uuidof(IDXGISwapChain2),(void**)&chain.p));TRY(chain->SetMaximumFrameLatency(1));
        latency=chain->GetFrameLatencyWaitableObject();if(!latency)return E_FAIL;
        Ref<ID3D11Texture2D> back;TRY(chain->GetBuffer(0,__uuidof(ID3D11Texture2D),(void**)&back.p));TRY(gpu->CreateRenderTargetView(back.p,nullptr,&target.p));
        TRY(gpu->CreateVertexShader(vertex,vl,nullptr,&vs.p));TRY(gpu->CreatePixelShader(pixel,pl,nullptr,&ps.p));
        D3D11_TEXTURE2D_DESC td={};td.Width=aw;td.Height=ah;td.ArraySize=td.MipLevels=1;td.SampleDesc.Count=1;
        td.Format=DXGI_FORMAT_B8G8R8A8_UNORM;td.BindFlags=D3D11_BIND_SHADER_RESOURCE;td.Usage=D3D11_USAGE_IMMUTABLE;
        D3D11_SUBRESOURCE_DATA image={pixels,aw*4,0};TRY(gpu->CreateTexture2D(&td,&image,&atlas.p));
        TRY(gpu->CreateShaderResourceView(atlas.p,nullptr,&field.p));
        D3D11_SAMPLER_DESC sd={};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
        TRY(gpu->CreateSamplerState(&sd,&sampler.p));D3D11_BUFFER_DESC bd={};bd.ByteWidth=32;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        TRY(gpu->CreateBuffer(&bd,nullptr,&uniforms.p));
        Ref<IDXGIDevice> dx;TRY(gpu->QueryInterface(__uuidof(IDXGIDevice),(void**)&dx.p));
        TRY(DCompositionCreateDevice(dx.p,__uuidof(IDCompositionDevice),(void**)&composition.p));
        TRY(composition->CreateTargetForHwnd(window,TRUE,&host.p));TRY(composition->CreateVisual(&root.p));
        TRY(composition->CreateVisual(&base.p));TRY(composition->CreateVisual(&wind.p));TRY(composition->CreateVisual(&foreground.p));
        Ref<IUnknown> rear,front;TRY(composition->CreateSurfaceFromHwnd(below,&rear.p));TRY(composition->CreateSurfaceFromHwnd(above,&front.p));
        TRY(base->SetContent(rear.p));TRY(foreground->SetContent(front.p));TRY(wind->SetContent(chain.p));
        TRY(composition->CreateScaleTransform(&size.p));TRY(wind->SetTransform(size.p));
        TRY(root->AddVisual(base.p,FALSE,nullptr));TRY(root->AddVisual(wind.p,TRUE,base.p));TRY(root->AddVisual(foreground.p,TRUE,wind.p));
        stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!stop)return HRESULT_FROM_WIN32(GetLastError());
        thread=CreateThread(nullptr,0,Run,this,0,nullptr);if(!thread)return HRESULT_FROM_WIN32(GetLastError());
        return S_OK;
    }
};
extern "C" __declspec(dllexport) HRESULT CompositionOpen(HWND window,HWND below,HWND above,unsigned w,unsigned h,const unsigned char* pixels,
 unsigned aw,unsigned ah,const void* vs,unsigned vl,const void* ps,unsigned pl,ObservatoryDevice** result){
    if(!result || !pixels || !w || !h || w>4096 || h>4096 || !aw || !ah || aw>8192 || ah>8192)return E_INVALIDARG;
    *result=nullptr;auto d=new(std::nothrow) ObservatoryDevice();if(!d)return E_OUTOFMEMORY;
    HRESULT hr=d->Initialize(window,below,above,w,h,pixels,aw,ah,vs,vl,ps,pl);
    if(FAILED(hr)){delete d;return hr;}*result=d;return S_OK;
}
extern "C" __declspec(dllexport) HRESULT CompositionPlace(ObservatoryDevice* d,float x,float y,float width,float height){
    if(!d || width<=0 || height<=0)return E_INVALIDARG;
    TRY(d->size->SetScaleX(width/d->w));TRY(d->size->SetScaleY(height/d->h));
    TRY(d->wind->SetOffsetX(x));TRY(d->wind->SetOffsetY(y));TRY(d->host->SetRoot(d->root.p));
    return d->composition->Commit();
}
extern "C" __declspec(dllexport) HRESULT CompositionEye(ObservatoryDevice* d,float x,float y,float energy,double drift,double swell,int moving){
    if(!d)return E_INVALIDARG;
    LARGE_INTEGER now;QueryPerformanceCounter(&now);
    AcquireSRWLockExclusive(&d->state);d->camera={x,y,energy,drift,swell,now.QuadPart,moving!=0};ReleaseSRWLockExclusive(&d->state);
    return InterlockedCompareExchange(&d->error,0,0);
}
extern "C" __declspec(dllexport) int CompositionFrames(ObservatoryDevice* d){return d?InterlockedCompareExchange(&d->frames,0,0):0;}
extern "C" __declspec(dllexport) void CompositionClose(ObservatoryDevice* d){delete d;}
