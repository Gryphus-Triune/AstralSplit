// A temporary, transparent target on the same adapter as the home scene.
// The scene's worker owns all immediate-context operations, including during handoff.
struct LaunchSurface {
    Ref<IDXGISwapChain2> chain;
    Ref<IDCompositionTarget> target; Ref<IDCompositionVisual> visual;
    Ref<ID3D11RenderTargetView> output;
    Ref<ID3D11Texture2D> body; Ref<ID3D11ShaderResourceView> bodyView, grains;
    Ref<ID3D11VertexShader> vertex; Ref<ID3D11PixelShader> pixel; Ref<ID3D11Buffer> uniforms;
    float values[12]={};
    int count=0, duration=0;
    bool meteor=false, outgoing=false;
    float progress=0;
    volatile LONG direction=0, presented=-1;
    LONGLONG previous=0;
    HANDLE latency=nullptr;
    LONGLONG start=0;
    volatile LONG finished=0,cancelled=0;
    ~LaunchSurface(){if(latency)CloseHandle(latency);}

    HRESULT Init(ID3D11Device* gpu,IDCompositionDevice* comp,HWND window,const float* bounds,
                 const float* seeds,int n,int ms,const void* vs,int vl,const void* ps,int pl){
        std::memcpy(values,bounds,7*sizeof(float));values[9]=bounds[7];
        values[10]=std::min(210.f*values[6],.17f*std::min(values[0],values[1]));
        count=n;duration=ms;
        Ref<IDXGIDevice> dx;Ref<IDXGIAdapter> adapter;Ref<IDXGIFactory2> factory;
        TRY(gpu->QueryInterface(__uuidof(IDXGIDevice),(void**)&dx.p));TRY(dx->GetAdapter(&adapter.p));
        TRY(adapter->GetParent(__uuidof(IDXGIFactory2),(void**)&factory.p));
        DXGI_SWAP_CHAIN_DESC1 d={};d.Width=UINT(values[0]);d.Height=UINT(values[1]);
        d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.SampleDesc.Count=1;d.BufferCount=2;
        d.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;d.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        d.Scaling=DXGI_SCALING_STRETCH;d.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;d.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        Ref<IDXGISwapChain1> first;TRY(factory->CreateSwapChainForComposition(gpu,&d,nullptr,&first.p));
        TRY(first->QueryInterface(__uuidof(IDXGISwapChain2),(void**)&chain.p));
        TRY(chain->SetMaximumFrameLatency(1));latency=chain->GetFrameLatencyWaitableObject();if(!latency)return E_FAIL;
        Ref<ID3D11Texture2D> back;TRY(chain->GetBuffer(0,__uuidof(ID3D11Texture2D),(void**)&back.p));
        TRY(gpu->CreateRenderTargetView(back.p,nullptr,&output.p));
        TRY(gpu->CreateVertexShader(vs,vl,nullptr,&vertex.p));TRY(gpu->CreatePixelShader(ps,pl,nullptr,&pixel.p));
        D3D11_BUFFER_DESC b={};b.ByteWidth=sizeof(values);b.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        TRY(gpu->CreateBuffer(&b,nullptr,&uniforms.p));
        b.ByteWidth=UINT(n)*12*sizeof(float);b.BindFlags=D3D11_BIND_SHADER_RESOURCE;b.Usage=D3D11_USAGE_IMMUTABLE;
        D3D11_SUBRESOURCE_DATA data={seeds,0,0};Ref<ID3D11Buffer> table;TRY(gpu->CreateBuffer(&b,&data,&table.p));
        D3D11_SHADER_RESOURCE_VIEW_DESC view={};view.Format=DXGI_FORMAT_R32G32B32A32_FLOAT;
        view.ViewDimension=D3D11_SRV_DIMENSION_BUFFER;view.Buffer.NumElements=3*n;
        TRY(gpu->CreateShaderResourceView(table.p,&view,&grains.p));
        D3D11_TEXTURE2D_DESC image={};image.Width=UINT(values[4]);image.Height=UINT(values[5]);
        image.Format=d.Format;image.SampleDesc.Count=image.ArraySize=image.MipLevels=1;image.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        TRY(gpu->CreateTexture2D(&image,nullptr,&body.p));TRY(gpu->CreateShaderResourceView(body.p,nullptr,&bodyView.p));
        TRY(comp->CreateTargetForHwnd(window,TRUE,&target.p));TRY(comp->CreateVisual(&visual.p));
        TRY(visual->SetContent(chain.p));TRY(target->SetRoot(visual.p));return comp->Commit();
    }
    void Draw(ID3D11DeviceContext* dc,ID3D11BlendState* blend,ID3D11RasterizerState* raster,ID3D11SamplerState* sampler,float t){
        values[7]=t;float zero[4]={};dc->OMSetRenderTargets(1,&output.p,nullptr);dc->ClearRenderTargetView(output.p,zero);
        dc->OMSetBlendState(blend,nullptr,0xffffffff);dc->RSSetState(raster);
        D3D11_VIEWPORT vp={0,0,values[0],values[1],0,1};D3D11_RECT clip={0,0,LONG(values[0]),LONG(values[1])};
        dc->RSSetViewports(1,&vp);dc->RSSetScissorRects(1,&clip);
        dc->IASetInputLayout(nullptr);dc->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        dc->VSSetShader(vertex.p,nullptr,0);dc->PSSetShader(pixel.p,nullptr,0);
        dc->VSSetShaderResources(0,1,&grains.p);dc->PSSetShaderResources(1,1,&bodyView.p);dc->PSSetSamplers(0,1,&sampler);
        dc->VSSetConstantBuffers(0,1,&uniforms.p);dc->PSSetConstantBuffers(0,1,&uniforms.p);
        auto draw=[&](int pass,int instances){
            values[8]=float(pass);dc->UpdateSubresource(uniforms.p,0,nullptr,values,0,0);dc->DrawInstanced(4,instances,0,0);
        };
        if(meteor){
            draw(0,1); // live body
            draw(1,24);draw(2,2); // trail segments and head
            if(!outgoing){draw(3,count*2);draw(4,2);draw(5,1);}
        }else{
        if(t>.555f)draw(0,1);
        if(t<.555f)draw(1,count*4);
        if(t>.105f && t<.884f)draw(2,1);
        if(values[9]!=0)draw(3,1);
        }
        ID3D11ShaderResourceView* none=nullptr;dc->VSSetShaderResources(0,1,&none);dc->PSSetShaderResources(1,1,&none);
        dc->OMSetRenderTargets(0,nullptr,nullptr);
    }
};
