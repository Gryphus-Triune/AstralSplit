// Immutable scene packets cross from WPF's dispatcher to the display worker.
// Texture ownership follows the packet until the last draw referencing it completes.
#include <d3d11_1.h>
#include <vector>
#include <array>
#include <memory>
#include <unordered_map>
#include <algorithm>
#include "SceneVectors.inl"
#include "LaunchSurface.inl"
static LONG64 sceneResourceSequence=0;
struct SceneParticleBuffer {
    Ref<ID3D11Buffer> values;
    Ref<ID3D11ShaderResourceView> view;
    unsigned elements=0;
    UINT64 lastGpuFrame=0;
};
struct SceneCloud {
    std::vector<float> words;
    std::shared_ptr<SceneParticleBuffer> particles;
    Ref<ID3D11ShaderResourceView> palette;
    float camera[4]={};int kind=0,count=0;bool drawn=false;
};
struct SceneRibbonFrame {
    std::vector<RibbonVertex> vertices;std::vector<float> spins;
    float view[16]={},widths[10]={};UINT colors[30]={};int stars=0;
};
struct SceneTile {
    std::shared_ptr<SceneRibbonFrame> ribbon;
    std::shared_ptr<SceneCloud> cloud;
    std::shared_ptr<SceneTile> output;
    UINT64 version=UINT64(InterlockedIncrement64(&sceneResourceSequence)),cached=0;
    bool baked=false,packed=false;float uvScaleX=1,uvScaleY=1,uvX=0,uvY=0;
    std::shared_ptr<SceneVector> shape;Ref<ID2D1Bitmap1> canvas;
    Ref<ID3D11Texture2D> color; Ref<ID3D11ShaderResourceView> view; Ref<ID3D11RenderTargetView> target;
    UINT width=0,height=0;
    UINT64 lastGpuFrame=0;
    UINT64 passIdentity=0;
};
struct SceneDraw {
    INT image,secondary,atlas,material;
    float origin[4],axis[4],span[4],param[28];
    INT scissor[4];
};
struct ScenePass {INT slot,width,height,first,count;};
enum SceneStage : INT { Setup, Streams, Sky, Lens, Projection, Layers, Composite, StageCount };
struct ScenePassTag {UINT64 identity;INT stage,reserved;};
struct ScenePacket {
    std::vector<ScenePass> passes;
    std::vector<ScenePassTag> tags;
    std::vector<SceneDraw> draws;
    std::unordered_map<int,std::shared_ptr<SceneTile>> art;
    std::vector<std::array<SceneTile*,3>> inputs;
    LONGLONG time=0,sequence=0;
    int scrollOwner=0;float anchor=0,nearOffset=0,farOffset=0;
};
struct ScrollMotion {
    LONGLONG stamp=0;
    double position=0,velocity=0,target=0,frequency=0;
    int owner=0,drag=0;
    double At(LONGLONG now,LONGLONG ticks) const {
        if(frequency<=0 || position==target)return target;
        double t=std::max(0.,double(now-stamp)/ticks),d=position-target;
        double x=target+(d+(velocity+frequency*d)*t)*std::exp(-frequency*t);
        return std::clamp(x,std::min(position,target),std::max(position,target));
    }
};
struct SceneResult {LONGLONG sequence,frames;double gpuMs;INT passes,draws,images;HRESULT status;INT activePasses,submissions,clouds,paths;};
struct SceneTiming {
    Ref<ID3D11Query> span,begin,end;
    static constexpr unsigned BoundaryLimit=64;
    std::array<Ref<ID3D11Query>,BoundaryLimit> boundaries;
    std::array<SceneStage,BoundaryLimit> stages;
    unsigned count=0;
    SceneStage stage=Setup;
    UINT64 frame=0;
    bool pending=false,overflow=false;
};
struct SceneConstantPage {
    Ref<ID3D11Buffer> buffer;
    std::vector<std::array<float,44>> words;
};
struct ScenePerfResult {
    LONGLONG packets,submitWaitTicks,submitTicks,frames,prepareTicks,recordTicks,presentTicks;
    LONGLONG targetCreates,targetReuses,pooledBytes,constantWrites,constantReuses,paletteCreates,paletteReuses;
    LONGLONG inputCreates,inputReuses,inputBytes,passCreates,passReuses,passBytes;
};
struct SceneFrameStamp {LONGLONG sequence,frame,packetAt,start,prepared,encoded,displayCallDone;};
struct SceneLayerResult {
    LONGLONG moved,matchedSize,idle,sizeMiss,pendingMiss,trimmed;
    LONGLONG prepared,vectorTicks,targetTicks,constantTicks;
};
struct SceneGpuStamp {LONGLONG frame;double milliseconds[StageCount];};
struct WindowScene {
    SceneD2D vectors;Batch lineDevice;
    std::shared_ptr<LaunchSurface> launch;
    volatile LONG parked=0;
    struct OrbitProgram {std::vector<BYTE> bytes;int sizes[5];float samples[404];};
    std::shared_ptr<const OrbitProgram> orbitProgram;
    Ref<ID3D11PixelShader> milestonePS;
    Ref<ID3D11VertexShader> particleVS[4];Ref<ID3D11PixelShader> particlePS[4];Ref<ID3D11Buffer> particleCamera;
    Ref<ID3D11Device> gpu;Ref<ID3D11DeviceContext> dc;Ref<ID3D11DeviceContext1> offsets;
    HWND owner=nullptr;Ref<IDXGIAdapter1> displayAdapter;
    Ref<IDXGISwapChain2> chain;Ref<IDCompositionDevice> comp;Ref<IDCompositionTarget> host;Ref<IDCompositionVisual> root;
    Ref<ID3D11VertexShader> vs;Ref<ID3D11PixelShader> ps;Ref<ID3D11SamplerState> linear;
    Ref<ID3D11BlendState> blend;Ref<ID3D11RasterizerState> raster;Ref<ID3D11Buffer> clockBuffer;
    Ref<ID3D11Texture2D> readback;
    std::vector<std::shared_ptr<SceneTile>> passes;
    std::vector<std::shared_ptr<SceneTile>> previousPasses;
    std::vector<std::unique_ptr<SceneConstantPage>> constants;
    std::vector<std::shared_ptr<SceneTile>> outputPool;
    std::vector<std::shared_ptr<SceneTile>> passPool;
    std::vector<std::shared_ptr<SceneParticleBuffer>> inputPool;
    UINT64 gpuFrame=0,gpuCompleted=0;
    static constexpr UINT64 PoolLimit=128ull*1024*1024;
    static constexpr size_t PoolCount=40;
    static constexpr UINT64 PassLimit=48ull*1024*1024;
    static constexpr size_t PassCount=96;
    static constexpr UINT64 InputLimit=16ull*1024*1024;
    static constexpr size_t InputCount=24;
    bool profile=false,gpuProfile=false;
    ScenePerfResult perf={};
    SceneLayerResult layers={},publishedLayers={};
    // These counters belong to the rendering worker until it publishes a frame.
    LONGLONG targetCreates=0,targetReuses=0,pooledBytes=0,constantWrites=0,constantReuses=0;
    LONGLONG inputCreates=0,inputReuses=0,inputBytes=0,passCreates=0,passReuses=0,passBytes=0;
    static constexpr size_t TraceCapacity=1024;
    std::array<SceneFrameStamp,TraceCapacity> trace={};
    std::array<SceneGpuStamp,TraceCapacity> gpuTrace={};
    UINT64 gpuSamples=0;
    struct CachedPalette {std::vector<UINT> colors;Ref<ID3D11ShaderResourceView> view;};
    CachedPalette palettes[3];
    SRWLOCK paletteGuard=SRWLOCK_INIT;
    std::unordered_map<int,std::shared_ptr<SceneTile>> resources;
    std::shared_ptr<ScenePacket> submitted,active;
    std::vector<UINT64> signatures;std::vector<bool> animated;
    SRWLOCK guard=SRWLOCK_INIT;
    ScrollMotion scroll;int shownScrollOwner=0;double shownScrollOffset=0;
    std::vector<bool> scrollPass;
    float lastScroll=0,lastThumb=0;int lastScrollOwner=0;
    HANDLE stop=nullptr,dirty=nullptr,worker=nullptr,latency=nullptr,ready=nullptr;
    LARGE_INTEGER freq={};unsigned width=0,height=0;
    SceneResult result={};LONG failed=S_OK;LONGLONG serial=0;
    SceneTiming timings[4];unsigned timingIndex=0;
    bool shown=false,onscreen=false;BYTE* capture=nullptr;unsigned captureStride=0,captureWidth=0,captureHeight=0;HRESULT captureResult=E_PENDING;
    SRWLOCK captureGuard=SRWLOCK_INIT;
    bool captureInProgress=false;
    volatile LONG presentedSize=0;
    static LONGLONG Counter(){LARGE_INTEGER now;QueryPerformanceCounter(&now);return now.QuadPart;}
    ~WindowScene(){
        if(stop)SetEvent(stop);if(worker){WaitForSingleObject(worker,INFINITE);CloseHandle(worker);}
        if(host.p && shown){host->SetRoot(nullptr);comp->Commit();}
        if(latency)CloseHandle(latency);if(stop)CloseHandle(stop);if(dirty)CloseHandle(dirty);if(ready)CloseHandle(ready);
    }
    HRESULT SetProgram(int kind,const void* v,unsigned vl,const void* p,unsigned pl){
        if(kind==4)return milestonePS.p?S_OK:gpu->CreatePixelShader(p,pl,nullptr,&milestonePS.p);
        if(kind<0||kind>3||particleVS[kind].p)return E_INVALIDARG;
        TRY(gpu->CreateVertexShader(v,vl,nullptr,&particleVS[kind].p));TRY(gpu->CreatePixelShader(p,pl,nullptr,&particlePS[kind].p));
        if(!particleCamera.p){D3D11_BUFFER_DESC b={};b.ByteWidth=32;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;TRY(gpu->CreateBuffer(&b,nullptr,&particleCamera.p));}
        return S_OK;
    }
    HRESULT Table(const void* data,unsigned n,DXGI_FORMAT format,Ref<ID3D11ShaderResourceView>& into){
        if(!n)return S_OK;
        D3D11_BUFFER_DESC buf={};buf.ByteWidth=n*(format==DXGI_FORMAT_R32_UINT?4:16);buf.BindFlags=D3D11_BIND_SHADER_RESOURCE;buf.Usage=D3D11_USAGE_IMMUTABLE;
        D3D11_SUBRESOURCE_DATA input={data,0,0};Ref<ID3D11Buffer> values;TRY(gpu->CreateBuffer(&buf,&input,&values.p));
        D3D11_SHADER_RESOURCE_VIEW_DESC d={};d.Format=format;d.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;d.Buffer.NumElements=n;
        return gpu->CreateShaderResourceView(values.p,&d,&into.p);
    }
    HRESULT Palette(int kind,const UINT* colors,unsigned count,Ref<ID3D11ShaderResourceView>& into){
        if(!count)return S_OK;
        AcquireSRWLockShared(&paletteGuard);
        auto& last=palettes[kind];
        bool same=last.colors.size()==count && !std::memcmp(last.colors.data(),colors,count*sizeof(UINT));
        if(same){into.p=last.view.p;into->AddRef();}
        ReleaseSRWLockShared(&paletteGuard);
        if(!same){
            TRY(Table(colors,count,DXGI_FORMAT_R32_UINT,into));
            std::vector<UINT> copy(colors,colors+count);
            AcquireSRWLockExclusive(&paletteGuard);
            last.colors.swap(copy);last.view.reset();last.view.p=into.p;last.view->AddRef();
            ReleaseSRWLockExclusive(&paletteGuard);
        }
        if(profile){AcquireSRWLockExclusive(&guard);if(same)++perf.paletteReuses;else ++perf.paletteCreates;ReleaseSRWLockExclusive(&guard);}
        return S_OK;
    }
    template<class T> auto OldestFree(std::vector<std::shared_ptr<T>>& pool){
        auto oldest=pool.end();
        for(auto i=pool.begin();i!=pool.end();++i){
            if(Available(*i) && (oldest==pool.end() || (*i)->lastGpuFrame<(*oldest)->lastGpuFrame))oldest=i;
        }
        return oldest;
    }
    template<class T> bool Available(const std::shared_ptr<T>& item)const{
        return item.use_count()==1 && item->lastGpuFrame<=gpuCompleted;
    }
    HRESULT PooledOutput(unsigned w,unsigned h,std::shared_ptr<SceneTile>& out){
        for(const auto& candidate:outputPool){
            if(candidate->width!=w || candidate->height!=h || !Available(candidate))continue;
            out=candidate;out->lastGpuFrame=gpuFrame;++targetReuses;
            return S_OK;
        }
        const UINT64 bytes=UINT64(w)*h*4;
        bool keep=bytes<=PoolLimit;
        while(keep && (outputPool.size()>=PoolCount || UINT64(pooledBytes)+bytes>PoolLimit)){
            auto oldest=OldestFree(outputPool);
            if(oldest==outputPool.end()){keep=false;break;}
            pooledBytes-=UINT64((*oldest)->width)*(*oldest)->height*4;
            outputPool.erase(oldest);
        }
        TRY(Texture(w,h,true,nullptr,0,out));
        out->lastGpuFrame=gpuFrame;++targetCreates;
        if(keep){outputPool.push_back(out);pooledBytes+=bytes;}
        return S_OK;
    }
    HRESULT DynamicOutput(SceneTile& tile){
        if(tile.output){tile.output->lastGpuFrame=gpuFrame;return S_OK;}
        return PooledOutput(tile.width,tile.height,tile.output);
    }
    HRESULT ParticleInput(SceneCloud& cloud){
        if(cloud.particles){cloud.particles->lastGpuFrame=gpuFrame;return S_OK;}
        // Commit data only for a packet the worker actually uses. The shaders
        // address the populated rows using the identical instance count.
        const auto rows=unsigned(cloud.words.size()/4);
        unsigned capacity=64;
        while(capacity<rows)capacity*=2;
        for(const auto& candidate:inputPool){
            if(candidate->elements!=capacity || !Available(candidate))continue;
            cloud.particles=candidate;++inputReuses;break;
        }
        if(!cloud.particles){
            auto buffer=std::make_shared<SceneParticleBuffer>();buffer->elements=capacity;
            D3D11_BUFFER_DESC storage={};storage.ByteWidth=capacity*16;storage.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            TRY(gpu->CreateBuffer(&storage,nullptr,&buffer->values.p));
            D3D11_SHADER_RESOURCE_VIEW_DESC read={};read.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;
            read.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;read.Buffer.NumElements=capacity;
            TRY(gpu->CreateShaderResourceView(buffer->values.p,&read,&buffer->view.p));
            cloud.particles=buffer;++inputCreates;
            const UINT64 bytes=UINT64(capacity)*16;
            while(inputPool.size()>=InputCount || UINT64(inputBytes)+bytes>InputLimit){
                auto oldest=OldestFree(inputPool);
                if(oldest==inputPool.end())break;
                inputBytes-=UINT64((*oldest)->elements)*16;inputPool.erase(oldest);
            }
            if(inputPool.size()<InputCount && UINT64(inputBytes)+bytes<=InputLimit){
                inputPool.push_back(std::move(buffer));inputBytes+=bytes;
            }
        }
        auto& input=*cloud.particles;input.lastGpuFrame=gpuFrame;
        D3D11_BOX used={0,0,0,rows*16,1,1};
        dc->UpdateSubresource(input.values.p,0,&used,cloud.words.data(),0,0);
        std::vector<float>().swap(cloud.words);
        return S_OK;
    }
    HRESULT Clouds(SceneTile& tile,float dt){
        TRY(DynamicOutput(tile));
        auto& state=*tile.cloud;if(state.drawn && (state.kind==2 || !state.count))return S_OK;
        auto& target=tile.output->target;
        ID3D11ShaderResourceView* nil[3]={};dc->PSSetShaderResources(0,3,nil);dc->OMSetRenderTargets(1,&target.p,nullptr);
        float zero[4]={};dc->ClearRenderTargetView(target.p,zero);
        if(state.count){
            TRY(ParticleInput(state));
            float constants[8]={state.camera[0],state.camera[1],state.camera[2],state.camera[3],dt,0,0,0};
            dc->UpdateSubresource(particleCamera.p,0,nullptr,constants,0,0);
            D3D11_VIEWPORT vp={0,0,float(tile.width),float(tile.height),0,1};dc->RSSetViewports(1,&vp);
            D3D11_RECT clip={0,0,(LONG)tile.width,(LONG)tile.height};dc->RSSetState(raster.p);dc->RSSetScissorRects(1,&clip);
            dc->OMSetBlendState(blend.p,nullptr,0xffffffff);dc->IASetInputLayout(nullptr);dc->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            dc->VSSetShader(particleVS[state.kind].p,nullptr,0);dc->PSSetShader(particlePS[state.kind].p,nullptr,0);
            ID3D11ShaderResourceView* reads[]={state.particles->view.p,state.palette.p};dc->VSSetShaderResources(0,2,reads);dc->VSSetConstantBuffers(0,1,&particleCamera.p);
            dc->DrawInstanced(4,state.count,0,0);dc->VSSetShaderResources(0,2,nil);
            if(state.kind==2)state.particles.reset();
        }
        dc->OMSetRenderTargets(0,nullptr,nullptr);state.drawn=true;return S_OK;
    }
    HRESULT Texture(unsigned w,unsigned h,bool target,const void* data,unsigned pitch,std::shared_ptr<SceneTile>& out){
        D3D11_TEXTURE2D_DESC desc={};desc.Width=w;desc.Height=h;desc.ArraySize=desc.MipLevels=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.Usage=data?D3D11_USAGE_IMMUTABLE:D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|(target?D3D11_BIND_RENDER_TARGET:0);
        D3D11_SUBRESOURCE_DATA init={data,pitch,0};auto tile=std::make_shared<SceneTile>();
        TRY(gpu->CreateTexture2D(&desc,data?&init:nullptr,&tile->color.p));TRY(gpu->CreateShaderResourceView(tile->color.p,nullptr,&tile->view.p));
        if(target)TRY(gpu->CreateRenderTargetView(tile->color.p,nullptr,&tile->target.p));
        tile->width=w;tile->height=h;out=tile;return S_OK;
    }
    HRESULT Init(HWND hwnd,bool visible,unsigned w,unsigned h,const void* v,unsigned vl,const void* p,unsigned pl){
        owner=hwnd;width=w;height=h;presentedSize=LONG((h<<16)|w);onscreen=visible;QueryPerformanceFrequency(&freq);
        wchar_t flag[2]={};profile=GetEnvironmentVariableW(L"ASTRALSPLIT_SCENE_PROFILE",flag,2)==1 && flag[0]==L'1';
        gpuProfile=GetEnvironmentVariableW(L"ASTRALSPLIT_SCENE_GPU_PROFILE",flag,2)==1 && flag[0]==L'1';
        Ref<IDXGIFactory2> factory;TRY(CreateDXGIFactory1(__uuidof(IDXGIFactory2),(void**)&factory.p));
        Ref<IDXGIAdapter1> adapter;HMONITOR monitor=MonitorFromWindow(hwnd,MONITOR_DEFAULTTONEAREST);
        for(unsigned i=0;;i++){
            TRY(factory->EnumAdapters1(i,&adapter.p));bool found=false;
            for(unsigned j=0;;j++){Ref<IDXGIOutput> o;if(FAILED(adapter->EnumOutputs(j,&o.p)))break;
                DXGI_OUTPUT_DESC d={};if(SUCCEEDED(o->GetDesc(&d)) && d.Monitor==monitor){found=true;break;}}
            if(found)break;adapter.reset();
        }
        TRY(D3D11CreateDevice(adapter.p,D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,
                              D3D11_SDK_VERSION,&gpu.p,nullptr,&dc.p));
        displayAdapter.p=adapter.p;displayAdapter->AddRef();
        TRY(dc->QueryInterface(__uuidof(ID3D11DeviceContext1),(void**)&offsets.p));
        lineDevice.device.p=gpu.p;gpu->AddRef();lineDevice.commands.p=dc.p;dc->AddRef();
        if(onscreen){
            DXGI_SWAP_CHAIN_DESC1 desc={};desc.Width=w;desc.Height=h;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count=1;desc.BufferCount=2;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;desc.Scaling=DXGI_SCALING_STRETCH;
            desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            Ref<IDXGISwapChain1> first;TRY(factory->CreateSwapChainForComposition(gpu.p,&desc,nullptr,&first.p));
            TRY(first->QueryInterface(__uuidof(IDXGISwapChain2),(void**)&chain.p));TRY(chain->SetMaximumFrameLatency(1));
            latency=chain->GetFrameLatencyWaitableObject();if(!latency)return E_FAIL;
            Ref<IDXGIDevice> dx;TRY(gpu->QueryInterface(__uuidof(IDXGIDevice),(void**)&dx.p));
            TRY(DCompositionCreateDevice(dx.p,__uuidof(IDCompositionDevice),(void**)&comp.p));
            // A second adapter can warm its own swapchain while the current one still owns the HWND.
            TRY(comp->CreateVisual(&root.p));TRY(root->SetContent(chain.p));
        }
        TRY(gpu->CreateVertexShader(v,vl,nullptr,&vs.p));TRY(gpu->CreatePixelShader(p,pl,nullptr,&ps.p));
        D3D11_SAMPLER_DESC sd={};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
        TRY(gpu->CreateSamplerState(&sd,&linear.p));
        D3D11_BLEND_DESC bd={};auto& mix=bd.RenderTarget[0];mix.BlendEnable=TRUE;
        mix.SrcBlend=mix.SrcBlendAlpha=D3D11_BLEND_ONE;mix.DestBlend=mix.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
        mix.BlendOp=mix.BlendOpAlpha=D3D11_BLEND_OP_ADD;mix.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        TRY(gpu->CreateBlendState(&bd,&blend.p));
        D3D11_RASTERIZER_DESC rd={};rd.FillMode=D3D11_FILL_SOLID;rd.CullMode=D3D11_CULL_NONE;rd.DepthClipEnable=TRUE;rd.ScissorEnable=TRUE;
        TRY(gpu->CreateRasterizerState(&rd,&raster.p));D3D11_BUFFER_DESC b={};b.ByteWidth=16;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        TRY(gpu->CreateBuffer(&b,nullptr,&clockBuffer.p));
        for(auto& t:timings){
            D3D11_QUERY_DESC q={D3D11_QUERY_TIMESTAMP_DISJOINT,0};TRY(gpu->CreateQuery(&q,&t.span.p));
            q.Query=D3D11_QUERY_TIMESTAMP;TRY(gpu->CreateQuery(&q,&t.begin.p));TRY(gpu->CreateQuery(&q,&t.end.p));
            if(gpuProfile)for(auto& boundary:t.boundaries)TRY(gpu->CreateQuery(&q,&boundary.p));
        }
        stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);dirty=CreateEventW(nullptr,FALSE,FALSE,nullptr);ready=CreateEventW(nullptr,FALSE,FALSE,nullptr);
        if(!stop||!dirty||!ready)return HRESULT_FROM_WIN32(GetLastError());
        worker=CreateThread(nullptr,0,Run,this,0,nullptr);return worker?S_OK:HRESULT_FROM_WIN32(GetLastError());
    }
    HRESULT SetPacket(const ScenePass* p,int pc,const SceneDraw* d,int n,LONGLONG* issue,const ScenePassTag* tags=nullptr){
        if(!pc || pc>384 || n<0 || n>65536 || p[pc-1].slot!=0)return E_INVALIDARG;
        const LONGLONG start=profile?Counter():0;
        auto packet=std::make_shared<ScenePacket>();packet->passes.assign(p,p+pc);packet->tags.resize(pc);
        if(n){packet->draws.assign(d,d+n);packet->inputs.resize(n);}
        UINT64 pixels=0;bool seen[384]={};
        for(int layer=0;layer<pc;layer++){
            const auto& pass=p[layer];
            if(pass.slot<0 || pass.slot>=pc || seen[pass.slot] || pass.width<1 || pass.height<1 || pass.width>8192 || pass.height>8192 ||
                pass.first<0 || pass.count<0 || pass.first>n-pass.count)return E_INVALIDARG;
            pixels+=UINT64(pass.width)*pass.height;if(pixels>128ull*1024*1024)return E_OUTOFMEMORY;
            for(int i=pass.first;i<pass.first+pass.count;i++){
                auto& q=packet->draws[i];
                if(q.material==22){
                    packet->scrollOwner=int(q.param[4]);packet->anchor=q.param[0];
                    packet->nearOffset=q.param[1];packet->farOffset=q.param[2];
                }
                for(int id:{q.image,q.secondary,q.atlas})if(id<0 && (id< -pc || !seen[-1-id]))return E_INVALIDARG;
            }
            seen[pass.slot]=true;
            if(tags)packet->tags[pass.slot]=tags[layer];
        }
        HRESULT hr=S_OK;
        const LONGLONG beforeLock=profile?Counter():0;
        std::shared_ptr<ScenePacket> replaced;
        AcquireSRWLockExclusive(&guard);
        if(profile)perf.submitWaitTicks+=Counter()-beforeLock;
        for(int i=0;i<n;i++){
            const auto& q=packet->draws[i];const int ids[]={q.image,q.secondary,q.atlas};
            for(int k=0;k<3;k++){
                int id=ids[k];if(id<=0)continue;
                auto item=packet->art.find(id);
                if(item==packet->art.end()){
                    const auto latest=resources.find(id);
                    if(latest==resources.end()){hr=E_INVALIDARG;break;}
                    item=packet->art.emplace(id,latest->second).first;
                }
                packet->inputs[i][k]=item->second.get();
            }
            if(FAILED(hr))break;
        }
        if(SUCCEEDED(hr)){
            packet->time=Counter();packet->sequence=++serial;
            replaced=std::move(submitted);submitted=packet;*issue=serial;
            if(profile){++perf.packets;perf.submitTicks+=packet->time-start;}
        }
        ReleaseSRWLockExclusive(&guard);if(SUCCEEDED(hr))SetEvent(dirty);return hr;
    }
    HRESULT Realize(SceneTile& t){
        if(t.view.p)return S_OK;if(!t.shape)return E_FAIL;
        std::shared_ptr<SceneTile> pixels;TRY(Texture(t.width,t.height,true,nullptr,0,pixels));
        TRY(vectors.Canvas(pixels->color.p,pixels->canvas));auto ctx=vectors.context.p;ctx->SetTarget(pixels->canvas.p);ctx->BeginDraw();
        ctx->SetTransform(D2D1::Matrix3x2F::Identity());ctx->Clear(D2D1::ColorF(0,0.f));
        auto& h=t.shape->info;float x=t.width/h.w,y=t.height/h.h;
        vectors.Paint(*t.shape,{x,0,0,y,-h.x*x,-h.y*y},1);
        HRESULT hr=ctx->EndDraw();ctx->SetTarget(nullptr);TRY(hr);
        t.view.p=pixels->view.p;pixels->view.p=nullptr;t.color.p=pixels->color.p;pixels->color.p=nullptr;return S_OK;
    }
    bool Direct(const SceneDraw& q,const SceneTile* tile){
        if(q.image<=0 || q.material!=0 || q.span[3]!=0 || q.axis[2]!=0 || q.axis[3]!=0 || q.span[0]!=1 || q.span[1]!=1)return false;
        return bool(tile->shape);
    }
    HRESULT BakeVectors(const std::shared_ptr<ScenePacket>& p){
        std::unordered_map<int,bool> sampled;
        for(size_t i=0;i<p->draws.size();i++){
            const auto& q=p->draws[i];const auto& inputs=p->inputs[i];
            if(inputs[0] && inputs[0]->shape && !Direct(q,inputs[0]))sampled[q.image]=true;
            if(inputs[1] && inputs[1]->shape)sampled[q.secondary]=true;
            if(inputs[2] && inputs[2]->shape)sampled[q.atlas]=true;
        }
        struct Cell{SceneTile* tile;int x,y;};
        std::vector<SceneTile*> tiles;std::vector<Cell> page;
        for(auto& pair:p->art){auto& t=*pair.second;if(!t.shape)continue;
            TRY(vectors.Prepare(*t.shape));
            if(t.view.p && !(t.packed && sampled.count(pair.first)))continue;
            if(t.packed){t.view.reset();t.color.reset();t.packed=false;t.uvX=t.uvY=0;t.uvScaleX=t.uvScaleY=1;}
            if(t.width>1020 || t.height>1020 || sampled.count(pair.first))TRY(Realize(t));
            else tiles.push_back(&t);
        }
        std::sort(tiles.begin(),tiles.end(),[](SceneTile* a,SceneTile* b){return a->height>b->height;});
        int x=1,y=1,row=0,usedW=0;
        auto flush=[&]()->HRESULT{
            if(page.empty())return S_OK;
            int w=usedW+2,h=y+row+2;std::shared_ptr<SceneTile> atlas;TRY(Texture(w,h,true,nullptr,0,atlas));
            TRY(vectors.Canvas(atlas->color.p,atlas->canvas));auto ctx=vectors.context.p;
            ctx->SetTarget(atlas->canvas.p);ctx->BeginDraw();ctx->SetTransform(D2D1::Matrix3x2F::Identity());ctx->Clear(D2D1::ColorF(0,0.f));
            for(auto& cell:page){auto& t=*cell.tile;auto& v=*t.shape;float sx=t.width/v.info.w,sy=t.height/v.info.h;
                ctx->SetTransform(D2D1::Matrix3x2F::Identity());ctx->PushAxisAlignedClip(D2D1::RectF(cell.x,cell.y,cell.x+t.width,cell.y+t.height),D2D1_ANTIALIAS_MODE_ALIASED);
                vectors.Paint(v,{sx,0,0,sy,cell.x-sx*v.info.x,cell.y-sy*v.info.y},1);ctx->PopAxisAlignedClip();
                t.view.p=atlas->view.p;t.view->AddRef();t.packed=true;t.uvX=float(cell.x)/w;t.uvY=float(cell.y)/h;t.uvScaleX=float(t.width)/w;t.uvScaleY=float(t.height)/h;
            }
            HRESULT hr=ctx->EndDraw();ctx->SetTarget(nullptr);page.clear();x=y=1;row=usedW=0;return hr;
        };
        for(auto t:tiles){
            if(x+(int)t->width+2>2048){x=1;y+=row+2;row=0;}
            if(y+(int)t->height+2>2048)TRY(flush());
            page.push_back({t,x,y});row=std::max(row,(int)t->height);x+=t->width+2;usedW=std::max(usedW,x);
        }
        return flush();
    }
    void HashPasses(const std::shared_ptr<ScenePacket>& p){
        signatures.assign(p->passes.size(),0);animated.assign(p->passes.size(),false);
        scrollPass.assign(p->passes.size(),false);
        for(auto& pass:p->passes){UINT64 hash=1469598103934665603ull;bool motion=false;
            auto word=[&](UINT64 v){hash^=v;hash*=1099511628211ull;};
            word(pass.width);word(pass.height);word(pass.count);
            for(int i=pass.first;i<pass.first+pass.count;i++){
                const auto& q=p->draws[i];const int ids[]={q.image,q.secondary,q.atlas};
                if(q.material==22 || q.material==23)scrollPass[pass.slot]=true;
                for(int k=0;k<3;k++){
                    int id=ids[k];
                    if(id>0){const auto t=p->inputs[i][k];word(t->version);motion=motion||(t->cloud && t->cloud->count && t->cloud->kind!=2);}
                    else if(id<0){word(signatures[-1-id]);motion=motion||animated[-1-id];
                        if(scrollPass[-1-id])scrollPass[pass.slot]=true;}else word(0);
                }
                word(q.material);motion=motion || q.span[3]!=0 || (q.material==7 && q.param[5]!=0) || (q.material==24 && q.param[9]!=0) ||
                    ((q.material==20 || q.material==21) && q.param[1]!=0);
                for(unsigned n=0;n<40;n++){UINT32 bits;std::memcpy(&bits,((const BYTE*)q.origin)+n*4,4);word(bits);}
                for(int k:q.scissor)word(k);
            }
            signatures[pass.slot]=hash;animated[pass.slot]=motion;
        }
    }
    static bool Fits(const std::shared_ptr<SceneTile>& tile,const ScenePass& pass){
        return tile && tile->width==unsigned(pass.width) && tile->height==unsigned(pass.height);
    }
    static UINT64 TileBytes(const SceneTile& tile){return UINT64(tile.width)*tile.height*4;}
    void KeepIdlePass(std::shared_ptr<SceneTile>& tile){
        const auto bytes=TileBytes(*tile);
        if(bytes>PassLimit){++layers.trimmed;return;}
        while(passPool.size()>=PassCount || UINT64(passBytes)+bytes>PassLimit){
            auto oldest=OldestFree(passPool);
            if(oldest==passPool.end()){++layers.trimmed;return;}
            passBytes-=TileBytes(**oldest);passPool.erase(oldest);++layers.trimmed;
        }
        passBytes+=bytes;passPool.push_back(std::move(tile));
    }
    HRESULT FindIdlePass(const ScenePass& pass,std::shared_ptr<SceneTile>& out){
        bool pending=false;
        for(auto i=passPool.begin();i!=passPool.end();++i){
            if(!Fits(*i,pass))continue;
            if(!Available(*i)){pending=true;continue;}
            passBytes-=TileBytes(**i);out=std::move(*i);passPool.erase(i);
            out->baked=false;++passReuses;++layers.idle;
            return S_OK;
        }
        TRY(Texture(pass.width,pass.height,true,nullptr,0,out));
        ++passCreates;
        if(pending)++layers.pendingMiss;else ++layers.sizeMiss;
        return S_OK;
    }
    HRESULT PrepareTargets(const ScenePacket& packet){
        const auto& client=packet.passes.back();
        if(unsigned(client.width)!=width || unsigned(client.height)!=height){
            if(!passes.empty())passes[0].reset();
            readback.reset();
            if(onscreen)TRY(chain->ResizeBuffers(0,client.width,client.height,DXGI_FORMAT_UNKNOWN,DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT));
            width=client.width;height=client.height;
        }
        previousPasses.swap(passes);passes.resize(packet.passes.size());
        if(!previousPasses.empty())passes[0]=std::move(previousPasses[0]);
        if(!passes[0]){
            if(onscreen){
                auto tile=std::make_shared<SceneTile>();tile->width=width;tile->height=height;
                TRY(chain->GetBuffer(0,__uuidof(ID3D11Texture2D),(void**)&tile->color.p));
                TRY(gpu->CreateRenderTargetView(tile->color.p,nullptr,&tile->target.p));passes[0]=std::move(tile);
            }else TRY(Texture(width,height,true,nullptr,0,passes[0]));
        }
        // A logical layer can move when a neighbor appears or disappears.
        // Reserve its texture before giving other layers an equal-size one.
        for(const auto& pass:packet.passes){
            const auto identity=packet.tags[pass.slot].identity;
            if(!pass.slot || !identity)continue;
            auto match=[&](const auto& tile){return Fits(tile,pass) && tile->passIdentity==identity;};
            if(size_t(pass.slot)<previousPasses.size() && match(previousPasses[pass.slot])){
                passes[pass.slot]=std::move(previousPasses[pass.slot]);continue;
            }
            auto found=std::find_if(previousPasses.begin(),previousPasses.end(),match);
            if(found!=previousPasses.end()){passes[pass.slot]=std::move(*found);++passReuses;++layers.moved;}
        }
        for(const auto& pass:packet.passes){
            if(!pass.slot || passes[pass.slot] || size_t(pass.slot)>=previousPasses.size())continue;
            auto& old=previousPasses[pass.slot];
            if(Fits(old,pass))passes[pass.slot]=std::move(old);
        }
        for(const auto& pass:packet.passes){
            auto& tile=passes[pass.slot];
            if(!tile){
                auto found=std::find_if(previousPasses.begin(),previousPasses.end(),[&](const auto& old){return Fits(old,pass);});
                if(found!=previousPasses.end()){
                    // These targets are private to this immediate context. The old
                    // draws and reads precede the clear for the next use.
                    tile=std::move(*found);tile->baked=false;++passReuses;++layers.matchedSize;
                }else TRY(FindIdlePass(pass,tile));
            }
            const auto identity=packet.tags[pass.slot].identity;
            if(tile->passIdentity!=identity){tile->passIdentity=identity;tile->baked=false;}
        }
        for(auto& old:previousPasses)if(old)KeepIdlePass(old);
        previousPasses.clear();
        return S_OK;
    }
    HRESULT Prepare(const std::shared_ptr<ScenePacket>& p){
        const auto start=profile?Counter():0;
        TRY(vectors.Init(gpu.p));
        TRY(BakeVectors(p));
        const auto baked=profile?Counter():0;
        HashPasses(p);
        dc->OMSetRenderTargets(0,nullptr,nullptr);ID3D11ShaderResourceView* nil[3]={};dc->PSSetShaderResources(0,3,nil);
        TRY(PrepareTargets(*p));
        const auto planned=profile?Counter():0;
        size_t blocks=(p->draws.size()+255)/256;
        while(constants.size()<blocks){auto cb=std::make_unique<SceneConstantPage>();
            D3D11_BUFFER_DESC b={};b.ByteWidth=65536;b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;b.Usage=D3D11_USAGE_DYNAMIC;b.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
            TRY(gpu->CreateBuffer(&b,nullptr,&cb->buffer.p));constants.push_back(std::move(cb));}
        std::array<std::array<float,44>,256> words;
        for(size_t block=0;block<blocks;block++){
            auto& page=*constants[block];
            size_t k=std::min<size_t>(256,p->draws.size()-block*256);
            for(size_t i=0;i<k;i++){
                size_t index=block*256+i;
                const auto& q=p->draws[index];float* ptr=words[i].data();
                std::memcpy(ptr,q.origin,sizeof(float)*40);
                const auto t=p->inputs[index][0];
                if(t && t->packed){
                    ptr[6]=t->uvX+ptr[6]*t->uvScaleX;ptr[7]=t->uvY+ptr[7]*t->uvScaleY;ptr[8]*=t->uvScaleX;ptr[9]*=t->uvScaleY;}
                ptr[40]=ptr[41]=ptr[42]=0;ptr[43]=float(q.material);
            }
            if(page.words.size()==k && !std::memcmp(page.words.data(),words.data(),k*sizeof(words[0]))){++constantReuses;continue;}
            page.words.assign(words.begin(),words.begin()+k);
            D3D11_MAPPED_SUBRESOURCE map={};TRY(dc->Map(page.buffer.p,0,D3D11_MAP_WRITE_DISCARD,0,&map));
            for(size_t i=0;i<k;i++)std::memcpy(static_cast<BYTE*>(map.pData)+i*256,words[i].data(),sizeof(words[0]));
            dc->Unmap(page.buffer.p,0);++constantWrites;
        }
        if(profile){
            ++layers.prepared;layers.vectorTicks+=baked-start;layers.targetTicks+=planned-baked;layers.constantTicks+=Counter()-planned;
        }
        active=p;return S_OK;
    }
    ID3D11ShaderResourceView* View(int id,const SceneTile* t){
        if(id<0)return passes[-1-id]->view.p;
        if(!t)return nullptr;return t->output?t->output->view.p:t->view.p;
    }
    HRESULT DrawRibbons(SceneTile& tile){
        if(tile.output){tile.output->lastGpuFrame=gpuFrame;return S_OK;}
        auto& b=lineDevice;unsigned h=tile.height/2;
        if(tile.ribbon->stars && !b.orbits.angles.p){
            AcquireSRWLockShared(&guard);auto code=orbitProgram;ReleaseSRWLockShared(&guard);
            if(!code)return E_UNEXPECTED;
            TRY(ParticleOrbitProgram(&b,code->bytes.data(),code->sizes,code->samples));
        }
        if(b.width!=tile.width || b.height!=h)b.orbits.reset();
        b.width=tile.width;b.height=h;b.selected=0;b.pending=-1;b.shown=-1;
        TRY(DynamicOutput(tile));
        auto& target=b.frames[0];target.reset();target.color.p=tile.output->color.p;target.color->AddRef();
        target.target.p=tile.output->target.p;target.target->AddRef();
        auto& data=*tile.ribbon;auto v=data.view;
        HRESULT hr=data.stars?drawOrbitData(&b,data.spins.data(),data.stars,v,data.colors,data.widths,false):
            drawRibbonData(&b,data.vertices.data(),(unsigned)data.vertices.size(),data.colors,data.widths,v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],false);
        target.reset();return hr;
    }
    void MarkStage(SceneTiming& t,SceneStage stage){
        if(t.stage==stage || t.overflow)return;
        if(t.count==SceneTiming::BoundaryLimit){t.overflow=true;return;}
        dc->End(t.boundaries[t.count].p);t.stages[t.count++]=t.stage;t.stage=stage;
    }
    static SceneStage DrawStage(SceneStage fallback,int material){
        if(material==2 || material==9)return Lens;
        if(material==6 || material==17)return Projection;
        if(material==7)return Sky;
        return fallback;
    }
    bool ReadStages(const SceneTiming& t,UINT64 start,UINT64 end,UINT64 frequency,SceneGpuStamp& sample){
        if(t.overflow || !frequency || end<start)return false;
        const double scale=1000./frequency;
        for(unsigned i=0;i<t.count;i++){
            UINT64 mark=0;
            if(dc->GetData(t.boundaries[i].p,&mark,sizeof(mark),D3D11_ASYNC_GETDATA_DONOTFLUSH)!=S_OK || mark<start || mark>end)return false;
            sample.milliseconds[t.stages[i]]+=(mark-start)*scale;start=mark;
        }
        sample.milliseconds[t.stage]+=(end-start)*scale;sample.frame=t.frame;
        return true;
    }
    HRESULT Render(bool present=true){
        const LONGLONG start=profile?Counter():0;
        AcquireSRWLockShared(&guard);auto next=submitted;ReleaseSRWLockShared(&guard);
        if(!next)return S_FALSE;
        ++gpuFrame;
        SceneTiming& t=timings[timingIndex];double elapsed=0;
        SceneGpuStamp sample={};
        if(t.pending){
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT v={};UINT64 first=0,last=0;
            if(dc->GetData(t.span.p,&v,sizeof(v),D3D11_ASYNC_GETDATA_DONOTFLUSH)==S_OK &&
               dc->GetData(t.begin.p,&first,sizeof(first),D3D11_ASYNC_GETDATA_DONOTFLUSH)==S_OK &&
               dc->GetData(t.end.p,&last,sizeof(last),D3D11_ASYNC_GETDATA_DONOTFLUSH)==S_OK){
                if(!v.Disjoint && v.Frequency){
                    elapsed=1000.*double(last-first)/v.Frequency;
                    if(gpuProfile && !ReadStages(t,first,last,v.Frequency,sample))sample.frame=0;
                }
                gpuCompleted=std::max(gpuCompleted,t.frame);t.pending=false;
            }
        }
        bool measure=!t.pending;
        const bool timeStages=measure && gpuProfile;
        if(measure){dc->Begin(t.span.p);dc->End(t.begin.p);t.count=0;t.stage=Setup;t.overflow=false;}
        if(next!=active)TRY(Prepare(next));
        if(timeStages)MarkStage(t,Streams);
        const LONGLONG prepared=Counter();
        float time[4]={float(double(prepared-next->time)/freq.QuadPart),0,0,0};
        AcquireSRWLockShared(&guard);auto motion=scroll;ReleaseSRWLockShared(&guard);
        float at=float(motion.At(prepared,freq.QuadPart));
        if(motion.owner==next->scrollOwner && next->scrollOwner)
            at=std::clamp(at,next->nearOffset,next->farOffset);
        time[1]=at;time[2]=float(motion.owner);time[3]=motion.drag?float(motion.target):at;
        bool scrollMoved=lastScroll!=at || lastThumb!=time[3] || lastScrollOwner!=motion.owner;
        dc->UpdateSubresource(clockBuffer.p,0,nullptr,time,0,0);dc->PSSetConstantBuffers(1,1,&clockBuffer.p);
        int actualPasses=0,submissions=0,clouds=0,paths=0;for(auto& pair:next->art){clouds+=bool(pair.second->cloud);paths+=bool(pair.second->shape);}
        for(auto& pair:next->art){
            if(pair.second->ribbon)TRY(DrawRibbons(*pair.second));
            if(pair.second->cloud)TRY(Clouds(*pair.second,time[0]));
        }
        auto pipeline=[&](){
        dc->OMSetBlendState(blend.p,nullptr,0xffffffff);dc->RSSetState(raster.p);
        dc->IASetInputLayout(nullptr);dc->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        dc->VSSetShader(vs.p,nullptr,0);dc->PSSetShader(ps.p,nullptr,0);dc->PSSetSamplers(0,1,&linear.p);
            dc->PSSetConstantBuffers(1,1,&clockBuffer.p);
            dc->VSSetConstantBuffers(1,1,&clockBuffer.p);
        };pipeline();
        for(auto& pass:next->passes){
            auto& out=*passes[pass.slot];
            out.lastGpuFrame=gpuFrame;
            if(pass.slot!=0 && out.baked && !animated[pass.slot] && !(scrollPass[pass.slot] && scrollMoved) && out.cached==signatures[pass.slot])continue;
            out.cached=signatures[pass.slot];out.baked=true;++actualPasses;
            const int tag=next->tags[pass.slot].stage;
            const SceneStage passStage=!pass.slot?Composite:tag>=Sky && tag<=Layers?SceneStage(tag):Layers;
            if(timeStages)MarkStage(t,passStage);
            ID3D11ShaderResourceView* nil[3]={};dc->PSSetShaderResources(0,3,nil);
            auto& tile=passes[pass.slot];dc->OMSetRenderTargets(1,&tile->target.p,nullptr);
            float clear[4]={};dc->ClearRenderTargetView(tile->target.p,clear);
            D3D11_VIEWPORT vp={0,0,float(pass.width),float(pass.height),0,1};dc->RSSetViewports(1,&vp);
            for(int i=pass.first;i<pass.first+pass.count;i++){
                const auto& q=next->draws[i];const auto& bound=next->inputs[i];
                ID3D11ShaderResourceView* inputs[]={View(q.image,bound[0]),View(q.secondary,bound[1]),View(q.atlas,bound[2])};
                dc->PSSetShaderResources(0,3,inputs);
                int end=i+1,limit=std::min(pass.first+pass.count,(i/256+1)*256);
                const auto stage=timeStages?DrawStage(passStage,q.material):passStage;
                while(end<limit){
                    const auto& r=next->draws[end];const auto& other=next->inputs[end];
                    if(q.material==20 || r.material==20 || ((q.material==24)!=(r.material==24)) || std::memcmp(q.scissor,r.scissor,sizeof(q.scissor)) ||
                       View(r.image,other[0])!=inputs[0] || View(r.secondary,other[1])!=inputs[1] || View(r.atlas,other[2])!=inputs[2] ||
                       (timeStages && DrawStage(passStage,r.material)!=stage))break;
                    ++end;
                }
                if(timeStages)MarkStage(t,stage);
                auto cb=constants[i/256]->buffer.p;UINT first=(i%256)*16,count=16*(end-i);
                offsets->VSSetConstantBuffers1(0,1,&cb,&first,&count);offsets->PSSetConstantBuffers1(0,1,&cb,&first,&count);
                D3D11_RECT clip={q.scissor[0],q.scissor[1],q.scissor[2],q.scissor[3]};dc->RSSetScissorRects(1,&clip);
                if(q.material==20){
                    int section=int(q.param[2]);
                    dc->VSSetShader(particleVS[3].p,nullptr,0);dc->PSSetShader(particlePS[3].p,nullptr,0);
                    dc->DrawInstanced(66,section<0?33:section==3?1:29,0,0);
                    dc->VSSetShader(vs.p,nullptr,0);dc->PSSetShader(ps.p,nullptr,0);
                }else if(q.material==24){
                    dc->PSSetShader(milestonePS.p,nullptr,0);
                    dc->DrawInstanced(4,end-i,0,0);
                    dc->PSSetShader(ps.p,nullptr,0);
                }else dc->DrawInstanced(4,end-i,0,0);
                i=end-1;++submissions;
            }

        }
        if(measure){dc->End(t.end.p);dc->End(t.span.p);t.frame=gpuFrame;t.pending=true;}timingIndex=(timingIndex+1)%4;
        const LONGLONG encoded=profile?Counter():0;
        ReadRequestedPixels();
        const LONGLONG beforePresent=profile?Counter():0;
        if(onscreen){if(present)TRY(chain->Present(1,0));}else dc->Flush();
        const LONGLONG finished=profile?Counter():0;
        AcquireSRWLockExclusive(&guard);result.sequence=next->sequence;result.frames++;if(elapsed>0)result.gpuMs=elapsed;
        if(sample.frame)gpuTrace[gpuSamples++%TraceCapacity]=sample;
        if(profile){
            perf.recordTicks+=encoded-prepared;
            perf.prepareTicks+=prepared-start;perf.presentTicks+=finished-beforePresent;
            trace[perf.frames%TraceCapacity]={next->sequence,result.frames,next->time,start,prepared,encoded,finished};
            ++perf.frames;
            perf.targetCreates=targetCreates;perf.targetReuses=targetReuses;perf.pooledBytes=pooledBytes;
            perf.constantWrites=constantWrites;perf.constantReuses=constantReuses;
            perf.inputCreates=inputCreates;perf.inputReuses=inputReuses;perf.inputBytes=inputBytes;
            perf.passCreates=passCreates;perf.passReuses=passReuses;perf.passBytes=passBytes;
            publishedLayers=layers;
        }
        lastScroll=at;lastThumb=time[3];lastScrollOwner=motion.owner;
        shownScrollOwner=next->scrollOwner;shownScrollOffset=motion.owner==next->scrollOwner?at:next->anchor;
        InterlockedExchange(&presentedSize,LONG((height<<16)|width));
        result.activePasses=actualPasses;result.submissions=submissions;result.clouds=clouds;result.paths=paths;
        result.draws=(INT)next->draws.size();result.passes=(INT)next->passes.size();result.images=(INT)next->art.size();ReleaseSRWLockExclusive(&guard);
        return S_OK;
    }
    void ReadRequestedPixels(){
        AcquireSRWLockExclusive(&captureGuard);
        if(capture){
            captureResult=captureHeight==height && captureWidth==width?ReadPixels(capture,captureStride):E_PENDING;
            capture=nullptr;SetEvent(ready);
        }
        ReleaseSRWLockExclusive(&captureGuard);
    }
    HRESULT ReadPixels(BYTE* output,unsigned stride){
        if(!readback.p){D3D11_TEXTURE2D_DESC td;passes[0]->color->GetDesc(&td);td.BindFlags=td.MiscFlags=0;td.Usage=D3D11_USAGE_STAGING;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            TRY(gpu->CreateTexture2D(&td,nullptr,&readback.p));}
        dc->CopyResource(readback.p,passes[0]->color.p);D3D11_MAPPED_SUBRESOURCE m={};TRY(dc->Map(readback.p,0,D3D11_MAP_READ,0,&m));
        for(unsigned y=0;y<height;y++)std::memcpy(output+y*size_t(stride),(BYTE*)m.pData+y*size_t(m.RowPitch),width*4);
        dc->Unmap(readback.p,0);return S_OK;
    }
    static DWORD WINAPI Run(void* p){return ((WindowScene*)p)->Work();}
    HRESULT Fly(LaunchSurface& f){
        LARGE_INTEGER now;QueryPerformanceCounter(&now);
        if(f.meteor){
            LONG direction=InterlockedCompareExchange(&f.direction,0,0);
            double dt=f.previous?double(now.QuadPart-f.previous)/freq.QuadPart:0;
            f.previous=now.QuadPart;
            f.progress=std::clamp(float(f.progress+direction*dt*1000/f.duration),0.f,1.f);
            bool atHome=f.outgoing?f.progress==0:f.progress==1;
            bool bodyVisible=f.outgoing?f.progress<.62f:f.progress>.4f;
            if(bodyVisible || atHome){
                TRY(Render(false));dc->OMSetRenderTargets(0,nullptr,nullptr);
                dc->CopyResource(f.body.p,passes[0]->color.p);
            }
            f.Draw(dc.p,blend.p,raster.p,linear.p,f.progress);
            TRY(f.chain->Present(1,0));
            if(direction && (direction>0?f.progress==1:f.progress==0)){
                if(atHome)TRY(chain->Present(0,0));
                InterlockedExchange(&f.finished,direction);
            }
            InterlockedExchange(&f.presented,LONG(f.progress*100000));
            return S_OK;
        }
        float phase=f.start?std::min(1.f,float(double(now.QuadPart-f.start)*1000/(double(freq.QuadPart)*f.duration))):0.f;
        // Keep the home graph current during the intake. Switching it back on at
        // 55.5% would submit accumulated atlas changes on the first expansion frame.
        TRY(Render(false));
        if(!f.start || phase>.555f){
            dc->OMSetRenderTargets(0,nullptr,nullptr);
            dc->CopyResource(f.body.p,passes[0]->color.p);
            // Prime the expanded-window draw before starting the clock. The clear in
            // the following draw discards this frame without ever presenting it.
            if(!f.start)f.Draw(dc.p,blend.p,raster.p,linear.p,.75f);
        }
        f.Draw(dc.p,blend.p,raster.p,linear.p,phase);
        TRY(f.chain->Present(1,0));
        if(!f.start){QueryPerformanceCounter(&now);f.start=now.QuadPart;}
        if(phase>=1){
            // The native host already holds the expanded frame when its cloak comes off.
            TRY(chain->Present(0,0));InterlockedExchange(&f.finished,1);
        }
        return S_OK;
    }
    DWORD Work(){
        DWORD id=0;HANDLE priority=AvSetMmThreadCharacteristicsW(L"Games",&id);
        HANDLE waits[]={stop,onscreen?latency:dirty};
        for(;;){
            AcquireSRWLockShared(&guard);auto flight=launch;ReleaseSRWLockShared(&guard);
            if(flight){
                HANDLE next[]={stop,flight->latency};
                DWORD tick=WaitForMultipleObjects(2,next,FALSE,50);
                if(tick==WAIT_OBJECT_0)break;
                if(tick==WAIT_TIMEOUT)continue;
                HRESULT hr=tick==WAIT_OBJECT_0+1?Fly(*flight):E_FAIL;
                if(FAILED(hr) && !InterlockedCompareExchange(&flight->cancelled,0,0)){InterlockedExchange(&failed,hr);break;}
                continue;
            }
            if(InterlockedCompareExchange(&parked,0,0)){
                HANDLE idle[]={stop,dirty};
                if(WaitForMultipleObjects(2,idle,FALSE,INFINITE)==WAIT_OBJECT_0)break;
                continue;
            }
            DWORD wake=WaitForMultipleObjects(2,waits,FALSE,onscreen?400:INFINITE);
            if(wake==WAIT_OBJECT_0)break;if(wake==WAIT_TIMEOUT)continue;
            if(wake!=WAIT_OBJECT_0+1){InterlockedExchange(&failed,E_FAIL);break;}
            HRESULT hr=Render();if(FAILED(hr)){InterlockedExchange(&failed,hr);break;}
            if(hr==S_FALSE && onscreen){HANDLE first[]={stop,dirty};if(WaitForMultipleObjects(2,first,FALSE,INFINITE)==WAIT_OBJECT_0)break;
                hr=Render();if(FAILED(hr)){InterlockedExchange(&failed,hr);break;}}
        }
        if(priority)AvRevertMmThreadCharacteristics(priority);return 0;
    }
};
#define SCENE_API extern "C" __declspec(dllexport)
SCENE_API void SceneScrollMotion(WindowScene* s,const ScrollMotion* motion){
    if(!s || !motion)return;
    AcquireSRWLockExclusive(&s->guard);s->scroll=*motion;ReleaseSRWLockExclusive(&s->guard);
    SetEvent(s->dirty);
}
SCENE_API int SceneDisplayedOffset(WindowScene* s,int owner,double* offset){
    if(!s||!offset||!owner)return 0;
    AcquireSRWLockShared(&s->guard);bool match=s->shownScrollOwner==owner;
    if(match)*offset=s->shownScrollOffset;
    ReleaseSRWLockShared(&s->guard);return match?1:0;
}
SCENE_API HRESULT SceneOpen(HWND host,int screen,unsigned w,unsigned h,const void* vs,unsigned vl,const void* ps,unsigned pl,WindowScene** out){
    if(!host || !w || !h || w>8192 || h>8192 || !vs || !ps || !out)return E_INVALIDARG;
    *out=nullptr;auto s=new(std::nothrow) WindowScene();if(!s)return E_OUTOFMEMORY;
    HRESULT hr=s->Init(host,screen!=0,w,h,vs,vl,ps,pl);if(FAILED(hr)){delete s;return hr;}*out=s;return S_OK;
}
SCENE_API HRESULT SceneImage(WindowScene* s,int id,unsigned w,unsigned h,const BYTE* pixels,unsigned stride){
    if(!s||id<=0||w<1||h<1||w>8192||h>8192||!pixels||stride<w*4)return E_INVALIDARG;
    std::shared_ptr<SceneTile> tile;TRY(s->Texture(w,h,false,pixels,stride,tile));
    AcquireSRWLockExclusive(&s->guard);s->resources[id]=tile;ReleaseSRWLockExclusive(&s->guard);return S_OK;
}
SCENE_API void SceneReleaseImage(WindowScene* s,int id){if(s){AcquireSRWLockExclusive(&s->guard);s->resources.erase(id);ReleaseSRWLockExclusive(&s->guard);}}
SCENE_API HRESULT SceneSubmit(WindowScene* s,const ScenePass* p,int pc,const SceneDraw* d,int count,LONGLONG* seq){
    if(!s||!p||pc<1||(!d&&count)||!seq)return E_INVALIDARG;return s->SetPacket(p,pc,d,count,seq);
}
SCENE_API HRESULT SceneSubmitTagged(WindowScene* s,const ScenePass* p,const ScenePassTag* tags,int pc,const SceneDraw* d,int count,LONGLONG* seq){
    if(!s||!p||!tags||pc<1||(!d&&count)||!seq)return E_INVALIDARG;return s->SetPacket(p,pc,d,count,seq,tags);
}
SCENE_API HRESULT SceneShow(WindowScene* s){
    if(!s||!s->onscreen)return E_INVALIDARG;
    if(!s->host.p)TRY(s->comp->CreateTargetForHwnd(s->owner,TRUE,&s->host.p));
    HRESULT hr=s->host->SetRoot(s->root.p);
    if(SUCCEEDED(hr))hr=s->comp->Commit();
    if(FAILED(hr)){
        s->host->SetRoot(nullptr);s->host.reset();s->shown=false;s->comp->Commit();return hr;
    }
    s->shown=true;return S_OK;
}
SCENE_API HRESULT SceneYieldTarget(WindowScene* s){
    if(!s || !s->host.p)return S_OK;
    HRESULT hr=s->host->SetRoot(nullptr);s->shown=false;s->host.reset();
    HRESULT commit=s->comp->Commit();return FAILED(hr)?hr:commit;
}
SCENE_API int SceneOwnsMonitor(WindowScene* s,HMONITOR monitor){
    if(!s || !monitor)return 0;
    for(UINT i=0;;++i){
        Ref<IDXGIOutput> output;if(FAILED(s->displayAdapter->EnumOutputs(i,&output.p)))return 0;
        DXGI_OUTPUT_DESC desc={};if(SUCCEEDED(output->GetDesc(&desc)) && desc.Monitor==monitor)return 1;
    }
}
SCENE_API HRESULT SceneBeginLaunch(WindowScene* s,HWND popup,const float* bounds,const float* seeds,int count,int duration,
                                   const void* vs,int vl,const void* ps,int pl){
    if(!s || !s->onscreen || !popup || !bounds || !seeds || count<1 || count>4096 || duration<100 || !vs || !ps ||
       bounds[0]<1 || bounds[1]<1 || bounds[0]>16384 || bounds[1]>16384)return E_INVALIDARG;
    LONG displayed=InterlockedCompareExchange(&s->presentedSize,0,0);
    if(bounds[4]!=(displayed&0xffff) || bounds[5]!=(displayed>>16))return E_INVALIDARG;
    auto flight=std::make_shared<LaunchSurface>();
    TRY(flight->Init(s->gpu.p,s->comp.p,popup,bounds,seeds,count,duration,vs,vl,ps,pl));
    if(bounds[7]<0){
        flight->meteor=true;flight->outgoing=bounds[7]==-1;
        flight->values[9]=flight->outgoing?1:0;
        flight->values[10]=bounds[8];flight->values[11]=bounds[9];
    }
    AcquireSRWLockExclusive(&s->guard);s->launch=flight;ReleaseSRWLockExclusive(&s->guard);SetEvent(s->dirty);return S_OK;
}
SCENE_API void ScenePark(WindowScene* s,int park){
    if(s){InterlockedExchange(&s->parked,park);SetEvent(s->dirty);}
}
SCENE_API int SceneMeteor(WindowScene* s,int direction){
    if(!s)return -2;
    AcquireSRWLockShared(&s->guard);auto f=s->launch;ReleaseSRWLockShared(&s->guard);
    if(!f || !f->meteor || FAILED(InterlockedCompareExchange(&s->failed,0,0)))return -2;
    if(direction){InterlockedExchange(&f->finished,0);InterlockedExchange(&f->direction,direction);}
    return InterlockedCompareExchange(&f->presented,0,0);
}
SCENE_API int SceneLaunchFinished(WindowScene* s){
    if(!s)return 0;
    AcquireSRWLockShared(&s->guard);auto f=s->launch;ReleaseSRWLockShared(&s->guard);
    return f?InterlockedCompareExchange(&f->finished,0,0):0;
}
SCENE_API void SceneEndLaunch(WindowScene* s){
    if(!s)return;
    AcquireSRWLockExclusive(&s->guard);auto f=std::move(s->launch);if(f)InterlockedExchange(&f->cancelled,1);ReleaseSRWLockExclusive(&s->guard);
    SetEvent(s->dirty);
}
SCENE_API void SceneStats(WindowScene* s,SceneResult* out){
    if(!s||!out)return;AcquireSRWLockShared(&s->guard);*out=s->result;ReleaseSRWLockShared(&s->guard);out->status=InterlockedCompareExchange(&s->failed,0,0);
}
SCENE_API int SceneProfile(WindowScene* s,ScenePerfResult* perf,SceneFrameStamp* rows,int limit){
    if(!s || !perf || limit<0 || (limit && !rows))return 0;
    AcquireSRWLockShared(&s->guard);
    *perf=s->perf;
    const int n=int(std::min<LONGLONG>(perf->frames,std::min<size_t>(limit,WindowScene::TraceCapacity)));
    for(int i=0;i<n;i++)rows[i]=s->trace[(perf->frames-n+i)%WindowScene::TraceCapacity];
    ReleaseSRWLockShared(&s->guard);
    return n;
}
SCENE_API int SceneProfileDetail(WindowScene* s,SceneLayerResult* layers,SceneGpuStamp* rows,int limit){
    if(!s || !layers || limit<0 || (limit && !rows))return 0;
    AcquireSRWLockShared(&s->guard);
    *layers=s->publishedLayers;
    const int n=int(std::min<UINT64>(s->gpuSamples,std::min<size_t>(limit,WindowScene::TraceCapacity)));
    for(int i=0;i<n;i++)rows[i]=s->gpuTrace[(s->gpuSamples-n+i)%WindowScene::TraceCapacity];
    ReleaseSRWLockShared(&s->guard);
    return n;
}
SCENE_API HRESULT SceneReadSized(WindowScene* s,BYTE* bytes,unsigned stride,unsigned width,unsigned height){
    if(!s||!bytes||!width||!height||width>8192||height>8192||stride<width*4)return E_INVALIDARG;
    auto error=InterlockedCompareExchange(&s->failed,0,0);if(FAILED(error))return error;
    AcquireSRWLockShared(&s->guard);bool available=bool(s->submitted);ReleaseSRWLockShared(&s->guard);
    if(!available)return E_PENDING;
    AcquireSRWLockExclusive(&s->captureGuard);
    if(s->captureInProgress){ReleaseSRWLockExclusive(&s->captureGuard);return E_PENDING;}
    s->captureInProgress=true;ResetEvent(s->ready);s->capture=bytes;s->captureStride=stride;s->captureWidth=width;s->captureHeight=height;
    ReleaseSRWLockExclusive(&s->captureGuard);SetEvent(s->dirty);
    DWORD result=WaitForSingleObject(s->ready,3500);
    AcquireSRWLockExclusive(&s->captureGuard);s->capture=nullptr;s->captureInProgress=false;
    HRESULT hr=result==WAIT_OBJECT_0?s->captureResult:HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    ReleaseSRWLockExclusive(&s->captureGuard);return hr;
}
SCENE_API HRESULT SceneRead(WindowScene* s,BYTE* bytes,unsigned stride){
    if(!s)return E_INVALIDARG;
    LONG displayed=InterlockedCompareExchange(&s->presentedSize,0,0);
    return SceneReadSized(s,bytes,stride,displayed & 0xffff,displayed>>16);
}
SCENE_API HRESULT SceneHealth(WindowScene* s){
    return s && s->gpu.p?s->gpu->GetDeviceRemovedReason():E_POINTER;
}
SCENE_API void SceneClose(WindowScene* s){delete s;}

