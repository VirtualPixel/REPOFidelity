using System;
using System.Runtime.InteropServices;
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

    // Output RT with UAV — DLSS needs write access
    private RenderTexture? _dlssOutputRT;

    // Depth/MV capture
    private RenderTexture? _depthRT;
    private RenderTexture? _motionVectorRT;
    private CommandBuffer? _depthCopyCmd;
    private CommandBuffer? _mvCopyCmd;
    private int _evalFailCount;
    private int _evalSuccessLogged;
    private bool _needsReset;

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
                Plugin.Log.LogWarning("DLSS: NGX D3D11 base init failed");
                return;
            }

            if (NGXBridge.NGXBridge_InitD3D12() == 0)
            {
                Plugin.Log.LogWarning("DLSS: NGX D3D12 interop init failed");
                return;
            }

            _available = NGXBridge.NGXBridge_IsDLSSAvailable_D3D12() != 0;
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDeviceDelegate(IntPtr self, out IntPtr device);

    public void Initialize(Camera camera, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        _camera = camera;
        _inputWidth = _dlaaMode ? outputWidth : inputWidth;
        _inputHeight = _dlaaMode ? outputHeight : inputHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;

        // Unity can recycle an RT's native ptr into a new resource; the native
        // bridge caches shared handles by pointer, so drop the cache on re-init.
        NGXBridge.NGXBridge_ClearSharedCache();

        if (_dlssOutputRT != null) { _dlssOutputRT.Release(); UnityEngine.Object.Destroy(_dlssOutputRT); }
        _dlssOutputRT = new RenderTexture(_outputWidth, _outputHeight, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear,
            enableRandomWrite = true
        };
        _dlssOutputRT.Create();

        // Avoid undefined first-frame flash.
        var prev = RenderTexture.active;
        RenderTexture.active = _dlssOutputRT;
        GL.Clear(false, true, Color.black);
        RenderTexture.active = prev;

        SetupCapture(_inputWidth, _inputHeight);

        _evalParams = NGXBridge.NGXBridge_AllocParams_D3D12();
        if (_evalParams == IntPtr.Zero) { Plugin.Log.LogError("DLSS: params alloc failed"); return; }

        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "Width", (uint)_inputWidth);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "Height", (uint)_inputHeight);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "OutWidth", (uint)_outputWidth);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "OutHeight", (uint)_outputHeight);
        NGXBridge.NGXBridge_ParamSetInt(_evalParams, "PerfQualityValue", GetQualityMode());

        // DLAA has full-res MVs; scaled modes get bit 1 = MV low-res flag.
        int createFlags = _dlaaMode ? 0 : (1 << 1);
        NGXBridge.NGXBridge_ParamSetInt(_evalParams, "DLSS.Feature.Create.Flags", createFlags);

        // Preset E (CNN) — tolerates Unity's MV jitter better than the transformer default.
        const int presetE = 5;
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Hint.Render.Preset.DLAA",             (uint)presetE);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Hint.Render.Preset.Quality",          (uint)presetE);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Hint.Render.Preset.Balanced",         (uint)presetE);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Hint.Render.Preset.Performance",      (uint)presetE);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams, "DLSS.Hint.Render.Preset.UltraPerformance", (uint)presetE);

        _dlssHandle = NGXBridge.NGXBridge_CreateDLSS_D3D12(_evalParams);

        if (_dlssHandle == IntPtr.Zero)
        {
            Plugin.Log.LogError("DLSS: Feature creation failed");
            return;
        }

        // A fresh feature has no temporal history; first eval needs Reset=1.
        _needsReset = true;
        Plugin.Log.LogInfo($"DLSS initialized: {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");
    }

    public void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (_dlssHandle == IntPtr.Zero || _evalParams == IntPtr.Zero)
        {
            Graphics.Blit(source, destination);
            return;
        }

        // DLSS was created for a specific input size — other render passes
        // (secondary cameras, post-fx stages) hand us arbitrary sources.
        // Pass those through untouched.
        if (source.width != _inputWidth || source.height != _inputHeight)
        {
            Graphics.Blit(source, destination);
            return;
        }

        NGXBridge.NGXBridge_ParamSetUInt(_evalParams,
            "DLSS.Render.Subrect.Dimensions.Width", (uint)source.width);
        NGXBridge.NGXBridge_ParamSetUInt(_evalParams,
            "DLSS.Render.Subrect.Dimensions.Height", (uint)source.height);
        // Unity MVs: UV-space, sign-flipped from DLSS's convention → pixel scale.
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "MV.Scale.X", -(float)_inputWidth);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "MV.Scale.Y", -(float)_inputHeight);

        // Jitter already baked into Unity MVs — pass 0 to avoid double-subtract.
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "Jitter.Offset.X", 0f);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "Jitter.Offset.Y", 0f);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "Sharpness", 0f);
        NGXBridge.NGXBridge_ParamSetFloat(_evalParams, "FrameTimeDeltaInMsec", Time.unscaledDeltaTime * 1000f);
        NGXBridge.NGXBridge_ParamSetInt(_evalParams, "Reset", _needsReset ? 1 : 0);

        var outputRT = _dlssOutputRT ?? destination;
        int ok = NGXBridge.NGXBridge_EvalDLSS_D3D12(
            _dlssHandle, _evalParams,
            source.GetNativeTexturePtr(),
            _depthRT != null ? _depthRT.GetNativeTexturePtr() : IntPtr.Zero,
            _motionVectorRT != null ? _motionVectorRT.GetNativeTexturePtr() : IntPtr.Zero,
            outputRT.GetNativeTexturePtr());

        if (ok == 0)
        {
            if (_evalFailCount++ < 5)
                Plugin.Log.LogWarning($"DLSS D3D12 eval failed (frame {_evalFailCount}) " +
                    $"src={source.width}x{source.height} out={outputRT.width}x{outputRT.height}");
            Graphics.Blit(source, destination);
        }
        else
        {
            _needsReset = false;
            if (_dlssOutputRT != null)
                Graphics.Blit(_dlssOutputRT, destination);
            if (_evalSuccessLogged++ < 3)
                Plugin.Log.LogInfo($"DLSS D3D12 eval OK — {source.width}x{source.height} -> {destination.width}x{destination.height}");
        }
    }

    public void OnResolutionChanged(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        NGXBridge.NGXBridge_ClearSharedCache();
        CleanupFeature();
        if (_camera != null)
            Initialize(_camera, inputWidth, inputHeight, outputWidth, outputHeight);
    }

    public void Dispose()
    {
        CleanupCapture();
        CleanupFeature();
        if (_dlssOutputRT != null) { _dlssOutputRT.Release(); UnityEngine.Object.Destroy(_dlssOutputRT); _dlssOutputRT = null; }
        if (_evalParams != IntPtr.Zero)
        {
            NGXBridge.NGXBridge_DestroyParams_D3D12(_evalParams);
            _evalParams = IntPtr.Zero;
        }
    }

    private void CleanupFeature()
    {
        if (_dlssHandle != IntPtr.Zero)
        {
            NGXBridge.NGXBridge_ReleaseDLSS_D3D12(_dlssHandle);
            _dlssHandle = IntPtr.Zero;
        }
    }

    private void SetupCapture(int width, int height)
    {
        CleanupCapture();

        _depthRT = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat)
            { filterMode = FilterMode.Point, enableRandomWrite = true };
        _depthRT.Create();
        _depthCopyCmd = new CommandBuffer { name = "DLSS Depth" };
        _depthCopyCmd.Blit(BuiltinRenderTextureType.Depth, _depthRT);

        _motionVectorRT = new RenderTexture(width, height, 0, RenderTextureFormat.RGHalf)
            { filterMode = FilterMode.Point, enableRandomWrite = true };
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
