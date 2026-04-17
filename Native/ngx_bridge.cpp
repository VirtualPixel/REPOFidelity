// ngx_bridge.cpp — C bridge around NVIDIA NGX for DLSS, linked against the
// official NGX SDK (nvsdk_ngx_d.lib).

#define NGX_ENABLE_DEPRECATED_SHUTDOWN
#define NGX_ENABLE_DEPRECATED_GET_PARAMETERS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>
#include <string>
#include <unordered_map>

#include "D:/downloads/streamline-sdk/external/ngx-sdk/include/nvsdk_ngx.h"
#include "D:/downloads/streamline-sdk/external/ngx-sdk/include/nvsdk_ngx_params.h"
#include "D:/downloads/streamline-sdk/external/ngx-sdk/include/nvsdk_ngx_helpers.h"

#pragma comment(lib, "D:/downloads/streamline-sdk/external/ngx-sdk/lib/Windows_x86_64/nvsdk_ngx_d.lib")

#define EXPORT extern "C" __declspec(dllexport)

typedef void(__cdecl* LogCallback)(const char* msg);
static LogCallback g_logCb = nullptr;

static void Log(const char* fmt, ...) {
    if (!g_logCb) return;
    char buf[512];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    g_logCb(buf);
}

static ID3D11Device* g_device = nullptr;
static ID3D11DeviceContext* g_context = nullptr;
static bool g_initialized = false;

// ===== private D3D12 device + shared NT-handle interop =====

static ID3D12Device*              g_d12Device     = nullptr;
static ID3D12CommandQueue*        g_d12Queue      = nullptr;
static ID3D12CommandAllocator*    g_d12Allocs[2]  = { nullptr, nullptr };
static UINT64                     g_d12AllocFence[2] = { 0, 0 };
static int                        g_d12AllocIdx   = 0;
static ID3D12GraphicsCommandList* g_d12CmdList    = nullptr;
static ID3D12Fence*               g_d12Fence      = nullptr;
static HANDLE                     g_d12FenceEvt   = nullptr;
static UINT64                     g_d12FenceVal   = 0;

static ID3D12Fence*               g_syncFenceD12  = nullptr;
static ID3D11Fence*               g_syncFenceD11  = nullptr;
static UINT64                     g_syncCounter   = 0;

static bool                       g_d12Initialized = false;

static Microsoft::WRL::ComPtr<IDXGIAdapter1> FindAdapterFor(ID3D11Device* d11) {
    using namespace Microsoft::WRL;
    ComPtr<IDXGIDevice> dxgiDev;
    if (FAILED(d11->QueryInterface(IID_PPV_ARGS(&dxgiDev)))) return nullptr;
    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(dxgiDev->GetAdapter(&adapter))) return nullptr;
    DXGI_ADAPTER_DESC desc{};
    adapter->GetDesc(&desc);

    ComPtr<IDXGIFactory4> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return nullptr;
    ComPtr<IDXGIAdapter1> byLuid;
    if (SUCCEEDED(factory->EnumAdapterByLuid(desc.AdapterLuid, IID_PPV_ARGS(&byLuid))))
        return byLuid;
    return nullptr;
}

static bool InitD3D12State() {
    if (g_d12Initialized) return true;
    if (!g_device) { Log("D3D12: need D3D11 device first"); return false; }

    if (GetEnvironmentVariableA("NGX_BRIDGE_D3D12_DEBUG", nullptr, 0) > 0) {
        Microsoft::WRL::ComPtr<ID3D12Debug> dbg;
        if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dbg)))) {
            dbg->EnableDebugLayer();
            Log("D3D12 debug layer enabled");
        }
    }

    auto adapter = FindAdapterFor(g_device);
    if (!adapter) { Log("D3D12: no matching DXGI adapter"); return false; }

    HRESULT hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_d12Device));
    if (FAILED(hr)) { Log("D3D12CreateDevice failed 0x%08X", hr); return false; }

    D3D12_COMMAND_QUEUE_DESC qd{};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    qd.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
    hr = g_d12Device->CreateCommandQueue(&qd, IID_PPV_ARGS(&g_d12Queue));
    if (FAILED(hr)) { Log("CreateCommandQueue failed 0x%08X", hr); return false; }

    for (int i = 0; i < 2; i++) {
        hr = g_d12Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_d12Allocs[i]));
        if (FAILED(hr)) { Log("CreateCommandAllocator[%d] failed 0x%08X", i, hr); return false; }
    }

    hr = g_d12Device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
        g_d12Allocs[0], nullptr, IID_PPV_ARGS(&g_d12CmdList));
    if (FAILED(hr)) { Log("CreateCommandList failed 0x%08X", hr); return false; }
    g_d12CmdList->Close();

    hr = g_d12Device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_d12Fence));
    if (FAILED(hr)) { Log("CreateFence failed 0x%08X", hr); return false; }
    g_d12FenceEvt = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    g_d12Initialized = true;
    Log("D3D12: device=%p queue=%p", (void*)g_d12Device, (void*)g_d12Queue);
    return true;
}

