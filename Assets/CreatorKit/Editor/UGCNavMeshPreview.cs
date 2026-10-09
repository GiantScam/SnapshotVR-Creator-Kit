using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;

// Ships with the Snapshot VR Creator Kit. Lets a creator spot-check, inside their own project,
// exactly what the live game will bake as walkable when their map loads for real - without
// memorizing the slope/step-height numbers from the creator guide or waiting for a full
// AssetBundle round-trip through Play Mode.
//
// Deliberately self-contained rather than depending on any game runtime code: this
// reimplements only the markup RULE itself - opt-out via UGCNoWalk/NoWalk, the UGC area tags into
// their named areas, blanket-walkable everything else. Keep this file self-contained; that boundary is exactly what the Creator
// Kit exists to enforce.
public static class UGCNavMeshPreview
{
    const string SurfaceName = "UGC NavMesh Preview";
    const int NotWalkableArea = 1; // matches "Not Walkable" in ProjectSettings/NavMeshAreas.asset
    static readonly string[] NoWalkTags = { "UGCNoWalk", "NoWalk" };

    // The live game bakes each of these tags into its own named area, and a spawn point only
    // accepts a landing in the area it was placed in. Without them here, the preview could not show
    // whether an area tag actually covers the floor under a spawn.
    static readonly (string tag, string areaName)[] AreaTags =
    {
        ("UGCPlayingAreaBlue", "UGC Playing Area Blue"),
        ("UGCPlayingAreaPink", "UGC Playing Area Pink"),
        ("UGCSpectatorArea", "UGC Spectator Area"),
        ("UGCSpectatorAreaBlue", "UGC Spectator Area Blue"),
        ("UGCSpectatorAreaPink", "UGC Spectator Area Pink"),
    };

    [MenuItem("Snapshot/UGC/Preview NavMesh", false, 21)]
    static void Preview()
    {
        var roots = Selection.gameObjects;
        if (roots == null || roots.Length == 0)
        {
            Debug.LogWarning("[UGC NavMesh Preview] Select your map's root object(s) in the Hierarchy first, then run this again.");
            return;
        }

        var surface = FindOrCreateSurface();

        // Track exactly the NavMeshModifiers this run adds, so they can be removed again after
        // the bake. Without this, a creator who saves their scene after previewing ships nav
        // components in their map - the baked NavMeshData itself is unaffected by removing them,
        // since the NavMeshSurface owns that data independently of the modifiers that fed it.
        var added = new List<NavMeshModifier>();
        ApplyMarkup(roots, added);
        ValidateCollectable(roots, surface);

        surface.BuildNavMesh();

        foreach (var modifier in added)
            if (modifier != null)
                Object.DestroyImmediate(modifier);

        Selection.activeGameObject = surface.gameObject;

        Debug.Log("[UGC NavMesh Preview] Bake complete. If you don't see it drawn in the Scene view, "
            + "open the \"AI Navigation\" overlay (Overlays menu on the Scene view toolbar), expand "
            + "Surfaces, and enable \"Show NavMesh\" - it's off by default even with the overlay visible.");
    }

    static NavMeshSurface FindOrCreateSurface()
    {
        GameObject go = GameObject.Find(SurfaceName);
        NavMeshSurface reuse = null;
        if (go != null)
            reuse = go.GetComponent<NavMeshSurface>();
        if (reuse != null)
            return reuse;

        if (go == null)
            go = new GameObject(SurfaceName);
        go.tag = "EditorOnly"; // this GameObject is a preview aid, never meant to ship in a bundle

        var surface = go.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.MarkedWithModifier; // matches the live game's surface
        surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders; // matches the live game's surface
        surface.defaultArea = 0; // "Walkable"
        surface.agentTypeID = 0; // "Humanoid" - the only agent type this project defines
        surface.layerMask = LayerMask.GetMask("Default", "TransparentFX"); // matches the live game's surface
        return surface;
    }

    // Mirrors the live game's own markup rule: opt-out first so a creator's own exclusion tag
    // survives the blanket include below, then mark every selected root walkable unless it
    // already carries a hand-authored NavMeshModifier. Keep in sync with the game's own markup
    // pass - see the Creator Kit README for the maintainer-facing cross-reference.
    static void ApplyMarkup(GameObject[] roots, List<NavMeshModifier> added)
    {
        foreach (var tag in NoWalkTags)
        {
            GameObject[] tagged;
            try { tagged = GameObject.FindGameObjectsWithTag(tag); }
            catch (UnityException) { continue; } // tag not defined in this project - nothing to mark

            foreach (var go in tagged)
                Mark(go, overrideArea: true, area: NotWalkableArea, ignoreFromBuild: false, added);
        }

        // Same order as the live game: area tags after the opt-out, before the blanket include.
        foreach (var (tag, areaName) in AreaTags)
        {
            int area = UnityEngine.AI.NavMesh.GetAreaFromName(areaName);
            if (area < 0) continue; // area not defined in this project's NavMeshAreas - nothing to mark
            GameObject[] tagged;
            try { tagged = GameObject.FindGameObjectsWithTag(tag); }
            catch (UnityException) { continue; }

            foreach (var go in tagged)
                Mark(go, overrideArea: true, area: area, ignoreFromBuild: false, added);
        }

        foreach (var root in roots)
        {
            if (root == null || root.GetComponent<NavMeshModifier>() != null)
                continue;
            Mark(root, overrideArea: false, area: 0, ignoreFromBuild: false, added);
        }
    }

