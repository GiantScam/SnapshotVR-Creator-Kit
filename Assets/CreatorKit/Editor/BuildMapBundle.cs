using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

// Ships with the Snapshot VR Creator Kit. Builds map bundles and tells the creator exactly which
// files to upload. One way in for a person, and two entry points for a script:
//
//   Snapshot > UGC > Map Bundles...    the window (MapBundlesWindow): pick maps and platforms and
//                                      build just those, or press one of its two "all" buttons
//   BuildMapBundle.Build               every AssetBundle name in the project, all three platforms
//   BuildMapBundle.BuildReferenceMaps  the kit's four example maps, all three platforms
//
// The last two are what the window's "all" buttons call, and what a command line calls
// (unity run <project> -- -executeMethod BuildMapBundle.Build). They have no menu item of their own.
//
// All three go through BuildMaps below. A few things it takes care of that are easy to get wrong
// hand-rolling a bundle build:
//
// 1. TAKES THE XR LOADER OFF FOR THE LINUX PASS. The kit project has OpenXR enabled for Windows
//    and Android, because that is what makes Unity keep the shader variants a headset needs to
//    draw both eyes. Windows and Linux share one loader setting and OpenXR does not support
//    Linux, so the loader is removed before the Linux pass and put back afterwards.
//
// 2. SKIPS PLATFORMS THE CREATOR HASN'T INSTALLED. Not every creator's machine has every
//    build module installed. An uninstalled Android or Linux module would otherwise fail the
//    switch mid-run and leave the editor on the wrong target. Each platform is checked first
//    and skipped with a clear message saying what to install.
//
// 3. RESTORES THE BUILD TARGET EVEN IF A BUILD THROWS. Wrapped in try/finally, so an
//    interrupted run can't strand the creator's project on Android.
//
// 4. TELLS THEM WHAT TO DO NEXT. Prints the full path of each file produced, flags any
//    platform that didn't build, and reminds them all three are needed before publishing.
//
// 5. REFUSES TO BUILD NOTHING. If no asset has a bundle name assigned - the single most
//    common first-time mistake - this stops and explains how to assign one, rather than
//    silently building zero bundles and reporting success.
//
// 6. SWITCHES EACH PLATFORM ONCE. When maps want different platforms, each platform is visited
//    once and builds every map that asked for it; a platform nobody asked for is never switched to.
public static class BuildMapBundle
{
    public static readonly (BuildTarget target, BuildTargetGroup group, string suffix, string module)[] Platforms =
    {
        (BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, "windows", "Windows Build Support"),
        (BuildTarget.Android,             BuildTargetGroup.Android,    "android", "Android Build Support"),
        (BuildTarget.StandaloneLinux64,   BuildTargetGroup.Standalone, "linux",   "Linux Build Support"),
    };

    public static string[] AllSuffixes => Platforms.Select(p => p.suffix).ToArray();

    // One map to build, and the platforms to build it for.
    public struct MapBuild
    {
        public AssetBundleBuild Bundle;
        public string[] Suffixes;
    }

    // This tool only makes sense inside a real Snapshot VR Creator Kit project - the exporter
    // writes KIT_VERSION.json there, and nowhere else. Refusing without it also means this never
    // runs by accident inside the source project this kit is cut from.
    public static bool IsKitProject =>
        AssetDatabase.IsValidFolder("Assets/CreatorKit") && File.Exists("Assets/CreatorKit/KIT_VERSION.json");

    public static void Build()
    {
        if (!IsKitProject)
        {
            Debug.LogError("[Build] This tool runs only inside the Snapshot VR Creator Kit project.");
            return;
        }

        var bundles = AssetDatabase.GetAllAssetBundleNames();
        if (bundles.Length == 0)
        {
            Debug.LogError("[Build] No AssetBundle name is assigned to anything in this project, so there is nothing to build.\n"
                + "Select your map's root prefab, look at the bottom of the Inspector, and set an AssetBundle name "
                + "(lowercase, no spaces - it becomes your map's identifier). Assign the same name to your skybox material.");
            return;
        }
        if (bundles.Length > 1)
            Debug.LogWarning($"[Build] {bundles.Length} bundle names found ({string.Join(", ", bundles)}). "
                + "All of them will be built. To build only some, use Snapshot > UGC > Map Bundles.");

        BuildMaps(bundles.Select(b => new MapBuild { Bundle = ProjectBundle(b), Suffixes = AllSuffixes }));
    }