static void WaitD3D12() {
    const UINT64 v = ++g_d12FenceVal;
    g_d12Queue->Signal(g_d12Fence, v);
    if (g_d12Fence->GetCompletedValue() < v) {
        g_d12Fence->SetEventOnCompletion(v, g_d12FenceEvt);
        WaitForSingleObject(g_d12FenceEvt, INFINITE);
    }
}

static void WaitFenceCPU(UINT64 v) {
    if (!v || !g_d12Fence || !g_d12FenceEvt) return;
    if (g_d12Fence->GetCompletedValue() >= v) return;
    g_d12Fence->SetEventOnCompletion(v, g_d12FenceEvt);
    WaitForSingleObject(g_d12FenceEvt, INFINITE);
}

struct Shared {
    ID3D11Texture2D*  proxyOnUnity = nullptr;
    ID3D12Resource*   d12          = nullptr;
    ID3D12Resource*   d12Internal  = nullptr;  // twin: shared has SIMULTANEOUS_ACCESS, DLSS needs UAV
    IDXGIKeyedMutex*  mutexD11     = nullptr;
    UINT              width  = 0;
    UINT              height = 0;
    DXGI_FORMAT       format = DXGI_FORMAT_UNKNOWN;
};
static std::unordered_map<void*, Shared> g_sharedCache;

// Unity's GetNativeTexturePtr returns ID3D11ShaderResourceView* on D3D11.
// Resolve it to the underlying ID3D11Resource.
static Microsoft::WRL::ComPtr<ID3D11Resource> ResolveUnityResource(void* unityPtr) {
    using namespace Microsoft::WRL;
    if (!unityPtr) return nullptr;
    IUnknown* unk = (IUnknown*)unityPtr;

    ComPtr<ID3D11ShaderResourceView> srv;
    if (SUCCEEDED(unk->QueryInterface(IID_PPV_ARGS(&srv)))) {
        ComPtr<ID3D11Resource> r;
        srv->GetResource(&r);
        return r;
    }
    ComPtr<ID3D11Resource> direct;
    if (SUCCEEDED(unk->QueryInterface(IID_PPV_ARGS(&direct)))) return direct;
    return nullptr;
}

