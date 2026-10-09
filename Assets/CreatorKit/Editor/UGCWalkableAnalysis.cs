using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// Ships with the Snapshot VR Creator Kit.
//
// The walkable area of one map, as the live game would bake it, held as plain triangles so it can
// be asked the questions a navmesh query cannot answer reliably in the editor:
//
//   which parts of it can a player actually get to?      (Regions)
//   what is under this point, and which part is that?    (Locate)
//
// "Can get to" is decided from where the game puts players - the spawn points - and from which
// triangles join up. Walkable surface no spawn connects to (the top of a rock, the inside of a
// closed box, a roof) is real navmesh but nobody can ever stand on it, so the checks built on this
// leave it out.
//
// Self-contained: no gameplay code, nothing outside this folder.
public sealed class UGCWalkableAnalysis
{
    public readonly Vector3[] Verts;
    public readonly int[] Tris;          // three vertex indices per triangle
    public readonly int[] Areas;         // navmesh area index per triangle
    public readonly int[] Component;     // connected part each triangle belongs to
    public int TriangleCount => Tris.Length / 3;

    const float Cell = 2f;
    readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();

    // Bakes `root` where it stands (see UGCNavMeshPreview.BakeInPlace). Null if nothing bakes.
    public static UGCWalkableAnalysis Bake(GameObject root) => FromTriangulation(UGCNavMeshPreview.BakeInPlace(root));

    public static UGCWalkableAnalysis FromTriangulation(NavMeshTriangulation tri) =>
        tri.indices == null || tri.indices.Length < 3 ? null : new UGCWalkableAnalysis(tri.vertices, tri.indices, tri.areas);

    UGCWalkableAnalysis(Vector3[] verts, int[] tris, int[] areas)
    {
        Verts = verts; Tris = tris; Areas = areas;
        Component = Connect();
        for (int t = 0; t < TriangleCount; t++)
        {
            Vector3 a = Verts[Tris[3 * t]], b = Verts[Tris[3 * t + 1]], c = Verts[Tris[3 * t + 2]];
            int x0 = CellOf(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), x1 = CellOf(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            int z0 = CellOf(Mathf.Min(a.z, Mathf.Min(b.z, c.z))), z1 = CellOf(Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = Key(x, z);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(t);
                }
        }
    }

    static int CellOf(float v) => Mathf.FloorToInt(v / Cell);
    static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

    // ---------------------------------------------------------------- which triangles join up

    // Two triangles are joined when they share an edge. The bake is cut into square tiles, and
    // across a tile seam the two sides are divided differently, so there an edge is matched to any
    // edge lying along the same seam line that overlaps it at the same height. Without that every
    // seam reads as a wall and one floor comes back as dozens of parts.
    int[] Connect()
    {
        int n = TriangleCount;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        // Vertices at the same spot in plan (to the centimetre) and within a step of each other in
        // height are one vertex. The height is loose on purpose: where two floors meet that the
        // bake keeps apart - a tagged area laid a few millimetres over a slab - their shared
        // corners come out a voxel apart, and they are still one walkable floor.
        var columns = new Dictionary<long, List<(float y, int id)>>();
        var welded = new int[Verts.Length];
        int nextId = 0;
        for (int i = 0; i < Verts.Length; i++)
        {
            long column = ((long)Mathf.RoundToInt(Verts[i].x * 100f) << 32) ^ (uint)Mathf.RoundToInt(Verts[i].z * 100f);
            if (!columns.TryGetValue(column, out var stack)) columns[column] = stack = new List<(float, int)>();
            int id = -1;
            foreach (var (y, known) in stack) if (Mathf.Abs(y - Verts[i].y) < 0.25f) { id = known; break; }
            if (id < 0) { id = nextId++; stack.Add((Verts[i].y, id)); }
            welded[i] = id;
        }

        var firstUse = new Dictionary<long, int>();     // edge -> the one triangle using it so far, or -1 once shared
        var ends = new Dictionary<long, (int a, int b)>();
        for (int t = 0; t < n; t++)
            for (int e = 0; e < 3; e++)
            {
                int ia = Tris[3 * t + e], ib = Tris[3 * t + (e + 1) % 3];
                int a = welded[ia], b = welded[ib];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (firstUse.TryGetValue(key, out int other)) { if (other >= 0) Union(t, other); firstUse[key] = -1; }
                else { firstUse[key] = t; ends[key] = (ia, ib); }
            }

        // Edges still used by one triangle only, lying along a line of constant X or constant Z.
        var seams = new Dictionary<long, List<(float lo, float hi, float yLo, float yHi, int tri)>>();
        foreach (var kv in firstUse)
        {
            if (kv.Value < 0) continue;
            Vector3 p = Verts[ends[kv.Key].a], q = Verts[ends[kv.Key].b];
            bool alongZ = Mathf.Abs(p.x - q.x) < 0.005f, alongX = Mathf.Abs(p.z - q.z) < 0.005f;
            if (alongZ == alongX) continue;
            long line = ((long)(alongZ ? 0 : 1) << 40) ^ (uint)Mathf.RoundToInt((alongZ ? p.x : p.z) * 50f);
            float s0 = alongZ ? p.z : p.x, s1 = alongZ ? q.z : q.x;
            if (!seams.TryGetValue(line, out var list)) seams[line] = list = new List<(float, float, float, float, int)>();
            list.Add(s0 < s1 ? (s0, s1, p.y, q.y, kv.Value) : (s1, s0, q.y, p.y, kv.Value));
        }
        foreach (var list in seams.Values)
        {
            list.Sort((u, v) => u.lo.CompareTo(v.lo));
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count && list[j].lo < list[i].hi - 0.05f; j++)
                {
                    float mid = (Mathf.Max(list[i].lo, list[j].lo) + Mathf.Min(list[i].hi, list[j].hi)) / 2f;
                    float yi = Mathf.Lerp(list[i].yLo, list[i].yHi, Mathf.InverseLerp(list[i].lo, list[i].hi, mid));
                    float yj = Mathf.Lerp(list[j].yLo, list[j].yHi, Mathf.InverseLerp(list[j].lo, list[j].hi, mid));
                    if (Mathf.Abs(yi - yj) < 0.5f) Union(list[i].tri, list[j].tri);
                }
        }

        var component = new int[n];
        for (int t = 0; t < n; t++) component[t] = Find(t);
        return component;
    }

