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
        // Block the game's pixelated render size unless user wants retro mode.
        // All three render tiers (Passthrough, NativeScaling, Upscaler) manage
        // their own dimensions via RenderTexturePatch.PrefixUpdate.
        return Settings.Pixelation;
    }
}