static Shared* GetOrCreateShared(void* unityPtr) {
    if (!unityPtr || !g_device || !g_d12Device) return nullptr;
    auto it = g_sharedCache.find(unityPtr);
    if (it != g_sharedCache.end()) return &it->second;

    auto gameRes = ResolveUnityResource(unityPtr);
    if (!gameRes) { Log("shared: could not resolve unity ptr %p to ID3D11Resource", unityPtr); return nullptr; }

    Microsoft::WRL::ComPtr<ID3D11Texture2D> gameTex;
    if (FAILED(gameRes.As(&gameTex))) { Log("shared: unity res is not a Texture2D"); return nullptr; }
    D3D11_TEXTURE2D_DESC desc{};
    gameTex->GetDesc(&desc);

    D3D11_TEXTURE2D_DESC pd = desc;
    pd.MipLevels = 1;
    pd.ArraySize = 1;
    pd.SampleDesc.Count = 1;
    pd.SampleDesc.Quality = 0;
    pd.Usage = D3D11_USAGE_DEFAULT;
    pd.CPUAccessFlags = 0;
    // NTHANDLE requires KEYEDMUTEX to also be set — driver rejects otherwise.
    pd.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

    // Prefer a typed format for the proxy — DLSS can't pick a view format from
    // a TYPELESS resource and silently writes zeros. CopyResource still works
    // because typed and typeless share the same parent format.
    const DXGI_FORMAT originalFmt = desc.Format;
    DXGI_FORMAT typedFmt = originalFmt;
    switch (originalFmt) {
        case DXGI_FORMAT_R8G8B8A8_TYPELESS: typedFmt = DXGI_FORMAT_R8G8B8A8_UNORM; break;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS: typedFmt = DXGI_FORMAT_B8G8R8A8_UNORM; break;
        case DXGI_FORMAT_R16G16_TYPELESS:   typedFmt = DXGI_FORMAT_R16G16_FLOAT;   break;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS: typedFmt = DXGI_FORMAT_R16G16B16A16_FLOAT; break;
        // R32_TYPELESS stays — R32_FLOAT is rejected by this driver for shared-NT.
        default: break;
    }

    ID3D11Texture2D* proxy = nullptr;
    HRESULT hr = E_FAIL;
    struct Attempt { DXGI_FORMAT fmt; UINT bind; };
    Attempt attempts[] = {
        { typedFmt,    D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS },
        { typedFmt,    D3D11_BIND_SHADER_RESOURCE },
        { originalFmt, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS },
        { originalFmt, D3D11_BIND_SHADER_RESOURCE },
    };
    for (auto& a : attempts) {
        pd.Format = a.fmt;
        pd.BindFlags = a.bind;
        hr = g_device->CreateTexture2D(&pd, nullptr, &proxy);
        if (SUCCEEDED(hr)) break;
    }
    if (FAILED(hr)) {
        Log("shared proxy CreateTexture2D failed 0x%08X (orig fmt=%d %ux%u)",
            hr, originalFmt, pd.Width, pd.Height);
        return nullptr;
    }

    Microsoft::WRL::ComPtr<IDXGIResource1> dxgiRes;
    HANDLE ntHandle = nullptr;
    if (FAILED(proxy->QueryInterface(IID_PPV_ARGS(&dxgiRes))) ||
        FAILED(dxgiRes->CreateSharedHandle(nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &ntHandle))) {
        Log("shared CreateSharedHandle failed");
        proxy->Release();
        return nullptr;
    }

    ID3D12Resource* d12res = nullptr;
    hr = g_d12Device->OpenSharedHandle(ntHandle, IID_PPV_ARGS(&d12res));
    CloseHandle(ntHandle);
    if (FAILED(hr)) {
        Log("shared d12 OpenSharedHandle failed 0x%08X", hr);
        proxy->Release();
        return nullptr;
    }

    ID3D12Resource* d12Internal = nullptr;
    {
        D3D12_HEAP_PROPERTIES hp{};
        hp.Type = D3D12_HEAP_TYPE_DEFAULT;

        D3D12_RESOURCE_DESC rd{};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        rd.Width  = pd.Width;
        rd.Height = pd.Height;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.Format = pd.Format;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        rd.Flags  = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

        HRESULT h2 = g_d12Device->CreateCommittedResource(
            &hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr,
            IID_PPV_ARGS(&d12Internal));
        if (FAILED(h2)) {
            Log("shared d12Internal CreateCommittedResource failed 0x%08X (fmt=%d %ux%u)",
                h2, pd.Format, pd.Width, pd.Height);
            d12res->Release(); proxy->Release();
            return nullptr;
        }
    }

    IDXGIKeyedMutex* mD11 = nullptr;
    proxy->QueryInterface(IID_PPV_ARGS(&mD11));

    Shared s;
    s.proxyOnUnity = proxy;
    s.d12          = d12res;
    s.d12Internal  = d12Internal;
    s.mutexD11     = mD11;
    s.width        = pd.Width;
    s.height       = pd.Height;
    s.format       = pd.Format;
    auto ins = g_sharedCache.emplace(unityPtr, s);
    Log("shared-tex for %p: %ux%u fmt=%d", unityPtr, s.width, s.height, s.format);
    return &ins.first->second;
}

static void FreeSharedCache() {
    for (auto& kv : g_sharedCache) {
        auto& s = kv.second;
        if (s.mutexD11)     s.mutexD11->Release();
        if (s.d12Internal)  s.d12Internal->Release();
        if (s.d12)          s.d12->Release();
        if (s.proxyOnUnity) s.proxyOnUnity->Release();
    }
    g_sharedCache.clear();
}

