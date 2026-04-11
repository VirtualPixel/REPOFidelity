using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity;

internal enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Apple,
    Unknown
}

internal enum GpuTier
{
    High,
    Mid,
    Low
}

internal static class GPUDetector
{
    public static string GpuName { get; private set; } = "Unknown";
    public static GpuVendor Vendor { get; private set; } = GpuVendor.Unknown;
    public static GpuTier Tier { get; private set; } = GpuTier.Low;
    public static bool DlssAvailable { get; private set; }
    public static int VramMb { get; private set; }
    public static bool IsD3D11 { get; private set; }
    public static bool IsIntegratedGpu { get; private set; }

    public static void Detect()
    {
        GpuName = SystemInfo.graphicsDeviceName ?? "Unknown";
        VramMb = SystemInfo.graphicsMemorySize;

        Vendor = DetectVendor(GpuName, SystemInfo.graphicsDeviceVendor);
        Tier = DetectTier(VramMb);
        IsD3D11 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
        IsIntegratedGpu = DetectIntegrated(GpuName, VramMb, Vendor);
        DlssAvailable = Vendor == GpuVendor.Nvidia && IsD3D11 && !IsIntegratedGpu;

        Plugin.Log.LogInfo($"GPU: {GpuName} | Vendor: {Vendor} | VRAM: {VramMb}MB | " +
            $"API: {SystemInfo.graphicsDeviceType} | iGPU: {IsIntegratedGpu} | Tier: {Tier}");
    }

    public static bool IsUpscalerSupported(UpscaleMode mode) => mode switch
    {
        UpscaleMode.Auto => true,
        UpscaleMode.Off => true,
        // DLSS/DLAA require NVIDIA discrete GPU + D3D11 for NGX
        UpscaleMode.DLSS or UpscaleMode.DLAA => Vendor == GpuVendor.Nvidia && IsD3D11 && !IsIntegratedGpu,
        UpscaleMode.FSR_Temporal => true,
        _ => false
    };

    // Returns supported upscaler names for the settings menu dropdown.
    public static string[] GetAvailableUpscalerNames()
    {
        var names = new List<string>();
        foreach (UpscaleMode mode in System.Enum.GetValues(typeof(UpscaleMode)))
        {
            if (mode == UpscaleMode.FSR) continue;
            if (mode == UpscaleMode.FSR4) continue;
            if (mode == UpscaleMode.DLAA) continue; // DLSS at 100% = DLAA, one dropdown entry
            if (IsUpscalerSupported(mode))
            {
                string name = mode == UpscaleMode.FSR_Temporal ? "FSR" : mode.ToString();
                names.Add(name);
            }
        }
        return names.ToArray();
    }

    private static GpuVendor DetectVendor(string name, string vendorString)
    {
        string combined = (name + " " + vendorString).ToUpperInvariant();
        if (combined.Contains("NVIDIA")) return GpuVendor.Nvidia;
        if (combined.Contains("AMD") || combined.Contains("ATI")) return GpuVendor.Amd;
        if (combined.Contains("INTEL")) return GpuVendor.Intel;
        if (combined.Contains("APPLE")) return GpuVendor.Apple;
        return GpuVendor.Unknown;
    }

    private static GpuTier DetectTier(int vramMb)
    {
        if (vramMb >= 8000) return GpuTier.High;
        if (vramMb >= 6000) return GpuTier.Mid;
        return GpuTier.Low;
    }

    private static bool DetectIntegrated(string name, int vramMb, GpuVendor vendor)
    {
        string upper = name.ToUpperInvariant();

        // Intel integrated (UHD, Iris, HD Graphics)
        if (vendor == GpuVendor.Intel &&
            (upper.Contains("UHD") || upper.Contains("IRIS") || upper.Contains("HD GRAPHICS")))
            return true;

        // AMD integrated (Radeon Graphics without a discrete model number, Vega iGPU)
        if (vendor == GpuVendor.Amd &&
            ((upper.Contains("VEGA") && !upper.Contains("VEGA 56") && !upper.Contains("VEGA 64")) ||
             (upper.Contains("RADEON GRAPHICS") && !upper.Contains("RX"))))
            return true;

        // Apple Silicon uses unified memory — powerful but bandwidth-shared like an iGPU.
        // FSR shader blits are affordable on M-series, so don't treat as iGPU for upscaler
        // selection, but note it for logging/future decisions.
        // (Apple Silicon returns false here — handled via BestUpscaler vendor checks instead)

        // Fallback: very low VRAM is a strong iGPU signal
        if (vramMb > 0 && vramMb < 2048)
            return true;

        return false;
    }
}