    static void Mark(GameObject go, bool overrideArea, int area, bool ignoreFromBuild, List<NavMeshModifier> added)
    {
        var modifier = go.GetComponent<NavMeshModifier>();
        bool isNew = modifier == null;
        if (isNew)
            modifier = go.AddComponent<NavMeshModifier>();

        modifier.overrideArea = overrideArea;
        modifier.area = area;
        modifier.ignoreFromBuild = ignoreFromBuild;
        modifier.applyToChildren = true;

        // Only track components THIS run added - a pre-existing modifier a creator authored by
        // hand must survive the cleanup pass in Preview().
        if (isNew)
            added.Add(modifier);
    }

    // Bakes one map prefab on its own, with the same rule and settings as Preview, and returns the
    // walkable area in square metres (summed over the baked triangles, area tags included).
    // Nothing else in the open scenes takes part: the surface collects only the map's own colliders,
    // the bake is moved 2 km away so it can't overlap a navmesh some open scene already has (an
    // overlap there skewed the first measurements by ~25 m²), and whatever was loaded beforehand is
    // measured first and subtracted. Navmesh tiles are laid out in world space, so a bake out there
    // differs from one at the origin by a fraction of a percent: compare these figures only with
    // other figures from this method. Leaves nothing behind.
    // Used by the kit's maintenance checks to confirm a map bakes the same here as where it came from.
    public static float MeasureWalkableArea(GameObject prefab, out int triangles)
    {
        var baseline = UnityEngine.AI.NavMesh.CalculateTriangulation();
        float baselineArea = Area(baseline);

        var inst = UGCMapValidator.InstantiateForValidation(prefab, out var temp);
        var surfaceGo = new GameObject(SurfaceName + " (measure)");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(surfaceGo, inst.scene);
        var added = new List<NavMeshModifier>();
        try
        {
            inst.transform.SetParent(surfaceGo.transform, true);
            surfaceGo.transform.position = MeasureOffset;

            var surface = surfaceGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;   // this map only, whatever else is open
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.defaultArea = 0;
            surface.agentTypeID = 0;
            surface.layerMask = LayerMask.GetMask("Default", "TransparentFX");

            // The markup rule of ApplyMarkup, applied to this map's objects only.
            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
            {
                if (System.Array.IndexOf(NoWalkTags, t.tag) >= 0)
                    Mark(t.gameObject, overrideArea: true, area: NotWalkableArea, ignoreFromBuild: false, added);
            }
            foreach (var (tag, areaName) in AreaTags)
            {
                int area = UnityEngine.AI.NavMesh.GetAreaFromName(areaName);
                if (area < 0) continue;
                foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                    if (t.CompareTag(tag)) Mark(t.gameObject, overrideArea: true, area: area, ignoreFromBuild: false, added);
            }

            surface.BuildNavMesh();
            var after = UnityEngine.AI.NavMesh.CalculateTriangulation();
            triangles = after.indices.Length / 3 - baseline.indices.Length / 3;
            float result = Area(after) - baselineArea;
            surface.RemoveData();
            return result;
        }
        finally
        {
            Object.DestroyImmediate(surfaceGo);   // takes the map instance and its modifiers with it
            if (temp.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(temp, true);
        }
    }

    static readonly Vector3 MeasureOffset = new Vector3(2000f, 0f, 2000f);

    // Bakes one map where it stands - a scene object, or the root of an open prefab - with the
    // same rule and settings as Preview, and returns that bake's triangles alone. Used by Validate
    // Map, which needs to know what is under each of the map's own markers, so the map cannot be
    // moved away for the bake the way MeasureWalkableArea moves it. Instead everything else is
    // kept out of it: every other navmesh surface and every modifier outside this map is switched
    // off for the duration, and anything that still has a navmesh loaded is measured first and
    // taken off the front of the result. Leaves nothing behind.
    public static UnityEngine.AI.NavMeshTriangulation BakeInPlace(GameObject root)
    {
        var otherSurfaces = new List<NavMeshSurface>();
        foreach (var s in Object.FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None))
            if (s.enabled) { s.enabled = false; otherSurfaces.Add(s); }
        var otherModifiers = new List<NavMeshModifier>();
        foreach (var m in Object.FindObjectsByType<NavMeshModifier>(FindObjectsSortMode.None))
            if (m.enabled && !m.transform.IsChildOf(root.transform)) { m.enabled = false; otherModifiers.Add(m); }

        var baseline = UnityEngine.AI.NavMesh.CalculateTriangulation();
        var surfaceGo = new GameObject(SurfaceName + " (validate)") { hideFlags = HideFlags.DontSave };
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.scene == root.scene) UnityEditor.SceneManagement.StageUtility.PlaceGameObjectInCurrentStage(surfaceGo);
        else if (root.scene.IsValid()) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(surfaceGo, root.scene);

