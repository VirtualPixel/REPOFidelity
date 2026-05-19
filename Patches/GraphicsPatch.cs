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
        // VR: the mod doesn't manage RT sizing, let the game run its own.
        if (VRCompat.Active) return true;
        // let game handle pixelation when enabled, otherwise we manage RT sizing
        return Settings.Pixelation;
    }
}
