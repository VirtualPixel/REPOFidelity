using System;
using System.Runtime.InteropServices;

namespace REPOFidelity.Upscalers;

// P/Invoke bindings for ngx_bridge.dll — wraps _nvngx.dll behind simple C exports.
internal static class NGXBridge
{
    private const string DLL = "ngx_bridge";

    // DLSS quality modes
    internal const int DLSS_MaxPerf = 0;
    internal const int DLSS_Balanced = 1;
    internal const int DLSS_MaxQuality = 2;
    internal const int DLSS_UltraPerformance = 3;
    internal const int DLSS_UltraQuality = 4;
    internal const int DLSS_DLAA = 5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallbackDelegate([MarshalAs(UnmanagedType.LPStr)] string message);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_SetLogCallback(LogCallbackDelegate cb);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NGXBridge_Load();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NGXBridge_InitD3D11(IntPtr device);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NGXBridge_IsDLSSAvailable();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NGXBridge_AllocParams();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_DestroyParams(IntPtr p);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_ParamSetInt(IntPtr p,
        [MarshalAs(UnmanagedType.LPStr)] string name, int val);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_ParamSetUInt(IntPtr p,
        [MarshalAs(UnmanagedType.LPStr)] string name, uint val);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_ParamSetFloat(IntPtr p,
        [MarshalAs(UnmanagedType.LPStr)] string name, float val);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_ParamSetResource(IntPtr p,
        [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr resource);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NGXBridge_CreateDLSS(IntPtr parameters);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NGXBridge_EvalDLSS(IntPtr handle, IntPtr parameters);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_ReleaseDLSS(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NGXBridge_Shutdown();

    // --- Managed helpers ---

    private static LogCallbackDelegate? _logDelegate;
    private static bool _logHooked;
    private static bool _preloaded;

    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string path);

    internal static bool Preload()
    {
        if (_preloaded) return true;
        _preloaded = true;

        var dllPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            "ngx_bridge.dll");

        if (!System.IO.File.Exists(dllPath))
        {
            Plugin.Log.LogWarning($"ngx_bridge.dll not found at: {dllPath}");
            return false;
        }

        var handle = LoadLibraryW(dllPath);
        if (handle == IntPtr.Zero)
        {
            Plugin.Log.LogWarning($"Failed to preload ngx_bridge.dll (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        Plugin.Log.LogInfo($"Preloaded ngx_bridge.dll from: {dllPath}");
        return true;
    }

    internal static void HookLog()
    {
        if (_logHooked) return;
        _logHooked = true;
        _logDelegate = msg => Plugin.Log.LogInfo($"[NGX] {msg}");
        NGXBridge_SetLogCallback(_logDelegate);
    }
}
