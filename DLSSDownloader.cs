using System.IO;

namespace REPOFidelity;

internal static class DLSSDownloader
{
    private const string DllName = "nvngx_dlss.dll";
    private const int MinDllSize = 10_000_000; // DLSS 2.x is ~14MB, 3.x is 40MB+

    internal static string GetDllPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            DllName);
    }

    internal static bool EnsureAvailable()
    {
        string path = GetDllPath();
        bool available = File.Exists(path) && new FileInfo(path).Length >= MinDllSize;

        if (available)
            Plugin.Log.LogDebug($"DLSS DLL: {path} ({new FileInfo(path).Length / 1024 / 1024}MB)");
        else
            Plugin.Log.LogWarning("nvngx_dlss.dll missing or invalid — DLSS/DLAA disabled. Reinstall the mod.");

        return available;
    }
}