    // ---------------------------------------------------------------- what is under a point

    // The triangle under (or over) a point: its XZ inside the triangle, and its surface no more
    // than `up` above or `down` below the point. The nearest in height wins. -1 if there is none.
    public int Locate(Vector3 p, float up, float down)
    {
        if (!grid.TryGetValue(Key(CellOf(p.x), CellOf(p.z)), out var list)) return -1;
        int best = -1; float bestGap = float.MaxValue;
        foreach (int t in list)
        {
            if (!HeightAt(t, p.x, p.z, out float y)) continue;
            float gap = y - p.y;
            if (gap > up || gap < -down || Mathf.Abs(gap) >= bestGap) continue;
            best = t; bestGap = Mathf.Abs(gap);
        }
        return best;
    }

    // The same, but looking outward up to `reach` metres when nothing is directly under the point.
    // A marker placed against a wall, or a button that is itself not floor, has none under it.
    public int LocateNear(Vector3 p, float reach, float up, float down)
    {
        int t = Locate(p, up, down);
        for (float r = 0.25f; t < 0 && r <= reach + 0.001f; r += 0.25f)
            for (int k = 0; k < 8 && t < 0; k++)
                t = Locate(p + Quaternion.Euler(0f, k * 45f, 0f) * Vector3.forward * r, up, down);
        return t;
    }

    // Every part of the walkable area with floor within `reach` metres of a point, in plan, and
    // inside the height window. For "can anyone walk up to this": the thing itself is usually
    // solid, and the flat top of its own collider is walkable surface too, so the nearest
    // triangle to it is the wrong one to ask about.
    public HashSet<int> ComponentsNear(Vector3 p, float reach, float up, float down)
    {
        var found = new HashSet<int>();
        for (float dx = -reach; dx <= reach + 0.001f; dx += 0.25f)
            for (float dz = -reach; dz <= reach + 0.001f; dz += 0.25f)
            {
                if (dx * dx + dz * dz > reach * reach + 0.001f) continue;
                if (!grid.TryGetValue(Key(CellOf(p.x + dx), CellOf(p.z + dz)), out var list)) continue;
                foreach (int t in list)
                    if (HeightAt(t, p.x + dx, p.z + dz, out float y) && y - p.y <= up && y - p.y >= -down) found.Add(Component[t]);
            }
        return found;
    }