        var added = new List<NavMeshModifier>();
        try
        {
            var surface = surfaceGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.MarkedWithModifier;
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.defaultArea = 0;
            surface.agentTypeID = 0;
            surface.layerMask = LayerMask.GetMask("Default", "TransparentFX");

            // The markup rule of ApplyMarkup, applied to this map's objects only.
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
                if (System.Array.IndexOf(NoWalkTags, t.tag) >= 0)
                    Mark(t.gameObject, overrideArea: true, area: NotWalkableArea, ignoreFromBuild: false, added);
            foreach (var (tag, areaName) in AreaTags)
            {
                int area = UnityEngine.AI.NavMesh.GetAreaFromName(areaName);
                if (area < 0) continue;
                foreach (var t in transforms)
                    if (t.CompareTag(tag)) Mark(t.gameObject, overrideArea: true, area: area, ignoreFromBuild: false, added);
            }
            if (root.GetComponent<NavMeshModifier>() == null)
                Mark(root, overrideArea: false, area: 0, ignoreFromBuild: false, added);

            surface.BuildNavMesh();
            var after = UnityEngine.AI.NavMesh.CalculateTriangulation();
            surface.RemoveData();

            int v0 = baseline.vertices.Length, i0 = baseline.indices.Length, a0 = baseline.areas.Length;
            if (i0 == 0 || after.indices.Length < i0 || after.vertices.Length < v0) return after;
            var mine = new UnityEngine.AI.NavMeshTriangulation
            {
                vertices = new Vector3[after.vertices.Length - v0],
                indices = new int[after.indices.Length - i0],
                areas = new int[after.areas.Length - a0],
            };
            System.Array.Copy(after.vertices, v0, mine.vertices, 0, mine.vertices.Length);
            System.Array.Copy(after.areas, a0, mine.areas, 0, mine.areas.Length);
            for (int i = 0; i < mine.indices.Length; i++) mine.indices[i] = after.indices[i0 + i] - v0;
            return mine;
        }
        finally
        {
            foreach (var modifier in added)
                if (modifier != null) Object.DestroyImmediate(modifier);
            Object.DestroyImmediate(surfaceGo);
            foreach (var m in otherModifiers) if (m != null) m.enabled = true;
            foreach (var s in otherSurfaces) if (s != null) s.enabled = true;
        }
    }

    static float Area(UnityEngine.AI.NavMeshTriangulation tri)
    {
        double sum = 0;
        for (int i = 0; i + 2 < tri.indices.Length; i += 3)
        {
            var a = tri.vertices[tri.indices[i]];
            var b = tri.vertices[tri.indices[i + 1]];
            var c = tri.vertices[tri.indices[i + 2]];
            sum += Vector3.Cross(b - a, c - a).magnitude * 0.5;
        }
        return (float)sum;
    }

    // Same two silent failure modes the live game's own validation catches - a collider on the
    // wrong layer, or no colliders at all - surfaced here as a console warning instead of
    // telemetry, since there is no live game session to report back to.
    static void ValidateCollectable(GameObject[] roots, NavMeshSurface surface)
    {
        int mask = surface.layerMask.value;
        var offLayer = new List<string>();
        int colliderCount = 0;

        foreach (var root in roots)
        {
            if (root == null) continue;
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger) continue;
                colliderCount++;
                if ((mask & (1 << collider.gameObject.layer)) == 0 && offLayer.Count < 20)
                    offLayer.Add(collider.name + " (layer " + LayerMask.LayerToName(collider.gameObject.layer) + ")");
            }
        }

        if (offLayer.Count > 0)
            Debug.LogWarning("[UGC NavMesh Preview] " + offLayer.Count + " collider(s) sit on layers outside Default/TransparentFX and will NOT generate navmesh in the real game: " + string.Join(", ", offLayer));

        if (colliderCount == 0)
            Debug.LogWarning("[UGC NavMesh Preview] No non-trigger colliders found under the selected object(s). The real game bakes navmesh from colliders only - this map would load with an empty, unplayable navmesh.");
    }
}
