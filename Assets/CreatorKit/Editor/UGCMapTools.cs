using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// Ships with the Snapshot VR Creator Kit.
//
//   Snapshot > UGC > Center Map     recentre a map on the origin
//
// Validate Map checks the size limit (on the floor players can reach) and says when a map that
// fails it is only off-centre. This is the fix for that case, which is the most common one.
//
// Self-contained on purpose - no dependency on any game runtime code - same boundary
// UGCNavMeshPreview.cs keeps.
public static class UGCMapTools
{
    // ---------------------------------------------------------------- center

    [MenuItem("Snapshot/UGC/Center Map", false, 22)]
    static void CenterMap()
    {
        var roots = Selection.gameObjects;
        if (roots == null || roots.Length != 1)
        {
            Debug.LogWarning("[UGC Center] Select exactly one map root in the Hierarchy, then run this again.");
            return;
        }
        var root = roots[0];

        Bounds b = default;
        bool have = false;
        foreach (var c in root.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger || c.bounds.size == Vector3.zero) continue;
            if (!have) { b = c.bounds; have = true; } else b.Encapsulate(c.bounds);
        }
        if (!have)
        {
            Debug.LogWarning("[UGC Center] No non-trigger colliders found under the selection - nothing to centre on.");
            return;
        }

        // X/Z only. Y is deliberately left alone: floor level at y=0 is a convention creators build
        // to, and the Y budget (-5..+22) is asymmetric precisely because down is special - idle
        // bodies park at y=-6. Silently lifting a map's floor would be a nasty surprise.
        var offset = new Vector3(b.center.x, 0f, b.center.z);
        if (offset.magnitude < 0.01f)
        {
            Debug.Log($"[UGC Center] Already centred - collider bounds centre is ({b.center.x:F2}, {b.center.z:F2}). Nothing to do.");
            return;
        }

        // Shift the root's direct CHILDREN, leaving the root itself at its own transform. Moving
        // the root instead would bake a non-zero position into the map prefab, and the loader
        // instantiates that prefab - relying on its root offset surviving is a bet not worth making.
        var children = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in root.transform) children.Add(child);

        Undo.RecordObjects(children.ConvertAll(c => (Object)c).ToArray(), "Center Map");
        foreach (var child in children)
            child.position -= offset;

        Debug.Log($"[UGC Center] Shifted {children.Count} top-level child object(s) by ({-offset.x:F2}, 0, {-offset.z:F2}).\n"
            + $"  collider bounds were X {b.min.x:F1}..{b.max.x:F1}  Z {b.min.z:F1}..{b.max.z:F1}\n"
            + $"  now                  X {b.min.x - offset.x:F1}..{b.max.x - offset.x:F1}  Z {b.min.z - offset.z:F1}..{b.max.z - offset.z:F1}\n"
            + "  Undo restores it. Re-run Snapshot > UGC > Validate Map to confirm.");
    }
}
