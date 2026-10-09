using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Ships with the Snapshot VR Creator Kit. Snapshot > UGC > Map Bundles...
//
// For iterating on one map among several: lists every map bundle in the project (and the kit's
// reference maps), lets you choose which platforms each one builds for, and builds only the maps
// you ask for. Windows alone is enough to test a map in the PC game; build all three before you
// publish. The choices are remembered per map, per project.
//
// All building goes through BuildMapBundle.BuildMaps, the same code a command line reaches through
// BuildMapBundle.Build and BuildReferenceMaps, so a map built here is the same file those produce.
public sealed class MapBundlesWindow : EditorWindow
{
    sealed class Row
    {
        public string Name;
        public bool IsReference;
        public AssetBundleBuild Bundle;
        public string Problem;      // why this map would load wrong, if anything
    }

    List<Row> _maps = new List<Row>();
    List<Row> _referenceMaps = new List<Row>();
    bool _showReference;
    Vector2 _scroll;

    [MenuItem("Snapshot/UGC/Map Bundles…", false, 40)]
    public static void Open()
    {
        var w = GetWindow<MapBundlesWindow>(false, "Map Bundles", true);
        w.minSize = new Vector2(620, 300);
        w.Rescan();
    }

    void OnFocus() => Rescan();

    void Rescan()
    {
        _maps = AssetDatabase.GetAllAssetBundleNames()
            .Select(n => new Row { Name = n, Bundle = BuildMapBundle.ProjectBundle(n) })
            .ToList();
        foreach (var row in _maps) row.Problem = FindProblem(row.Bundle);

        _referenceMaps = (BuildMapBundle.LoadReferenceMapBuilds(BuildMapBundle.ReferenceMapListPath, quiet: true)
                ?? new AssetBundleBuild[0])
            .Select(b => new Row { Name = b.assetBundleName, Bundle = b, IsReference = true })
            .ToList();
        Repaint();
    }

    // The two mistakes that make a map load wrong without any error: nothing in it, or an asset it
    // uses that carries another bundle's name (see BuildMapBundle.BuildMaps).
    static string FindProblem(AssetBundleBuild bundle)
    {
        if (bundle.assetNames == null || bundle.assetNames.Length == 0)
            return "No assets carry this bundle name.";
        // Prefabs the game will place by themselves: those no other prefab in the bundle contains.
        var prefabs = BuildMapBundle.TopLevelAssets(bundle.assetNames)
            .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefabs.Count == 0)
            return "No prefab carries this bundle name, so there is no map in it.";
        if (prefabs.Count > 1)
            return $"{prefabs.Count} separate prefabs will each be placed at their own origin when this map loads: "
                + $"{string.Join(", ", prefabs.Take(5).Select(Path.GetFileNameWithoutExtension))}{(prefabs.Count > 5 ? ", …" : "")}. "
                + "Fine if the map is split across them on purpose; otherwise clear the bundle name on the ones that aren't the map.";