    // A bundle named in this project: every asset carrying that AssetBundle name.
    public static AssetBundleBuild ProjectBundle(string bundleName) => new AssetBundleBuild
    {
        assetBundleName = bundleName,
        assetNames = AssetDatabase.GetAssetPathsFromAssetBundle(bundleName),
    };

    // The game places every prefab listed at the top level of a bundle, each at its own origin. A
    // creator who gives the bundle name to their map AND to the prop prefabs the map is built from
    // (easy to do: select the folder, set the name) would get one loose extra copy of every prop
    // at the middle of the map. So a prefab that another prefab in the same bundle already
    // contains is dropped from the top-level list here. It is still packed into the bundle,
    // because the map uses it; it just isn't something for the game to place by itself.
    public static string[] TopLevelAssets(string[] assetNames)
    {
        var prefabs = assetNames.Where(IsPrefab).ToList();
        if (prefabs.Count < 2) return assetNames;

        var nested = new HashSet<string>();
        foreach (var prefab in prefabs)
            foreach (var dependency in AssetDatabase.GetDependencies(prefab, true))
                if (dependency != prefab && IsPrefab(dependency))
                    nested.Add(dependency);

        return assetNames.Where(a => !nested.Contains(a)).ToArray();
    }

    static bool IsPrefab(string path) => path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase);

    // The kit's reference maps carry no AssetBundle name (if they did, Build Map Bundle would build
    // all of them next to your own map), so they are built from this list instead: which assets
    // make up each one. It is written when the kit is cut.
    public const string ReferenceMapListPath = "Assets/CreatorKit/ReferenceMaps/ReferenceMapBundles.json";

    [System.Serializable] class ReferenceMapList { public ReferenceMapEntry[] bundles; }
    [System.Serializable] class ReferenceMapEntry { public string name; public string[] assets; }

    // Builds the kit's own reference maps, the same way and into the same place as Build Map
    // Bundle. Your own map is not touched: nothing here reads or changes AssetBundle names.
    public static void BuildReferenceMaps()
    {
        var builds = LoadReferenceMapBuilds(ReferenceMapListPath);
        if (builds == null) return;
        BuildMaps(builds.Select(b => new MapBuild { Bundle = b, Suffixes = AllSuffixes }));
    }

    // Null (with the reason logged) if the list is missing, empty, or names an asset that isn't there.
    public static AssetBundleBuild[] LoadReferenceMapBuilds(string listPath, bool quiet = false)
    {
        if (!File.Exists(listPath))
        {
            if (!quiet) Debug.LogError($"[Build] {listPath} not found. This tool runs only inside the Snapshot VR Creator Kit project.");
            return null;
        }

        var list = JsonUtility.FromJson<ReferenceMapList>(File.ReadAllText(listPath));
        if (list?.bundles == null || list.bundles.Length == 0)
        {
            if (!quiet) Debug.LogError($"[Build] {listPath} lists no reference maps.");
            return null;
        }

        var builds = new List<AssetBundleBuild>();
        var missing = new List<string>();
        foreach (var entry in list.bundles)
        {
            foreach (var asset in entry.assets)
                if (!File.Exists(asset)) missing.Add($"{entry.name}: {asset}");
            builds.Add(new AssetBundleBuild { assetBundleName = entry.name, assetNames = entry.assets });
        }
        if (missing.Count > 0)
        {
            if (!quiet) Debug.LogError("[Build] The reference map list names assets that are not in this project. "
                + "Assets/CreatorKit/ has probably been edited or only partly upgraded:\n   " + string.Join("\n   ", missing));
            return null;
        }
        return builds.ToArray();
    }

    // Where a built map lands: Assets/StreamingAssets/<bundle>/<bundle>_<platform>.assetbundle,
    // the layout the game reads local maps from (copy the folder into the game to test).
    public static string OutputPath(string bundleName, string suffix) =>
        Path.Combine(Application.streamingAssetsPath, bundleName, $"{bundleName}_{suffix}.assetbundle");

    // And what to upload: ModioUploads/<bundle>/<bundle>_<platform>.zip at the project root,
    // holding that one .assetbundle file. A mod.io modfile is a zip, and the game unzips each one
    // it downloads before looking for the map inside, so the bare .assetbundle can't be uploaded.
    public static string UploadsRoot => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ModioUploads");

    public static string UploadsFolder(string bundleName) => Path.Combine(UploadsRoot, bundleName);

    public static string UploadZipPath(string bundleName, string suffix) =>
        Path.Combine(UploadsFolder(bundleName), $"{bundleName}_{suffix}.zip");

    static void WriteUploadZip(string bundleFile, string bundleName, string suffix)
    {
        string zipPath = UploadZipPath(bundleName, suffix);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var zipStream = File.Create(zipPath))
        using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry(Path.GetFileName(bundleFile)).Open())
        using (var source = File.OpenRead(bundleFile))
            source.CopyTo(entry);
    }

    // Builds each map for its own platforms, and only the maps given. Everything a map uses goes
    // into its bundle, with one exception that depends on what is built together: an asset that
    // carries a DIFFERENT map's AssetBundle name. Built alongside that other map, it stays in the
    // other bundle, and the game - which loads one bundle per map - shows this map with it
    // missing. Built on its own, it is copied in and looks fine. Validate Map reports such assets
    // and the Map Bundles window flags them; the fix is to clear that asset's bundle name.
    public static void BuildMaps(IEnumerable<MapBuild> maps)
    {
        if (!IsKitProject)
        {
            Debug.LogError("[Build] This tool runs only inside the Snapshot VR Creator Kit project.");
            return;
        }

        var requests = maps.Where(m => m.Suffixes != null && m.Suffixes.Length > 0)
            .Select(m => new MapBuild
            {
                Suffixes = m.Suffixes,
                Bundle = new AssetBundleBuild
                {
                    assetBundleName = m.Bundle.assetBundleName,
                    assetNames = TopLevelAssets(m.Bundle.assetNames),
                },
            })
            .ToList();
        if (requests.Count == 0)
        {
            Debug.LogWarning("[Build] Nothing to build: no map has a platform selected.");
            return;
        }

        Directory.CreateDirectory(Application.streamingAssetsPath);

        var original = EditorUserBuildSettings.activeBuildTarget;
        var originalGroup = BuildPipeline.GetBuildTargetGroup(original);
        var produced = new List<string>();
        var skipped = new List<string>();
        List<XRLoader> removedLoaders = null;

        try
        {
            foreach (var (target, group, suffix, module) in Platforms)
            {
                var wanted = requests.Where(m => m.Suffixes.Contains(suffix)).Select(m => m.Bundle).ToArray();
                if (wanted.Length == 0) continue;     // nobody asked for this platform: don't switch to it

                // A creator without the module installed would otherwise fail mid-switch.
                if (!BuildPipeline.IsBuildTargetSupported(group, target))
                {
                    skipped.Add($"{suffix} - {module} is not installed (add it via Unity Hub > Installs > Add Modules)");
                    continue;
                }

                // The loader comes off before the switch, not just before the build.
                bool isLinux = target == BuildTarget.StandaloneLinux64;
                if (isLinux) removedLoaders = RemoveStandaloneXRLoaders();

                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                {
                    if (isLinux) { RestoreStandaloneXRLoaders(removedLoaders); removedLoaders = null; }
                    skipped.Add($"{suffix} - Unity refused to switch to this build target");
                    continue;
                }

                // Unity refuses a bundle with the same name as the folder it is built into, so the
                // folder gets a name no map would have (a map called "windows" would clash otherwise).
                var temp = Path.Combine("Temp", "MapBundleBuild", "_out_" + suffix);
                Directory.CreateDirectory(temp);

                // Platform is switched first so textures compress correctly for each target.
                var manifest = BuildPipeline.BuildAssetBundles(temp, wanted, BuildAssetBundleOptions.None, target);
                if (isLinux) { RestoreStandaloneXRLoaders(removedLoaders); removedLoaders = null; }
                if (manifest == null)
                {
                    skipped.Add($"{suffix} - the build failed, see the errors above");
                    continue;
                }

                foreach (var bundle in wanted.Select(b => b.assetBundleName))
                {
                    var src = Path.Combine(temp, bundle);
                    if (!File.Exists(src)) continue;

                    var dst = OutputPath(bundle, suffix);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, overwrite: true);
                    WriteUploadZip(dst, bundle, suffix);
                    produced.Add(bundle + "_" + suffix);
                }
            }
        }
        finally
        {
            // Always put the project back, even if something above threw.
            RestoreStandaloneXRLoaders(removedLoaders);
            EditorUserBuildSettings.SwitchActiveBuildTarget(originalGroup, original);
            if (Directory.Exists("Temp/MapBundleBuild"))
                Directory.Delete("Temp/MapBundleBuild", true);
            AssetDatabase.Refresh();
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine($"[Build] Built {produced.Count} bundle(s): {string.Join(", ", produced)}");
        report.AppendLine("   To test in the game:  " + Application.streamingAssetsPath + "/<map>/");
        report.AppendLine("   To upload to mod.io:  " + UploadsRoot.Replace('\\', '/') + "/<map>/<map>_<platform>.zip");

        var partial = requests.Where(m => AllSuffixes.Any(s => !File.Exists(OutputPath(m.Bundle.assetBundleName, s))))
            .Select(m => m.Bundle.assetBundleName).ToList();

        if (skipped.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("NOT built:");
            foreach (var s in skipped) report.AppendLine("   " + s);
        }
        if (partial.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"Not yet built for every platform: {string.Join(", ", partial)}. That's fine for testing, but you "
                + "need all three (windows, android, linux) before publishing - players on a missing platform can't load the map.");
        }
        else
        {
            report.AppendLine();
            report.AppendLine("Every map here has all three platforms built. Snapshot > UGC > Upload Maps to mod.io sends them to "
                + "the map's mod.io page for you. By hand instead: upload each map's three zips from ModioUploads/<map>/, "
                + "each as its own modfile tagged with its platform: windows = Windows, android = Meta Quest, linux = Linux.");
        }

        if (skipped.Count > 0) Debug.LogWarning(report.ToString());
        else Debug.Log(report.ToString());
    }

    // Returns the loaders it took off, for RestoreStandaloneXRLoaders. Null if there were none.
    static List<XRLoader> RemoveStandaloneXRLoaders()
    {
        var manager = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone)?.Manager;
        if (manager == null || manager.activeLoaders.Count == 0) return null;

        var removed = manager.activeLoaders.ToList();
        foreach (var loader in removed)
            XRPackageMetadataStore.RemoveLoader(manager, loader.GetType().FullName, BuildTargetGroup.Standalone);
        EditorUtility.SetDirty(manager);
        return removed;
    }

    static void RestoreStandaloneXRLoaders(List<XRLoader> loaders)
    {
        if (loaders == null) return;
        var manager = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone)?.Manager;
        if (manager == null) return;

        foreach (var loader in loaders)
            XRPackageMetadataStore.AssignLoader(manager, loader.GetType().FullName, BuildTargetGroup.Standalone);
        EditorUtility.SetDirty(manager);
    }
}
