using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Ships with the Snapshot VR Creator Kit.
//
//   Snapshot > UGC > Validate Map     the whole map: static checks, then a bake
//
// Almost every way a map can be wrong in this pipeline is SILENT: no error, no warning, nothing
// red. The map loads, renders, and is quietly broken. This runs the whole static assertion suite in
// one go, against a finished map, so those failures are found in seconds instead of by a playtester.
//
// Deliberately usable on ANY map at ANY time - a hand-edited prefab, a generated one, or whatever is
// open in a prefab stage right now. That matters: the checks originally lived inside the map
// generators, which meant you only got them if you rebuilt from a script, and a level designer
// nudging cover around by hand got nothing.
//
// Geometry and metadata come first. Then it bakes the walkable area with the live game's exact
// settings (UGCWalkableAnalysis.cs) and checks what only a bake can show: that every spawn stands
// on floor, that spectators cannot walk, be set down or shoot onto the playing area, and that the
// floor players can reach is inside the size limit. To SEE the walkable area, use
//
//   Snapshot > UGC > Preview NavMesh
//
// Self-contained: no gameplay code, no networking, nothing outside this folder.
//
// Validate()/ValidatePrefabAsset() are the callable entry points other tooling (the exporter's
// gate, this map's own menu item) builds on - both return the same Finding list the menu item logs.
public static class UGCMapValidator
{
    public enum Severity { Info, Warning, Error }

    public sealed class Finding
    {
        public Severity Level;
        public string Message;
        public string ObjectPath;
    }

    // Must stay in sync with the position budget the game enforces at load.
    const float LimitXZ = 70f;
    const float MaxY = 22f;
    const float MinY = -5f;

    // The layout system's own footprint. On a map that hosts layouts, this rectangle is
    // reserved: ~30 bunkers and both start boxes are dropped into it at load, on top of whatever
    // you left there.
    const float LayoutHalfX = 20f;
    const float LayoutHalfZ = 25f;
    const float LayoutClearHeight = 4f;
    const float LayoutEdgeTolerance = 0.5f;

    const float ColliderOversizeTolerance = 1.10f;
    const float OverheadClearance = 2.5f;

    // Where the game holds a newly connected player until it places them: 6 m below the origin.
    // Their head sits up to ~2 m above it and a few metres to the side (wherever they stand in
    // their room), so the whole volume must be empty.
    static readonly Vector3 HoldingPosition = new Vector3(0f, -6f, 0f);
    static readonly Vector3 HoldingClearSize = new Vector3(8f, 5f, 8f);

    // Room and team sizes the game enforces. Every spawn set has to seat the people sent to it.
    const int RoomSize = 10;
    const int TeamSize = 6;
    // How far a joining player may land from the nearest pedestal before the lobby reads as broken.
    const float LobbyPedestalReach = 20f;

    // Footprints of what the game swaps in, in the marker's local space (the marker's scale applies).
    // Used to catch a swapped object that will cut through the map around it.
    static readonly (string tag, Vector3 center, Vector3 size)[] SwappedFootprints =
    {
        ("UGCAdScreenPlaceholder", new Vector3(0f, 0f, 0f), new Vector3(6f, 2f, 0.04f)),
        ("UGCStartBoxBlue", new Vector3(0f, 0.59f, -0.35f), new Vector3(5.4f, 0.94f, 1.7f)),
        ("UGCStartBoxPink", new Vector3(0f, 0.59f, -0.35f), new Vector3(5.4f, 0.94f, 1.7f)),
        ("UGCMirrorPlaceholder", new Vector3(0f, 1.59f, 0f), new Vector3(2f, 3f, 0.04f)),
    };

    // The tags the game REPLACES with a real object. Markup tags (UGCNoWalk, UGCLayoutOrigin,
    // UGCNoLayoutSupport, the area tags) are read in place and are not in this list - they nest
    // harmlessly, and treating them as swappable produces false alarms.
    static readonly string[] SwappedTags =
    {
        "UGCSpawnPointBlue", "UGCSpawnPointPink", "UGCSpawnPointSpectator",
        "UGCSpawnPointSpectatorBlue", "UGCSpawnPointSpectatorPink",
        "UGCPedestalPlaceholder", "UGCAdScreenPlaceholder", "UGCGunCalibrationPlaceholder",
        "UGCPreviewTablePlaceholder", "UGCConcedeButtonBlue", "UGCConcedeButtonPink",
        "UGCStartBoxBlue", "UGCStartBoxPink", "UGCKillTeamButtonBlue", "UGCKillTeamButtonPink",
        "UGCMirrorPlaceholder",
    };

    static readonly string[] PlayingSpawnTags = { "UGCSpawnPointBlue", "UGCSpawnPointPink" };
    static readonly string[] SpectatorSpawnTags =
    { "UGCSpawnPointSpectator", "UGCSpawnPointSpectatorBlue", "UGCSpawnPointSpectatorPink" };

    [MenuItem("Snapshot/UGC/Validate Map", false, 20)]
    static void ValidateMenu()
    {
        // Collider.bounds is (0,0,0) on a prefab ASSET - physics only computes bounds for colliders
        // that are in a scene, and it returns that silently. Every size check below would compare
        // zeros and pass trivially. So whatever we are handed, validate a LIVE object.
        var target = ResolveTarget(out var temp, out var throwaway, out string label);
        if (target == null)
        {
            Debug.LogWarning("[UGC Validate] Open a map prefab, or select one in the Project window "
                + "(or its root in the Hierarchy), then run this again.");
            return;
        }

        try
        {
            var findings = Validate(target);
            LogFindings(target, label, findings);
        }
        finally
        {
            if (throwaway != null) Object.DestroyImmediate(throwaway);
            if (temp.IsValid()) EditorSceneManager.CloseScene(temp, true);
        }
    }

    // Puts a throwaway instance of a prefab asset somewhere physics is real. Normally that is a
    // temporary additive scene (returned in `temp`, for the caller to close). Unity refuses to add
    // a scene while an untitled, never-saved scene is open - the state right after File > New
    // Scene - so in that case the instance goes into the open scene instead and `temp` stays
    // invalid. Either way the caller destroys the instance when it is done.
    public static GameObject InstantiateForValidation(GameObject prefab, out Scene temp)
    {
        temp = default;
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                return (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());

        temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        return (GameObject)PrefabUtility.InstantiatePrefab(prefab, temp);
    }