static bool InitSharedFence() {
    if (g_syncFenceD12 && g_syncFenceD11) return true;
    if (!g_d12Device || !g_device) return false;

    HRESULT hr = g_d12Device->CreateFence(
        0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g_syncFenceD12));
    if (FAILED(hr)) { Log("CreateFence(SHARED) failed 0x%08X", hr); return false; }

    HANDLE fh = nullptr;
    hr = g_d12Device->CreateSharedHandle(g_syncFenceD12, nullptr, GENERIC_ALL, nullptr, &fh);
    if (FAILED(hr)) { Log("fence CreateSharedHandle failed 0x%08X", hr); return false; }

    Microsoft::WRL::ComPtr<ID3D11Device5> d5;
    hr = g_device->QueryInterface(IID_PPV_ARGS(&d5));
    if (FAILED(hr)) { Log("game device has no ID3D11Device5 0x%08X", hr); CloseHandle(fh); return false; }

    hr = d5->OpenSharedFence(fh, IID_PPV_ARGS(&g_syncFenceD11));
    CloseHandle(fh);
    if (FAILED(hr)) { Log("OpenSharedFence failed 0x%08X", hr); return false; }

    Log("shared fence: d12=%p d11=%p", (void*)g_syncFenceD12, (void*)g_syncFenceD11);
    return true;
}

// ---------------------------------------------------------------------------

EXPORT void NGXBridge_SetLogCallback(LogCallback cb) {
    g_logCb = cb;
}

EXPORT int NGXBridge_Load() {
    // no-op — SDK handles DLL loading internally
    return 1;
}

EXPORT int NGXBridge_InitD3D11(ID3D11Device* device) {
    if (g_initialized) return 1;
    if (!device) return 0;

    g_device = device;
    device->GetImmediateContext(&g_context);
    if (!g_context) { Log("NGXBridge: GetImmediateContext failed"); return 0; }

    // get bridge DLL directory for feature DLL search path
    wchar_t bridgePath[MAX_PATH];
    HMODULE bridgeModule = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)&NGXBridge_Load, &bridgeModule);
    GetModuleFileNameW(bridgeModule, bridgePath, MAX_PATH);
    std::wstring bridgeDir(bridgePath);
    auto slash = bridgeDir.find_last_of(L'\\');
    if (slash != std::wstring::npos)
        bridgeDir = bridgeDir.substr(0, slash);

    Log("NGXBridge: Data path: %ls", bridgeDir.c_str());

    auto result = NVSDK_NGX_D3D11_Init_with_ProjectID(
        "REPOFidelity",
        NVSDK_NGX_ENGINE_TYPE_UNITY,
        "2022.3",
        bridgeDir.c_str(),
        device,
        nullptr,
        NVSDK_NGX_Version_API
    );
    Log("NGXBridge: Init_with_ProjectID result: 0x%08X", result);

    if (!NVSDK_NGX_SUCCEED(result)) {
        result = NVSDK_NGX_D3D11_Init(0x12345678, bridgeDir.c_str(), device, nullptr, NVSDK_NGX_Version_API);
        Log("NGXBridge: Init fallback result: 0x%08X", result);
    }

    g_initialized = NVSDK_NGX_SUCCEED(result);
    return g_initialized ? 1 : 0;
}

// Called by NGX when it wants to log something — forward to our log.
static void NVSDK_CONV NgxLogCallback(const char* msg, NVSDK_NGX_Logging_Level /*level*/, NVSDK_NGX_Feature /*feature*/) {
    if (msg) Log("[NGX-internal] %s", msg);
}

