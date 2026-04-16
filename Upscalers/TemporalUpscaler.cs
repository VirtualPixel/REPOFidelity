using REPOFidelity.Shaders;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity.Upscalers;

// Temporal upscaler: accumulates detail over jittered frames via motion vectors + depth reprojection.
internal class TemporalUpscaler : IUpscaler
{
    public string Name => "FSR Temporal";
    public bool IsAvailable
    {
        get
        {
            if (_material == null && !_shaderChecked)
            {
                _shaderChecked = true;
                var shader = ShaderBundle.LoadShader("Hidden/REPOFidelity/FSR_Temporal");
                if (shader != null && shader.isSupported)
                    _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            return _material != null;
        }
    }

    private bool _shaderChecked;

    private Material? _material;
    private Camera? _camera;
    private RenderTexture? _historyRT;
    private int _inputWidth, _inputHeight;
    private int _outputWidth, _outputHeight;
    private bool _needsReset = true;

    // Motion vector capture
    private RenderTexture? _motionVectorRT;
    private CommandBuffer? _mvCopyCmd;

    private static readonly int PrevTexId = Shader.PropertyToID("_PrevTex");
    private static readonly int MotionVectorTexId = Shader.PropertyToID("_MotionVectorTex");
    private static readonly int DepthTexId = Shader.PropertyToID("_DepthTex");
    private static readonly int OutputSizeId = Shader.PropertyToID("_OutputSize");
    private static readonly int InputSizeId = Shader.PropertyToID("_InputSize");
    private static readonly int JitterId = Shader.PropertyToID("_Jitter");
    private static readonly int ResetId = Shader.PropertyToID("_Reset");

    public void Initialize(Camera camera, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        _camera = camera;
        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _needsReset = true;

        if (_material == null)
        {
            Plugin.Log.LogWarning("Temporal upscale shader not available");
            return;
        }

        // Create history buffer at output resolution
        _historyRT = new RenderTexture(outputWidth, outputHeight, 0, RenderTextureFormat.DefaultHDR)
        {
            filterMode = FilterMode.Bilinear
        };
        _historyRT.Create();

        SetupMotionVectorCapture(inputWidth, inputHeight);

        Plugin.Log.LogInfo($"Temporal upscaler initialized: {inputWidth}x{inputHeight} -> {outputWidth}x{outputHeight}");
    }

    public void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (_material == null || _historyRT == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

        // Set shader parameters
        _material.SetTexture(PrevTexId, _historyRT);
        if (_motionVectorRT != null)
            _material.SetTexture(MotionVectorTexId, _motionVectorRT);
        _material.SetVector(OutputSizeId, new Vector4(_outputWidth, _outputHeight,
            1f / _outputWidth, 1f / _outputHeight));
        _material.SetVector(InputSizeId, new Vector4(_inputWidth, _inputHeight,
            1f / _inputWidth, 1f / _inputHeight));

        // jitter in pixel units for the reconstruction shader
        var mgr = UpscalerManager.Instance;
        float jx = mgr != null ? mgr.JitterX * _inputWidth : 0f;
        float jy = mgr != null ? mgr.JitterY * _inputHeight : 0f;
        _material.SetVector(JitterId, new Vector2(jx, jy));
        _material.SetFloat(ResetId, _needsReset ? 1f : 0f);
        _needsReset = false;

        // Temporal upscale: source (low res) + history (high res) -> destination (high res)
        Graphics.Blit(source, destination, _material);

        // Copy result to history for next frame
        Graphics.Blit(destination, _historyRT);
    }

    public void OnResolutionChanged(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        Dispose();
        if (_camera != null)
            Initialize(_camera, inputWidth, inputHeight, outputWidth, outputHeight);
    }

    public void Dispose()
    {
        CleanupMotionVectorCapture();

        if (_historyRT != null)
        {
            _historyRT.Release();
            Object.Destroy(_historyRT);
            _historyRT = null;
        }
    }

    private void SetupMotionVectorCapture(int width, int height)
    {
        CleanupMotionVectorCapture();

        _motionVectorRT = new RenderTexture(width, height, 0, RenderTextureFormat.RGFloat)
        {
            filterMode = FilterMode.Point
        };
        _motionVectorRT.Create();

        _mvCopyCmd = new CommandBuffer { name = "FSR Temporal MV Copy" };
        _mvCopyCmd.Blit(BuiltinRenderTextureType.MotionVectors, _motionVectorRT);

        if (_camera != null)
            _camera.AddCommandBuffer(CameraEvent.AfterEverything, _mvCopyCmd);
    }

    private void CleanupMotionVectorCapture()
    {
        if (_camera != null && _mvCopyCmd != null)
            _camera.RemoveCommandBuffer(CameraEvent.AfterEverything, _mvCopyCmd);

        _mvCopyCmd?.Dispose();
        _mvCopyCmd = null;

        if (_motionVectorRT != null)
        {
            _motionVectorRT.Release();
            Object.Destroy(_motionVectorRT);
            _motionVectorRT = null;
        }
    }

}
