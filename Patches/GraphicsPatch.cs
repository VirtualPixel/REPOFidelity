using HarmonyLib;

namespace REPOFidelity.Patches;

[HarmonyPatch(typeof(GraphicsManager))]
internal static class GraphicsPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GraphicsManager.UpdateRenderSize))]
    public static bool PrefixUpdateRenderSize()
    {
        if (!Settings.ModEnabled) return true; // Let game handle when mod off
        return Settings.Pixelation;
    }
}
