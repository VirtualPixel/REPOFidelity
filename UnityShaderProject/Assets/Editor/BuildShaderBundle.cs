using UnityEditor;
using UnityEngine;
using System.IO;

public class BuildShaderBundle
{
    [MenuItem("REPOFidelity/Build Shader Bundle")]
    public static void Build()
    {
        string[] shaderPaths = {
            "Assets/Shaders/CAS.shader",
            "Assets/Shaders/FSR_EASU.shader",
            "Assets/Shaders/FSR_RCAS.shader",
            "Assets/Shaders/FSR_Temporal.shader",
            "Assets/Shaders/SSAO.shader",
            "Assets/Shaders/SSR.shader"
        };

        AssetBundleBuild[] builds = new AssetBundleBuild[1];
        builds[0].assetBundleName = "repofidelity_shaders";
        builds[0].assetNames = shaderPaths;

        foreach (var sp in shaderPaths)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(sp);
            Debug.Log($"Shader '{sp}': {(shader != null ? shader.name : "NOT FOUND")}");
        }

        string outputDir = "Assets/../Build";
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        BuildPipeline.BuildAssetBundles(outputDir, builds,
            BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

        string bundlePath = Path.Combine(outputDir, "repofidelity_shaders");
        string destPath = Path.GetFullPath("Assets/../../repofidelity_shaders");

        if (File.Exists(bundlePath))
        {
            File.Copy(bundlePath, destPath, true);
            Debug.Log($"Shader bundle built: {destPath}");
        }
        else
        {
            Debug.LogError("Shader bundle build failed!");
        }
    }
}
