using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace REPOFidelity;

// The parts of Plugin.Awake that are not wiring: the REPO_HD notice, the
// settings bring-up, the per-class Harmony pass, and the hardware banner.
internal static class Bootstrap
{
    internal static bool HasPlugin(string guid) => Chainloader.PluginInfos.ContainsKey(guid);

    internal static void InitSettings(ConfigFile config, bool menuLib)
    {
        // The HideFromREPOConfig marker is presence-based: REPOConfig hides the mod
        // whenever the tag exists, regardless of the bound value. So bind it only when
        // MenuLib is the settings surface; without MenuLib, skip it so REPOConfig shows
        // the entries as the only in-game config surface.
        if (menuLib)
            config.Bind("_", "Hidden", true,
                new ConfigDescription("", null, "HideFromREPOConfig"));
        config.SaveOnConfigSet = true;

        Settings.Init();
        GPUDetector.Detect();
        Settings.ResolveAutoDefaults();
        // Bind settings to ConfigEntries after defaults resolve so the getters
        // return real values. Visibility is conditional; the binding is not.
        ConfigIntegration.Initialize(config);
        Diagnostics.Init(config);
    }

    internal static void WarnIfRepoHd()
    {
        if (!HasPlugin("BlueAmulet.REPO_HD")) return;
        Plugin.Log.LogWarning("REPO_HD detected! REPO Fidelity covers all REPO_HD features. " +
                              "Please remove REPO_HD to avoid conflicts.");
    }

    // Patch each class individually so one bad HarmonyPatch annotation (wrong
    // method name, stale game version, etc.) can't abort the rest of Awake; menu
    // integration and DLSS setup must still run.
    internal static void PatchEachClass(Harmony harmony)
    {
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"Harmony patch failed for {type.Name}: {ex}"); }
        }
    }

    // GPUDetector.Detect already logged the GPU line. No Screen dims here: the
    // chainloader runs before the engine creates the window, so Screen.width is a
    // pre-init stub (304x201 in one crash report); the [ultrawide] panel line
    // logs the real ones once the window is up.
    internal static void LogBanner(bool menuLib)
    {
        Plugin.Log.LogInfo($"REPO Fidelity v{BuildInfo.Version} loaded");
        Plugin.Log.LogInfo($"CPU: {SystemInfo.processorType} ({SystemInfo.processorCount} threads)");
        Plugin.Log.LogInfo($"RAM: {SystemInfo.systemMemorySize}MB | Platform: {Application.platform} | API: {SystemInfo.graphicsDeviceType}");
        Plugin.Log.LogInfo($"DLSS Available: {GPUDetector.DlssAvailable}");
        Plugin.Log.LogInfo(menuLib
            ? "MenuLib detected, settings added to graphics menu"
            : "MenuLib not found, settings exposed via REPOConfig / config file");
    }
}
