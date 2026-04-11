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
    private int _jitterIndex;
    private bool _needsReset = true;

    // Motion vector capture
    private RenderTexture? _motionVectorRT;
    private CommandBuffer? _mvCopyCmd;

    // Halton sequence for sub-pixel jitter
    private static readonly float[] HaltonX = GenerateHalton(2, 32);
    private static readonly float[] HaltonY = GenerateHalton(3, 32);

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

        // Apply jitter for next frame's rendering
        ApplyJitter();

        // Set shader parameters
        _material.SetTexture(PrevTexId, _historyRT);
        if (_motionVectorRT != null)
            _material.SetTexture(MotionVectorTexId, _motionVectorRT);
        _material.SetVector(OutputSizeId, new Vector4(_outputWidth, _outputHeight,
            1f / _outputWidth, 1f / _outputHeight));
        _material.SetVector(InputSizeId, new Vector4(_inputWidth, _inputHeight,
            1f / _inputWidth, 1f / _inputHeight));

        float jx = (HaltonX[_jitterIndex] - 0.5f);
        float jy = (HaltonY[_jitterIndex] - 0.5f);
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

    private void ApplyJitter()
    {
        _jitterIndex = (_jitterIndex + 1) % HaltonX.Length;
        // Jitter is passed to the shader via _Jitter uniform only.
        // We do NOT modify camera.projectionMatrix — that breaks
        // WorldToViewportPoint used by game UI (prices, labels, etc.)
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

    private static float[] GenerateHalton(int baseVal, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            float f = 1f, r = 0f;
            int idx = i + 1;
            while (idx > 0) { f /= baseVal; r += f * (idx % baseVal); idx /= baseVal; }
            result[i] = r;
        }
        return result;
    }
}
