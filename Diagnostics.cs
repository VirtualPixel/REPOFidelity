using BepInEx.Configuration;

namespace REPOFidelity;

// Crash-isolation switches for the temp-allocator corruption report (#14). Each
// one stands down a single scene mutation so a tester can bisect the crash
// without custom builds per run. All default to true (normal behavior); they
// are read at scene-apply time, so a config edit needs a game restart to land.
internal static class Diagnostics
{
    internal static ConfigEntry<bool> ParticleAutoCull = null!;
    internal static ConfigEntry<bool> ParticleShadows = null!;
    internal static ConfigEntry<bool> AvatarPreviewUpgrade = null!;

    internal static void Init(ConfigFile cfg)
    {
        ParticleAutoCull = cfg.Bind("Diagnostics", "Particle Auto Cull", true,
            "Crash hunt switch, leave on unless asked to change it. Off = the mod no longer changes particle culling modes.");
        ParticleShadows = cfg.Bind("Diagnostics", "Particle Shadow Restore", true,
            "Crash hunt switch, leave on unless asked to change it. Off = the mod leaves particle shadow casting exactly as the game set it, on every preset.");
        AvatarPreviewUpgrade = cfg.Bind("Diagnostics", "Avatar Preview Upgrade", true,
            "Crash hunt switch, leave on unless asked to change it. Off = the menu avatar preview keeps its vanilla render texture.");

        // always logged so a pasted log identifies which run this was
        Plugin.Log.LogInfo($"Diagnostics: particleAutoCull={ParticleAutoCull.Value} " +
            $"particleShadows={ParticleShadows.Value} avatarPreview={AvatarPreviewUpgrade.Value}");
    }
}