    public bool HeightAt(int t, float x, float z, out float y)
    {
        Vector3 a = Verts[Tris[3 * t]], b = Verts[Tris[3 * t + 1]], c = Verts[Tris[3 * t + 2]];
        float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        y = 0f;
        if (Mathf.Abs(d) < 1e-8f) return false;
        float u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
        float v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
        float w = 1f - u - v;
        const float edge = -0.002f;
        if (u < edge || v < edge || w < edge) return false;
        y = u * a.y + v * b.y + w * c.y;
        return true;
    }

    public float AreaOf(System.Func<int, bool> include)
    {
        double sum = 0;
        for (int t = 0; t < TriangleCount; t++)
            if (include(Component[t]))
                sum += Vector3.Cross(Verts[Tris[3 * t + 1]] - Verts[Tris[3 * t]], Verts[Tris[3 * t + 2]] - Verts[Tris[3 * t]]).magnitude * 0.5;
        return (float)sum;
    }

    public bool BoundsOf(System.Func<int, bool> include, out Bounds bounds)
    {
        bounds = default; bool have = false;
        for (int t = 0; t < TriangleCount; t++)
        {
            if (!include(Component[t])) continue;
            for (int k = 0; k < 3; k++)
            {
                var p = Verts[Tris[3 * t + k]];
                if (!have) { bounds = new Bounds(p, Vector3.zero); have = true; } else bounds.Encapsulate(p);
            }
        }
        return have;
    }

    // ---------------------------------------------------------------- where the game puts players

    public static readonly string[] PlayingSpawnTags = { "UGCSpawnPointBlue", "UGCSpawnPointPink" };
    public static readonly string[] SpectatorSpawnTags = { "UGCSpawnPointSpectator", "UGCSpawnPointSpectatorBlue", "UGCSpawnPointSpectatorPink" };

    // The parts of the walkable area players can be in, found from the map's own markers.
    public sealed class Regions
    {
        public readonly HashSet<int> Playing = new HashSet<int>();      // where a round is played
        public readonly HashSet<int> Spectator = new HashSet<int>();    // where spectator spawns stand
        public readonly Dictionary<Transform, int> TriangleOf = new Dictionary<Transform, int>();   // each spawn's own triangle, -1 if it has none
        public bool Reachable(int component) => Playing.Contains(component) || Spectator.Contains(component);
        public bool Any => Playing.Count + Spectator.Count > 0;
    }

    // Playing area: wherever a team spawn stands. A map that hosts layouts places none - the
    // layout brings them - so there it is whatever floor lies under the layout's footprint, which
    // is centred on the origin unless the map moves it with a UGCLayoutOrigin marker.
    public Regions FindRegions(IEnumerable<Transform> transforms)
    {
        var all = transforms.ToArray();
        var regions = new Regions();
        foreach (var t in all)
        {
            bool playing = PlayingSpawnTags.Contains(t.gameObject.tag), spectator = SpectatorSpawnTags.Contains(t.gameObject.tag);
            if (!playing && !spectator) continue;
            int tri = LocateNear(t.position, 0.5f, 1.5f, 3f);
            regions.TriangleOf[t] = tri;
            if (tri >= 0) (playing ? regions.Playing : regions.Spectator).Add(Component[tri]);
        }
        if (regions.Playing.Count == 0 && !all.Any(t => t.gameObject.tag == "UGCNoLayoutSupport"))
        {
            var origin = all.FirstOrDefault(t => t.gameObject.tag == "UGCLayoutOrigin");
            foreach (var local in new[] { Vector3.zero, new Vector3(0f, 0f, 20f), new Vector3(0f, 0f, -20f), new Vector3(12f, 0f, 10f), new Vector3(-12f, 0f, -10f) })
            {
                int tri = LocateNear(origin != null ? origin.TransformPoint(local) : local, 3f, 2f, 3f);
                if (tri >= 0) regions.Playing.Add(Component[tri]);
            }
        }
        return regions;
    }
}