// Only legacy surfaces published before the native host connected use this snapshot.
 // The normal ribbon, particle and field paths submit source data to the worker.
SCENE_API HRESULT SceneParticles(WindowScene* scene,int id,Batch* batch,int slot){
    if(!scene||!batch||id<=0||slot<0||slot>=batch->slots||!batch->frames[slot].color.p)return E_INVALIDARG;
    auto source=batch->frames[slot].color.p;D3D11_TEXTURE2D_DESC td;source->GetDesc(&td);
    td.Usage=D3D11_USAGE_STAGING;td.BindFlags=td.MiscFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    Ref<ID3D11Texture2D> stage;TRY(batch->device->CreateTexture2D(&td,nullptr,&stage.p));
    batch->commands->CopyResource(stage.p,source);D3D11_MAPPED_SUBRESOURCE pixels={};
    TRY(batch->commands->Map(stage.p,0,D3D11_MAP_READ,0,&pixels));
    HRESULT hr=SceneImage(scene,id,td.Width,td.Height,(const BYTE*)pixels.pData,pixels.RowPitch);
    batch->commands->Unmap(stage.p,0);return hr;
}
SCENE_API HRESULT SceneOrbitCode(WindowScene* s,const BYTE* code,const int* sizes,const float* table){
    if(!s||!code||!sizes||!table)return E_INVALIDARG;
    auto program=std::make_shared<WindowScene::OrbitProgram>();int total=0;
    for(int i=0;i<5;i++){if(sizes[i]<=0 || sizes[i]>1048576)return E_INVALIDARG;program->sizes[i]=sizes[i];total+=sizes[i];}
    program->bytes.assign(code,code+total);std::copy(table,table+404,program->samples);
    AcquireSRWLockExclusive(&s->guard);s->orbitProgram=program;ReleaseSRWLockExclusive(&s->guard);return S_OK;
}
SCENE_API HRESULT SceneRibbon(WindowScene* s,int id,int w,int h,const RibbonVertex* points,int count,
    const UINT* colors,const float* widths,const float* view,const float* spins,int stars){
    if(!s||id<=0||w<1||h<1||w>8192||h>4096||!colors||!widths||!view||count<0||count>240000||stars<0||stars>128||
       (stars && !spins)||(!stars && (!points || !count)))return E_INVALIDARG;
    auto data=std::make_shared<SceneRibbonFrame>();
    if(count)data->vertices.assign(points,points+count);
    if(stars)data->spins.assign(spins,spins+128*4);
    data->stars=stars;std::copy(view,view+(stars?16:8),data->view);
    std::copy(colors,colors+30,data->colors);std::copy(widths,widths+10,data->widths);
    auto tile=std::make_shared<SceneTile>();tile->width=w;tile->height=h*2;tile->ribbon=data;
    AcquireSRWLockExclusive(&s->guard);s->resources[id]=tile;ReleaseSRWLockExclusive(&s->guard);return S_OK;
}

