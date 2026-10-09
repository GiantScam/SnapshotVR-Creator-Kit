using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

// Kit maintenance only - lives under _KitDev, which is left out of release zips.
//
// A map bundle is compiled in this project and rendered by the game, so every setting here that
// decides shader variants, texture formats or graphics-API programs has to match the game's.
// Apply() sets them; Verify() reads them back and fails loudly on any that drifted. Run Verify
// after any Unity, URP or graphics-settings change, before cutting a release.
//
// Batch mode:
//   Unity -batchmode -quit -projectPath <kit> -executeMethod KitProjectSetup.ApplyAndVerify
public static class KitProjectSetup
{
    const string LogPrefix = "[KitSetup]";

    const string GoodPipelinePath = "Assets/Rendering/SnapshotGoodRenderPipeline.asset";
    const string FantasticPipelinePath = "Assets/Rendering/SnapshotFantasticRenderPipeline 1.asset";
    const string SettingsFolder = "Assets/Settings";

    // XR is enabled here for one reason: URP decides whether to keep the stereo shader variants
    // from whether the build target has an active XR loader. Built without one, a map renders
    // correctly in the left eye only on a headset. Nothing in this project runs in XR.
    const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";
    const string XRSettingsPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
    static readonly BuildTargetGroup[] XRTargets = { BuildTargetGroup.Standalone, BuildTargetGroup.Android };

    static readonly string[] KeepFlags =
    {
        "m_LightmapKeepPlain", "m_LightmapKeepDirCombined", "m_LightmapKeepDynamicPlain",
        "m_LightmapKeepDynamicDirCombined", "m_LightmapKeepShadowMask", "m_LightmapKeepSubtractive",
        "m_FogKeepLinear", "m_FogKeepExp", "m_FogKeepExp2",
    };

    [MenuItem("Snapshot/UGC/Kit Dev/Apply Project Settings", false, 900)]
    public static void Apply()
    {
        PlayerSettings.companyName = "Giant Scam Industries";
        PlayerSettings.productName = "Snapshot VR Creator Kit";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        PlayerSettings.Android.textureCompressionFormats = new[] { TextureCompressionFormat.ASTC };

        EditorSettings.serializationMode = SerializationMode.ForceText;
        VersionControlSettings.mode = "Visible Meta Files";

        var good = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(GoodPipelinePath);
        if (good != null) GraphicsSettings.defaultRenderPipeline = good;

        // Custom stripping with every mode kept. Automatic decides from the scenes in the build,
        // and this project has no gameplay scenes to decide from.
        var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        graphics.FindProperty("m_LightmapStripping").intValue = 1;
        graphics.FindProperty("m_FogStripping").intValue = 1;
        foreach (var flag in KeepFlags) graphics.FindProperty(flag).boolValue = true;
        graphics.ApplyModifiedPropertiesWithoutUndo();

        ApplyXR();

        MoveIntoSettingsFolder(EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>());
        if (GraphicsSettings.TryGetRenderPipelineSettings<URPDefaultVolumeProfileSettings>(out var volume))
            MoveIntoSettingsFolder(volume.volumeProfile);

        AssetDatabase.SaveAssets();
        Debug.Log($"{LogPrefix} project settings applied.");
    }

    [MenuItem("Snapshot/UGC/Kit Dev/Verify Project Settings", false, 901)]
    public static void VerifyMenuItem() => Verify();

