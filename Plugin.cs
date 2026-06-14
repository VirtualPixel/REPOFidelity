using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace REPOFidelity;

[BepInPlugin(PluginGuid, PluginName, BuildInfo.Version)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "Vippy.REPOFidelity";
    public const string PluginName = "REPO Fidelity";

    internal static ManualLogSource Log = null!;
    internal static Plugin Instance = null!;

    private Harmony? _harmony;

    private void Awake()
    {
        Log = Logger;
        Instance = this;

        if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("BlueAmulet.REPO_HD"))
        {
            Log.LogWarning("REPO_HD detected! REPO Fidelity covers all REPO_HD features. " +
                           "Please remove REPO_HD to avoid conflicts.");
        }

        bool menuLib = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("nickklmao.menulib");

        // The HideFromREPOConfig marker is presence-based: REPOConfig hides the mod
        // whenever the tag exists, regardless of the bound value. So bind it only when
        // MenuLib is the settings surface; without MenuLib, skip it so REPOConfig shows
        // the entries as the only in-game config surface.
        if (menuLib)
            Config.Bind("_", "Hidden", true,
                new BepInEx.Configuration.ConfigDescription("", null, "HideFromREPOConfig"));
        Config.SaveOnConfigSet = true;

        Settings.Init();
        GPUDetector.Detect();
        Settings.ResolveAutoDefaults();

        // Bind settings to ConfigEntries after defaults resolve so the getters
        // return real values. Visibility is conditional; the binding is not.
        ConfigIntegration.Initialize(Config);

        _harmony = new Harmony(PluginGuid);
        // Patch each class individually so one bad HarmonyPatch annotation
        // (wrong method name, stale game version, etc.) can't abort the rest
        // of Awake; menu integration and DLSS setup must still run.
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            try { _harmony.CreateClassProcessor(type).Patch(); }
            catch (System.Exception ex) { Log.LogWarning($"Harmony patch failed for {type.Name}: {ex}"); }
        }


        Log.LogInfo($"REPO Fidelity v{BuildInfo.Version} loaded");
        Log.LogInfo($"GPU: {GPUDetector.GpuName} ({GPUDetector.Vendor}, Tier: {GPUDetector.Tier}, VRAM: {GPUDetector.VramMb}MB)");
        Log.LogInfo($"CPU: {UnityEngine.SystemInfo.processorType} ({UnityEngine.SystemInfo.processorCount} threads)");
        Log.LogInfo($"RAM: {UnityEngine.SystemInfo.systemMemorySize}MB | Platform: {UnityEngine.Application.platform} | API: {UnityEngine.SystemInfo.graphicsDeviceType}");
        Log.LogInfo($"Display: {UnityEngine.Screen.width}x{UnityEngine.Screen.height} (aspect {(float)UnityEngine.Screen.width / UnityEngine.Screen.height:F2})");
        Log.LogInfo($"DLSS Available: {GPUDetector.DlssAvailable}");

        if (menuLib)
        {
            MenuIntegration.Initialize();
            Log.LogInfo("MenuLib detected, settings added to graphics menu");
        }
        else
        {
            Log.LogInfo("MenuLib not found, settings exposed via REPOConfig / config file");
        }
    }

    private void LateUpdate()
    {
        Settings.UpdateCpuGate();
        Overlay.UpdateLines();
    }

    private void OnGUI()
    {
        Overlay.Draw();
    }
}
