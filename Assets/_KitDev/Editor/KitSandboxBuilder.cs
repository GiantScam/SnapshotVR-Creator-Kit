using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Kit maintenance only - lives under _KitDev, which is left out of release zips.
//
// Builds Assets/Scenes/KitSandbox.unity, the scene a creator opens first: a camera, a
// directional light, the default skybox, the walkable-limit cage and the reference avatar.
// The cage and avatar are kit content, so they are only added once Assets/CreatorKit exists;
// re-run this after the first export to pick them up.
//
// Batch mode:
//   Unity -batchmode -quit -projectPath <kit> -executeMethod KitSandboxBuilder.Build
public static class KitSandboxBuilder
{
    const string LogPrefix = "[KitSandbox]";
    const string ScenePath = "Assets/Scenes/KitSandbox.unity";

    static readonly string[] ReferencePrefabs =
    {
        "Assets/CreatorKit/Placeholders/walkable_limit_reference.prefab",
        "Assets/CreatorKit/Placeholders/reference_avatar.prefab",
    };

    [MenuItem("Snapshot/UGC/Kit Dev/Rebuild Sandbox Scene", false, 910)]
    public static void BuildMenuItem()
    {
        if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Rebuild Sandbox Scene",
                $"{ScenePath} already exists and will be replaced.", "Replace", "Cancel"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
    }

    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        int placed = 0;
        foreach (var path in ReferencePrefabs)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"{LogPrefix} {path} not found - left out. Re-run after the kit content is exported.");
                continue;
            }
            PrefabUtility.InstantiatePrefab(prefab, scene);
            placed++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"{LogPrefix} wrote {ScenePath} with {placed}/{ReferencePrefabs.Length} reference prefab(s).");
    }
}
