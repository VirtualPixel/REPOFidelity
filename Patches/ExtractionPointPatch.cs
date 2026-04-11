using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

[HarmonyPatch(typeof(ExtractionPoint))]
internal static class ExtractionPointPatch
{
    [HarmonyPrefix]
    [HarmonyPatch("Start")]
    public static void PrefixStart(ExtractionPoint __instance)
    {
        if (!Settings.ExtractionPointFlicker) return;
        if (__instance.haulGoalScreen?.transform == null) return;

        __instance.haulGoalScreen.transform.localPosition += new Vector3(0f, 0f, -0.002f);
    }
}
