using UnityEngine;

namespace REPOFidelity.Upscalers;

internal interface IUpscaler
{
    string Name { get; }
    bool IsAvailable { get; }
    void Initialize(Camera camera, int inputWidth, int inputHeight, int outputWidth, int outputHeight);
    void OnRenderImage(RenderTexture source, RenderTexture destination);
    void OnResolutionChanged(int inputWidth, int inputHeight, int outputWidth, int outputHeight);
    void Dispose();
}