    static GameObject ResolveTarget(out Scene temp, out GameObject throwaway, out string label)
    {
        temp = default;
        throwaway = null;
        label = "";

        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
        {
            label = System.IO.Path.GetFileNameWithoutExtension(stage.assetPath) + " (open prefab stage)";
            return stage.prefabContentsRoot;
        }

        var sel = Selection.activeGameObject;
        if (sel == null) return null;

        if (sel.scene.IsValid())            // already a live object in a scene
        {
            label = sel.name + " (scene object)";
            return sel;
        }

        // A prefab asset: validate a throwaway instance, so physics is real.
        string path = AssetDatabase.GetAssetPath(sel);
        label = System.IO.Path.GetFileNameWithoutExtension(path) + " (prefab asset)";
        throwaway = InstantiateForValidation(sel, out temp);
        return throwaway;
    }

    // ---------------------------------------------------------------- public entry points

    // Runs the whole suite against a scene object or a prefab-stage root and returns every
    // finding. Does not log - callers that want console output call LogFindings (the menu item
    // does; so can a caller that just wants the console noise too).
    public static List<Finding> Validate(GameObject root)
    {
        return RunChecks(root);
    }

    // Validates a throwaway instance of the prefab at assetPath (see InstantiateForValidation),
    // cleans it up, and restores whatever scene was active before the call.
    public static List<Finding> ValidatePrefabAsset(string assetPath)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
        {
            return new List<Finding> {
                new Finding { Level = Severity.Error, Message = "Could not load a prefab at " + assetPath, ObjectPath = assetPath }
            };
        }