EXPORT int NGXBridge_InitD3D12() {
    if (!InitD3D12State()) return 0;
    if (!InitSharedFence()) return 0;

    wchar_t bridgePath[MAX_PATH];
    HMODULE bridgeModule = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)&NGXBridge_Load, &bridgeModule);
    GetModuleFileNameW(bridgeModule, bridgePath, MAX_PATH);
    std::wstring dir(bridgePath);
    auto slash = dir.find_last_of(L'\\');
    if (slash != std::wstring::npos) dir = dir.substr(0, slash);

    NVSDK_NGX_FeatureCommonInfo commonInfo{};
    commonInfo.LoggingInfo.LoggingCallback = &NgxLogCallback;
    // Verbose logging firehoses every frame's telemetry — only opt in when diagnosing.
    commonInfo.LoggingInfo.MinimumLoggingLevel =
        (GetEnvironmentVariableA("NGX_BRIDGE_VERBOSE", nullptr, 0) > 0)
        ? NVSDK_NGX_LOGGING_LEVEL_VERBOSE
        : NVSDK_NGX_LOGGING_LEVEL_OFF;
    commonInfo.LoggingInfo.DisableOtherLoggingSinks = false;

    auto r = NVSDK_NGX_D3D12_Init_with_ProjectID(
        "REPOFidelity",
        NVSDK_NGX_ENGINE_TYPE_UNITY,
        "2022.3",
        dir.c_str(),
        g_d12Device,
        &commonInfo,
        NVSDK_NGX_Version_API);
    Log("NGX D3D12 Init_with_ProjectID: 0x%08X", r);
    return NVSDK_NGX_SUCCEED(r) ? 1 : 0;
}

EXPORT void NGXBridge_ParamSetInt(NVSDK_NGX_Parameter* p, const char* name, int val) {
    if (p) NVSDK_NGX_Parameter_SetI(p, name, val);
}

EXPORT void NGXBridge_ParamSetUInt(NVSDK_NGX_Parameter* p, const char* name, unsigned int val) {
    if (p) NVSDK_NGX_Parameter_SetUI(p, name, val);
}

EXPORT void NGXBridge_ParamSetFloat(NVSDK_NGX_Parameter* p, const char* name, float val) {
    if (p) NVSDK_NGX_Parameter_SetF(p, name, val);
}

EXPORT int NGXBridge_IsDLSSAvailable_D3D12() {
    if (!g_d12Initialized) return 0;
    NVSDK_NGX_Parameter* p = nullptr;
    auto r = NVSDK_NGX_D3D12_GetCapabilityParameters(&p);
    Log("D3D12 GetCapabilityParameters: 0x%08X", r);
    if (!NVSDK_NGX_SUCCEED(r) || !p) return 0;

    unsigned int available = 0;
    int supported = 0;
    int featureInit = -1;
    p->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    p->Get("SuperSampling.Supported", &supported);
    p->Get("SuperSampling.FeatureInitResult", &featureInit);
    Log("D3D12 SuperSampling: Available=%u Supported=%d FeatureInit=%d",
        available, supported, featureInit);
    return (available > 0 || supported > 0) ? 1 : 0;
}

EXPORT void* NGXBridge_AllocParams_D3D12() {
    NVSDK_NGX_Parameter* p = nullptr;
    auto r = NVSDK_NGX_D3D12_AllocateParameters(&p);
    return NVSDK_NGX_SUCCEED(r) ? p : nullptr;
}

EXPORT void NGXBridge_DestroyParams_D3D12(NVSDK_NGX_Parameter* p) {
    if (p) NVSDK_NGX_D3D12_DestroyParameters(p);
}

EXPORT void NGXBridge_ClearSharedCache() { FreeSharedCache(); }

EXPORT void* NGXBridge_CreateDLSS_D3D12(NVSDK_NGX_Parameter* params) {
    if (!g_d12Initialized || !g_d12CmdList || !g_d12Allocs[0]) return nullptr;

    g_d12Allocs[g_d12AllocIdx]->Reset();
    g_d12CmdList->Reset(g_d12Allocs[g_d12AllocIdx], nullptr);

    NVSDK_NGX_Handle* handle = nullptr;
    auto r = NVSDK_NGX_D3D12_CreateFeature(g_d12CmdList, NVSDK_NGX_Feature_SuperSampling, params, &handle);
    Log("D3D12 CreateFeature: 0x%08X handle=%p", r, (void*)handle);

    g_d12CmdList->Close();
    ID3D12CommandList* lists[] = { g_d12CmdList };
    g_d12Queue->ExecuteCommandLists(1, lists);
    WaitD3D12();

    return NVSDK_NGX_SUCCEED(r) ? handle : nullptr;
}

