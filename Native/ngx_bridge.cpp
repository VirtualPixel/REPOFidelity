// ngx_bridge.cpp — Thin C bridge to NVIDIA NGX for DLSS.
// Loads _nvngx.dll on demand (NOT in DllMain) to avoid crashes.
// Exports simple C functions callable from C# via P/Invoke.
//
// Key fixes based on NVIDIA DLSS SDK research:
// - Uses Init_with_ProjectID with FeatureCommonInfo PathListInfo
// - Correct vtable layout from nvsdk_ngx_params.h
// - Correct feature flags (DepthInverted = bit 3)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <cstdint>
#include <cstdio>
#include <string>

// ---------------------------------------------------------------------------
// NGX types (from nvsdk_ngx_defs.h / nvsdk_ngx.h)
// ---------------------------------------------------------------------------

using NVSDK_NGX_Result    = uint32_t;
using NVSDK_NGX_Handle    = void;
using NVSDK_NGX_Parameter = void;

#define NGX_SUCCEED(x) (((x) & 0xFFF00000) != 0xBAD00000)
#define NGX_FAILED(x)  (((x) & 0xFFF00000) == 0xBAD00000)
constexpr uint32_t NGX_ERR_FEATURE_NOT_SUPPORTED = 0xBAD00002;
constexpr uint32_t NGX_ERR_MISSING_INPUT         = 0xBAD0000B;
constexpr int      NGX_FEATURE_SUPER_SAMPLING = 1;

// CORRECT vtable layout from nvsdk_ngx_params.h:
// class NVSDK_NGX_Parameter {
//   virtual void Set(const char*, unsigned long long) = 0;  // [0]
//   virtual void Set(const char*, float) = 0;               // [1]
//   virtual void Set(const char*, double) = 0;              // [2]
//   virtual void Set(const char*, unsigned int) = 0;        // [3]
//   virtual void Set(const char*, int) = 0;                 // [4]
//   virtual void Set(const char*, ID3D11Resource*) = 0;     // [5]
//   virtual void Set(const char*, ID3D12Resource*) = 0;     // [6]
//   virtual void Set(const char*, void*) = 0;               // [7]
//   virtual NVSDK_NGX_Result Get(const char*, unsigned long long*) = 0; // [8]
//   virtual NVSDK_NGX_Result Get(const char*, float*) = 0;              // [9]
//   virtual NVSDK_NGX_Result Get(const char*, double*) = 0;             // [10]
//   virtual NVSDK_NGX_Result Get(const char*, unsigned int*) = 0;       // [11]
//   virtual NVSDK_NGX_Result Get(const char*, int*) = 0;                // [12]
//   ...
//   virtual void Reset() = 0;                                           // [16]
// };

// Vtable indices (no virtual destructor in pure abstract class)
constexpr int VT_SET_ULL      = 0;
constexpr int VT_SET_FLOAT    = 1;
constexpr int VT_SET_DOUBLE   = 2;
constexpr int VT_SET_UINT     = 3;
constexpr int VT_SET_INT      = 4;
constexpr int VT_SET_D3D11    = 5;
constexpr int VT_SET_D3D12    = 6;
constexpr int VT_SET_VOIDPTR  = 7;
constexpr int VT_GET_ULL      = 8;
constexpr int VT_GET_FLOAT    = 9;
constexpr int VT_GET_DOUBLE   = 10;
constexpr int VT_GET_UINT     = 11;
constexpr int VT_GET_INT      = 12;

static void** GetVtable(NVSDK_NGX_Parameter* p) {
    return *reinterpret_cast<void***>(p);
}

// (FeatureCommonInfo struct removed — passing nullptr to Init works fine)

// ---------------------------------------------------------------------------
// NGX function pointer types
// ---------------------------------------------------------------------------