// The checks Validate Map runs on the baked walkable area. Each one is a way a map has shipped
// broken with nothing in the editor to show for it.
public static class UGCWalkableChecks
{
    // Must stay in sync with the position budget the game enforces at load.
    const float LimitXZ = 70f, MaxY = 22f, MinY = -5f;

    // How far from a spawn the game may set a player down: they land offset by wherever they are
    // standing in their own room. And how far above and below the spawn it looks for floor.
    const float DriftReach = 5f, DriftUp = 1.5f, DriftDown = 3f;

    // A paintball: muzzle speed, drop, and how long it lives. The highest a gun can be held.
    const float PaintSpeed = 65f, PaintDrop = 7.08f, PaintLife = 5f, GunHeight = 2.8f;

    public static void Run(GameObject root, Transform[] transforms, System.Action<string, string> Fail, System.Action<string, string> Warn, System.Action<string> Info)
    {
        Physics.SyncTransforms();
        var nav = UGCWalkableAnalysis.Bake(root);
        if (nav == null)
        {
            Fail("Baking the walkable area produced nothing, so there is no floor for anyone to stand on. "
                + "Check that the ground has non-trigger colliders on layer Default or TransparentFX and is not tagged UGCNoWalk.", null);
            return;
        }
        var regions = nav.FindRegions(transforms);
        string Names(IEnumerable<Transform> ts) { var a = ts.Select(t => t.name).ToArray(); return string.Join(", ", a.Take(10)) + (a.Length > 10 ? ", ..." : ""); }
        bool Is(Transform t, string[] tags) => tags.Contains(t.gameObject.tag);

        // --- every spawn stands on floor ---------------------------------------------------
        var offFloor = regions.TriangleOf.Where(kv => kv.Value < 0).Select(kv => kv.Key).ToArray();
        if (offFloor.Length > 0)
            Fail(offFloor.Length + " spawn point(s) are not on walkable floor (none within 0.5 m, from 1.5 m above to 3 m below the marker). "
                + "A player sent there is put somewhere else. Move them onto floor that bakes: " + Names(offFloor), null);

        // --- the playing spawns share one floor ---------------------------------------------
        var playingSpawns = regions.TriangleOf.Where(kv => kv.Value >= 0 && Is(kv.Key, UGCWalkableAnalysis.PlayingSpawnTags)).ToArray();
        if (playingSpawns.Length > 0)
        {
            int main = playingSpawns.GroupBy(kv => nav.Component[kv.Value]).OrderByDescending(g => g.Count()).First().Key;
            var cutOff = playingSpawns.Where(kv => nav.Component[kv.Value] != main).Select(kv => kv.Key).ToArray();
            if (cutOff.Length > 0)
                Fail(cutOff.Length + " team spawn point(s) stand on floor that does not connect to the rest of the playing area. Players "
                    + "starting there can never reach the other team. Look for a ramp or stair that does not overlap its landing, "
                    + "a step over 0.45 m, or a doorway that is blocked: " + Names(cutOff), null);
        }

        // --- spectators cannot walk onto the field -----------------------------------------
        var spectatorSpawns = regions.TriangleOf.Where(kv => kv.Value >= 0 && Is(kv.Key, UGCWalkableAnalysis.SpectatorSpawnTags)).ToArray();
        var unsealed = spectatorSpawns.Where(kv => regions.Playing.Contains(nav.Component[kv.Value])).Select(kv => kv.Key).ToArray();
        if (unsealed.Length > 0)
            Warn(unsealed.Length + " spectator spawn(s) stand on floor that connects to the playing area, so an eliminated player can "
                + "walk or teleport straight back onto the field. Solid objects do not stop a player; only a break in the walkable "
                + "floor does. Close the barrier all the way round (an unbroken collider, with no gate left open), or raise the "
                + "area out of reach: " + Names(unsealed), null);

        // --- the size limit, on floor players can reach ------------------------------------
        if (regions.Any && nav.BoundsOf(regions.Reachable, out var b))
        {
            bool over = Mathf.Abs(b.min.x) > LimitXZ || Mathf.Abs(b.max.x) > LimitXZ || Mathf.Abs(b.min.z) > LimitXZ || Mathf.Abs(b.max.z) > LimitXZ
                     || b.max.y > MaxY || b.min.y < MinY;
            if (over)
            {
                bool offCentre = b.size.x <= LimitXZ * 2f && b.size.z <= LimitXZ * 2f && b.max.y <= MaxY && b.min.y >= MinY;
                Fail("Floor that players can reach runs past the size limit: it spans X " + b.min.x.ToString("F1") + ".." + b.max.x.ToString("F1")
                    + ", Y " + b.min.y.ToString("F1") + ".." + b.max.y.ToString("F1") + ", Z " + b.min.z.ToString("F1") + ".." + b.max.z.ToString("F1")
                    + " against X/Z +-" + LimitXZ + ", Y " + MinY + ".." + MaxY + ". A player standing past it appears in the wrong place to everyone else. "
                    + (offCentre ? "The map is not too big, it is off-centre: run Snapshot > UGC > Center Map."
                                 : "Shrink the reachable area, or tag the floor out there UGCNoWalk."), null);
            }
        }

        // --- kill buttons and pedestals can be walked to ------------------------------------
        if (regions.Playing.Count > 0)
        {
            var strandedButtons = transforms.Where(t => t.gameObject.tag.StartsWith("UGCKillTeamButton") || t.gameObject.tag.StartsWith("UGCStartBox"))
                .Where(t => !nav.ComponentsNear(t.position, 2f, 0.5f, 1.5f).Overlaps(regions.Playing)).ToArray();
            if (strandedButtons.Length > 0)
                Warn(strandedButtons.Length + " start box(es) or kill button(s) have no playing-area floor within 2 m, so the other team cannot "
                    + "walk up and press them: " + Names(strandedButtons), null);
        }
        var pedestalParts = new HashSet<int>();
        foreach (var t in transforms.Where(t => t.gameObject.tag == "UGCPedestalPlaceholder"))
            pedestalParts.UnionWith(nav.ComponentsNear(t.position - t.forward * 0.8f, 1f, 0.5f, 1.5f));      // used from the marker's -Z side
        var noPedestal = spectatorSpawns.Where(kv => !pedestalParts.Contains(nav.Component[kv.Value])).Select(kv => kv.Key).ToArray();
        if (noPedestal.Length > 0)
            Warn(noPedestal.Length + " spectator spawn(s) are in an area with no pedestal a player there can walk to. Every pit - the lobby "
                + "and each team's - needs its own, with floor on its -Z side: " + Names(noPedestal), null);

        // --- a landing that drifts across the barrier ---------------------------------------
        // The game sets a player down offset from the spawn by wherever they stand in their room,
        // and accepts the landing if the floor there carries the same area as the spawn's own.
        // Sealing the floor does not help: the player is placed, not walked.
        var drifting = new List<string>();
        foreach (var kv in regions.TriangleOf.Where(kv => kv.Value >= 0))
        {
            bool spectator = Is(kv.Key, UGCWalkableAnalysis.SpectatorSpawnTags);
            int own = nav.Component[kv.Value], area = nav.Areas[kv.Value];
            if (spectator && regions.Playing.Contains(own)) continue;      // already reported as unsealed
            float nearest = float.MaxValue;
            for (float dx = -DriftReach; dx <= DriftReach; dx += 0.5f)
                for (float dz = -DriftReach; dz <= DriftReach; dz += 0.5f)
                {
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d > DriftReach || d >= nearest) continue;
                    int tri = nav.Locate(kv.Key.position + new Vector3(dx, 0f, dz), DriftUp, DriftDown);
                    if (tri < 0 || nav.Areas[tri] != area) continue;
                    int there = nav.Component[tri];
                    bool wrongSide = spectator ? regions.Playing.Contains(there) : regions.Spectator.Contains(there) && !regions.Playing.Contains(there);
                    if (wrongSide) nearest = d;
                }
            if (nearest < float.MaxValue) drifting.Add(kv.Key.name + " (" + nearest.ToString("F1") + " m)");
        }
        if (drifting.Count > 0)
            Warn(drifting.Count + " spawn point(s) can set a player down on the wrong side of a barrier. A player lands offset from the spawn "
                + "by wherever they are standing in their own room, and floor of the other kind lies within " + DriftReach + " m at about the "
                + "same height with nothing to tell the two apart. Tag each spectator area's floor UGCSpectatorArea / ...Blue / ...Pink "
                + "(a thin collider-only box over a shared slab works): a spawn then only accepts a landing in its own area. "
                + string.Join(", ", drifting.Take(10)) + (drifting.Count > 10 ? ", ..." : ""), null);

