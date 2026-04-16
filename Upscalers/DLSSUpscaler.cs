using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity.Upscalers;

internal class DLSSUpscaler : IUpscaler
{
    public string Name => _dlaaMode ? "DLAA" : "DLSS";

    public bool IsAvailable
    {
        get
        {
            if (!_probed) Probe();
            return _available;
        }
    }

    private readonly bool _dlaaMode;
    private bool _probed;
    private bool _available;

    private Camera? _camera;
    private IntPtr _dlssHandle;
    private IntPtr _evalParams;
    private int _inputWidth, _inputHeight;
    private int _outputWidth, _outputHeight;

    // Depth/MV capture
    private RenderTexture? _depthRT;
    private RenderTexture? _motionVectorRT;
    private CommandBuffer? _depthCopyCmd;
    private CommandBuffer? _mvCopyCmd;

    public DLSSUpscaler(bool dlaaMode = false)
    {
        _dlaaMode = dlaaMode;
    }

    private void Probe()
    {
        if (_probed) return;
        _probed = true;

        try
        {
            // Ensure nvngx_dlss.dll is available (auto-find from installed games)
            if (!DLSSDownloader.EnsureAvailable())
            {
                Plugin.Log.LogWarning("DLSS: nvngx_dlss.dll not available — DLSS disabled");
                return;
            }

            if (!NGXBridge.Preload())
            {
                Plugin.Log.LogWarning("DLSS: ngx_bridge.dll not available");
                return;
            }

            NGXBridge.HookLog();

            if (NGXBridge.NGXBridge_Load() == 0)
            {
                Plugin.Log.LogWarning("DLSS: ngx_bridge failed to load _nvngx.dll");
                return;
            }

            // Get D3D11 device from a temporary texture
            var tempTex = new Texture2D(1, 1);
            var texPtr = tempTex.GetNativeTexturePtr();
            // ID3D11ShaderResourceView -> ID3D11DeviceChild::GetDevice (vtable index 3)
            var vtable = System.Runtime.InteropServices.Marshal.ReadIntPtr(texPtr);
            var getDevPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var getDevice = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<GetDeviceDelegate>(getDevPtr);
            getDevice(texPtr, out var devicePtr);
            UnityEngine.Object.Destroy(tempTex);

            if (devicePtr == IntPtr.Zero)
            {
                Plugin.Log.LogWarning("DLSS: Could not extract D3D11 device");
                return;
            }
            Plugin.Log.LogInfo($"DLSS: D3D11 device: 0x{devicePtr:X}");

            if (NGXBridge.NGXBridge_InitD3D11(devicePtr) == 0)
            {
                Plugin.Log.LogWarning("DLSS: NGX init failed");
                return;
            }

            _available = NGXBridge.NGXBridge_IsDLSSAvailable() != 0;
            Plugin.Log.LogInfo($"DLSS: available = {_available}");
        }
        catch (DllNotFoundException)
        {
            Plugin.Log.LogWarning("DLSS: ngx_bridge.dll not found — DLSS disabled");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"DLSS probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(
        System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate void GetDeviceDelegate(IntPtr self, out IntPtr device);

    public void Initialize(Camera camera, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        _camera = camera;
        _inputWidth = _dlaaMode ? outputWidth : inputWidth;
        _inputHeight = _dlaaMode ? outputHeight : inputHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;

        SetupCapture(_inputWidth, _inputHeight);

        var p = NGXBridge.NGXBridge_AllocParams();
        if (p == IntPtr.Zero) { Plugin.Log.LogError("DLSS: AllocParams failed"); return; }

        NGXBridge.NGXBridge_ParamSetUInt(p, "Width", (uint)_inputWidth);
        NGXBridge.NGXBridge_ParamSetUInt(p, "Height", (uint)_inputHeight);
        NGXBridge.NGXBridge_ParamSetUInt(p, "OutWidth", (uint)_outputWidth);
        NGXBridge.NGXBridge_ParamSetUInt(p, "OutHeight", (uint)_outputHeight);
        NGXBridge.NGXBridge_ParamSetInt(p, "PerfQualityValue", GetQualityMode());
        // Flags: bit0=IsHDR, bit1=MVLowRes, bit2=MVJittered, bit3=DepthInverted
        NGXBridge.NGXBridge_ParamSetInt(p, "DLSS.Feature.Create.Flags",
            (1 << 1) | (1 << 3)); // MVLowRes | DepthInverted

        _dlssHandle = NGXBridge.NGXBridge_CreateDLSS(p);
        NGXBridge.NGXBridge_DestroyParams(p);

        if (_dlssHandle == IntPtr.Zero)
        {
            Plugin.Log.LogError("DLSS: Feature creation failed");
            return;
        }

        _evalParams = NGXBridge.NGXBridge_AllocParams();
        Plugin.Log.LogInfo($"DLSS initialized: {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");
    }

    public void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (_dlssHandle == IntPtr.Zero || _evalParams == IntPtr.Zero)
        {
            Graphics.Blit(source, destination);
            return;
        }

        NGXBridge.NGXBridge_ParamSetResource(_evalParams, "Color", source.GetNativeTexturePtr());
        NGXBridge.NGXBridge_ParamSetResource(_evalParams, "Output", destination.GetNativeTexturePtr());

        if (_depthRT != null)
            NGXBridge.NGXBridge_ParamSetResource(_evalParams, "Depth", _depthRT.GetNativeTexturePtr());
        if (_motionVectorRT != null)
            NGXBridge.NGXBridge_ParamSetResource(_evalParams, "MotionVectors", _motionVectorRT.GetNativeTexturePtr());

        var mgr = UpscalerManager.Instance;
        float jitterX = mgr != null ? mgr.JitterX : 0f;
        float jitterY = mgr != null ? mgr.JitterY : 0f;
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "Jitter.Offset.X", jitterX);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "Jitter.Offset.Y", jitterY);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "MV.Scale.X", -(float)_inputWidth);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "MV.Scale.Y", -(float)_inputHeight);

        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "Sharpness", Settings.Sharpening);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Render.Subrect.Dimensions.Width", (uint)_inputWidth);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Render.Subrect.Dimensions.Height", (uint)_inputHeight);
        NGXBridge.NGXBridge_ParamSetInt(_evalParams, "Reset", 0);

        if (NGXBridge.NGXBridge_EvalDLSS(_dlssHandle, _evalParams) == 0)
            Graphics.Blit(source, destination);
    }

    public void OnResolutionChanged(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        CleanupFeature();
        if (_camera != null)
            Initialize(_camera, inputWidth, inputHeight, outputWidth, outputHeight);
    }

    public void Dispose()
    {
        CleanupCapture();
        CleanupFeature();
        if (_evalParams != IntPtr.Zero)
        {
            NGXBridge.NGXBridge_DestroyParams(_evalParams);
            _evalParams = IntPtr.Zero;
        }
    }

    private void CleanupFeature()
    {
        if (_dlssHandle != IntPtr.Zero)
        {
            NGXBridge.NGXBridge_ReleaseDLSS(_dlssHandle);
            _dlssHandle = IntPtr.Zero;
        }
    }

    private void SetupCapture(int width, int height)
    {
        CleanupCapture();

        _depthRT = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point };
        _depthRT.Create();
        _depthCopyCmd = new CommandBuffer { name = "DLSS Depth" };
        _depthCopyCmd.Blit(BuiltinRenderTextureType.Depth, _depthRT);

        _motionVectorRT = new RenderTexture(width, height, 0, RenderTextureFormat.RGFloat) { filterMode = FilterMode.Point };
        _motionVectorRT.Create();
        _mvCopyCmd = new CommandBuffer { name = "DLSS MV" };
        _mvCopyCmd.Blit(BuiltinRenderTextureType.MotionVectors, _motionVectorRT);

        if (_camera != null)
        {
            _camera.AddCommandBuffer(CameraEvent.AfterDepthTexture, _depthCopyCmd);
            _camera.AddCommandBuffer(CameraEvent.AfterEverything, _mvCopyCmd);
        }
    }

    private void CleanupCapture()
    {
        if (_camera != null)
        {
            if (_depthCopyCmd != null) _camera.RemoveCommandBuffer(CameraEvent.AfterDepthTexture, _depthCopyCmd);
            if (_mvCopyCmd != null) _camera.RemoveCommandBuffer(CameraEvent.AfterEverything, _mvCopyCmd);
        }
        _depthCopyCmd?.Dispose(); _depthCopyCmd = null;
        _mvCopyCmd?.Dispose(); _mvCopyCmd = null;
        if (_depthRT != null) { _depthRT.Release(); UnityEngine.Object.Destroy(_depthRT); _depthRT = null; }
        if (_motionVectorRT != null) { _motionVectorRT.Release(); UnityEngine.Object.Destroy(_motionVectorRT); _motionVectorRT = null; }
    }

    private int GetQualityMode()
    {
        if (_dlaaMode) return NGXBridge.DLSS_DLAA;
        return Settings.ResolvedRenderScale switch
        {
            >= 77 => NGXBridge.DLSS_MaxQuality,
            >= 59 => NGXBridge.DLSS_Balanced,
            >= 45 => NGXBridge.DLSS_MaxPerf,
            _ => NGXBridge.DLSS_UltraPerformance
        };
    }

}
