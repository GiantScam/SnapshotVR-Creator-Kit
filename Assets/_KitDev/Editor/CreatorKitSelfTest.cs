using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Kit maintenance tooling - not part of a release (Assets/_KitDev is export-ignored).
//
// Checks a freshly exported kit from the inside, where the tags, layers and packages really are
// the kit's own: nothing under Assets/CreatorKit has a missing script, a missing mesh or a missing
// material; every tag in use is defined in this project; every asset the reference map list names
// exists; all four reference maps pass Validate Map; each bakes the same walkable area as it did
// in the game project; and the shipped build path produces a loadable bundle, with intact tags,
// for all three platforms.
//
//   Snapshot > UGC > Kit Dev > Run Self-Test
//   unity run <kit> -- -executeMethod CreatorKitSelfTest.Run -logFile <log>     (exit 1 on failure)
//   The game project's exporter runs it after an export (Phase 5) when asked to.
public static class CreatorKitSelfTest
{
    const string KitRoot = "Assets/CreatorKit";

    [MenuItem("Snapshot/UGC/Kit Dev/Run Self-Test", false, 920)]
    public static void RunMenuItem() => Check();

    public static void Run()
    {
        bool ok = Check();
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }

    public static bool Check()
    {
        var problems = new List<string>();
        var report = new StringBuilder();
        var definedTags = new HashSet<string>(UnityEditorInternal.InternalEditorUtility.tags);

        // 1. Every prefab: no missing script, mesh or material; no undefined tag.
        var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { KitRoot }).Select(AssetDatabase.GUIDToAssetPath).ToList();
        foreach (var path in prefabs)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) { problems.Add($"{path}: could not be loaded"); continue; }

            int missingScripts = 0, missingMeshes = 0, missingMaterials = 0;
            var badTags = new HashSet<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                string tag;
                try { tag = t.gameObject.tag; } catch { tag = "<undefined>"; }
                if (!definedTags.Contains(tag)) badTags.Add(tag);

                var filter = t.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh == null) missingMeshes++;
                var collider = t.GetComponent<MeshCollider>();
                if (collider != null && collider.sharedMesh == null) missingMeshes++;
                var renderer = t.GetComponent<Renderer>();
                if (renderer != null) missingMaterials += renderer.sharedMaterials.Count(m => m == null || m.shader == null);
            }
            if (missingScripts > 0) problems.Add($"{path}: {missingScripts} missing script(s)");
            if (missingMeshes > 0) problems.Add($"{path}: {missingMeshes} missing mesh(es)");
            if (missingMaterials > 0) problems.Add($"{path}: {missingMaterials} missing material(s) or shader(s)");
            if (badTags.Count > 0) problems.Add($"{path}: tag(s) not defined in this project: {string.Join(", ", badTags)}");
        }
        report.AppendLine($"prefabs checked: {prefabs.Count}");
        report.AppendLine("tags defined: " + string.Join(" | ", UnityEditorInternal.InternalEditorUtility.tags));

        // 2. Nothing in the kit still carries a bundle name (it would be built with a creator's map).
        var named = AssetDatabase.GetAllAssetBundleNames()
            .SelectMany(AssetDatabase.GetAssetPathsFromAssetBundle)
            .Where(p => p.StartsWith(KitRoot + "/")).ToList();
        foreach (var p in named) problems.Add($"{p}: still has an AssetBundle name");

        // 3. The reference map list resolves.
        var builds = BuildMapBundle.LoadReferenceMapBuilds(BuildMapBundle.ReferenceMapListPath);
        if (builds == null) problems.Add("the reference map list is missing, empty, or names an asset that is not here");
        else report.AppendLine("reference map list: " + string.Join(", ", builds.Select(b => $"{b.assetBundleName} ({b.assetNames.Length} assets)")));

        // 4. Validate Map on every reference map.
        foreach (var path in prefabs.Where(p => p.StartsWith(KitRoot + "/ReferenceMaps/")))
        {
            var findings = UGCMapValidator.ValidatePrefabAsset(path);
            int errors = 0, warnings = 0;
            foreach (var f in findings)
            {
                if (f.Level == UGCMapValidator.Severity.Error) { errors++; problems.Add($"{path} :: {f.ObjectPath} - {f.Message}"); }
                else if (f.Level == UGCMapValidator.Severity.Warning)
                {
                    warnings++;
                    report.AppendLine($"   warning: {f.ObjectPath} - {f.Message}");
                }
            }
            report.AppendLine($"{Path.GetFileNameWithoutExtension(path)}: {errors} error(s), {warnings} warning(s)");
        }

        // 5. Walkable area against the figures the exporter measured in the game project with the
        //    same code. A difference means a setting or package drifted between the two projects.
        CheckWalkableArea(problems, report);

        // 6. The shipped build path, all three platforms, on one reference map, under a kitcheck_
        //    name so it can't be mistaken for a real map. Then the Windows file is loaded back and
        //    its tags compared with the source prefab. The files stay in Assets/StreamingAssets
        //    (ignored by git) for importing into the game: Snapshot > UGC > Import Kit Bundles there.
        if (builds != null) CheckBuild(builds, problems, report);

        if (problems.Count == 0)
        {
            Debug.Log("[KitSelfTest] PASSED\n" + report);
            return true;
        }
        Debug.LogError($"[KitSelfTest] FAILED - {problems.Count} problem(s)\n" + report + "\n" + string.Join("\n", problems));
        return false;
    }

    const string MeasurementsPath = "Assets/_KitDev/ExportMeasurements.json";
    const float AreaTolerance = 0.005f;      // 0.5%: the same code on the same engine should agree exactly
    const string BuildCheckMap = "ugcthegym";

    [System.Serializable] class Measurements { public string unityVersion; public MapMeasurement[] maps; }
    [System.Serializable] class MapMeasurement { public string prefab; public float walkableArea; public int triangles; }

    static void CheckWalkableArea(List<string> problems, StringBuilder report)
    {
        if (!File.Exists(MeasurementsPath))
        {
            problems.Add($"{MeasurementsPath} is missing - export the kit again (the exporter writes it)");
            return;
        }
        var expected = JsonUtility.FromJson<Measurements>(File.ReadAllText(MeasurementsPath));
        foreach (var m in expected.maps)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(m.prefab);
            if (prefab == null) { problems.Add($"{m.prefab}: listed in {MeasurementsPath} but not in the kit"); continue; }

            float area = UGCNavMeshPreview.MeasureWalkableArea(prefab, out int tris);
            float diff = m.walkableArea > 0 ? Mathf.Abs(area - m.walkableArea) / m.walkableArea : area;
            string line = $"{Path.GetFileNameWithoutExtension(m.prefab)}: walkable {area:F1} m² here, {m.walkableArea:F1} m² in the game "
                + $"({tris} / {m.triangles} triangles)";
            report.AppendLine(line);
            if (diff > AreaTolerance)
                problems.Add(line + $" - differs by {diff:P2}. A navigation setting or package version no longer matches the game.");
        }
    }

    static void CheckBuild(AssetBundleBuild[] builds, List<string> problems, StringBuilder report)
    {
        var source = builds.FirstOrDefault(b => b.assetBundleName == BuildCheckMap);
        if (source.assetNames == null) { problems.Add($"{BuildCheckMap} is not in the reference map list"); return; }

        string name = "kitcheck_" + BuildCheckMap;
        var started = System.DateTime.UtcNow.AddSeconds(-2);
        BuildMapBundle.BuildMaps(new[] { new BuildMapBundle.MapBuild
        {
            Bundle = new AssetBundleBuild { assetBundleName = name, assetNames = source.assetNames },
            Suffixes = BuildMapBundle.AllSuffixes,
        } });

        foreach (var suffix in BuildMapBundle.AllSuffixes)
        {
            string file = BuildMapBundle.OutputPath(name, suffix);
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < started)
                problems.Add($"{name}: no fresh {suffix} build at {file}");
            else
                report.AppendLine($"{name}_{suffix}: {new FileInfo(file).Length / 1048576f:F2} MB");

            // The upload zip must hold exactly that bundle, byte for byte, under its own file name.
            string zipPath = BuildMapBundle.UploadZipPath(name, suffix);
            if (!File.Exists(zipPath) || File.GetLastWriteTimeUtc(zipPath) < started)
            {
                problems.Add($"{name}: no fresh {suffix} upload zip at {zipPath}");
                continue;
            }
            using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.Count == 1 ? zip.Entries[0] : null;
                if (entry == null || entry.FullName != Path.GetFileName(file) || entry.Length != new FileInfo(file).Length)
                    problems.Add($"{name}: {Path.GetFileName(zipPath)} should hold only {Path.GetFileName(file)}; it holds "
                        + string.Join(", ", zip.Entries.Select(e => $"{e.FullName} ({e.Length} bytes)")));
            }
        }

        // Round-trip the Windows file: the map loads, and every tag survived.
        AssetBundle.UnloadAllAssetBundles(true);
        var bundle = AssetBundle.LoadFromFile(BuildMapBundle.OutputPath(name, "windows"));
        if (bundle == null) { problems.Add($"{name}_windows could not be loaded"); return; }
        try
        {
            string prefabPath = source.assetNames.First(p => p.EndsWith(".prefab") && p.Contains("/ReferenceMaps/"));
            var built = bundle.LoadAsset<GameObject>(prefabPath);
            var original = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (built == null) { problems.Add($"{name}_windows has no {prefabPath}"); return; }

            var a = TagCensus(built);
            var b = TagCensus(original);
            var mismatched = a.Keys.Union(b.Keys)
                .Where(k => (a.TryGetValue(k, out int x) ? x : 0) != (b.TryGetValue(k, out int y) ? y : 0)).ToList();
            if (mismatched.Count > 0) problems.Add($"{name}_windows: tag counts differ from the source prefab for {string.Join(", ", mismatched)}");
            else report.AppendLine($"{name}_windows: loads, tags identical to the source prefab ({a.Count} distinct)");
        }
        finally
        {
            bundle.Unload(true);
        }
    }

    static Dictionary<string, int> TagCensus(GameObject root)
    {
        var d = new Dictionary<string, int>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            d.TryGetValue(t.tag, out int c);
            d[t.tag] = c + 1;
        }
        return d;
    }
}
