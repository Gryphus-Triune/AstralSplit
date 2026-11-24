// A Direct2D hardware pass on the same D3D11/D3D9Ex double-buffered surfaces as the particles.
struct RibbonVertex { float x,y; int slot; };

extern "C" __declspec(dllexport) HRESULT ParticleOpenRibbon(HWND owner,Batch* sibling,Batch** result) {
    *result=nullptr;auto b=new(std::nothrow) Batch();if(!b)return E_OUTOFMEMORY;
    HRESULT hr=initDevice(*b,owner,sibling);
    if(SUCCEEDED(hr)){
        D3D11_QUERY_DESC q={D3D11_QUERY_EVENT,0};hr=b->device->CreateQuery(&q,&b->fence.p);
    }
    if(FAILED(hr)){delete b;return hr;}*result=b;return S_OK;
}
static D2D1_COLOR_F ribbonColor(unsigned c) {
    return {((c>>16)&255)/255.f,((c>>8)&255)/255.f,(c&255)/255.f,(c>>24)/255.f};
}
static HRESULT prepareRibbons(Batch& b,const unsigned* colors) {
    auto& r=b.ribbons;
    if(!r.factory.p)TRY(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory1),nullptr,(void**)&r.factory.p));
    if(!r.device.p){
        Ref<IDXGIDevice> dxgi;TRY(b.device->QueryInterface(__uuidof(IDXGIDevice),(void**)&dxgi.p));
        TRY(r.factory->CreateDevice(dxgi.p,&r.device.p));
    }
    if(!r.context.p)TRY(r.device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&r.context.p));
    if(!r.pen.p){
        D2D1_STROKE_STYLE_PROPERTIES prop={D2D1_CAP_STYLE_ROUND,D2D1_CAP_STYLE_ROUND,D2D1_CAP_STYLE_ROUND,D2D1_LINE_JOIN_ROUND,10,D2D1_DASH_STYLE_SOLID,0};
        TRY(static_cast<ID2D1Factory*>(r.factory.p)->CreateStrokeStyle(&prop,nullptr,0,&r.pen.p));
    }
    auto target=static_cast<ID2D1RenderTarget*>(r.context.p);
    for(unsigned i=0;i<10;i++)if(!r.ink[i].p){
        D2D1_GRADIENT_STOP stops[3]={{0,ribbonColor(colors[3*i])},{.5f,ribbonColor(colors[3*i+1])},{1,ribbonColor(colors[3*i+2])}};
        Ref<ID2D1GradientStopCollection> collection;
        TRY(target->CreateGradientStopCollection(stops,3,D2D1_GAMMA_2_2,D2D1_EXTEND_MODE_CLAMP,&collection.p));
        D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES axis={{0,0},{1,0}};
        TRY(target->CreateLinearGradientBrush(&axis,nullptr,collection.p,&r.ink[i].p));
    }
    return S_OK;
}
static HRESULT drawRibbonData(Batch* b,const RibbonVertex* points,unsigned count,
        const unsigned* colors,const float* widths,float x,float y,float sx,float sy,float bx,float by,float rx,float ry,bool notify){
    if(!b || !points || !colors || !widths || !b->width)return E_INVALIDARG;
    if(b->pending>=0)return S_FALSE;
    TRY(prepareRibbons(*b,colors));
    int next=b->selected>=0?b->selected:(b->shown==0?1:0);b->selected=-1;auto& dst=b->frames[next];auto& ribbon=b->ribbons;
    if(!dst.ribbonTarget.p){
        Ref<IDXGISurface> surface;TRY(dst.color->QueryInterface(__uuidof(IDXGISurface),(void**)&surface.p));
        D2D1_BITMAP_PROPERTIES1 props={};
        props.pixelFormat={DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED};
        props.dpiX=props.dpiY=96.f;
        props.bitmapOptions=D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
        TRY(ribbon.context->CreateBitmapFromDxgiSurface(surface.p,&props,&dst.ribbonTarget.p));
    }
    Ref<ID2D1PathGeometry> geometry[20];Ref<ID2D1GeometrySink> sinks[20];bool begun[20]={};
    for(unsigned i=0;i<count;i++){
        const auto& p=points[i];int slot=p.slot<0?~p.slot:p.slot;
        if(slot<0 || slot>=20)return E_INVALIDARG;
        if(p.slot<0){
            if(!geometry[slot].p){TRY(ribbon.factory->CreatePathGeometry(&geometry[slot].p));TRY(geometry[slot]->Open(&sinks[slot].p));}
            if(begun[slot])sinks[slot]->EndFigure(D2D1_FIGURE_END_OPEN);
            sinks[slot]->BeginFigure({p.x,p.y},D2D1_FIGURE_BEGIN_HOLLOW);begun[slot]=true;
        }else{
            if(!begun[slot])return E_INVALIDARG;
            D2D1_POINT_2F run[64];unsigned length=0;
            do{
                run[length++]={points[i].x,points[i].y};
                if(i+1>=count || length==64 || points[i+1].slot!=slot)break;
                ++i;
            }while(true);
            sinks[slot]->AddLines(run,length);
        }
    }
    for(int i=0;i<20;i++)if(begun[i]){sinks[i]->EndFigure(D2D1_FIGURE_END_OPEN);TRY(sinks[i]->Close());}
    auto c=ribbon.context.p;c->SetTarget(dst.ribbonTarget.p);c->SetDpi(96,96);c->BeginDraw();
    const D2D1_COLOR_F clear={};c->Clear(&clear);
    for(int side=0;side<2;side++){
        D2D1_MATRIX_3X2_F transform={sx,0,0,sy,-x*sx,side*b->height-y*sy};c->SetTransform(&transform);
        for(int i=0;i<10;i++)if(geometry[i+10*side].p){
            auto brush=ribbon.ink[i].p;brush->SetStartPoint({bx,by});brush->SetEndPoint({rx,ry});
            c->DrawGeometry(geometry[i+10*side].p,brush,widths[i],ribbon.pen.p);
        }
    }
    HRESULT hr=c->EndDraw();c->SetTarget(nullptr);if(FAILED(hr))return hr;
    if(notify){b->commands->End(b->fence.p);b->commands->Flush();b->pending=next;}
    return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ParticleRibbons(Batch* b,const RibbonVertex* points,unsigned count,
        const unsigned* colors,const float* widths,float x,float y,float sx,float sy,float bx,float by,float rx,float ry){
    return drawRibbonData(b,points,count,colors,widths,x,y,sx,sy,bx,by,rx,ry,true);
}

// Rendering and WPF must use the same camera pose. If hardware is late, the caller can draw the vector frame.
extern "C" __declspec(dllexport) HRESULT ParticleRibbonFence(Batch* b) {
    if(!b || b->pending<0)return S_FALSE;
    LARGE_INTEGER started,frequency,now;QueryPerformanceCounter(&started);QueryPerformanceFrequency(&frequency);
    for(;;){
        BOOL ready=FALSE;HRESULT hr=b->commands->GetData(b->fence.p,&ready,sizeof(ready),D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if(FAILED(hr) || (hr==S_OK && ready))return hr;
        QueryPerformanceCounter(&now);if((now.QuadPart-started.QuadPart)*1000>=frequency.QuadPart)return S_FALSE;
        SwitchToThread();
    }
}
