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
        Diagnostics.Init(Config);

        _harmony = new Harmony(PluginGuid);
        Bootstrap.PatchEachClass(_harmony);
        Bootstrap.LogBanner(menuLib);
        if (menuLib) MenuIntegration.Initialize();
    }

    private void LateUpdate()
    {
        Settings.UpdateCpuGate();
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
