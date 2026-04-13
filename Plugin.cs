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

        // Hide from REPOConfig — we use our own settings menu via MenuLib
        Config.Bind("_", "Hidden", true,
            new BepInEx.Configuration.ConfigDescription("", null, "HideFromREPOConfig"));
        Config.SaveOnConfigSet = false;

        Settings.Init();
        GPUDetector.Detect();
        Settings.ResolveAutoDefaults();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        Log.LogInfo($"REPO Fidelity v{BuildInfo.Version} loaded");
        Log.LogInfo($"GPU: {GPUDetector.GpuName} ({GPUDetector.Vendor}, Tier: {GPUDetector.Tier}, VRAM: {GPUDetector.VramMb}MB)");
        Log.LogInfo($"CPU: {UnityEngine.SystemInfo.processorType} ({UnityEngine.SystemInfo.processorCount} threads)");
        Log.LogInfo($"RAM: {UnityEngine.SystemInfo.systemMemorySize}MB | Platform: {UnityEngine.Application.platform} | API: {UnityEngine.SystemInfo.graphicsDeviceType}");
        Log.LogInfo($"DLSS Available: {GPUDetector.DlssAvailable}");

        if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("nickklmao.menulib"))
        {
            MenuIntegration.Initialize();
            Log.LogInfo("MenuLib detected — settings added to graphics menu");
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
