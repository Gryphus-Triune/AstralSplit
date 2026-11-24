// Retained Direct2D curves for the native compositor. Paths stay in DIPs until
// their final transform, including the solar filaments and orbital approaches.
struct SceneSegment {int kind,flags;float x1,y1,x2,y2,x3,y3;};
struct SceneInk {int kind,count,gamma,spread;float x0,y0,x1,y1,rx,ry,alpha;};
struct SceneStop {float at;unsigned color;};
struct SceneShapeInfo {
    float x,y,w,h,stroke;
    int fillRule,firstCap,lastCap,join;
    float miter;
    SceneInk fill,edge;
};
struct SceneVector {
    SceneShapeInfo info={};std::vector<SceneSegment> segments;std::vector<SceneStop> stops;
    Ref<ID2D1PathGeometry> path;Ref<ID2D1Brush> inside,edge;Ref<ID2D1StrokeStyle> stroke;
};
struct SceneD2D {
    Ref<ID2D1Factory1> factory;Ref<ID2D1Device> device;Ref<ID2D1DeviceContext> context;
    HRESULT Init(ID3D11Device* gpu){
        if(context.p)return S_OK;
        TRY(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory1),nullptr,(void**)&factory.p));
        Ref<IDXGIDevice> dx;TRY(gpu->QueryInterface(__uuidof(IDXGIDevice),(void**)&dx.p));TRY(factory->CreateDevice(dx.p,&device.p));
        TRY(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&context.p));context->SetDpi(96,96);return S_OK;
    }
    HRESULT Canvas(ID3D11Texture2D* tex,Ref<ID2D1Bitmap1>& bitmap){
        if(bitmap.p)return S_OK;Ref<IDXGISurface> surface;TRY(tex->QueryInterface(__uuidof(IDXGISurface),(void**)&surface.p));
        D2D1_BITMAP_PROPERTIES1 d={};d.pixelFormat={DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED};
        d.dpiX=d.dpiY=96;d.bitmapOptions=D2D1_BITMAP_OPTIONS(D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
        return context->CreateBitmapFromDxgiSurface(surface.p,&d,&bitmap.p);
    }
    HRESULT Brush(const SceneInk& spec,const SceneStop* stops,Ref<ID2D1Brush>& brush){
        if(!spec.kind)return S_OK;
        auto target=static_cast<ID2D1RenderTarget*>(context.p);
        if(spec.kind==1){
            Ref<ID2D1SolidColorBrush> solid;auto c=ribbonColor(stops[0].color);
            TRY(target->CreateSolidColorBrush(c,&solid.p));brush.p=solid.p;solid.p=nullptr;return S_OK;
        }
        std::vector<D2D1_GRADIENT_STOP> data;data.reserve(spec.count);
        for(int i=0;i<spec.count;i++)data.push_back({stops[i].at,ribbonColor(stops[i].color)});
        Ref<ID2D1GradientStopCollection> colors;
        TRY(target->CreateGradientStopCollection(data.data(),(UINT)data.size(),spec.gamma?D2D1_GAMMA_1_0:D2D1_GAMMA_2_2,
            spec.spread==1?D2D1_EXTEND_MODE_MIRROR:spec.spread==2?D2D1_EXTEND_MODE_WRAP:D2D1_EXTEND_MODE_CLAMP,&colors.p));
        if(spec.kind==2){Ref<ID2D1LinearGradientBrush> ink;D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES g={{spec.x0,spec.y0},{spec.x1,spec.y1}};
            TRY(target->CreateLinearGradientBrush(g,colors.p,&ink.p));brush.p=ink.p;ink.p=nullptr;
        }else{Ref<ID2D1RadialGradientBrush> ink;D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES g={{spec.x0,spec.y0},{spec.x1-spec.x0,spec.y1-spec.y0},spec.rx,spec.ry};
            TRY(target->CreateRadialGradientBrush(g,colors.p,&ink.p));brush.p=ink.p;ink.p=nullptr;}
        return S_OK;
    }
    HRESULT Prepare(SceneVector& v){
        if(v.path.p)return S_OK;
        Ref<ID2D1PathGeometry> path;TRY(factory->CreatePathGeometry(&path.p));Ref<ID2D1GeometrySink> sink;TRY(path->Open(&sink.p));
        sink->SetFillMode(v.info.fillRule?D2D1_FILL_MODE_WINDING:D2D1_FILL_MODE_ALTERNATE);
        for(auto& s:v.segments){
            if(s.kind==0){sink->BeginFigure({s.x1,s.y1},s.flags?D2D1_FIGURE_BEGIN_FILLED:D2D1_FIGURE_BEGIN_HOLLOW);continue;}
            if(s.kind==4){sink->EndFigure(s.flags?D2D1_FIGURE_END_CLOSED:D2D1_FIGURE_END_OPEN);continue;}
            sink->SetSegmentFlags(D2D1_PATH_SEGMENT(s.flags));
            if(s.kind==1)sink->AddLine({s.x1,s.y1});
            else if(s.kind==2){D2D1_QUADRATIC_BEZIER_SEGMENT q={{s.x1,s.y1},{s.x2,s.y2}};sink->AddQuadraticBezier(q);}
            else if(s.kind==3){D2D1_BEZIER_SEGMENT b={{s.x1,s.y1},{s.x2,s.y2},{s.x3,s.y3}};sink->AddBezier(b);}
            else if(s.kind==5){D2D1_ARC_SEGMENT a={{s.x1,s.y1},{s.x2,s.y2},s.x3,
               s.y3>=2?D2D1_SWEEP_DIRECTION_CLOCKWISE:D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
               int(s.y3)%2?D2D1_ARC_SIZE_LARGE:D2D1_ARC_SIZE_SMALL};sink->AddArc(a);}
        }
        TRY(sink->Close());TRY(Brush(v.info.fill,v.stops.data(),v.inside));TRY(Brush(v.info.edge,v.stops.data()+v.info.fill.count,v.edge));
        if(v.edge.p){D2D1_STROKE_STYLE_PROPERTIES p={(D2D1_CAP_STYLE)v.info.firstCap,(D2D1_CAP_STYLE)v.info.lastCap,D2D1_CAP_STYLE_FLAT,
              (D2D1_LINE_JOIN)v.info.join,v.info.miter,D2D1_DASH_STYLE_SOLID,0};
            TRY(static_cast<ID2D1Factory*>(factory.p)->CreateStrokeStyle(&p,nullptr,0,&v.stroke.p));}
        v.path.p=path.p;path.p=nullptr;return S_OK;
    }
    void Paint(SceneVector& v,D2D1_MATRIX_3X2_F tx,float opacity){
        context->SetTransform(tx);
        if(v.inside.p){v.inside->SetOpacity(opacity*v.info.fill.alpha);context->FillGeometry(v.path.p,v.inside.p);}
        if(v.edge.p){v.edge->SetOpacity(opacity*v.info.edge.alpha);context->DrawGeometry(v.path.p,v.edge.p,v.info.stroke,v.stroke.p);}
    }
};