        // --- line of fire from the spectator areas ------------------------------------------
        var fire = LineOfFire(root, nav, regions, spectatorSpawns.Where(kv => !regions.Playing.Contains(nav.Component[kv.Value])).ToArray());
        foreach (var f in fire.Where(f => f.landed > 0))
            Warn(f.landed + " of " + f.shots + " test shots fired from the " + f.tag + " area land on the playing area. Spectator paint "
                + "cannot eliminate anyone, but it distracts and gives positions away. Net the area in to at least 4 m above its "
                + "floor, with a collider on the net and no slot under it. First one: from " + f.from.ToString("F1") + ", aimed "
                + f.yaw.ToString("F0") + " deg round and " + f.pitch.ToString("F0") + " deg up, lands at " + f.at.ToString("F1") + ".", null);

        float unreachable = nav.AreaOf(c => !regions.Reachable(c));
        string span = regions.Any && nav.BoundsOf(regions.Reachable, out var reach)
            ? ". Floor players can reach spans X " + reach.min.x.ToString("F1") + ".." + reach.max.x.ToString("F1") + ", Y " + reach.min.y.ToString("F1") + ".." + reach.max.y.ToString("F1")
              + ", Z " + reach.min.z.ToString("F1") + ".." + reach.max.z.ToString("F1") + " (limit X/Z +-" + LimitXZ + ", Y " + MinY + ".." + MaxY + ")"
            : "";
        Info("Walkable area as the game will bake it: playing " + nav.AreaOf(regions.Playing.Contains).ToString("F0") + " m2, spectator "
            + nav.AreaOf(c => regions.Spectator.Contains(c) && !regions.Playing.Contains(c)).ToString("F0") + " m2"
            + (unreachable > 0.5f ? ", plus " + unreachable.ToString("F0") + " m2 that no spawn connects to (tops of props, sealed pockets) and is left out of every check" : "")
            + (fire.Count > 0 ? ". Line of fire: " + fire.Sum(f => f.shots) + " test shots from " + fire.Count + " spectator area(s), " + fire.Sum(f => f.landed) + " landed on the playing area" : "")
            + span + ".");
    }

    struct Fire { public string tag; public int shots, landed; public Vector3 from, at; public float yaw, pitch; }

    // Fires fans of shots from each spectator area: from every spawn and from as far as a player
    // can walk from it in eight directions (the edge of the area is where a gun gets closest to
    // the field), at waist height and at the highest a gun can be held, all the way round, from
    // steeply down to almost straight up. A shot ends when it hits something or after its
    // lifetime. It counts when it hits the playing area's floor, or something within standing
    // height above that floor.
    static List<Fire> LineOfFire(GameObject root, UGCWalkableAnalysis nav, UGCWalkableAnalysis.Regions regions, KeyValuePair<Transform, int>[] spawns)
    {
        var results = new List<Fire>();
        if (regions.Playing.Count == 0 || spawns.Length == 0) return results;

        float top = float.MinValue, bottom = float.MaxValue;
        foreach (var c in root.GetComponentsInChildren<Collider>())
        {
            if (c.isTrigger || c.bounds.size == Vector3.zero) continue;
            top = Mathf.Max(top, c.bounds.max.y); bottom = Mathf.Min(bottom, c.bounds.min.y);
        }
        var scene = root.scene.IsValid() ? root.scene.GetPhysicsScene() : Physics.defaultPhysicsScene;
        var buffer = new RaycastHit[64];
        float[] pitches = { -45f, -25f, -10f, 0f, 8f, 16f, 30f, 45f, 60f, 75f, 88f };

        foreach (var group in spawns.GroupBy(kv => kv.Key.gameObject.tag))
        {
            // Standing points: each spawn, and the last floor reached walking away from it.
            var stand = new Dictionary<Vector3Int, Vector3>();
            void Add(Vector3 p) { var k = new Vector3Int(Mathf.RoundToInt(p.x / 1.5f), Mathf.RoundToInt(p.y / 1.5f), Mathf.RoundToInt(p.z / 1.5f)); if (!stand.ContainsKey(k)) stand[k] = p; }
            foreach (var kv in group)
            {
                int own = nav.Component[kv.Value];
                nav.HeightAt(kv.Value, kv.Key.position.x, kv.Key.position.z, out float y0);
                var start = new Vector3(kv.Key.position.x, y0, kv.Key.position.z);
                int startTri = nav.Locate(start, 0.3f, 0.3f);
                if (startTri < 0) continue;      // a spawn at the very edge: its own spot is not inside a triangle
                Add(start);
                for (int k = 0; k < 8; k++)
                {
                    var dir = Quaternion.Euler(0f, k * 45f, 0f) * Vector3.forward;
                    var p = start;
                    for (int step = 0; step < 160; step++)
                    {
                        var q = p + dir * 0.25f;
                        int tri = nav.Locate(q, 0.6f, 0.6f);
                        if (tri < 0 || nav.Component[tri] != own) break;
                        nav.HeightAt(tri, q.x, q.z, out q.y);
                        p = q;
                    }
                    Add(p);
                }
            }
            var points = stand.Values.ToList();
            int stride = Mathf.Max(1, Mathf.CeilToInt(points.Count / 60f));

            var fire = new Fire { tag = group.Key };
            for (int i = 0; i < points.Count; i += stride)
                foreach (float height in new[] { 1.2f, GunHeight })
                    for (float yaw = 0f; yaw < 360f; yaw += 15f)
                        foreach (float pitch in pitches)
                        {
                            var muzzle = points[i] + Vector3.up * height;
                            var v = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward * PaintSpeed;
                            fire.shots++;
                            if (!Shoot(scene, root.transform, buffer, muzzle, v, top + 0.5f, bottom - 1f, out var hit)) continue;
                            // Where a player could be: on the playing floor or within standing height of it.
                            // A shot stopped by the spectators' own deck or parapet, metres above a field
                            // that runs underneath, has not reached anyone.
                            int tri = nav.Locate(hit + Vector3.up * 0.05f, 0.3f, 2.5f);
                            if (tri < 0 || !regions.Playing.Contains(nav.Component[tri])) continue;
                            if (fire.landed++ == 0) { fire.from = muzzle; fire.at = hit; fire.yaw = yaw; fire.pitch = pitch; }
                        }
            results.Add(fire);
        }
        return results;
    }

    // One shot. True, with the point, if it hits something of this map before it expires.
    static bool Shoot(PhysicsScene scene, Transform root, RaycastHit[] buffer, Vector3 p, Vector3 v, float top, float bottom, out Vector3 hitPoint)
    {
        const float dt = 0.03f;
        hitPoint = default;
        float t = 0f;
        while (t < PaintLife)
        {
            if (p.y > top && v.y > 0f)
            {
                // Above everything and still rising: nothing to hit until it is back at this height.
                float air = 2f * v.y / PaintDrop;
                if (t + air >= PaintLife) return false;
                p += new Vector3(v.x, 0f, v.z) * air; v.y = -v.y; t += air;
                continue;
            }
            var next = p + v * dt + Vector3.down * (0.5f * PaintDrop * dt * dt);
            var step = next - p; float length = step.magnitude;
            int n = scene.Raycast(p, step / length, buffer, length, ~0, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (buffer[i].distance < nearest && buffer[i].transform.IsChildOf(root)) { nearest = buffer[i].distance; hitPoint = buffer[i].point; }
            if (nearest < float.MaxValue) return true;
            p = next; v.y -= PaintDrop * dt; t += dt;
            if (p.y < bottom) return false;
        }
        return false;
    }
}
