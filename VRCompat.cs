using UnityEngine.XR;

namespace REPOFidelity;

/// <summary>
/// VR coexistence. REPOFidelity's HD pipeline redirects the main camera onto its own
/// render texture and jitters the projection matrix every frame for temporal upscaling.
/// Both assume a single flat display, so under a VR mod like RepoXR they collapse the
/// stereo view (each eye ends up rendering the wrong projection). When a headset is
/// active the mod skips its camera pipeline and runs only the camera-agnostic
/// optimization layer.
///
/// Detection is Unity's own XR state, so there is no dependency on RepoXR specifically.
/// </summary>
internal static class VRCompat
{
    /// <summary>True while an XR headset is driving the display.</summary>
    internal static bool Active => XRSettings.enabled && XRSettings.isDeviceActive;
}
