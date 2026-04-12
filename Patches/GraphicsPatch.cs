using HarmonyLib;

namespace REPOFidelity.Patches;

[HarmonyPatch(typeof(GraphicsManager))]
internal static class GraphicsPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GraphicsManager.UpdateRenderSize))]
    public static bool PrefixUpdateRenderSize()
    {
        if (!Settings.ModEnabled) return true;
        // let game handle pixelation when enabled, otherwise we manage RT sizing
        return Settings.Pixelation;
    }
}
