// ngx_bridge.cpp — C bridge to NVIDIA NGX for DLSS.
// Links against the official NGX SDK (nvsdk_ngx_d.lib) instead of
// manual vtable walking. Uses proper SDK functions for all param access.

#define NGX_ENABLE_DEPRECATED_SHUTDOWN
#define NGX_ENABLE_DEPRECATED_GET_PARAMETERS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <cstdint>
#include <cstdio>
#include <string>

#include "D:/downloads/streamline-sdk/external/ngx-sdk/include/nvsdk_ngx.h"
#include "D:/downloads/streamline-sdk/external/ngx-sdk/include/nvsdk_ngx_params.h"
#include "D:/downloads/streamline-sdk/external/ngx-sdk/include/nvsdk_ngx_helpers.h"

#pragma comment(lib, "D:/downloads/streamline-sdk/external/ngx-sdk/lib/Windows_x86_64/nvsdk_ngx_d.lib")

#define EXPORT extern "C" __declspec(dllexport)

// Log callback
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

    // use Init_with_ProjectID — engine type UNITY may enable D3D11 DLSS
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
        // fallback to basic init
        result = NVSDK_NGX_D3D11_Init(0x12345678, bridgeDir.c_str(), device, nullptr, NVSDK_NGX_Version_API);
        Log("NGXBridge: Init fallback result: 0x%08X", result);
    }

    g_initialized = NVSDK_NGX_SUCCEED(result);
    return g_initialized ? 1 : 0;
}

EXPORT int NGXBridge_IsDLSSAvailable() {
    if (!g_initialized) return 0;

    NVSDK_NGX_Parameter* params = nullptr;
    auto result = NVSDK_NGX_D3D11_GetCapabilityParameters(&params);
    Log("NGXBridge: GetCapabilityParameters result: 0x%08X", result);

    if (NVSDK_NGX_SUCCEED(result) && params) {
        unsigned int available = 0;
        int supported = 0;
        params->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
        params->Get("SuperSampling.Supported", &supported);

        int featureInit = -1;
        params->Get("SuperSampling.FeatureInitResult", &featureInit);

        Log("NGXBridge: SuperSampling.Available=%u Supported=%d FeatureInit=%d",
            available, supported, featureInit);
        if (available > 0 || supported > 0) return 1;
    }

    return 0;
}

EXPORT void* NGXBridge_AllocParams() {
    NVSDK_NGX_Parameter* p = nullptr;
    auto r = NVSDK_NGX_D3D11_AllocateParameters(&p);
    if (NVSDK_NGX_SUCCEED(r) && p) return p;
    return nullptr;
}

EXPORT void NGXBridge_DestroyParams(NVSDK_NGX_Parameter* p) {
    if (p) NVSDK_NGX_D3D11_DestroyParameters(p);
}

EXPORT NVSDK_NGX_Parameter* NGXBridge_GetParams() {
    NVSDK_NGX_Parameter* p = nullptr;
    auto r = NVSDK_NGX_D3D11_GetParameters(&p);
    if (NVSDK_NGX_SUCCEED(r) && p) return p;
    return nullptr;
}

// Use the SDK's proper typed setter functions instead of vtable hacking
EXPORT void NGXBridge_ParamSetInt(NVSDK_NGX_Parameter* p, const char* name, int val) {
    if (p) NVSDK_NGX_Parameter_SetI(p, name, val);
}

EXPORT void NGXBridge_ParamSetUInt(NVSDK_NGX_Parameter* p, const char* name, unsigned int val) {
    if (p) NVSDK_NGX_Parameter_SetUI(p, name, val);
}

EXPORT void NGXBridge_ParamSetFloat(NVSDK_NGX_Parameter* p, const char* name, float val) {
    if (p) NVSDK_NGX_Parameter_SetF(p, name, val);
}

EXPORT void NGXBridge_ParamSetResource(NVSDK_NGX_Parameter* p, const char* name, ID3D11Resource* res) {
    if (p) NVSDK_NGX_Parameter_SetD3d11Resource(p, name, res);
}

EXPORT void NGXBridge_ParamSetVoidPtr(NVSDK_NGX_Parameter* p, const char* name, void* ptr) {
    if (p) NVSDK_NGX_Parameter_SetVoidPointer(p, name, ptr);
}

EXPORT void* NGXBridge_CreateDLSS(NVSDK_NGX_Parameter* params) {
    if (!g_context) return nullptr;
    NVSDK_NGX_Handle* handle = nullptr;
    auto result = NVSDK_NGX_D3D11_CreateFeature(g_context, NVSDK_NGX_Feature_SuperSampling, params, &handle);
    Log("NGXBridge: CreateFeature result: 0x%08X (handle=%p)", result, handle);
    return NVSDK_NGX_SUCCEED(result) ? handle : nullptr;
}

EXPORT int NGXBridge_EvalDLSS(NVSDK_NGX_Handle* handle, NVSDK_NGX_Parameter* params) {
    if (!g_context || !handle) return 0;

    auto result = NVSDK_NGX_D3D11_EvaluateFeature(g_context, handle, params, nullptr);
    static int logCount = 0;
    if (!NVSDK_NGX_SUCCEED(result) && logCount++ < 3)
        Log("NGXBridge: EvalDLSS failed 0x%08X", result);
    if (NVSDK_NGX_SUCCEED(result) && logCount < 100) {
        logCount = 100;
        Log("NGXBridge: EvalDLSS SUCCEEDED");
    }
    return NVSDK_NGX_SUCCEED(result) ? 1 : 0;
}

EXPORT void NGXBridge_ReleaseDLSS(NVSDK_NGX_Handle* handle) {
    if (handle) NVSDK_NGX_D3D11_ReleaseFeature(handle);
}

EXPORT ID3D11Resource* NGXBridge_ExtractResource(void* unityPtr) {
    if (!unityPtr) return nullptr;
    IUnknown* unk = static_cast<IUnknown*>(unityPtr);
    ID3D11Resource* resource = nullptr;
    if (SUCCEEDED(unk->QueryInterface(__uuidof(ID3D11Resource), (void**)&resource))) {
        resource->Release();
        return resource;
    }
    return static_cast<ID3D11Resource*>(unityPtr);
}

EXPORT void NGXBridge_Shutdown() {
    if (g_initialized) {
        NVSDK_NGX_D3D11_Shutdown();
        g_initialized = false;
    }
    if (g_context) { g_context->Release(); g_context = nullptr; }
    g_device = nullptr;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
