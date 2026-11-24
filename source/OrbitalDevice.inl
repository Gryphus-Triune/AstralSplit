struct OrbitView {float matrix[4],depth[4],mapping[4],physical[4],gradient[4],colors[30][4],widths[12];};
static HRESULT orbitBuffer(ID3D11Device* dev,UINT bytes,const void* data,ID3D11Buffer** out){
    D3D11_BUFFER_DESC header={};header.ByteWidth=bytes;header.Usage=data?D3D11_USAGE_IMMUTABLE:D3D11_USAGE_DEFAULT;
    header.BindFlags=D3D11_BIND_CONSTANT_BUFFER;D3D11_SUBRESOURCE_DATA init={data,0,0};
    return dev->CreateBuffer(&header,data?&init:nullptr,out);
}
extern "C" __declspec(dllexport) HRESULT ParticleOrbitProgram(Batch* b,const unsigned char* code,const int* sizes,const float* table){
    if(!b || !table || !code || !sizes)return E_INVALIDARG;auto& p=b->orbits;
    for(int i=0;i<5;i++)if(sizes[i]<=0 || sizes[i]>1048576)return E_INVALIDARG;
    if(p.angles.p)return S_OK;
    TRY(b->device->CreateVertexShader(code,sizes[0],nullptr,&p.vertex.p));code+=sizes[0];
    TRY(b->device->CreateGeometryShader(code,sizes[1],nullptr,&p.slices.p));code+=sizes[1];
    TRY(b->device->CreatePixelShader(code,sizes[2],nullptr,&p.coverage.p));code+=sizes[2];
    TRY(b->device->CreateVertexShader(code,sizes[3],nullptr,&p.quad.p));code+=sizes[3];
    TRY(b->device->CreatePixelShader(code,sizes[4],nullptr,&p.ink.p));
    D3D11_RASTERIZER_DESC raster={};raster.FillMode=D3D11_FILL_SOLID;raster.CullMode=D3D11_CULL_NONE;raster.DepthClipEnable=TRUE;
    TRY(b->device->CreateRasterizerState(&raster,&p.raster.p));
    D3D11_BLEND_DESC bp={};auto& rt=bp.RenderTarget[0];rt.BlendEnable=TRUE;
    rt.SrcBlend=rt.SrcBlendAlpha=rt.DestBlend=rt.DestBlendAlpha=D3D11_BLEND_ONE;
    rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_MAX;rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
    TRY(b->device->CreateBlendState(&bp,&p.maximum.p));
    TRY(orbitBuffer(b->device.p,sizeof(OrbitView),nullptr,&p.view.p));
    TRY(orbitBuffer(b->device.p,101*16,table,&p.orbit.p));
    return orbitBuffer(b->device.p,128*16,nullptr,&p.angles.p);
}
static HRESULT drawOrbitData(Batch* b,const float* rotations,UINT stars,const float* view,const uint32_t* colors,const float* widths,bool notify){
    if(!b || !rotations || !view || !colors || !widths || !stars || stars>128 || !b->orbits.angles.p)return E_INVALIDARG;
    if(b->pending>=0)return S_FALSE;
    auto& p=b->orbits;int next=b->selected>=0?b->selected:(b->shown==0?1:0);auto& dst=b->frames[next];
    if(!p.resource.p){
        D3D11_TEXTURE2D_DESC td={};td.Width=b->width;td.Height=b->height*2;td.ArraySize=10;td.MipLevels=1;td.SampleDesc.Count=1;
        td.Format=DXGI_FORMAT_R8_UNORM;td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        TRY(b->device->CreateTexture2D(&td,nullptr,&p.texture.p));TRY(b->device->CreateRenderTargetView(p.texture.p,nullptr,&p.target.p));
        TRY(b->device->CreateShaderResourceView(p.texture.p,nullptr,&p.resource.p));
    }
    OrbitView constants={};std::memcpy(constants.matrix,view,4*sizeof(float));std::memcpy(constants.depth,view+4,4*sizeof(float));std::memcpy(constants.mapping,view+8,4*sizeof(float));
    constants.physical[0]=b->width;constants.physical[1]=b->height;std::memcpy(constants.gradient,view+12,4*sizeof(float));
    for(int i=0;i<30;i++){auto c=colors[i];constants.colors[i][0]=((c>>16)&255)/255.f;constants.colors[i][1]=((c>>8)&255)/255.f;constants.colors[i][2]=(c&255)/255.f;constants.colors[i][3]=(c>>24)/255.f;}
    std::memcpy(constants.widths,widths,10*sizeof(float));
    auto c=b->commands.p;c->UpdateSubresource(p.view.p,0,nullptr,&constants,0,0);c->UpdateSubresource(p.angles.p,0,nullptr,rotations,0,0);
    ID3D11Buffer* tables[]={p.view.p,p.orbit.p,p.angles.p};c->VSSetConstantBuffers(0,3,tables);c->PSSetConstantBuffers(0,1,tables);
    D3D11_VIEWPORT vp={0,0,float(b->width),float(2*b->height),0,1};c->RSSetViewports(1,&vp);c->RSSetState(p.raster.p);
    c->IASetInputLayout(nullptr);c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(p.vertex.p,nullptr,0);c->GSSetShader(p.slices.p,nullptr,0);c->PSSetShader(p.coverage.p,nullptr,0);
    const float clear[4]={};c->ClearRenderTargetView(p.target.p,clear);c->OMSetRenderTargets(1,&p.target.p,nullptr);
    c->OMSetBlendState(p.maximum.p,nullptr,0xffffffff);c->DrawInstanced(6,stars*200,0,0);
    c->OMSetRenderTargets(1,&dst.target.p,nullptr);c->OMSetBlendState(nullptr,nullptr,0xffffffff);
    c->GSSetShader(nullptr,nullptr,0);c->VSSetShader(p.quad.p,nullptr,0);c->PSSetShader(p.ink.p,nullptr,0);c->PSSetShaderResources(0,1,&p.resource.p);
    c->Draw(3,0);ID3D11ShaderResourceView* clearSlot=nullptr;c->PSSetShaderResources(0,1,&clearSlot);c->OMSetRenderTargets(0,nullptr,nullptr);
    b->selected=-1;
    if(notify){c->End(b->fence.p);c->Flush();b->pending=next;}
    return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ParticleOrbitDraw(Batch* b,const float* rotations,UINT stars,const float* view,const uint32_t* colors,const float* widths){
    return drawOrbitData(b,rotations,stars,view,colors,widths,true);
}