        var foreign = AssetDatabase.GetDependencies(bundle.assetNames, true)
            .Select(d => (path: d, name: AssetImporter.GetAtPath(d)?.assetBundleName))
            .Where(d => !string.IsNullOrEmpty(d.name) && d.name != bundle.assetBundleName)
            .ToList();
        if (foreign.Count == 0) return null;
        return $"Uses {foreign.Count} asset(s) assigned to another bundle ({string.Join(", ", foreign.Select(f => f.name).Distinct())}), "
            + $"e.g. {Path.GetFileName(foreign[0].path)}. They will be missing in game. Clear their bundle name.";
    }

    // ---------------------------------------------------------------- remembered choices

    static string PrefKey(Row row, string what) =>
        $"SnapshotVRKit.MapBundles.{PlayerSettings.productGUID}.{(row.IsReference ? "ref." : "")}{row.Name}.{what}";

    static bool GetPlatform(Row row, string suffix) =>
        EditorPrefs.GetBool(PrefKey(row, suffix), suffix == "windows");   // new maps default to Windows only

    static void SetPlatform(Row row, string suffix, bool on) => EditorPrefs.SetBool(PrefKey(row, suffix), on);

    static bool GetChecked(Row row) => EditorPrefs.GetBool(PrefKey(row, "checked"), !row.IsReference);
    static void SetChecked(Row row, bool on) => EditorPrefs.SetBool(PrefKey(row, "checked"), on);

    static string[] Selected(Row row) => BuildMapBundle.AllSuffixes.Where(s => GetPlatform(row, s)).ToArray();

    // ---------------------------------------------------------------- GUI

    void OnGUI()
    {
        if (!BuildMapBundle.IsKitProject)
        {
            EditorGUILayout.HelpBox("This window works only inside the Snapshot VR Creator Kit project.", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField("Build only the maps you are working on, for the platforms you need.", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("Windows alone is enough to test in the PC game. Publishing needs all three.", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Your maps", EditorStyles.boldLabel);
        if (_maps.Count == 0)
            EditorGUILayout.HelpBox("No AssetBundle names in this project yet. Select your map's root prefab, set an "
                + "AssetBundle name at the bottom of the Inspector, and give its skybox material the same name.", MessageType.Info);
        foreach (var row in _maps) DrawRow(row);

        EditorGUILayout.Space();
        _showReference = EditorGUILayout.Foldout(_showReference, $"Reference maps ({_referenceMaps.Count})", true);
        if (_showReference)
            foreach (var row in _referenceMaps) DrawRow(row);

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();
        var checkedRows = _maps.Concat(_referenceMaps).Where(GetChecked).ToList();
        int fileCount = checkedRows.Sum(r => Selected(r).Length);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(fileCount == 0))
                if (GUILayout.Button($"Build checked maps ({checkedRows.Count} map(s), {fileCount} file(s))", GUILayout.Height(28)))
                    Build(checkedRows);
            if (GUILayout.Button("Refresh", GUILayout.Width(70), GUILayout.Height(28))) Rescan();
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(_maps.Count == 0))
                if (GUILayout.Button("Build all my maps, all platforms")) Defer(BuildMapBundle.Build);
            using (new EditorGUI.DisabledScope(_referenceMaps.Count == 0))
                if (GUILayout.Button("Build reference maps, all platforms")) Defer(BuildMapBundle.BuildReferenceMaps);
        }
    }

    void DrawRow(Row row)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                SetChecked(row, EditorGUILayout.ToggleLeft(row.Name, GetChecked(row), EditorStyles.boldLabel, GUILayout.Width(220)));

                foreach (var p in BuildMapBundle.Platforms)
                {
                    bool installed = BuildPipeline.IsBuildTargetSupported(p.group, p.target);
                    var label = new GUIContent(p.suffix, installed
                        ? $"Build {row.Name} for {p.suffix}"
                        : $"{p.module} is not installed (Unity Hub > Installs > Add Modules)");
                    using (new EditorGUI.DisabledScope(!installed))
                        SetPlatform(row, p.suffix, EditorGUILayout.ToggleLeft(label, GetPlatform(row, p.suffix), GUILayout.Width(80)));
                }

                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(Selected(row).Length == 0))
                    if (GUILayout.Button("Build", GUILayout.Width(60))) Build(new[] { row });

                // The zips to upload to mod.io (the built bundles themselves are in StreamingAssets).
                var uploads = BuildMapBundle.UploadsFolder(row.Name);
                using (new EditorGUI.DisabledScope(!Directory.Exists(uploads)))
                    if (GUILayout.Button(new GUIContent("Uploads", "Open the folder with this map's mod.io upload zips"), GUILayout.Width(66)))
                        EditorUtility.RevealInFinder(Directory.GetFiles(uploads).FirstOrDefault() ?? uploads);
            }

            EditorGUILayout.LabelField("Last built:  " + string.Join("     ", BuildMapBundle.AllSuffixes.Select(s =>
            {
                var file = BuildMapBundle.UploadZipPath(row.Name, s);
                return s + " " + (File.Exists(file) ? File.GetLastWriteTime(file).ToString("MMM d HH:mm") : "-");
            })), EditorStyles.miniLabel);

            if (row.Problem != null)
                EditorGUILayout.HelpBox(row.Problem, MessageType.Warning);
        }
    }

    void Build(IEnumerable<Row> rows)
    {
        var maps = rows.Select(r => new BuildMapBundle.MapBuild { Bundle = r.Bundle, Suffixes = Selected(r) }).ToList();
        Defer(() => BuildMapBundle.BuildMaps(maps));
    }

    // A build switches platforms and reimports, which must not happen in the middle of drawing
    // this window: run it once the GUI pass has finished, then refresh the timestamps.
    void Defer(Action build)
    {
        EditorApplication.delayCall += () =>
        {
            build();
            if (this != null) Rescan();
        };
        GUIUtility.ExitGUI();
    }
}