SCENE_API HRESULT SceneVectorTile(WindowScene* s,int id,int w,int h,const SceneShapeInfo* info,const SceneSegment* segments,int n,const SceneStop* stops,int colors){
    if(!s||id<=0||!info||!segments||n<1||n>130000||!stops||colors<1||colors>96||
       info->fill.count+info->edge.count!=colors||w<=0||h<=0||!info->w||!info->h)return E_INVALIDARG;
    auto v=std::make_shared<SceneVector>();v->info=*info;v->segments.assign(segments,segments+n);v->stops.assign(stops,stops+colors);
    auto tile=std::make_shared<SceneTile>();tile->shape=v;tile->width=w;tile->height=h;
    AcquireSRWLockExclusive(&s->guard);s->resources[id]=tile;ReleaseSRWLockExclusive(&s->guard);return S_OK;
}

SCENE_API HRESULT SceneProgram(WindowScene* s,int kind,const void* vs,unsigned vl,const void* ps,unsigned pl){
    if(!s||!vs||!ps)return E_INVALIDARG;return s->SetProgram(kind,vs,vl,ps,pl);
}
SCENE_API HRESULT SceneStream(WindowScene* s,int id,int kind,int w,int h,const float* camera,const float* words,int length,const UINT* palette,int colors){
    if(!s||id<=0||kind<0||kind>2||!camera||length<0||length>600000||colors<0||colors>16000||(!words && length)||
       (colors && !palette)|| w<1||h<1||w>8192||h>4096||length%(kind?12:36))return E_INVALIDARG;
    auto tile=std::make_shared<SceneTile>();tile->width=w;tile->height=h*2;
    auto c=std::make_shared<SceneCloud>();std::copy(camera,camera+4,c->camera);c->kind=kind;c->count=length/(kind?12:36);
    if(length)c->words.assign(words,words+length);
    TRY(s->Palette(kind,palette,colors,c->palette));
    tile->cloud=c;AcquireSRWLockExclusive(&s->guard);s->resources[id]=tile;ReleaseSRWLockExclusive(&s->guard);return S_OK;
}
