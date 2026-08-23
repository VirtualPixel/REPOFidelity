using System;

namespace REPOFidelity;

// Sizing math for the menu avatar preview render texture, kept free of UnityEngine
// types so it can be exercised outside the game (REPOFidelity.Tests).
//
// The target is the LONG edge. Until 1.7.8 it was the short one, and since the
// aspect is preserved the vanilla 208x416 preview landed at 1024x2048: four times
// the surface the constant claims, on a UI slot about 400px tall, reallocated on
// every pause-menu open. That is the texture named in the #14 crash log.
internal static class PreviewRt
{
    internal const int TargetLongDim = 1024;
    internal const int TargetMsaa = 4;

    internal static bool NeedsBump(int width, int height, int antiAliasing) =>
        Math.Max(width, height) < TargetLongDim || antiAliasing < TargetMsaa;

    internal static (int Width, int Height) Target(int width, int height)
    {
        int longDim = Math.Max(width, height);
        if (longDim <= 0 || longDim >= TargetLongDim)
            return (Math.Max(1, width), Math.Max(1, height));

        double scale = (double)TargetLongDim / longDim;
        return (Scale(width, scale), Scale(height, scale));
    }

    static int Scale(int value, double scale) =>
        Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
}