typedef NVSDK_NGX_Result(__cdecl* PFN_Init)(uint64_t appId, const wchar_t* path, ID3D11Device* dev, const void* featureInfo, uint32_t ver);
typedef NVSDK_NGX_Result(__cdecl* PFN_InitProjectID)(const char* projectId, int engineType, const char* engineVer, const wchar_t* dataPath, ID3D11Device* dev, const void* featureInfo, uint32_t ver);
typedef NVSDK_NGX_Result(__cdecl* PFN_GetFeatureReqs)(ID3D11Device* dev, const void* featureInfo, NVSDK_NGX_Parameter* params, NVSDK_NGX_Parameter** outReqs);
typedef NVSDK_NGX_Result(__cdecl* PFN_Shutdown)    ();
typedef NVSDK_NGX_Result(__cdecl* PFN_GetCap)      (NVSDK_NGX_Parameter** out);
typedef NVSDK_NGX_Result(__cdecl* PFN_GetParams)   (NVSDK_NGX_Parameter** out);
typedef NVSDK_NGX_Result(__cdecl* PFN_Alloc)       (NVSDK_NGX_Parameter** out);
typedef NVSDK_NGX_Result(__cdecl* PFN_Destroy)     (NVSDK_NGX_Parameter* p);
typedef NVSDK_NGX_Result(__cdecl* PFN_Create)      (ID3D11DeviceContext* ctx, int feat, NVSDK_NGX_Parameter* p, NVSDK_NGX_Handle** out);
typedef NVSDK_NGX_Result(__cdecl* PFN_Eval)        (ID3D11DeviceContext* ctx, NVSDK_NGX_Handle* h, NVSDK_NGX_Parameter* p, void* cb);
typedef NVSDK_NGX_Result(__cdecl* PFN_Release)     (NVSDK_NGX_Handle* h);

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

static HMODULE              g_ngxModule   = nullptr;
static bool                 g_initialized = false;
static ID3D11Device*        g_device      = nullptr;
static ID3D11DeviceContext* g_context     = nullptr;
static std::wstring         g_appDir;

static PFN_Init          fn_Init          = nullptr;
static PFN_InitProjectID fn_InitProjID   = nullptr;
static PFN_Shutdown      fn_Shutdown      = nullptr;
static PFN_GetCap        fn_GetCap        = nullptr;
static PFN_GetCap        fn_GetDevCap     = nullptr;
static PFN_GetParams     fn_GetParams     = nullptr;
static PFN_Alloc         fn_Alloc         = nullptr;
static PFN_Destroy       fn_Destroy       = nullptr;
static PFN_Create        fn_Create        = nullptr;
static PFN_Eval          fn_Eval          = nullptr;
static PFN_Release       fn_Release       = nullptr;

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

// ---------------------------------------------------------------------------
// Find _nvngx.dll in driver store
// ---------------------------------------------------------------------------

static std::wstring FindNGXDll() {
    WIN32_FIND_DATAW fd;
    const wchar_t* pattern = L"C:\\Windows\\System32\\DriverStore\\FileRepository\\nv*";
    HANDLE hFind = FindFirstFileW(pattern, &fd);
    if (hFind == INVALID_HANDLE_VALUE) return L"";

    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            std::wstring path = L"C:\\Windows\\System32\\DriverStore\\FileRepository\\";
            path += fd.cFileName;
            path += L"\\_nvngx.dll";
            if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) {
                FindClose(hFind);
                return path;
            }
        }
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);
    return L"";
}

// ---------------------------------------------------------------------------
// Exported C API
// ---------------------------------------------------------------------------

#define EXPORT extern "C" __declspec(dllexport)

EXPORT void NGXBridge_SetLogCallback(LogCallback cb) {
    g_logCb = cb;
}