        var restoreScene = SceneManager.GetActiveScene();
        Scene temp = default;
        GameObject inst = null;
        try
        {
            inst = InstantiateForValidation(prefab, out temp);
            return RunChecks(inst);
        }
        finally
        {
            if (inst != null) Object.DestroyImmediate(inst);
            if (temp.IsValid()) EditorSceneManager.CloseScene(temp, true);
            if (restoreScene.IsValid() && restoreScene != temp) SceneManager.SetActiveScene(restoreScene);
        }
    }

    // ---------------------------------------------------------------- the suite

    static List<Finding> RunChecks(GameObject root)
    {
        var findings = new List<Finding>();
        void Fail(string msg, string objPath = null) => findings.Add(new Finding { Level = Severity.Error, Message = msg, ObjectPath = objPath ?? root.name });
        void Warn(string msg, string objPath = null) => findings.Add(new Finding { Level = Severity.Warning, Message = msg, ObjectPath = objPath ?? root.name });
        void Info(string msg) => findings.Add(new Finding { Level = Severity.Info, Message = msg, ObjectPath = root.name });

        var transforms = root.GetComponentsInChildren<Transform>(true);
        var colliders = root.GetComponentsInChildren<Collider>(true);
        var solid = colliders.Where(c => !c.isTrigger).ToArray();

        // --- 1. is there anything to walk on at all -------------------------------------
        if (solid.Length == 0)
            Fail("No non-trigger colliders anywhere. The walkable area is built from physics "
                + "colliders only, so this map would load completely unplayable while looking fine.");

        // --- 2. layers ------------------------------------------------------------------
        var badLayers = solid.Select(c => LayerMask.LayerToName(c.gameObject.layer))
            .Where(l => l != "Default" && l != "TransparentFX").Distinct().ToArray();
        if (badLayers.Length > 0)
            Fail("Non-trigger colliders on layers outside Default/TransparentFX: "
                + string.Join(", ", badLayers) + ". These generate no walkable area, silently.");

        // --- 2b. components and tags the game does not accept ------------------------------
        // The game removes these when the map loads, before anything in it runs. Nothing warns
        // you in the editor, and the map then behaves differently in a room than it did here.
        var removedAtLoad = new SortedDictionary<string, int>();
        int missingScripts = 0;
        foreach (var t in transforms)
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) { missingScripts++; continue; }
                if (IsAllowedComponent(c)) continue;
                string n = c.GetType().Name;
                removedAtLoad[n] = removedAtLoad.TryGetValue(n, out int count) ? count + 1 : 1;
            }
        if (removedAtLoad.Count > 0)
            Fail(removedAtLoad.Values.Sum() + " component(s) the game removes when the map loads: "
                + string.Join(", ", removedAtLoad.Select(kv => kv.Key + " x" + kv.Value).Take(12))
                + ". A map may carry Unity's own engine, UI and render-pipeline components only - no scripts, "
                + "no navigation components, and no camera, audio listener, video player or interactive UI "
                + "(buttons, toggles, sliders, raycasters). Delete them; the map should not depend on them.");
        if (missingScripts > 0)
            Fail(missingScripts + " missing script(s). A component whose script is not in this project "
                + "does nothing in the game either - remove it.");

        var ignoredTags = transforms.Select(t => t.gameObject.tag).Where(tg => !IsHonouredTag(tg))
            .GroupBy(tg => tg).Select(g => g.Key + " x" + g.Count()).ToArray();
        if (ignoredTags.Length > 0)
            Warn("Tags the game ignores: " + string.Join(", ", ignoredTags.Take(12)) + ". Only the UGC tags, Bunker "
                + "and NoWalk mean anything to the game; any other tag is cleared when the map loads. "
                + "For solid geometry of your own, use UGCSolid or UGCNoWalk.");

        // --- 3. colliders larger than the thing they represent ---------------------------
        // Invisible in the editor and in a screenshot: nothing overlaps, nothing warns, the walkable
        // area still builds. You find it in a headset when paint stops in mid-air. Compared PER
        // GAMEOBJECT so honest cases (a thin trunk collider under a wide canopy, on separate
        // objects) never trip it.
        var oversize = new List<string>();
        foreach (var t in transforms)
        {
            var col = t.GetComponent<Collider>();
            var rend = t.GetComponent<Renderer>();
            if (col == null || rend == null || col is MeshCollider) continue;
            Vector3 c = col.bounds.size, r = rend.bounds.size;
            float wx = r.x > 0.001f ? c.x / r.x : 1f;
            float wy = r.y > 0.001f ? c.y / r.y : 1f;
            float wz = r.z > 0.001f ? c.z / r.z : 1f;
            float worst = Mathf.Max(wx, Mathf.Max(wy, wz));
            if (worst > ColliderOversizeTolerance)
                oversize.Add(t.name + " " + worst.ToString("F2") + "x");
        }
        // A warning, not an error: someone placing objects by hand rarely produces this, and may
        // mean it when they do (a blocker with a small visual). A script that sizes colliders by
        // arithmetic produces it by the hundred, and that is who this is for.
        if (oversize.Count > 0)
            Warn(oversize.Count + " collider(s) are bigger than the geometry they represent - they "
                + "block shots against nothing a player can see. A SphereCollider scales by "
                + "max(|sx|,|sy|,|sz|) and a capsule can never be shorter than 2x its radius, so "
                + "neither wraps a squat shape; use a BoxCollider or a convex MeshCollider there. "
                + string.Join(", ", oversize.Take(12)));

        // --- 3b. a box round something round ----------------------------------------------
        // The check above cannot see this one: a BoxCollider round a cylinder or a ball has
        // exactly the same bounds as the mesh. Its corners still stand 41% of the radius outside
        // the surface, and paint that visibly clears the object stops there. Known for Unity's
        // own round primitives, which is where it is easy to tell.
        var boxed = transforms
            .Where(t => t.GetComponent<BoxCollider>() != null && !t.GetComponent<BoxCollider>().isTrigger)
            .Select(t => new { t, mesh = t.GetComponent<MeshFilter>() != null ? t.GetComponent<MeshFilter>().sharedMesh : null })
            .Where(x => x.mesh != null && (x.mesh.name == "Cylinder" || x.mesh.name == "Sphere" || x.mesh.name == "Capsule"))
            // Only where the corner is big enough to matter: 41% of the radius, 5 cm and up.
            .Where(x => Mathf.Min(Mathf.Abs(x.t.lossyScale.x), Mathf.Abs(x.t.lossyScale.z)) * 0.5f * 0.41f >= 0.05f)
            .Select(x => x.t.name).ToArray();
        if (boxed.Length > 0)
            Warn(boxed.Length + " round object(s) (Unity's Cylinder, Sphere or Capsule mesh) carry a BoxCollider. The box's corners "
                + "stand 41% of the radius outside the surface, so paint that visibly clears the object stops in mid-air "
                + "there. Use the collider that matches the shape - for a cylinder, a convex MeshCollider using "
                + "Assets/CreatorKit/Meshes/CylinderCollision.asset: "
                + string.Join(", ", boxed.Distinct().Take(10)) + (boxed.Distinct().Count() > 10 ? ", ..." : ""));

        // --- 4. position budget ----------------------------------------------------------
        // The budget limits where a player can STAND, not where the map may have geometry.
        // Scenery past it (a skyline, a treeline, a stadium frame) is fine, and so is a solid
        // object out there that nobody can get onto. Only the bake can tell the difference, so the
        // verdict is in section 14, measured on the floor players can reach.

        // --- 4b. the holding position under the map ---------------------------------------
        // A player who has just connected waits at (0, -6, 0) until the game places them. The
        // walkable limit (-5) keeps players from STANDING down there, but not geometry from
        // existing there. A solid object in that volume puts the waiting player's head inside it:
        // the screen blacks out, and it stays black, because a head inside geometry is never
        // allowed to see.
        var holdingHalf = HoldingClearSize / 2f;
        var inHolding = Physics.OverlapBox(HoldingPosition, holdingHalf, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)
            .Where(c => c.transform.IsChildOf(root.transform)).Select(c => c.name).Distinct().ToArray();
        if (inHolding.Length > 0)
            Fail(inHolding.Length + " collider(s) inside the space under the map where newly connected players wait (x/z +-"
                + holdingHalf.x + " m, y " + (HoldingPosition.y - holdingHalf.y) + " to " + (HoldingPosition.y + holdingHalf.y)
                + " m). A player whose head is inside geometry sees only black, so a joining player would be stuck "
                + "blind until they are placed - keep this volume empty: " + string.Join(", ", inHolding.Take(8)));

        // --- 5. mesh readability ---------------------------------------------------------
        // A MeshCollider feeding the live bake contributes nothing unless its mesh is readable, and
        // says nothing when it isn't - the classic "works in Play Mode, dead in a bundle".
        var unreadable = root.GetComponentsInChildren<MeshCollider>(true)
            .Where(mc => mc.sharedMesh == null || !mc.sharedMesh.isReadable)
            .Select(mc => mc.name).Distinct().ToArray();
        if (unreadable.Length > 0)
            Fail(unreadable.Length + " MeshCollider(s) have an unreadable mesh. Enable Read/Write "
                + "Enabled on the source model, or they contribute no walkable area and no collision "
                + "once built into a bundle: " + string.Join(", ", unreadable.Take(10)));

        // --- 6. duplicated swap tags ------------------------------------------------------
        var dupes = new List<string>();
        foreach (var t in transforms)
        {
            string tg = t.gameObject.tag;
            if (t.parent == null || !SwappedTags.Contains(tg)) continue;
            for (var a = t.parent; a != null; a = a.parent)
                if (a.gameObject.tag == tg) { dupes.Add(t.name + " (" + tg + ") under " + a.name); break; }
        }
        if (dupes.Count > 0)
            Fail(dupes.Count + " object(s) repeat a swap tag an ancestor already carries. The game "
                + "replaces every tagged object, so this builds a second copy inside the first - and "
                + "if you re-tagged the parent to the other team, the child keeps the original team: "
                + string.Join(", ", dupes.Take(10)));

        // --- 7. scoreboards ---------------------------------------------------------------
        foreach (var t in transforms.Where(t => t.gameObject.tag == "UGCScoreboardScreen"))
        {
            var r = t.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null)
                Fail("Scoreboard '" + t.name + "' has no renderer/material for the score to be drawn onto.", t.name);
            else if (!r.sharedMaterial.HasProperty("_Image"))
                Fail("Scoreboard '" + t.name + "' uses '" + r.sharedMaterial.shader.name + "', which has no "
                    + "_Image property. The live score is injected into _Image, so on any other material "
                    + "it is a silent no-op. Use UGCScoreboardScreen.mat.", t.name);
            else
                CheckScoreboardFace(root, t, r, Fail);
        }

        // --- 8. floor finishes hanging over a void ----------------------------------------
        // A renderer-only finish has no collider, so it is invisible to every other check here and
        // to the walkable-area preview. Lay one across a pit and you get a floor in mid-air, which
        // from underneath is a ceiling at head height.
        foreach (var t in transforms)
        {
            var r = t.GetComponent<Renderer>();
            if (r == null || t.GetComponent<Collider>() != null) continue;
            var b = r.bounds;
            if (b.size.y > 0.2f || b.size.x < 3f || b.size.z < 3f || b.min.y < 0.5f) continue;

            int floating = 0, total = 0;
            float worstDrop = 0f;
            for (float x = b.min.x + 0.5f; x < b.max.x; x += 1f)
            for (float z = b.min.z + 0.5f; z < b.max.z; z += 1f)
            {
                total++;
                // From WELL ABOVE, and only count support that is BELOW the finish. A ray started
                // just under the finish begins inside the deck holding it up and reports nothing;
                // a ray that merely "hit something" counts props standing on top as support.
                if (!RaycastMap(root, new Vector3(x, b.max.y + 20f, z), Vector3.down, 60f, out var hit)) continue;
                if (hit.point.y >= b.min.y - 0.35f) continue;
                floating++;
                worstDrop = Mathf.Max(worstDrop, b.min.y - hit.point.y);
            }
            if (floating > 0)
                Fail("'" + t.name + "' is a floor finish hanging over a void: " + floating + "/" + total
                    + " of it has nothing beneath, up to " + worstDrop.ToString("F2") + " m of clear air. "
                    + "From below this reads as a ceiling. Cut the finish to match the solid geometry under it.", t.name);
        }

        // --- 9. layout footprint / spawn inventory ----------------------------------------
        bool noLayout = transforms.Any(t => t.gameObject.tag == "UGCNoLayoutSupport");
        int playing = transforms.Count(t => PlayingSpawnTags.Contains(t.gameObject.tag));
        int spectator = transforms.Count(t => SpectatorSpawnTags.Contains(t.gameObject.tag));

        if (noLayout)
        {
            if (playing == 0)
                Fail("Tagged UGCNoLayoutSupport but places no team spawn points. Nothing else will "
                    + "place them, so players would have nowhere to spawn.");
        }
        else
        {
            if (playing > 0)
                Fail(playing + " team spawn point(s) on a map that hosts layouts. The layout system "
                    + "places its own; tag an object UGCNoLayoutSupport if you meant to supply them.");

            // Measured LayoutEdgeTolerance inside the rectangle, so a perimeter wall standing ON
            // the boundary line is not counted as standing in the footprint.
            float hx = LayoutHalfX - LayoutEdgeTolerance, hz = LayoutHalfZ - LayoutEdgeTolerance;
            var intruders = solid
                .Where(c => c.bounds.max.y > 0.05f && c.bounds.min.y < LayoutClearHeight
                         && c.bounds.min.x < hx && c.bounds.max.x > -hx
                         && c.bounds.min.z < hz && c.bounds.max.z > -hz)
                .Select(c => c.name).Distinct().ToArray();
            if (intruders.Length > 0)
                Fail(intruders.Length + " collider(s) stand inside the layout footprint (X +-" + LayoutHalfX
                    + ", Z +-" + LayoutHalfZ + "). The layout drops ~30 bunkers and both start boxes in "
                    + "there, straight through this geometry: " + string.Join(", ", intruders.Take(10)));
        }
        if (spectator == 0)
            Fail("No spectator spawn points. Every map has to place its own: without them, eliminated players are "
                + "left standing where they were hit, and joining players are dropped on a playing spawn.");

        // --- 9b. spawn capacity -------------------------------------------------------------
        // Where the game sends people. The LOBBY - where a joining player, a Spectator-team player
        // and the whole room at match end go - is the neutral UGCSpawnPointSpectator set if the map
        // has one, otherwise Blue's spectator spawns. A dead or between-rounds player goes to their
        // own team's spectator spawns (the team's pit).
        int Count(string tg) => transforms.Count(t => t.gameObject.tag == tg);
        int blueSpec = Count("UGCSpawnPointSpectatorBlue"), pinkSpec = Count("UGCSpawnPointSpectatorPink"), neutralSpec = Count("UGCSpawnPointSpectator");
        bool neutralLobby = neutralSpec > 0;
        string lobbyTag = neutralLobby ? "UGCSpawnPointSpectator" : "UGCSpawnPointSpectatorBlue";
        int lobby = neutralLobby ? neutralSpec : blueSpec;
        if (spectator > 0 && lobby < RoomSize)
            Fail("The lobby seats " + lobby + " but a room holds " + RoomSize + ". Joining players, Spectator-team players and "
                + "everyone at match end land on " + lobbyTag + (neutralLobby ? "" : " (the map has no neutral spectator spawns, "
                + "so Blue's are the lobby)") + " - place at least " + RoomSize + " of them, together, with a pedestal.");
        if (neutralLobby && blueSpec > 0 && blueSpec < TeamSize)
            Fail("Only " + blueSpec + " UGCSpawnPointSpectatorBlue; a team holds up to " + TeamSize + ".");
        if (pinkSpec > 0 && pinkSpec < TeamSize)
            Fail("Only " + pinkSpec + " UGCSpawnPointSpectatorPink; a team holds up to " + TeamSize + ".");
        if (blueSpec > 0 && pinkSpec == 0)
            Warn("No UGCSpawnPointSpectatorPink: eliminated Pink players will be sent to any spectator spawn, "
                + "including Blue's.");
        if (noLayout)
            foreach (var tg in PlayingSpawnTags)
            {
                int n = Count(tg);
                if (n > 0 && n < TeamSize)
                    Fail("Only " + n + " " + tg + "; a team holds up to " + TeamSize + ", and extra players share a spawn.");
            }
        // Every pit - the lobby and each team's spectator area - needs its own pedestal: many
        // settings (an admin's room controls, a player's own locomotion options) are meant to be
        // changed mid-match, and a player on either team may be the one who needs them.
        var pedestals = transforms.Where(t => t.gameObject.tag == "UGCPedestalPlaceholder").Select(t => t.position).ToArray();
        var farFromPedestal = transforms.Where(t => SpectatorSpawnTags.Contains(t.gameObject.tag))
            .Where(t => pedestals.Length == 0 || pedestals.Min(p => Vector3.Distance(p, t.position)) > LobbyPedestalReach)
            .Select(t => t.name).ToArray();
        if (farFromPedestal.Length > 0)
            Fail(farFromPedestal.Length + " spectator spawn(s) more than " + LobbyPedestalReach + " m from any pedestal. Every pit - "
                + "the lobby and each team's spectator area - needs its own pedestal, in reach of the spawns: "
                + string.Join(", ", farFromPedestal.Take(10)));
        if (noLayout && Count("UGCPreviewTablePlaceholder") > 0)
            Warn("UGCPreviewTablePlaceholder on a map that declares no layout support. It is deleted at load - "
                + "there is no layout to preview - so leave it out.");

        // --- 9c. game modes ------------------------------------------------------------------
        // Race To 5 and Timed end a round by elimination and work on any map. Xball ends a round
        // only when the surviving team presses the other team's kill button or the eliminated team
        // presses its own concede button, so the game offers it only on a map with both, for EACH
        // team. A layout brings the kill buttons (in its start boxes); a fixed-spawn map has
        // whatever was placed. Concede buttons are always the map's own. Leaving Xball out is a
        // legitimate choice (the summary says which modes the map offers); a button for one team
        // only is always a mistake.
        bool blueConcede = Count("UGCConcedeButtonBlue") > 0, pinkConcede = Count("UGCConcedeButtonPink") > 0;
        if (blueConcede != pinkConcede)
            Warn("Only " + (blueConcede ? "Blue" : "Pink") + " has a concede button. Xball needs one for each team, so it "
                + "stays unavailable on this map. Place one in " + (blueConcede ? "Pink" : "Blue") + "'s pit too, or remove it.");
        if (noLayout)
        {
            bool blueButton = Count("UGCStartBoxBlue") + Count("UGCKillTeamButtonBlue") > 0;
            bool pinkButton = Count("UGCStartBoxPink") + Count("UGCKillTeamButtonPink") > 0;
            if (blueButton != pinkButton)
                Warn("Only " + (blueButton ? "Blue" : "Pink") + " has a start box or kill button. Xball needs one for each team, "
                    + "so it stays unavailable on this map, and in the other modes only one team can be knocked out "
                    + "by a button press. Place one for " + (blueButton ? "Pink" : "Blue") + " too, or remove it.");
        }

        // --- 10. overhead clearance (informational) ----------------------------------------
        // Clearance is a DIFFERENCE: the gap between a collider's underside and whatever is under
        // it, not the underside's height above y = 0. Measured that way, a tree trunk standing on a
        // 2 m hill is not "something at head height", and a beam 2.2 m over a raised deck is.
        var low = new List<string>();
        foreach (var c in solid)
        {
            if (c.bounds.size == Vector3.zero) continue;
            float gap = ClearanceBelow(c, root.transform);
            if (gap > 1.9f && gap < OverheadClearance)
                low.Add(c.name + "@" + gap.ToString("F2"));
        }
        low = low.Distinct().Take(8).ToList();
        if (low.Count > 0)
            Warn("Colliders whose underside is between 1.9 and " + OverheadClearance + " m above what is under them. "
                + "Fine if they are mounted flat against something, but anything a player walks UNDER must clear "
                + OverheadClearance + " m or it reads as a wall at head height: " + string.Join(", ", low));

        // --- 11. bundle -------------------------------------------------------------------
        ValidateBundle(root, Fail, Warn);

        // --- 12. swapped objects cutting through the map ------------------------------------
        // The marker is replaced by a real object of a known size at the marker's transform. A 6 m
        // ad screen centred on a marker 1 m from a wall, or a start box straddling a post, passes
        // every other check and is obvious the moment you stand in front of it.
        foreach (var (tg, c0, s0) in SwappedFootprints)
            foreach (var t in transforms.Where(t => t.gameObject.tag == tg))
            {
                var hits = GeometryInBox(root, t, c0, s0, t);
                if (hits.Count > 0)
                    Fail("'" + t.name + "' (" + tg + ") will cut through the map once swapped in - the real object is "
                        + s0.x + " x " + s0.y + " x " + s0.z + " m (times the marker's scale). Clear it of: "
                        + string.Join(", ", hits.Take(6)), t.name);
            }

        // --- 13. z-fighting between cubes -----------------------------------------------------
        // Two faces in the same plane, pointing the same way, with different materials: the GPU
        // cannot decide which is in front, and the joint shimmers in a headset. Typical where a
        // stair tread overlaps its landing, a pilaster sits flush with the next wall, or a floor
        // finish ends flush with the deck under it. Checked on Unity's built-in Cube only.
        var fights = CoplanarCubeFaces(root);
        if (fights.Count > 0)
            Warn(fights.Count + " pair(s) of cube faces share a plane, face the same way and use different materials - "
                + "they will z-fight (shimmer). Move one of each pair a few millimetres proud, or shorten it so the "
                + "faces no longer overlap: " + string.Join(", ", fights.Take(10)));

        // --- 14. the walkable area, baked ---------------------------------------------------
        // Everything above is geometry and metadata. These need the walkable area the game will
        // bake: spawns on floor, spectators sealed off from the playing area on foot, at spawn
        // time and in line of fire, and the size limit measured on floor players can reach.
        if (solid.Length > 0)
            UGCWalkableChecks.Run(root, transforms, Fail, Warn, Info);

        return findings;
    }

    // Pairs of built-in-Cube renderers with a same-facing, coplanar (within 1.5 mm), overlapping
    // face and different materials. Same material in the same plane renders identically, so those
    // are not reported; opposite-facing coplanar faces are back to back and never fight.
    static List<string> CoplanarCubeFaces(GameObject root)
    {
        const float tol = 0.0015f, minArea = 0.0004f;
        var boxes = root.GetComponentsInChildren<MeshRenderer>(true)
            .Where(r => { var mf = r.GetComponent<MeshFilter>(); return mf != null && mf.sharedMesh != null && mf.sharedMesh.name == "Cube"; })
            .ToArray();
        int n = boxes.Length;
        var c = new Vector3[n]; var ax = new Vector3[n][]; var h = new Vector3[n]; var aabb = new Bounds[n];
        for (int k = 0; k < n; k++)
        {
            var t = boxes[k].transform;
            c[k] = t.position; ax[k] = new[] { t.right, t.up, t.forward };
            var ls = t.lossyScale; h[k] = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)) / 2f;
            aabb[k] = boxes[k].bounds; aabb[k].Expand(tol * 2f);
        }

        // 2D extent of face (k, axis i, side s) on the basis (u, v)
        Rect Face(int k, int i, int s, Vector3 u, Vector3 v)
        {
            int j = (i + 1) % 3, l = (i + 2) % 3;
            var fc = c[k] + ax[k][i] * (s * h[k][i]);
            float umin = float.MaxValue, umax = float.MinValue, vmin = float.MaxValue, vmax = float.MinValue;
            for (int a = -1; a <= 1; a += 2)
            for (int b = -1; b <= 1; b += 2)
            {
                var p = fc + ax[k][j] * (a * h[k][j]) + ax[k][l] * (b * h[k][l]);
                float pu = Vector3.Dot(p, u), pv = Vector3.Dot(p, v);
                umin = Mathf.Min(umin, pu); umax = Mathf.Max(umax, pu); vmin = Mathf.Min(vmin, pv); vmax = Mathf.Max(vmax, pv);
            }
            return Rect.MinMaxRect(umin, vmin, umax, vmax);
        }

        var found = new List<string>();
        for (int a = 0; a < n; a++)
        for (int b = a + 1; b < n; b++)
        {
            if (!aabb[a].Intersects(aabb[b]) || boxes[a].sharedMaterial == boxes[b].sharedMaterial) continue;
            bool fight = false;
            for (int i = 0; i < 3 && !fight; i++)
            for (int s = -1; s <= 1 && !fight; s += 2)
            {
                var na = ax[a][i] * s; float da = Vector3.Dot(na, c[a]) + h[a][i];
                for (int i2 = 0; i2 < 3 && !fight; i2++)
                for (int s2 = -1; s2 <= 1 && !fight; s2 += 2)
                {
                    var nb = ax[b][i2] * s2;
                    if (Vector3.Dot(na, nb) < 0.9999f) continue;
                    if (Mathf.Abs(da - (Vector3.Dot(nb, c[b]) + h[b][i2])) > tol) continue;
                    var u = ax[a][(i + 1) % 3]; var v = ax[a][(i + 2) % 3];
                    var ra = Face(a, i, s, u, v); var rb = Face(b, i2, s2, u, v);
                    float ow = Mathf.Min(ra.xMax, rb.xMax) - Mathf.Max(ra.xMin, rb.xMin);
                    float oh = Mathf.Min(ra.yMax, rb.yMax) - Mathf.Max(ra.yMin, rb.yMin);
                    if (ow > 0f && oh > 0f && ow * oh >= minArea) fight = true;
                }
            }
            if (fight) found.Add(boxes[a].name + " / " + boxes[b].name);
        }
        return found;
    }

    // Nearest hit on THIS map only. A prefab asset is validated in an additive scene, which shares
    // the physics world with whatever scene is open - another map's geometry must not count.
    static bool RaycastMap(GameObject root, Vector3 origin, Vector3 dir, float dist, out RaycastHit best)
    {
        best = default;
        bool any = false;
        foreach (var h in Physics.RaycastAll(origin, dir, dist, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!h.transform.IsChildOf(root.transform)) continue;
            if (!any || h.distance < best.distance) { best = h; any = true; }
        }
        return any;
    }

    // Colliders and renderers of the map (not of `ignore`) that intersect a box given in `frame`'s
    // local space. Physics for colliders; renderer bounds for renderer-only trim, which physics
    // cannot see and which is exactly what cut through the ad screens and scoreboards of the
    // shipped reference maps.
    static List<string> GeometryInBox(GameObject root, Transform frame, Vector3 center, Vector3 size, Transform ignore)
    {
        var found = new SortedSet<string>();
        var half = Vector3.Scale(size / 2f, frame.lossyScale);
        // Filtered to this map: see RaycastMap.
        foreach (var c in Physics.OverlapBox(frame.TransformPoint(center), half, frame.rotation, ~0, QueryTriggerInteraction.Ignore))
            if (c.transform.IsChildOf(root.transform) && !c.transform.IsChildOf(ignore)) found.Add(c.name);

        var inv = frame.worldToLocalMatrix;
        var box = new Bounds(center, size);
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            if (r.transform.IsChildOf(ignore) || r.GetComponent<Collider>() != null) continue;
            var b = r.bounds;
            if (b.size.magnitude > 40f || b.size.y < 0.03f) continue; // whole-map shells; painted floor lines
            var lb = new Bounds(inv.MultiplyPoint3x4(b.min), Vector3.zero);
            for (int k = 0; k < 8; k++)
                lb.Encapsulate(inv.MultiplyPoint3x4(new Vector3((k & 1) == 0 ? b.min.x : b.max.x, (k & 2) == 0 ? b.min.y : b.max.y, (k & 4) == 0 ? b.min.z : b.max.z)));
            if (box.Intersects(lb)) found.Add(r.name);
        }
        return found.ToList();
    }

    // What the game lets a map carry. It applies exactly this rule when a map loads and removes
    // everything that fails it, so this must match the game or the verdict here is worthless.
    //
    // Allowed: components from Unity's engine modules, uGUI and the render pipeline, minus the
    // types below. Matched by full name against the type and its base types, so a subclass is
    // caught too.
    static readonly HashSet<string> BlockedComponentTypes = new HashSet<string>
    {
        "UnityEngine.Camera",
        "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData",
        "UnityEngine.AudioListener",
        "UnityEngine.Video.VideoPlayer",
        "UnityEngine.AI.NavMeshAgent",
        "UnityEngine.AI.NavMeshObstacle",
        "UnityEngine.AI.OffMeshLink",
        "UnityEngine.EventSystems.EventSystem",
        "UnityEngine.EventSystems.BaseInputModule",
        "UnityEngine.EventSystems.BaseRaycaster",
        "UnityEngine.EventSystems.EventTrigger",
        "UnityEngine.UI.Selectable",
    };

    static readonly HashSet<string> HonouredTags = new HashSet<string>
    {
        "Untagged", "EditorOnly", "Bunker", "NoWalk", "Crowd Mesh",
    };

    static bool IsHonouredTag(string tag) => HonouredTags.Contains(tag) || tag.StartsWith("UGC");

    static bool IsAllowedComponent(Component component)
    {
        if (component is Transform) return true;

        var type = component.GetType();
        string assembly = type.Assembly.GetName().Name;
        bool allowedAssembly = assembly == "UnityEngine" || assembly == "UnityEngine.UI"
            || (assembly.StartsWith("UnityEngine.") && assembly.EndsWith("Module"))
            || assembly.StartsWith("Unity.RenderPipelines.");
        if (!allowedAssembly) return false;

        for (var t = type; t != null; t = t.BaseType)
            if (BlockedComponentTypes.Contains(t.FullName))
                return false;

        return true;
    }

    // A scoreboard goes on ONE flat face: a Quad. The score is drawn on every face of the mesh it
    // is given, so on a Cube it also appears on the back, the edges and the top - stretched,
    // mirrored or upside down - and only one of the six is right.
    //
    // On a flat mesh, check that the players are on the side that is drawn (UGC/Screen, like any
    // shader that culls back faces, shows nothing from behind) and that the image is upright. Both
    // only for UGC/Screen: another shader may draw both sides, and then "behind" is not an error.
    //
    // This used to also fail any scoreboard with geometry within 0.25 m in front of its face.
    // Removed: a screen recessed in a frame or under a canopy trips it while being perfectly
    // visible, and whether a screen can be seen is obvious on a first look at the map.
    static void CheckScoreboardFace(GameObject root, Transform t, Renderer r, System.Action<string, string> Fail)
    {
        var mf = t.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;
        var mesh = mf.sharedMesh;

        var normals = mesh.normals;
        if (normals == null || normals.Length == 0) return;
        Vector3 faceNormal = normals[0].normalized;
        if (normals.Any(n => Vector3.Angle(n, faceNormal) > 5f))
        {
            Fail("Scoreboard '" + t.name + "' is on '" + mesh.name + "', a mesh with faces pointing in more than one "
                + "direction. The score is drawn on every face, so all but one show it stretched, mirrored or upside "
                + "down. Put the scoreboard material on a Quad (GameObject > 3D Object > Quad), and give it a backing "
                + "object of its own if it needs one.", t.name);
            return;
        }

        if (r.sharedMaterial.shader.name != "UGC/Screen") return;

        var tris = mesh.triangles; var verts = mesh.vertices; var uvs = mesh.uv;
        if (tris.Length < 3) return;

        // The drawn side, from the winding (what the GPU culls by), not the stored normals, which
        // a hand-made mesh can get wrong. A negative scale on one or three axes flips it.
        var s = t.lossyScale;
        float handedness = Mathf.Sign(s.x * s.y * s.z);
        var wound = Vector3.Cross(verts[tris[1]] - verts[tris[0]], verts[tris[2]] - verts[tris[0]]);
        var drawn = (t.TransformDirection(wound.normalized) * handedness).normalized;

        int Floor(Vector3 dirWorld)
        {
            int hitsFloor = 0;
            var origin = r.bounds.center;
            var right = Vector3.Cross(Vector3.up, dirWorld).normalized;
            for (float dist = 3f; dist <= 15f; dist += 3f)
                for (float lat = -6f; lat <= 6f; lat += 3f)
                {
                    var p = origin + dirWorld * dist + right * lat + Vector3.up * 5f;
                    // Floor below the board's middle: players look up at a screen, and a roof
                    // level with it or above is not where they stand.
                    if (RaycastMap(root, p, Vector3.down, 5f + r.bounds.size.y + 6f, out var h) && h.normal.y > 0.8f
                        && h.point.y < r.bounds.center.y) hitsFloor++;
                }
            return hitsFloor;
        }

        var flat = drawn; flat.y = 0f;
        if (flat.sqrMagnitude > 0.25f)
        {
            flat.Normalize();
            int front = Floor(flat), back = Floor(-flat);
            if (back > front)
                Fail("Scoreboard '" + t.name + "' shows its score on the side facing away from the players (" + front
                    + " floor samples in front vs " + back + " behind); from where they stand it draws nothing. "
                    + "Turn it 180 degrees.", t.name);
        }

        // Which way is "up" in the image: the direction texture V increases across the first triangle.
        if (uvs.Length == verts.Length && Mathf.Abs(drawn.y) < 0.7f)
        {
            Vector3 p0 = verts[tris[0]], p1 = verts[tris[1]], p2 = verts[tris[2]];
            Vector2 w0 = uvs[tris[0]], w1 = uvs[tris[1]], w2 = uvs[tris[2]];
            Vector3 e1 = p1 - p0, e2 = p2 - p0; Vector2 d1 = w1 - w0, d2 = w2 - w0;
            float det = d1.x * d2.y - d2.x * d1.y;
            if (Mathf.Abs(det) > 1e-6f)
            {
                var vDir = t.TransformVector((e2 * d1.x - e1 * d2.x) / det);
                if (vDir.y < 0f)
                    Fail("Scoreboard '" + t.name + "' is upside down: the top of its image points at the floor. "
                        + "Rotate it 180 degrees around the axis it faces along.", t.name);
            }
        }
    }

    // The smallest gap between a collider's underside and the map's own geometry beneath it,
    // sampled under its centre and towards each corner of its footprint. Infinity when nothing of
    // the map is under it (nobody can walk there), or when its underside is sunk into the ground.
    static float ClearanceBelow(Collider c, Transform root)
    {
        var b = c.bounds;
        float smallest = float.PositiveInfinity;
        foreach (var offset in new[] { Vector2.zero, new Vector2(0.4f, 0.4f), new Vector2(-0.4f, 0.4f), new Vector2(0.4f, -0.4f), new Vector2(-0.4f, -0.4f) })
        {
            var from = new Vector3(b.center.x + offset.x * b.extents.x * 2f, b.min.y - 0.01f, b.center.z + offset.y * b.extents.z * 2f);
            float nearest = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(from, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider != c && hit.collider.transform.IsChildOf(root) && hit.distance < nearest)
                    nearest = hit.distance;
            if (nearest < smallest) smallest = nearest;
        }
        return smallest;
    }

    static void LogFindings(GameObject root, string label, List<Finding> findings)
    {
        foreach (var f in findings)
        {
            switch (f.Level)
            {
                case Severity.Error: Debug.LogError("[UGC Validate] " + f.Message); break;
                case Severity.Warning: Debug.LogWarning("[UGC Validate] " + f.Message); break;
                default: Debug.Log("[UGC Validate] " + f.Message); break;
            }
        }

        int problems = findings.Count(f => f.Level == Severity.Error);

        var transforms = root.GetComponentsInChildren<Transform>(true);
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        var colliders = root.GetComponentsInChildren<Collider>(true);
        var solid = colliders.Where(c => !c.isTrigger).ToArray();

        var box = new Bounds();
        bool have = false;
        foreach (var c in colliders) { if (c.bounds.size == Vector3.zero) continue; if (!have) { box = c.bounds; have = true; } else box.Encapsulate(c.bounds); }
        foreach (var r in renderers) { if (r.bounds.size == Vector3.zero) continue; if (!have) { box = r.bounds; have = true; } else box.Encapsulate(r.bounds); }

        bool noLayout = transforms.Any(t => t.gameObject.tag == "UGCNoLayoutSupport");
        int playing = transforms.Count(t => PlayingSpawnTags.Contains(t.gameObject.tag));
        int spectator = transforms.Count(t => SpectatorSpawnTags.Contains(t.gameObject.tag));
        bool Tagged(params string[] tags) => transforms.Any(t => tags.Contains(t.gameObject.tag));
        bool killButtons = !noLayout
            || (Tagged("UGCStartBoxBlue", "UGCKillTeamButtonBlue") && Tagged("UGCStartBoxPink", "UGCKillTeamButtonPink"));
        bool concedeButtons = Tagged("UGCConcedeButtonBlue") && Tagged("UGCConcedeButtonPink");
        string noXball = !killButtons && !concedeButtons ? "a start box or kill button, and a concede button,"
            : !killButtons ? "a start box or kill button" : "a concede button";

        var tagCounts =transforms.Select(t => t.gameObject.tag).Where(t => t != "Untagged")
            .GroupBy(t => t).OrderByDescending(x => x.Count()).Select(x => x.Key + "=" + x.Count());
        int tris = 0;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;

        Debug.Log("[UGC Validate] " + label + "\n"
            + "  objects        : " + transforms.Length + "\n"
            + "  renderers      : " + renderers.Length + ", triangles ~" + tris.ToString("N0") + "\n"
            + "  colliders      : " + colliders.Length + " (" + solid.Length + " solid, "
                + colliders.Count(c => c is MeshCollider) + " MeshCollider)\n"
            + "  bounds         : min=" + box.min.ToString("F1") + " max=" + box.max.ToString("F1")
                + "   budget X/Z +-" + LimitXZ + ", Y " + MinY + ".." + MaxY + "\n"
            + "  layout support : " + (noLayout ? "NO (UGCNoLayoutSupport) - supplies its own spawns"
                                               : "yes - the layout places team spawns and start boxes") + "\n"
            + "  spawns         : " + playing + " playing, " + spectator + " spectator\n"
            + "  game modes     : Race To 5, Timed" + (killButtons && concedeButtons ? ", Xball\n"
                : " - no Xball (it needs " + noXball + " for EACH team)\n")
            + "  tags           : " + string.Join(", ", tagCounts) + "\n"
            + (problems == 0
                ? "  RESULT         : no errors. Read any warnings above.\n"
                  + "                   Still to do: play-test that every tagged marker became a real object."
                : "  RESULT         : " + problems + " problem(s) - see the errors above."));
    }

    static void ValidateBundle(GameObject root, System.Action<string, string> Fail, System.Action<string, string> Warn)
    {
        var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root) ?? root;
        string path = AssetDatabase.GetAssetPath(source);
        if (string.IsNullOrEmpty(path))
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            path = stage != null ? stage.assetPath : null;
        }
        if (string.IsNullOrEmpty(path)) return;      // unsaved scene object; nothing to check

        var importer = AssetImporter.GetAtPath(path);
        if (importer == null) return;
        if (string.IsNullOrEmpty(importer.assetBundleName))
        {
            // The kit's own reference maps carry no name on purpose: Build Reference Maps builds
            // them from a list. Telling a creator to name one would be wrong advice.
            var referenceMaps = BuildMapBundle.LoadReferenceMapBuilds(BuildMapBundle.ReferenceMapListPath, quiet: true);
            if (referenceMaps != null && referenceMaps.Any(b => b.assetNames.Contains(path)))
                return;

            Warn("This prefab is not assigned to an AssetBundle yet. Set its Bundle name at the "
                + "bottom of the Inspector before building.", null);
            return;
        }

        string bundle = importer.assetBundleName;
        var contents = AssetDatabase.GetAssetPathsFromAssetBundle(bundle);
        int skyboxes = contents.Select(AssetDatabase.LoadAssetAtPath<Material>)
            .Count(m => m != null && m.shader != null && m.shader.name.StartsWith("Skybox/"));
        if (skyboxes == 0)
            Warn("Bundle '" + bundle + "' contains no skybox material, so this map will use the game's "
                + "default sky. That is allowed. To give the map its own, include exactly one as a "
                + "top-level object; it is detected by type, no tag needed.", null);
        else if (skyboxes > 1)
            Fail("Bundle '" + bundle + "' contains " + skyboxes + " skybox materials. Only one is used; "
                + "the rest are ignored, and which one wins is not something you control.", null);

        // The game places every prefab listed at the top level of a bundle, each at its own origin.
        // The build tools leave out any prefab this map already contains, so giving the bundle
        // name to the props a map is built from is harmless. What is left is a prefab that carries
        // the name but that the map does NOT use: that one really is placed by itself. It may be
        // on purpose (a map split across two prefabs), so this is a warning.
        var used = new HashSet<string>(AssetDatabase.GetDependencies(path, true));
        var loose = contents
            .Where(p => p.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase) && p != path && !used.Contains(p))
            .Select(System.IO.Path.GetFileName).ToArray();
        if (loose.Length > 0)
            Warn("Bundle '" + bundle + "' also contains " + loose.Length + " prefab(s) that this map does not use. The game "
                + "places every such prefab at its own origin when the map loads. If that isn't what you want, clear their "
                + "Bundle name: " + string.Join(", ", loose.Take(8)) + (loose.Length > 8 ? ", …" : ""), null);

        // A shared asset with an explicit bundle name lives in exactly ONE bundle, and every other
        // bundle referencing it gets a dependency that is never loaded - the reference comes back
        // empty at runtime while looking fine in the editor.
        var strays = AssetDatabase.GetDependencies(path, true)
            .Where(d => d.StartsWith("Assets/"))
            .Select(d => new { d, imp = AssetImporter.GetAtPath(d) })
            .Where(x => x.imp != null && !string.IsNullOrEmpty(x.imp.assetBundleName)
                        && x.imp.assetBundleName != bundle)
            .Select(x => System.IO.Path.GetFileName(x.d) + " -> '" + x.imp.assetBundleName + "'")
            .Distinct().Take(8).ToArray();
        if (strays.Length > 0)
            Fail("This map depends on asset(s) assigned to a DIFFERENT bundle. Only one bundle is "
                + "loaded at a time, so those references resolve to nothing at runtime (a material "
                + "with no shader, say) while looking correct in the editor. Clear their Bundle name "
                + "so each bundle gets its own copy: " + string.Join(", ", strays), null);
    }
}
