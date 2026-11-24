// Sample once at exactly the same texel centers, with the same VS, camera, and
// front/back viewports as the displayed magnetic disk. No interpolated polar grid.
extern "C" __declspec(dllexport) HRESULT ParticleEnablePolar(Batch* b,const void* writer,unsigned nw,const void* reader,unsigned nr){
    if(!b || !writer || !reader)return E_INVALIDARG;
    TRY(b->device->CreatePixelShader(writer,nw,nullptr,&b->mapWriter.p));
    return b->device->CreatePixelShader(reader,nr,nullptr,&b->mapReader.p);
}
static HRESULT createFieldMaps(Batch& b){
    D3D11_TEXTURE2D_DESC d={};d.Width=b.width;d.Height=b.height*2;d.MipLevels=1;d.ArraySize=2;
    d.Format=DXGI_FORMAT_R32G32_FLOAT;d.SampleDesc.Count=1;d.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    TRY(b.device->CreateTexture2D(&d,nullptr,&b.mapTexture.p));
    TRY(b.device->CreateShaderResourceView(b.mapTexture.p,nullptr,&b.mapView.p));
    for(UINT i=0;i<2;i++){
        D3D11_RENDER_TARGET_VIEW_DESC target={};target.Format=d.Format;target.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
        target.Texture2DArray.ArraySize=1;target.Texture2DArray.FirstArraySlice=i;
        TRY(b.device->CreateRenderTargetView(b.mapTexture.p,&target,&b.mapTargets[i].p));
    }
    return S_OK;
}
static HRESULT prepareField(Batch& b,const float* points,const float* camera){
    if(b.mapUnavailable || points[8]!=0 || points[19]!=1 || points[30]!=2 || points[10]!=0 || points[21]!=1 || points[32]!=0)return E_FAIL;
    float signature[22];for(int i=0;i<3;i++)std::memcpy(signature+i*6,points+i*11,6*sizeof(float));
    std::memcpy(signature+18,camera,4*sizeof(float));
    if(b.mapValid && !std::memcmp(signature,b.mapKey,sizeof(signature)))return S_OK;
    if(!b.mapTexture.p){
        HRESULT hr=createFieldMaps(b);if(FAILED(hr)){b.mapUnavailable=true;return hr;}
    }
    auto c=b.commands.p;
    ID3D11ShaderResourceView* none=nullptr;c->PSSetShaderResources(0,1,&none);
    c->OMSetBlendState(nullptr,nullptr,0xffffffff);c->PSSetShader(b.mapWriter.p,nullptr,0);
    c->OMSetRenderTargets(1,&b.mapTargets[0].p,nullptr);c->DrawInstanced(4,2,0,0);
    c->OMSetRenderTargets(1,&b.mapTargets[1].p,nullptr);c->DrawInstanced(4,1,0,2);
    c->OMSetRenderTargets(0,nullptr,nullptr);
    std::memcpy(b.mapKey,signature,sizeof(signature));b.mapValid=true;return S_OK;
}