EXPORT int NGXBridge_Load() {
    if (g_ngxModule) return 1;

    auto path = FindNGXDll();
    if (path.empty()) {
        Log("NGXBridge: _nvngx.dll not found in driver store");
        return 0;
    }

    Log("NGXBridge: Loading _nvngx.dll...");
    g_ngxModule = LoadLibraryW(path.c_str());
    if (!g_ngxModule) {
        Log("NGXBridge: LoadLibrary failed (error %lu)", GetLastError());
        return 0;
    }

    fn_Init          = (PFN_Init)         GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_Init");
    fn_InitProjID    = (PFN_InitProjectID)GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_Init_ProjectID");
    Log("NGXBridge: Init_ProjectID: %s", fn_InitProjID ? "found" : "NOT FOUND");
    fn_Shutdown      = (PFN_Shutdown)     GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_Shutdown");
    fn_GetCap        = (PFN_GetCap)       GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_GetCapabilityParameters");
    fn_GetDevCap     = (PFN_GetCap)       GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_GetDeviceCapabilityParameters");
    fn_GetParams     = (PFN_GetParams)    GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_GetParameters");
    fn_Alloc         = (PFN_Alloc)        GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_AllocateParameters");
    fn_Destroy       = (PFN_Destroy)      GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_DestroyParameters");
    fn_Create        = (PFN_Create)       GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_CreateFeature");
    fn_Eval          = (PFN_Eval)         GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_EvaluateFeature");
    fn_Release       = (PFN_Release)      GetProcAddress(g_ngxModule, "NVSDK_NGX_D3D11_ReleaseFeature");

    if (!fn_Init) { Log("NGXBridge: No Init export"); return 0; }
    if (!fn_Create || !fn_Eval || !fn_Release) { Log("NGXBridge: Missing feature exports"); return 0; }
    if (!fn_Alloc) { Log("NGXBridge: No AllocateParameters"); return 0; }

    Log("NGXBridge: All exports resolved");
    return 1;
}

EXPORT int NGXBridge_InitD3D11(ID3D11Device* device) {
    if (g_initialized) return 1;
    if (!g_ngxModule || !device) return 0;

    g_device = device;
    device->GetImmediateContext(&g_context);
    if (!g_context) { Log("NGXBridge: GetImmediateContext failed"); return 0; }

    // Get app directory (game exe)
    wchar_t exePath[MAX_PATH];
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    g_appDir = std::wstring(exePath);
    auto lastSlash = g_appDir.find_last_of(L'\\');
    if (lastSlash != std::wstring::npos)
        g_appDir = g_appDir.substr(0, lastSlash);

    // Get bridge DLL directory (where NGX actually looks for nvngx_dlss.dll!)
    wchar_t bridgePath[MAX_PATH];
    HMODULE bridgeModule = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)&NGXBridge_Load, &bridgeModule);
    GetModuleFileNameW(bridgeModule, bridgePath, MAX_PATH);
    std::wstring bridgeDir(bridgePath);
    auto bridgeSlash = bridgeDir.find_last_of(L'\\');
    if (bridgeSlash != std::wstring::npos)
        bridgeDir = bridgeDir.substr(0, bridgeSlash);

    Log("NGXBridge: App directory: %ls", g_appDir.c_str());
    Log("NGXBridge: Bridge DLL directory: %ls", bridgeDir.c_str());

    // Check nvngx_dlss.dll in BOTH locations
    std::wstring dlssInApp = g_appDir + L"\\nvngx_dlss.dll";
    std::wstring dlssInBridge = bridgeDir + L"\\nvngx_dlss.dll";
    Log("NGXBridge: nvngx_dlss.dll in app dir: %s",
        GetFileAttributesW(dlssInApp.c_str()) != INVALID_FILE_ATTRIBUTES ? "YES" : "NO");
    Log("NGXBridge: nvngx_dlss.dll in bridge dir: %s",
        GetFileAttributesW(dlssInBridge.c_str()) != INVALID_FILE_ATTRIBUTES ? "YES" : "NO");

    NVSDK_NGX_Result result;

    // Pass bridge DLL directory as data path — this is where NGX searches
    // for feature modules (nvngx_dlss.dll)
    result = fn_Init(
        0x12345678,            // App ID
        bridgeDir.c_str(),     // Data path = bridge DLL dir (where nvngx_dlss.dll is)
        device,                // D3D11 device
        nullptr,               // FeatureCommonInfo
        0x14                   // SDK version
    );
    Log("NGXBridge: Init result: 0x%08X (dataPath=%ls)", result, bridgeDir.c_str());

    g_initialized = NGX_SUCCEED(result);
    if (!g_initialized) Log("NGXBridge: Init failed");
    else Log("NGXBridge: Initialized successfully");

    return g_initialized ? 1 : 0;
}