    public static bool Verify()
    {
        var failures = new List<string>();
        void Check(string what, object actual, object expected)
        {
            if (!Equals(actual, expected)) failures.Add($"{what}: is '{actual}', expected '{expected}'");
        }

        Check("Color Space", PlayerSettings.colorSpace, ColorSpace.Linear);
        Check("Stereo Rendering Path", PlayerSettings.stereoRenderingPath, StereoRenderingPath.Instancing);
        Check("Windows auto graphics API", PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64), false);
        Check("Windows graphics APIs", Join(PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64)), "Direct3D11");
        Check("Android auto graphics API", PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android), false);
        Check("Android graphics APIs", Join(PlayerSettings.GetGraphicsAPIs(BuildTarget.Android)), "Vulkan");
        Check("Android texture compression", Join(PlayerSettings.Android.textureCompressionFormats), "ASTC");
        Check("Asset Serialization", EditorSettings.serializationMode, SerializationMode.ForceText);
        Check("Version Control mode", VersionControlSettings.mode, "Visible Meta Files");

        var good = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(GoodPipelinePath);
        var fantastic = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(FantasticPipelinePath);
        if (good == null) failures.Add($"{GoodPipelinePath} did not load as a pipeline asset");
        if (fantastic == null) failures.Add($"{FantasticPipelinePath} did not load as a pipeline asset");
        Check("Default render pipeline", GraphicsSettings.defaultRenderPipeline, good);

        // URP strips shader variants against the pipeline assets on the quality levels it can
        // see, so both tiers must be present, each on its own asset.
        Check("Quality levels", string.Join(", ", QualitySettings.names), "Fantastic, Good");
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            string level = QualitySettings.names[i];
            var expected = level == "Good" ? good : level == "Fantastic" ? fantastic : null;
            Check($"Quality level '{level}' pipeline", QualitySettings.GetRenderPipelineAssetAt(i), expected);
        }
        if (good is UniversalRenderPipelineAsset goodUrp) Check($"{goodUrp.name} MSAA", goodUrp.msaaSampleCount, 4);

        var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        Check("Lightmap stripping mode (1 = Custom)", graphics.FindProperty("m_LightmapStripping").intValue, 1);
        Check("Fog stripping mode (1 = Custom)", graphics.FindProperty("m_FogStripping").intValue, 1);
        foreach (var flag in KeepFlags) Check(flag, graphics.FindProperty(flag).boolValue, true);

        foreach (var group in XRTargets)
        {
            var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
            var loaders = xr != null && xr.Manager != null ? xr.Manager.activeLoaders : null;
            Check($"{group} XR loaders", loaders == null ? "none" : string.Join(", ", loaders.Select(l => l.GetType().FullName)), OpenXRLoaderType);
            Check($"{group} XR initialize on startup", xr != null && xr.InitManagerOnStart, false);
            var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            Check($"{group} OpenXR render mode", openXR != null ? openXR.renderMode.ToString() : "no settings", OpenXRSettings.RenderMode.SinglePassInstanced.ToString());
        }

        var global = EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<UniversalRenderPipeline>();
        if (global == null)
        {
            failures.Add("no URP Global Settings asset is registered in Graphics settings");
        }
        else
        {
            if (GraphicsSettings.TryGetRenderPipelineSettings<RenderGraphSettings>(out var renderGraph))
                Check("URP Compatibility Mode", renderGraph.enableRenderCompatibilityMode, false);
            else
                failures.Add("URP RenderGraphSettings not found");

            if (GraphicsSettings.TryGetRenderPipelineSettings<URPShaderStrippingSetting>(out var urpStrip))
            {
                Check("URP strip unused variants", urpStrip.stripUnusedVariants, true);
                Check("URP strip unused post-processing variants", urpStrip.stripUnusedPostProcessingVariants, false);
                Check("URP strip screen-coord-override variants", urpStrip.stripScreenCoordOverrideVariants, true);
            }
            else failures.Add("URPShaderStrippingSetting not found");

            if (GraphicsSettings.TryGetRenderPipelineSettings<ShaderStrippingSetting>(out var strip))
                Check("Strip runtime debug shaders", strip.stripRuntimeDebugShaders, true);
            else
                failures.Add("ShaderStrippingSetting not found");
        }

        if (failures.Count == 0)
        {
            Debug.Log($"{LogPrefix} VERIFY PASSED - every checked setting matches.");
            return true;
        }
        Debug.LogError($"{LogPrefix} VERIFY FAILED - {failures.Count} setting(s) differ:\n   " + string.Join("\n   ", failures));
        return false;
    }

    public static void ApplyAndVerify()
    {
        Apply();
        bool ok = Verify();
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }

    // The OpenXR loader on Windows/Linux Standalone and Android, single-pass instanced, as in the
    // game. Initialize-on-startup is off, so pressing Play here never tries to start a headset.
    static void ApplyXR()
    {
        EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
        if (perTarget == null)
        {
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            Directory.CreateDirectory(Path.GetDirectoryName(XRSettingsPath));
            AssetDatabase.CreateAsset(perTarget, XRSettingsPath);
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        }

        foreach (var group in XRTargets)
        {
            if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
            if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

            var settings = perTarget.SettingsForBuildTarget(group);
            settings.InitManagerOnStart = false;
            var manager = perTarget.ManagerSettingsForBuildTarget(group);
            if (!manager.activeLoaders.Any(l => l.GetType().FullName == OpenXRLoaderType)
                && !XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderType, group))
                Debug.LogError($"{LogPrefix} could not assign the OpenXR loader for {group}.");
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(manager);

            var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (openXR != null)
            {
                openXR.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                EditorUtility.SetDirty(openXR);
            }
        }
        EditorUtility.SetDirty(perTarget);
    }

    static string Join<T>(IEnumerable<T> values) => string.Join(", ", values.Select(v => v.ToString()));

    static void MoveIntoSettingsFolder(Object asset)
    {
        if (asset == null) return;
        string path = AssetDatabase.GetAssetPath(asset);
        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/") || path.StartsWith(SettingsFolder + "/")) return;

        if (!AssetDatabase.IsValidFolder(SettingsFolder)) AssetDatabase.CreateFolder("Assets", "Settings");
        string error = AssetDatabase.MoveAsset(path, SettingsFolder + "/" + Path.GetFileName(path));
        if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"{LogPrefix} could not move {path}: {error}");
    }
}
