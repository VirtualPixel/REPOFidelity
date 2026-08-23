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
        Bootstrap.WarnIfRepoHd();

        bool menuLib = Bootstrap.HasPlugin("nickklmao.menulib");
        Bootstrap.InitSettings(Config, menuLib);

        _harmony = new Harmony(PluginGuid);
        Bootstrap.PatchEachClass(_harmony);
        Bootstrap.LogBanner(menuLib);
        if (menuLib) MenuIntegration.Initialize();
    }

    private void LateUpdate()
    {
        Settings.UpdateCpuGate();
        Patches.SceneOptimizer.TickDeferredApply();
        Overlay.UpdateLines();
    }

    private void OnGUI()
    {
        Overlay.Draw();
#if DEBUG
        DebugUltrawideWindow.DrawLabel(); // Debug builds only: ultrawide sim label
#endif
    }
}