EXPORT int NGXBridge_TryCreateDirect() {
    if (!g_initialized || !g_context || !fn_Create) return 0;

    NVSDK_NGX_Parameter* p = nullptr;
    const char* source = "unknown";

    if (fn_GetParams) {
        auto r = fn_GetParams(&p);
        if (NGX_SUCCEED(r) && p) source = "GetParameters";
    }
    if (!p && fn_Alloc) {
        auto r = fn_Alloc(&p);
        if (NGX_SUCCEED(r) && p) source = "AllocateParameters";
    }
    if (!p) { Log("NGXBridge: TryCreate - no params available"); return 0; }

    Log("NGXBridge: TryCreate using %s", source);

    // Set params using correct vtable layout
    auto vt = GetVtable(p);
    auto setUI = reinterpret_cast<void(*)(void*, const char*, unsigned int)>(vt[VT_SET_UINT]);
    auto setI  = reinterpret_cast<void(*)(void*, const char*, int)>(vt[VT_SET_INT]);

    setUI(p, "Width", 1920);
    setUI(p, "Height", 1080);
    setUI(p, "OutWidth", 3840);
    setUI(p, "OutHeight", 2160);
    setI(p, "PerfQualityValue", 1);
    setI(p, "DLSS.Feature.Create.Flags", (1 << 1) | (1 << 3));

    // DIAGNOSTIC: Read back parameters to verify vtable is correct
    auto getUI = reinterpret_cast<NVSDK_NGX_Result(*)(void*, const char*, unsigned int*)>(vt[VT_GET_UINT]);
    auto getI  = reinterpret_cast<NVSDK_NGX_Result(*)(void*, const char*, int*)>(vt[VT_GET_INT]);
    if (getUI && getI) {
        unsigned int w = 0, h = 0, ow = 0, oh = 0;
        int pqv = -1, flags = -1;
        getUI(p, "Width", &w);
        getUI(p, "Height", &h);
        getUI(p, "OutWidth", &ow);
        getUI(p, "OutHeight", &oh);
        getI(p, "PerfQualityValue", &pqv);
        getI(p, "DLSS.Feature.Create.Flags", &flags);
        Log("NGXBridge: VERIFY — Width=%u Height=%u OutWidth=%u OutHeight=%u PQV=%d Flags=%d",
            w, h, ow, oh, pqv, flags);
    }

    NVSDK_NGX_Handle* handle = nullptr;
    auto result = fn_Create(g_context, NGX_FEATURE_SUPER_SAMPLING, p, &handle);
    Log("NGXBridge: TryCreate result: 0x%08X (handle=%p)", result, handle);

    if (handle && fn_Release) fn_Release(handle);
    if (fn_Destroy) fn_Destroy(p);

    if (NGX_SUCCEED(result)) return 1;
    if (result == NGX_ERR_FEATURE_NOT_SUPPORTED) {
        Log("NGXBridge: DLSS not supported on this GPU");
        return 0;
    }

    Log("NGXBridge: Feature creation error 0x%08X (not FeatureNotSupported — DLSS may be available)", result);
    return 1;
}