EXPORT void NGXBridge_ReleaseDLSS_D3D12(NVSDK_NGX_Handle* h) {
    if (h) NVSDK_NGX_D3D12_ReleaseFeature(h);
}

static void TransitionBarrier(ID3D12GraphicsCommandList* cl, ID3D12Resource* res,
                              D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    if (!res || before == after) return;
    D3D12_RESOURCE_BARRIER b{};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
    b.Transition.pResource = res;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = before;
    b.Transition.StateAfter  = after;
    cl->ResourceBarrier(1, &b);
}

EXPORT int NGXBridge_EvalDLSS_D3D12(
    NVSDK_NGX_Handle*    handle,
    NVSDK_NGX_Parameter* params,
    void*                colorPtr,
    void*                depthPtr,
    void*                motionPtr,
    void*                outputPtr)
{
    if (!g_d12Initialized || !handle || !params) return 0;
    if (!g_context) return 0;

    Shared* sColor  = GetOrCreateShared(colorPtr);
    Shared* sDepth  = depthPtr  ? GetOrCreateShared(depthPtr)  : nullptr;
    Shared* sMotion = motionPtr ? GetOrCreateShared(motionPtr) : nullptr;
    Shared* sOutput = GetOrCreateShared(outputPtr);
    if (!sColor || !sOutput) { Log("shared cache lookup failed"); return 0; }

    auto colorRes  = ResolveUnityResource(colorPtr);
    auto depthRes  = ResolveUnityResource(depthPtr);
    auto motionRes = ResolveUnityResource(motionPtr);
    auto outputRes = ResolveUnityResource(outputPtr);

    // D3D11 drives the keyed mutex — acquire/release pairs flush writes across
    // the API boundary. D3D12 never touches it.
    auto acqD11 = [](Shared* s, UINT64 k) {
        if (s && s->mutexD11) s->mutexD11->AcquireSync(k, INFINITE);
    };
    auto relD11 = [](Shared* s, UINT64 k) {
        if (s && s->mutexD11) s->mutexD11->ReleaseSync(k);
    };

    acqD11(sColor, 0); acqD11(sDepth, 0); acqD11(sMotion, 0); acqD11(sOutput, 0);
    if (colorRes  && sColor->proxyOnUnity)  g_context->CopyResource(sColor->proxyOnUnity,  colorRes.Get());
    if (depthRes  && sDepth  && sDepth->proxyOnUnity)  g_context->CopyResource(sDepth->proxyOnUnity,  depthRes.Get());
    if (motionRes && sMotion && sMotion->proxyOnUnity) g_context->CopyResource(sMotion->proxyOnUnity, motionRes.Get());
    relD11(sColor, 1); relD11(sDepth, 1); relD11(sMotion, 1); relD11(sOutput, 1);
    g_context->Flush();

    // Wait on this slot's fence from 2 frames ago before reusing the allocator.
    WaitFenceCPU(g_d12AllocFence[g_d12AllocIdx]);
    g_d12Allocs[g_d12AllocIdx]->Reset();
    g_d12CmdList->Reset(g_d12Allocs[g_d12AllocIdx], nullptr);

    // SIMULTANEOUS_ACCESS on the shared resource blocks DLSS's internal barriers,
    // so we stage inputs through a D3D12-native twin.
    auto copyInputToInternal = [](Shared* s) {
        if (!s) return;
        TransitionBarrier(g_d12CmdList, s->d12Internal, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST);
        g_d12CmdList->CopyResource(s->d12Internal, s->d12);
        TransitionBarrier(g_d12CmdList, s->d12Internal, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    };
    copyInputToInternal(sColor);
    copyInputToInternal(sDepth);
    copyInputToInternal(sMotion);

    TransitionBarrier(g_d12CmdList, sOutput->d12Internal, D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);

    NVSDK_NGX_Parameter_SetD3d12Resource(params, NVSDK_NGX_Parameter_Color,         sColor->d12Internal);
    NVSDK_NGX_Parameter_SetD3d12Resource(params, NVSDK_NGX_Parameter_Output,        sOutput->d12Internal);
    if (sDepth)  NVSDK_NGX_Parameter_SetD3d12Resource(params, NVSDK_NGX_Parameter_Depth,         sDepth->d12Internal);
    if (sMotion) NVSDK_NGX_Parameter_SetD3d12Resource(params, NVSDK_NGX_Parameter_MotionVectors, sMotion->d12Internal);

    auto r = NVSDK_NGX_D3D12_EvaluateFeature_C(g_d12CmdList, handle, params, nullptr);

    // inputs back to COMMON for next frame's shared→internal copy
    auto restoreInput = [](Shared* s) {
        if (!s) return;
        TransitionBarrier(g_d12CmdList, s->d12Internal, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COMMON);
    };
    restoreInput(sColor);
    restoreInput(sDepth);
    restoreInput(sMotion);

    TransitionBarrier(g_d12CmdList, sOutput->d12Internal, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
    g_d12CmdList->CopyResource(sOutput->d12, sOutput->d12Internal);
    TransitionBarrier(g_d12CmdList, sOutput->d12Internal, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COMMON);

    g_d12CmdList->Close();
    ID3D12CommandList* lists[] = { g_d12CmdList };
    g_d12Queue->ExecuteCommandLists(1, lists);

    const UINT64 allocFence = ++g_d12FenceVal;
    g_d12Queue->Signal(g_d12Fence, allocFence);
    g_d12AllocFence[g_d12AllocIdx] = allocFence;
    g_d12AllocIdx = (g_d12AllocIdx + 1) % 2;

    const UINT64 outFence = ++g_syncCounter;
    if (g_syncFenceD12) g_d12Queue->Signal(g_syncFenceD12, outFence);

    if (!NVSDK_NGX_SUCCEED(r)) {
        static int failCount = 0;
        if (failCount++ < 3) Log("D3D12 EvalDLSS failed 0x%08X", r);
        acqD11(sColor, 1); acqD11(sDepth, 1); acqD11(sMotion, 1); acqD11(sOutput, 1);
        relD11(sColor, 0); relD11(sDepth, 0); relD11(sMotion, 0); relD11(sOutput, 0);
        return 0;
    }

    // GPU-side ordering — D3D11 waits on the fence before reading output,
    // CPU doesn't block so Unity can keep building the next frame.
    Microsoft::WRL::ComPtr<ID3D11DeviceContext4> ctx4;
    if (g_syncFenceD11 && SUCCEEDED(g_context->QueryInterface(IID_PPV_ARGS(&ctx4))))
        ctx4->Wait(g_syncFenceD11, outFence);

    acqD11(sColor, 1); acqD11(sDepth, 1); acqD11(sMotion, 1); acqD11(sOutput, 1);
    if (outputRes && sOutput->proxyOnUnity)
        g_context->CopyResource(outputRes.Get(), sOutput->proxyOnUnity);
    relD11(sColor, 0); relD11(sDepth, 0); relD11(sMotion, 0); relD11(sOutput, 0);

    return 1;
}

EXPORT void NGXBridge_Shutdown() {
    FreeSharedCache();

    if (g_initialized) { NVSDK_NGX_D3D11_Shutdown(); g_initialized = false; }
    if (g_d12Initialized) { NVSDK_NGX_D3D12_Shutdown(); g_d12Initialized = false; }

    if (g_syncFenceD11) { g_syncFenceD11->Release(); g_syncFenceD11 = nullptr; }
    if (g_syncFenceD12) { g_syncFenceD12->Release(); g_syncFenceD12 = nullptr; }

    if (g_d12Fence && g_d12Queue) WaitD3D12();
    if (g_d12CmdList) { g_d12CmdList->Release(); g_d12CmdList = nullptr; }
    for (int i = 0; i < 2; i++) {
        if (g_d12Allocs[i]) { g_d12Allocs[i]->Release(); g_d12Allocs[i] = nullptr; }
    }
    if (g_d12Queue)   { g_d12Queue->Release();   g_d12Queue   = nullptr; }
    if (g_d12Fence)   { g_d12Fence->Release();   g_d12Fence   = nullptr; }
    if (g_d12FenceEvt){ CloseHandle(g_d12FenceEvt); g_d12FenceEvt = nullptr; }
    if (g_d12Device)  { g_d12Device->Release();  g_d12Device  = nullptr; }

    if (g_context) { g_context->Release(); g_context = nullptr; }
    g_device = nullptr;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