EXPORT int NGXBridge_IsDLSSAvailable() {
    if (!g_initialized) return 0;

    if (fn_GetCap) {
        NVSDK_NGX_Parameter* cap = nullptr;
        auto result = fn_GetCap(&cap);
        Log("NGXBridge: GetCapabilityParameters result: 0x%08X (cap=%p)", result, cap);

        if (NGX_SUCCEED(result) && cap) {
            auto vt = GetVtable(cap);
            auto getUI = reinterpret_cast<NVSDK_NGX_Result(*)(void*, const char*, unsigned int*)>(vt[VT_GET_UINT]);

            unsigned int available = 0, supported = 0;
            if (getUI) {
                getUI(cap, "SuperSampling.Available", &available);
                getUI(cap, "SuperSampling.Supported", &supported);
            }

            Log("NGXBridge: SuperSampling.Available = %u, Supported = %u", available, supported);
            if (available > 0 || supported > 0) return 1;
        }
    }

    Log("NGXBridge: Capability check negative, trying direct feature creation...");
    return NGXBridge_TryCreateDirect();
}

EXPORT void* NGXBridge_AllocParams() {
    NVSDK_NGX_Parameter* p = nullptr;
    if (fn_GetParams) {
        auto r = fn_GetParams(&p);
        if (NGX_SUCCEED(r) && p) return p;
    }
    if (fn_Alloc) {
        auto r = fn_Alloc(&p);
        if (NGX_SUCCEED(r) && p) return p;
    }
    return nullptr;
}

EXPORT void NGXBridge_DestroyParams(NVSDK_NGX_Parameter* p) {
    if (fn_Destroy && p) fn_Destroy(p);
}

EXPORT void NGXBridge_ParamSetInt(NVSDK_NGX_Parameter* p, const char* name, int val) {
    if (!p) return;
    auto fn = reinterpret_cast<void(*)(void*, const char*, int)>(GetVtable(p)[VT_SET_INT]);
    fn(p, name, val);
}

EXPORT void NGXBridge_ParamSetUInt(NVSDK_NGX_Parameter* p, const char* name, unsigned int val) {
    if (!p) return;
    auto fn = reinterpret_cast<void(*)(void*, const char*, unsigned int)>(GetVtable(p)[VT_SET_UINT]);
    fn(p, name, val);
}

EXPORT void NGXBridge_ParamSetFloat(NVSDK_NGX_Parameter* p, const char* name, float val) {
    if (!p) return;
    auto fn = reinterpret_cast<void(*)(void*, const char*, float)>(GetVtable(p)[VT_SET_FLOAT]);
    fn(p, name, val);
}

EXPORT void NGXBridge_ParamSetResource(NVSDK_NGX_Parameter* p, const char* name, ID3D11Resource* res) {
    if (!p) return;
    auto fn = reinterpret_cast<void(*)(void*, const char*, ID3D11Resource*)>(GetVtable(p)[VT_SET_D3D11]);
    fn(p, name, res);
}

EXPORT void* NGXBridge_CreateDLSS(NVSDK_NGX_Parameter* params) {
    if (!fn_Create || !g_context) return nullptr;
    NVSDK_NGX_Handle* handle = nullptr;
    auto result = fn_Create(g_context, NGX_FEATURE_SUPER_SAMPLING, params, &handle);
    Log("NGXBridge: CreateFeature result: 0x%08X (handle=%p)", result, handle);
    return NGX_SUCCEED(result) ? handle : nullptr;
}

EXPORT int NGXBridge_EvalDLSS(NVSDK_NGX_Handle* handle, NVSDK_NGX_Parameter* params) {
    if (!fn_Eval || !g_context || !handle) return 0;
    auto result = fn_Eval(g_context, handle, params, nullptr);
    return NGX_SUCCEED(result) ? 1 : 0;
}

EXPORT void NGXBridge_ReleaseDLSS(NVSDK_NGX_Handle* handle) {
    if (fn_Release && handle) fn_Release(handle);
}

EXPORT void NGXBridge_Shutdown() {
    if (g_initialized && fn_Shutdown) {
        fn_Shutdown();
        g_initialized = false;
    }
    if (g_context) { g_context->Release(); g_context = nullptr; }
    g_device = nullptr;
    if (g_ngxModule) { FreeLibrary(g_ngxModule); g_ngxModule = nullptr; }
}

// Minimal DllMain — does NOTHING to avoid crashes
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
