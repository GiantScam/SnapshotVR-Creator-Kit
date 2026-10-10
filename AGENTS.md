# Building a Snapshot VR Map — Guide for AI Agents

**You are an AI coding agent working inside the Snapshot VR Creator Kit.** This file tells you how to build
a map that works, and — more importantly — how to *prove* it works before anyone puts on a headset.

If you are a human, read `README.md` at the top of the kit instead — this file assumes you can run
Unity Editor scripts and read back the results. An agent should read `README.md` and `REFERENCE.md` too: together they are the
creator guide, and they cover the same rules from the human's side.

---

## The one thing to understand first

**Almost every way a map can be wrong in this pipeline is silent.** Nothing throws. There is no red text.
The map loads, renders, and is quietly broken — a piece of geometry players fall through, a spawn point
that never became a spawn point, an invisible wall that eats paint.

So your job is not "build geometry". It is **"build geometry, plus the checks that prove it is legal"**.
A map you have not measured is a map you have not finished. Budget as much effort for verification as
for building.

## What a map actually is

A Unity prefab built into an AssetBundle, containing **geometry only** — meshes, colliders, materials.
No scripts. Anywhere you want a gameplay object, you place a plain marker prefab from
`Assets/CreatorKit/Placeholders/` carrying the right tag. At load, the game finds every tag and swaps in the real,
wired-up object at your marker's exact position, rotation and scale.

That is why there is nothing to script — and why a wrong tag fails silently, because the marker just
stays an inert mesh.

**The game lights the map.** It loads into a scene that already has one white, shadowless
directional light and ambient light taken from the sky, so a map with no lights of its own is lit.
Neither lights nor a skybox are required, and a creator is free to add both. Each light costs frame
time on a standalone headset. With a skybox material in the bundle, the sky and the ambient light
follow it; with none, the map keeps the game's default sky.

**Materials: what the game's renderer does with them.** A map is drawn by the game's render
pipeline (URP), on a standalone headset as well as a PC, so author to what that pipeline has
switched on:

- **Shaders.** `Universal Render Pipeline/Lit`, `Simple Lit` and `Unlit` all work, and so do your
  own. A shader of your own must support single-pass instanced stereo, or it draws in one eye.
- **Textures and normal maps work.** Import a normal map as type *Normal map*. Only the sun lights
  per pixel, so a normal map shows under the sun and not under lights you add. Textures are
  recompressed for each platform when the bundle is built; nothing to set.
- **Nothing casts a shadow.** Real-time shadows are off for every light, the sun included. Shadow
  settings on your renderers and lights do nothing; if a map needs shade, paint it into the
  textures or the geometry's colours.
- **Lights you add are cheap and coarse.** They light per vertex, and on a headset each object
  takes the sun plus the **one** added light that reaches it most strongly (two on PC). A large
  flat surface with few vertices shows almost nothing of a point light; subdivide it or do
  without.
- **Transparency works** (set the material's Surface Type to Transparent), but every transparent
  layer is drawn over what is behind it and costs frame time on a headset. Keep it to small
  areas: a strip of water, a window.
- **No depth or scene-colour texture.** Shaders that read them (soft particles, refraction,
  depth-faded shorelines, screen-space effects) will not work. HDR is off on headsets, so do not
  rely on emission brighter than white to glow.

**Build your map in its own top-level folder, `Assets/<YourMapName>/` — never inside `Assets/CreatorKit/`.**
That folder is replaced wholesale on every kit upgrade; anything you put inside it is deleted the moment
someone updates. Reference placeholders, bunkers and materials from `Assets/CreatorKit/` by all means —
just don't build into it.

## Decide this first: layout or fixed spawns?

It changes the geometry, the tags, and what you must check.

|  | **Layout map** | **Fixed-spawn map** |
|---|---|---|
| What it is | The game puts a tournament bunker layout into your map; the room's admin picks which one, and it does not change by itself | Your map is a permanent, fully custom space |
| Layout support | Tag the mod.io page **Arena** | Tag the mod.io page **Map** (primary); `UGCNoLayoutSupport` tag on any one object is the local-testing fallback |
| Team playing spawns | The game places them — you place **none** | **You** place all of them |
| Spectator spawns | You place them | You place them |
| Start boxes / kill buttons | The game places them | You place them if you want them |
| Game modes offered | Race To 5, Timed — plus Xball **only** with a concede button for each team | Race To 5, Timed — plus Xball **only** with a kill button **and** a concede button for each team |
| Central rectangle | **Reserved** — keep it clear | Yours |
| Example in the kit | `Assets/CreatorKit/ReferenceMaps/UGCTheGym` | `Assets/CreatorKit/ReferenceMaps/UGCDusty`, `UGCBackyardWoodsball` |

> **`UGCDusty` is the multi-level example.** If your map has ramps, stairs, tunnels or a bridge,
> open that one first — it is the only reference map that solves those, and most of the traps below
> are ones it hit.

> **A fixed-spawn map gets no preview table.** The game *deletes* a `UGCPreviewTablePlaceholder` on a
> map with no layout support rather than swapping it, because there is no layout for it to
> preview. Placing one is not an error, it is just dead weight — don't bother. A preview table you
> model into your own geometry rather than placing as a placeholder cannot be deleted for you, so
> the game makes it **inert** instead: it stays as scenery and simply never populates. Either way
> you cannot get a working one on a map with no layout.

> **No start box means no pre-round penalty.** On a layout map the game requires a player's gun to
> be touching the barrel box inside their start box when the round begins, or they take a short
> shooting penalty — it stops people aiming downfield during the countdown. That rule now applies
> **only when the map actually contains a start box**, so a fixed-spawn map that places none simply
> has no pre-round gun requirement. If you *do* place `UGCStartBoxBlue`/`Pink`, the rule comes back
> on with it, for both teams — so place both or neither, or one team starts penalised and the other
> doesn't.

**For a layout map, the reserved rectangle is X ±20 m, Z ±25 m, centred on the world origin.** Build
outward from it and keep your own colliders out of it above floor level — otherwise the layout's ~30
bunkers and both start boxes land straight through your geometry. Build your playable area around the
world origin and you never need a `UGCLayoutOrigin` marker.

## Numbers — read them, don't infer them

| Constraint | Value | Notes |
|---|---|---|
| Max ramp slope | **30°** | Steeper bakes no walkable surface at all |
| Recommended ramp slope | ≤ 20° | VR comfort |
| Max step / stair riser | **0.45 m** | Author to **0.40** — the limit is inclusive but leaves no margin |
| Min tread depth | 0.20 m | Recommended stairs: 0.18 riser / 0.30 tread |
| Ramp/stair-to-landing overlap | **10–15 cm** | A flush butt joint does **not** connect. This one is invisible and catches everybody |
| Overhead clearance | ≥ **2.5 m** | Anything players walk *under* |
| Walkable extent from centre | X/Z **±70 m** | Applies to where players can *stand* |
| Walkable height range | **−5 m to +22 m** | Same rule vertically |
| Keep empty under the origin | x/z **±4 m**, y **−8.5 to −3.5 m** | Newly connected players wait at (0, −6, 0) until placed; any solid object there blacks out their screen. Validate Map fails on it. |
| Layers | `Default` or `TransparentFX` **only** | Any other layer generates no walkable area, silently |

> **Players duck.** The game's body is as tall as the player's head is above their floor at that
> moment, so there is no fixed player height to design to. The walkable area is baked for someone
> crouching: floor under anything about 0.75 m or more above it stays walkable, and a player who
> ducks can go there. A pipe to crawl under or a low lean-to to hide in is legitimate cover; do not
> block it off. The 2.5 m clearance is for what players should pass under standing up: lower than
> that, a standing player's head goes into it and their view fades out until they duck.

> **The ramp slope limit does not apply to stairs.** Only riser height and tread depth decide whether a
> staircase works; its overall pitch is irrelevant. A 0.18/0.22 flight climbs at 39° and is perfectly
> walkable.

> **Scenery outside ±70 m is fine** — the limit is about where players can *stand*. Just make it
> genuinely unreachable: tag it `UGCNoWalk` **and** make it steeper than the slope limit, so neither
> mechanism has to be right on its own.

## Rules that are not negotiable

1. **Every walkable surface needs a real, non-trigger collider.** The walkable area is built from
   physics colliders only. A mesh with no collider is invisible to it — players fall through or float.
2. **Keep everything on layer `Default` or `TransparentFX`.**
3. **Tag every wall and piece of cover** `UGCSolid` (walkable top is fine) or `UGCNoWalk` (solid but not
   standable). Either gives the head-in-geometry fade; without one, players can peek through your walls
   by leaning in. `Bunker` is only for actual paintball bunkers (the props in
   `Assets/CreatorKit/Bunkers/` carry it already): paint hitting a `Bunker` sounds like an inflatable, so do not put it on your own geometry.
4. **Do not add NavMesh components of any kind.** The game generates the walkable area from your
   colliders at load.
5. **A map carries content, never behaviour.** When a map loads, the game deletes every component
   that is not one of Unity's own engine, UI or render-pipeline components, before the map appears:
   - **Removed:** any script, NavMesh components, `Camera`, `AudioListener`, `VideoPlayer`, and
     interactive UI (`Button`, `Toggle`, `Slider`, input fields, raycasters, `EventSystem`).
   - **Kept:** meshes, colliders, materials, textures, custom shaders, lights, particle systems,
     audio sources, animation, and world-space `Canvas` / `Image` / `Text` for signage.
   - **Tags:** only the tags in "Placing gameplay markers" below, plus `Bunker`, mean anything. Any
     other tag is cleared at load.

   Nothing in the editor warns you. `Validate Map` applies the same rule and lists what would be
   removed. Never design a map that depends on a removed component to work.
6. **If your geometry came from an imported model (FBX/OBJ), enable "Read/Write Enabled"** on its import
   settings. Without it the mesh works in Play Mode and silently contributes nothing once built into a
   bundle. Geometry built in Unity (primitives, ProBuilder) is unaffected.

## Build it with a script, not by hand

Past a few hundred objects, author the map as **data** in an editor script with a menu item, and re-run
it. You get constraint checking as part of building, reproducible output, and no lost work if the editor
crashes. Hand-author the intent — zone extents, each piece of cover, each facility position; let the
script handle only scatter within a zone, off a fixed seed. Make re-running **overwrite in place** or
you will accumulate `map 1.prefab`, `map 2.prefab`.

Practical notes that save a rebuild:

- **Build from Unity's built-in primitive meshes.** They are proven in bundles, share one mesh across
  every instance (so instancing and static batching work), and keep the bundle tiny. Triangle costs:
  **Cube 12 · Cylinder 80 · Sphere 768.** Never build bulk geometry from spheres.
- **The built-in cylinder is 2 units tall**, so `scale.y = height / 2`.
- **Give every shape the collider that matches it.** A box gets a `BoxCollider`, a ball a
  `SphereCollider`, a capsule a `CapsuleCollider`. Anything else (a cylinder, a wedge, an arch, an
  imported model) gets a `MeshCollider`. Mesh colliders are fully supported: the walkable area and
  paint both use them. The collider is what paint hits, so wherever it is bigger than what the
  player sees there is an invisible wall, and wherever it is smaller paint passes through cover.
  - **Not a box round a cylinder.** The box's corners stand 41% of the radius outside the surface
    (25 cm on a 1.2 m pipe), and a shot that visibly clears the pipe stops in mid-air.
    `Validate Map` cannot see this: the box and the cylinder have the same bounds.
  - **Unity's own Cylinder comes with a `CapsuleCollider`**, whose rounded ends leave the rim of the
    flat top uncovered. Swap it for a `MeshCollider` (Convex ticked) whose mesh is
    `Assets/CreatorKit/Meshes/CylinderCollision.asset`: the Cylinder's exact shape, as a kit asset
    that is readable wherever the bundle loads. Every cylinder in the reference maps uses it.
  - **A `MeshCollider` needs a readable mesh** (rule 6 above). A mesh you generate in a script and
    save as an asset is readable; an imported model needs Read/Write Enabled.
  - **A simpler collision mesh is fine, and often better.** A 12-sided prism round a pipe is within
    2% of the surface and far cheaper than the visual mesh. Tick **Convex** on any closed solid.
  > **Spheres and capsules do not follow non-uniform scale.** Unity scales a `SphereCollider` by
  > `max(|sx|,|sy|,|sz|)`, and a `CapsuleCollider` can never be shorter than `2 × radius`. On a flat,
  > wide object the small axis is simply ignored and the collider inflates to the large one. A bush
  > 1.11 m tall once shipped a 2.39 m sphere around it and read in the headset as **an invisible wall
  > eating paint nowhere near the bush**. Nothing in the editor showed it. On a squashed shape use a
  > `BoxCollider` or a `MeshCollider`.
- **Floors: one slab, everything else painted on top.** Make the walkable surface a single box collider,
  and lay zone finishes and painted lines on it as **renderer-only** boxes about 6 mm proud — no
  collider. Too small to create a second walkable level, enough to stop z-fighting, and it makes "my
  floor is one connected surface" true by construction.
- **Overhead geometry clears 2.5 m above the floor UNDER IT** — a difference, not an absolute height.
  A tunnel roof 4.4 m up over a walkway raised to 1.6 m leaves 2.8 m, and a check written against
  absolute height would just as happily pass one leaving 2.0 m. Decide explicitly whether a height
  parameter means the centre or the underside of a volume, too: a canopy whose *centre* was placed at
  the intended height put its underside at 0.5 m and turned a treeline into a solid wall at eye level.
- **A raised floor leaves walkable area sealed underneath it.** The walkable-area builder works from
  surfaces, not solids, so the ground under a solid platform stays walkable with the platform as a
  ceiling — unreachable, harmless, and on one reference map it was 27% of everything the map produced.
  If you care about the numbers being honest, bury one thin **`UGCNoWalk`** box at the platform's base
  (give it a collider and no renderer, or it will z-fight); "not standable" wins over "standable" at
  the same height. Anything tagged `UGCNoWalk` already — walls, blocks — never has the problem.
- **Build a handrail beside a ramp from the ramp itself**, not from two typed-in heights. A flat wall
  alongside a rising ramp is buried at the high end and floating in mid-air at the low end. You will
  not see it from above; you cannot miss it standing on it.
- **Give overhead scenery no collider.** Players cannot reach it, and it avoids walkable islands on top.

> **Watch for floor paint over a hole.** The floor-finish trick above has one failure mode nothing
> can catch for you: a finish has no collider, so it is invisible to the walkable-area preview, to
> bounds checks, to everything. Lay one across a sunken area or a gap and you get a floor hanging in
> mid-air — which, from underneath, is a ceiling at head height. A reference map in this kit shipped
> exactly that: a floor finish spanning a pit, giving a "room" 1.56 m tall with a staircase climbing
> into its ceiling.
>
> To check: for each thin renderer-only slab, sample its footprint and cast a ray **down from well
> above it**, and flag any point where the first solid thing you hit is *below* the finish. Don't
> start the ray just under the finish — it begins inside the deck that supports it and reports
> nothing, making every slab look wrong.

## Driving the editor: Unity CLI and the Pipeline package

Everything in this guide assumes you can run an editor script and read back what it reported. Editing
files and hoping is not enough: you cannot see whether a script compiled, and none of the checks under
"Verify" have run until Unity runs them. The recommended route is the **Unity CLI** (`unity ...`) with
Unity's **Pipeline** package (`com.unity.pipeline`), which lets you control the editor the human has
open. The kit project includes the Pipeline package; the CLI is installed on the human's machine,
not in the project, so check for it.

1. **Is the CLI installed?** Run `unity --version`. If it is missing, ask the human to install it;
   `REFERENCE.md` ("AI-assisted creation") links to Unity's instructions.
2. **Is an editor reachable?** Run `unity status`. A project in state `ready` can be driven. If more
   than one editor is open, pass `--project-path <this project>` on every command, or you may be
   driving a different project.
3. **No editor listed?** The project is probably not open in Unity: ask the human to open it, or
   use `unity run` (below). If it is open and still not listed, check `Packages/manifest.json` for
   `com.unity.pipeline`. A project made from an older kit may not have it; **ask the human before
   adding it** with `unity pipeline install`. It is the only package you may add to a kit project:
   editor tooling, and it does not change what goes into a bundle.
4. **Learn the commands from the editor, not from memory.** `unity command` lists what this editor
   exposes; the set depends on the package version. Expect commands to recompile scripts, report
   compile status, read the console, and evaluate C# in the editor.

The loop, once it works:

- **After every script edit, recompile and confirm it.** Trigger a recompile, poll until it reports
  finished, then read the compile result. A console with no errors can be a stale console from before
  your edit; only a completed compile proves your code is what is running.
- **Run your builder and the kit's tools by evaluating C#**, for example
  `UnityEditor.EditorApplication.ExecuteMenuItem("Snapshot/UGC/Validate Map")`, then read the console
  for what they reported. Select the map's prefab first; the tools act on the selection.
- **Measure with C#, not by eye.** Collider bounds, spawn positions and clearances are one evaluated
  expression each, and they are the evidence your report needs.
- **Change prefabs and scenes through the editor.** Do not hand-edit `.prefab`, `.unity` or `.asset`
  files while an editor is reachable: the editor does not see the change until a reimport, and a wrong
  file ID breaks the object without an error.

**With the project closed** you can still run any static editor method:
`unity run <project folder> -- -executeMethod <Class.Method> -logFile <path>`, then read the log file.
Unity starts up for every run, so batch your work into one method: build, validate, and print every
measurement in a single pass. This does not work while the human has the project open in Unity.

If you can do neither, say so at the top of your report. A map built without running the checks is
unverified, whatever the script looks like.

## Placing gameplay markers

Drag from `Assets/CreatorKit/Placeholders/`. Position, rotation and scale carry over; only the placeholder mesh is
discarded.

| You place | Tag | Becomes |
|---|---|---|
| Team spawn point | `UGCSpawnPointBlue` / `UGCSpawnPointPink` | A live team spawn, coloured automatically |
| Spectator spawn | `UGCSpawnPointSpectator` | A **lobby** spawn — shared by both teams and the Spectator team; see "Where the game sends players" |
| Team spectator spawn | `UGCSpawnPointSpectatorBlue` / `…Pink` | A team's pit spawn (eliminated / between rounds). Blue's double as the lobby if you place no neutral ones. |
| Central hub fixture | `UGCPedestalPlaceholder` | The map-select & settings pedestal — **one in every pit** |
| Wall/mounted screen | `UGCAdScreenPlaceholder` | A working advertisement screen |
| Practice fixture | `UGCGunCalibrationPlaceholder` | The gun calibration station |
| Table near spawn | `UGCPreviewTablePlaceholder` | The map/layout preview table |
| Team exit button | `UGCConcedeButtonBlue` / `…Pink` | A concede/forfeit button |
| Team starting box | `UGCStartBoxBlue` / `…Pink` | The team's start box, with its kill button live |
| Standalone kill button | `UGCKillTeamButtonBlue` / `…Pink` | A kill-team button on its own |
| Scoreboard screen | `UGCScoreboardScreen` | Live match score, texture-injected — see below |
| Blocking volume | `UGCNoWalk` | Not swapped — solid but not standable |
| Solid geometry | `UGCSolid` | Not swapped — solid, standable on top, gets the head-in-geometry fade |
| Layout origin | `UGCLayoutOrigin` | Not swapped — only if a layout map's playable area isn't at `(0,0,0)` |
| Area floor | `UGCSpectatorArea` / `…Blue` / `…Pink`, `UGCPlayingAreaBlue` / `…Pink` | Not swapped — keeps a spawn's landing inside its own area; see "Where the game sends players", item 5 |
| No-layout test marker | `UGCNoLayoutSupport` | Not swapped — on any one object, declares "no layout". Read only when the mod page carries neither tag: needed for local testing, harmless on a published Map; see "Publishing" |

> **On start boxes and kill buttons, the colour names the team that gets ELIMINATED, not the presser.**
> A box tagged `…Blue` is Blue's, and **Pink** players press it to knock Blue out. The concede button is
> the opposite and deliberately so — that one is pressed by your *own* team to forfeit.

> **The scoreboard needs one specific material.** A mesh tagged `UGCScoreboardScreen` receives the live
> score as a texture injected into a shader property named `_Image`. **Only `Assets/CreatorKit/Materials/UGCScoreboardScreen.mat`
> (shader `UGC/Screen`) has that property.** On any other material the injection is a silent no-op — the
> mesh just keeps the colour you gave it, and you will not notice until a match is running. Every
> reference map in this kit shipped with a dead scoreboard for exactly this reason. Keep the material's
> `_BaseColor` white: the shader multiplies the injected image by it, so a dark colour dims the score
> rather than tinting a bezel.

> **Place the actual placeholder prefabs — do not just make an empty GameObject and tag it.** Both
> work at runtime, because the game replaces the whole object anyway. But an empty GameObject is
> **invisible in the Scene view**, so you cannot see your own spawn points, click them, or check they
> are where you meant. If you generate your map from a script, instantiate the prefab from
> `Assets/CreatorKit/Placeholders/` and override the tag on the instance root for the specific role.

> **Never nest a swapped tag inside the same tag.** The game swaps every tagged object, so a marker
> sitting under another object that carries the same tag builds a second copy inside the first — and
> if the outer one was re-tagged to the other team, the inner one keeps the original team, giving you
> a Blue button inside your Pink one. The kit's placeholders carry their tag on the root only; this
> happens when you tag a parent and its child, or put a marker inside your own tagged prefab.
> `Validate Map` reports it.

**A wrong tag produces no error in the game.** `Validate Map` catches a tag the game ignores, a
doubled-up tag and a missing spawn. It cannot know which tag you *meant*, or prove the swap produced a
working object. That is what the play-test pass at the bottom is for.

## Sealing an area players must not walk back into

If your map sends players somewhere they should not be able to return from — a spectator area, a staging
pit behind a fence — **an opening in the barrier is a route**, and it must be closed with geometry.

Both ways a player moves are constrained by the walkable area, not by colliders:

- **Physically walking** in their room is not blocked by solid objects. The game keeps players in bounds
  by checking whether they are standing on walkable area, so the barrier has to be a **gap in the
  walkable area**, not merely something solid.
- **Teleport is not a jump.** It walks the player's avatar along a route through the walkable area, so it
  cannot cross a gap either.

A continuous thin collider produces both at once — the walkable area is eroded back from each side of
it, giving roughly a 0.6 m unwalkable band under a 0.08 m panel. Getting players *in* still works,
because round start teleports them directly to their spawn points and needs no route.

So: build the barrier unbroken. If you want it to *read* as a gate, model a closed gate — frame, kick
panel, latch — over continuous fence, rather than leaving a hole.

## Where the game sends players — spectator areas and the lobby

Spectator spawns are not decoration. The game sends people to them at fixed moments, and a map is
only as sensible as where those moments put them:

| When | Where the player lands |
|---|---|
| Joining the room | the **lobby** |
| The match ends | **everyone** → the lobby |
| Joining the Spectator team | the lobby |
| Eliminated, or between rounds | a random spectator spawn of **their own team** — the team's *pit* |
| Round start | their team's playing spawns (fixed-spawn map) or the layout's |

**The lobby is your neutral `UGCSpawnPointSpectator` spawns** — a shared area where both teams gather
between matches and where the Spectator team watches from. **If you place no neutral spawns, Blue's
spectator spawns are the lobby instead,** and Blue's pit doubles as it. Both are valid; a neutral lobby
is the better map.

Every spawn set is a *pit*, and every pit is laid out the same way:

1. **Seat everyone.** A room holds **10** players and a team up to **6**. The lobby needs at least 10
   spawns; each team's pit at least 6; and on a fixed-spawn map, at least 6 playing spawns per team.
   Spectator spawns are picked at random *with repeats*, so a few extra costs nothing and means fewer
   people land on top of each other.
2. **Every pit gets its own pedestal** — the lobby and both team pits — in front of its spawns. Pedestal
   settings are meant to be changed mid-match (a player's own locomotion options; the room's admin
   controls, and the admin can be on either team), so a player must be able to reach one from wherever
   the game has put them. The calibration station belongs in the lobby. Each team's concede button goes
   in that team's pit: between rounds, that is where the team is standing.
3. **Seal every spectator area from the playing area** — see the section above — so an eliminated
   player cannot walk or teleport back in. An area raised out of reach (a balcony, a deck on posts,
   with no stairs down) is sealed by construction.
4. **No line of fire onto the playing area.** Spectator paint cannot eliminate anyone, but a spectator
   who can see and shoot onto the field can still distract, ghost and body-block. Enclose the area with
   netting whose collider stops paint (above a parapet, overlap the net's collider with the parapet so
   no slot is left between them). **Make the net at least 4 m tall, measured from the floor the
   spectators stand on** — higher than anyone can raise a gun, so nothing can be fired downward over
   it. A gun pushed *through* the net cannot fire (the game refuses a shot when something solid is
   between the player and the barrel). A shot lives for 5 seconds; at 65 m/s with 7.08 m/s² of drop,
   anything that clears a 4 m net is still in the air when it expires, so the net is enough on its
   own. A roof is **recommended, not required**: a net panel, or a collider-only box tagged
   `UGCNoWalk`, across the top costs one collider and closes the area whatever the map's shape.
   Check by firing fans of shots from inside
   the area, both just inside the net all the way round and from the back of it, at the highest a
   gun can be held (~3 m) and at every pitch from steeply down to straight up; end each shot after
   5 seconds. Control-test it with the net's colliders switched off, which must show shots landing.
5. **Protect against play-space drift with the area tags.** A player lands offset from the spawn by
   however far they were standing from the centre of their room — several metres in a large play
   space. If the playing area's walkable ground lies within a few metres of a spectator spawn at a
   similar height (up to ~3 m below or 1.5 m above), that landing can be *on the field*, through a
   net. Tag the spectator area's floor `UGCSpectatorAreaBlue` / `…Pink` / `UGCSpectatorArea`: a spawn
   then only accepts a landing in the area it was placed in. That holds in both directions: with
   the pit's floor tagged, a playing spawn beside the pit cannot land a player inside it either.
   - If the whole map shares one floor slab, add a thin **collider-only, renderer-less** box (2 cm
     thick, top 5 mm above the floor) over that zone's share of it, tagged with the area — it wins over
     the slab beneath. Also tag the zone's parent object, so stands and platforms standing in it resolve
     to the same area. A spawn placed on a platform samples the platform's area, not the floor's.
   - `Preview NavMesh` bakes the area tags, so you can check coverage before building.
6. **Neutral spawns are all one lobby.** Place them together, in one area with its pedestal — a stray
   neutral spawn somewhere else becomes a random place to put a joining player.

**Every swapped-in object has a real size — clear it of the map.** The ad screen is a **6 × 2 m**
panel centred on its marker and readable from the marker's **+Z (blue axis)** side; scale the marker to
fit a smaller wall. The start box is **5.4 m** wide. The pedestal and calibration station are used from
their marker's **−Z** side (the user faces +Z). A pilaster, post or wall edge inside any of these looks
exactly as broken in a headset as it sounds. Validate Map checks the ad screen and start box.

**Scoreboards: nothing in front of the screen, and the right face out.** The screen must sit in front
of every bezel, pilaster and moulding behind it — even 2 cm of trim proud of the screen draws straight
through the score. On Unity's built-in **Cube**, only the **+Z** face shows the image upright; the
−Z face shows it upside down, so point +Z at the players. On a custom quad, keep its UVs **0..1** — the
score image does not repeat, so UVs in metres shrink it into one corner (or scale the material's
`_Image` tiling back to 0..1).

**Preview tables only on a layout map.** On a map that declares no layout support the table is deleted
at load — there is no layout to preview.

**A fixed-spawn map does not need start boxes.** You place the spawn points; the start box adds only
the pre-round rule that a player's gun must be touching its barrel box. Leave them out unless you
want that rule — and then place one for both teams.

**Xball needs a kill button and a concede button for each team.** Race To 5 (the default) and Timed
end a round when a team is eliminated, so they work on every map. In Xball a round ends only when the
surviving team presses the *other* team's kill button, or the eliminated team presses its own concede
button. The game therefore offers Xball only on a map that has both, for both teams:

- **Kill buttons** — a start box (`UGCStartBoxBlue`/`Pink`) or a standalone
  `UGCKillTeamButtonBlue`/`Pink`. A layout map gets both from the layout; a fixed-spawn map has only
  what you place. Make sure the other team can walk to each one.
- **Concede buttons** — `UGCConcedeButtonBlue`/`Pink`, one in each team's pit. No layout supplies
  these: a layout map must place them too.

With either missing for either team, Xball never appears in the pedestal's mode list, and a room set
to Xball drops to Race To 5 when the map loads. A concede button you place is live in every mode,
not only Xball, and no room setting turns it off. Validate Map prints the modes the map will offer
and warns on a one-team button.

**No two faces in the same plane.** Where two parts meet flush — a stair's top tread overlapping its
landing, a pilaster level with the next wall, a floor finish ending level with the deck under it —
faces that point the same way in the same plane with different materials *z-fight*: the joint shimmers
in a headset. Keep one of each pair a few millimetres proud (4 mm is invisible as a step), or stop it
short so they do not overlap. This is easy to produce by the hundred when generating a map from a
script; Validate Map warns about it for parts built from Unity's Cube.

## Verify — the tools, and what each one actually proves

Run the first two before you build, every time. Neither needs a bundle build first. The rest of the
table is the recentring, build and publish tools, and two reference prefabs. All of them are under
`Snapshot > UGC`.

| Tool | Proves or does |
|---|---|
| `Snapshot > UGC > Validate Map` | **Run this first.** The whole static assertion suite in one pass — oversized colliders, position budget, layers, mesh readability, duplicated swap tags, dead scoreboard materials, floor finishes hanging over a void, geometry inside the layout footprint, spawn inventory and capacity (lobby 10, team 6, pedestal in reach), the game modes the map will offer, components and tags the game removes at load, ad screens and start boxes cutting through the map, an upside-down cube scoreboard, coplanar faces that will z-fight, overhead clearance, bundle problems. It then **bakes the walkable area** and checks what only a bake shows: every spawn stands on floor, the team spawns share one connected floor, no spectator spawn's floor connects to the playing area, no spawn can set a player down across a barrier (the area tags), test shots fired from each spectator area do not land on the playing area, start boxes, kill buttons and pedestals can be walked to, and the floor players can reach is inside the size limit. It does **not** see trim in front of a scoreboard screen. Works on a prefab asset, a scene object, or whatever is open in a prefab stage. |
| `Snapshot > UGC > Preview NavMesh` | What the live game will treat as walkable — same agent, same layers, same rules. Also warns about colliders on the wrong layer and about a map with no colliders at all. |
| `Snapshot > UGC > Center Map` | Recentres on the origin (X/Z only, undoable). The fix when Validate Map says the floor players can reach is past the size limit but the map is only off-centre |
| `Snapshot > UGC > Map Bundles…` | The one place bundles are built. A window listing every map bundle in the project (and the reference maps): pick the platforms per map and build only the maps you're working on (Windows alone is enough to test in the PC game), or press **Build all my maps, all platforms** before publishing, or **Build reference maps, all platforms** to get a known-good map to load in the game. Also flags a map that uses an asset assigned to another bundle, which would be missing in game. From a command line, the two "all" buttons are `BuildMapBundle.Build` and `BuildMapBundle.BuildReferenceMaps` |
| `Snapshot > UGC > Upload Maps to mod.io…` | Uploads a built map's three platform zips to its mod.io page. Needs the human's mod.io sign-in — see "Publishing" |
| `Assets/CreatorKit/Placeholders/walkable_limit_reference.prefab` | A visible ±70 m cage, so you can *see* the limit while building |
| `Assets/CreatorKit/Placeholders/reference_avatar.prefab` | The real player at true size — the fastest scale gut-check |

> If Preview NavMesh draws nothing: open the **AI Navigation** overlay on the Scene view toolbar, expand
> Surfaces, and tick **Show NavMesh**. It is off by default.

### The checks, and why each one exists

`Snapshot > UGC > Validate Map` runs the first two and the last one for you. **The ones in between
it cannot do**, because they depend on what you intended: script them for your own map. All are
listed because understanding *why* each exists tells you what to avoid building in the first place:

- **Collider vs renderer size, per GameObject.** Flag any collider more than ~10% larger than the
  renderer on the same object. Per-GameObject is what makes this usable: a tree is legitimately a thin
  trunk collider under a wide canopy renderer, but those are separate objects, so honest cases never
  trip it.
  > **Measure on an object in a SCENE, not on the prefab asset.** `Collider.bounds` is `(0,0,0)` for
  > every collider of a prefab that has not been instantiated — silently. Any size check run against the
  > asset compares zeros and passes trivially. `Renderer.bounds` works fine on an asset, which is
  > exactly why this hides. Instantiate, measure, destroy.
- **Layer census.** Collect distinct layers under your root; expect only `Default` (and
  `TransparentFX`, if you used it).

  *From here on, write the check yourself:*
- **Tag census.** Count tags on the finished prefab and compare against what you intended to place.
- **Mesh readability**, on every `MeshCollider`, **after loading your built bundle back** — the only test
  that reproduces the real failure. In-editor readability proves nothing about what shipped.
- **Steps and stairs:** probe straight *down* at each step's expected height, one probe per step, with
  a tight radius. Do **not** sample along a straight line up a staircase — most of that line is inside
  the solid mass under the steps, and you will get false failures. Do **not** use one probe height for
  the whole flight with a generous radius either: that just finds the ground under the stairs and
  calls it a pass.
  > **Whatever else you tag, do not tag a staircase `UGCNoWalk`.** That tag means "not standable", so
  > the entire flight bakes as a solid lump and the route it serves silently stops existing. A
  > staircase is floor. This has happened.
- **Doorways and gaps, one at a time.** Scan *across* each opening at 5 cm steps and report how much
  continuous walkable width you find.
  > **"Everything is connected" is not the same as "this doorway is open."** If your map has a loop in
  > it — and most do — every area stays reachable the long way round even with a doorway completely
  > bricked up, so a whole-map connectivity check comes back clean. A reference map shipped a build
  > with all three of its signature doorways blocked and every other check passing. Check each
  > chokepoint on its own.
- **Spectator areas, which Validate Map does for you.** It bakes the walkable area, works out which
  parts the team spawns stand on (the playing area) and which the spectator spawns stand on, and
  reports: a spectator spawn whose floor connects to the playing area (a hole in a barrier), a spawn
  with floor of the other kind inside 5 m at a similar height and nothing to tell the two apart (a
  missing area tag), and how many of its test shots from each spectator area reach the playing area.
  You do not need to write these. If you want to see a barrier's own line, sample along it at 10 cm
  steps and require every sample to find no walkable area.

> **Control-test any check before you believe it.** Run it against a version you *know* is broken first —
> remove a panel, then confirm your barrier check reports exactly that hole. Otherwise "no problems
> found" is indistinguishable from a check that cannot find problems. This costs one run and has caught
> a bogus metric more than once.
>
> **Run two controls, not one: known-broken *and* known-good.** A doorway scan that sampled from 0.30 m
> up with a 0.10 m search radius, against a walkable surface at 0.02 m, reported "blocked" for every
> doorway on the map — correct for three of them, and it would have kept saying it after they were
> fixed. The known-good control is what catches a probe that can only ever say "no".

### What the editor cannot tell you

- **`NavMesh.CalculatePath` is unreliable here.** It reports partial paths across maps that are known to
  work fine. Do not use it to judge connectivity.
- **Raw connected-component counts are misleading.** Navmesh tile seams fragment them — and note that
  the walkable-area builder works from *surfaces*, not solids, so the inside of any closed box (a solid
  bleacher, a crate) becomes its own sealed pocket of walkable area that nobody can reach. Harmless, but
  it inflates both the count and the total area. Judge a staircase by probing each step, not by counting
  regions.

## Publishing

1. Give your map prefab and its skybox material (if it has one) the same AssetBundle name (Inspector, bottom). Leave
   every other asset the map uses with **no** bundle name; Unity packs them in automatically, and
   that is what lets several maps share an asset (see the note on shared assets below).
2. Open `Snapshot > UGC > Map Bundles…` and press **Build all my maps, all platforms** — Windows,
   Android and Linux in one pass (from a command line: `BuildMapBundle.Build`). You need all three
   to publish. While iterating, the same window builds one map for Windows only, which is all a
   local test needs and much faster.
   Each build writes the bundles to `Assets/StreamingAssets/<bundle>/<bundle>_<platform>.assetbundle`
   (for testing) **and** a zip of each to `ModioUploads/<bundle>/<bundle>_<platform>.zip` at the
   project root (for uploading).
3. Test the built bundle in the real game before publishing. Copy the kit's
   `Assets/StreamingAssets/<bundle>/` folder into the installed game's `SnapshotVR_Data/StreamingAssets/`.
   On PC VR, create a room (so your machine hosts it) and select `[Local] <bundle>` from the map list. The
   Quest version cannot load local maps. Your human has to put on the headset for this step, so say
   which parts of the checklist below still need it.
4. The mod page comes first, and your human creates it on the mod.io website: on
   <https://mod.io/g/snapshot-vr>, **Add > Level**, then a name, a logo, and the tag — **Map** for a
   fixed-spawn map, **Arena** for a layout map. The tag is single-select and
   mod.io does not require it, so have them check it was actually set. The page is made once per map.
5. Upload with `Snapshot > UGC > Upload Maps to mod.io…`. **This step is your human's, not yours**: it runs
   under their mod.io account. Tell them to:
   1. Sign in, once per machine (their email, then the code mod.io emails them; or a personal
      access token with write access from <https://mod.io/me/access#tokens>).
   2. Press **List my mods** and choose the mod page beside the bundle. The choice is saved in
      `ProjectSettings/ModioMapUploads.json`.
   3. Tick the map, type a version (a new one for every upload), press **Dry run** and read the
      report, then press **Upload**.

   What you should know about the tool, so you can prepare for it and explain its report:
   - It lists a map only if `ModioUploads/<bundle>/` has zips, so build first (step 2).
   - It runs `Validate Map` on the bundle's prefab and **refuses to upload a map with errors**. Get
     the map to zero errors before you hand over; do not tell the human to tick the override.
   - It uploads each platform's zip as its own file with the right platform, and skips a platform
     whose identical file is already on the page (live or awaiting review). Re-running after a
     partial failure is safe.
   - A new file is not live when the upload finishes. It is reviewed first.

   Never ask for, store or type their mod.io sign-in or token yourself, and do not call the upload
   from a script on their behalf.

   By hand instead: add each platform's **zip** from `ModioUploads/<bundle>/` to the mod.io page as
   its own modfile, tagged exactly like this: `_windows` → **Windows**, `_android` → **Meta Quest** (not
   Android), `_linux` → **Linux** (not Linux Server). Never the bare `.assetbundle`: the game unzips
   every map it downloads and cannot open one that isn't a zip. It also ignores a file tagged with any
   other platform, with no error the creator can see.

> **The mod page is where layout support is declared, not the bundle.** For a published map the
> game reads the mod page's **Arena**/**Map** tag first; it falls back to an in-bundle
> `UGCNoLayoutSupport` tag only when the page has neither, and with no in-bundle tag either the map
> is treated as an Arena. The mod page wins because it is editable
> without rebuilding and republishing three bundles — a wrong answer there costs a page edit, not a
> release. Use the tag while testing locally (there is no mod page yet). It can stay in a bundle
> published as a **Map**: the two agree, and the bundle that was tested is the bundle that ships. If
> the page says **Arena** and the bundle still carries the tag, the page wins and the game logs a
> warning naming the conflict. **If the page has neither tag, the in-bundle tag is what decides:**
> present, the map has no layouts; absent, it is an Arena. So the tag is never wrong on a Map, but
> it is not inert, and it must not stand in for the page's tag. mod.io does not require one, so
> have the human confirm it was set.

> **A shared asset must be assigned to NO bundle.** Anything with an explicit bundle name lives in
> exactly one bundle; other bundles referencing it get a dependency that is never loaded, and the
> reference comes back empty at runtime — a material with no shader, say. It looks fine in the editor,
> because the editor can still see your project copy. Leave shared assets unassigned and Unity copies
> them into each bundle that needs them.

## Before you publish — the checklist

Every one of these has silently gone wrong on a real map. None of them throw an error.

- [ ] `Validate Map` reports **zero errors**, and every warning has been read and judged.
- [ ] Walked every ramp and stair — nothing blocked, nothing climbable that shouldn't be.
- [ ] Every ramp/stair-to-platform join actually **overlaps** rather than butting flush.
- [ ] Pedestal, ad screens, calibration station, preview table and concede buttons all became real
      interactive objects, not inert meshes. A tag typo looks identical until you try to use it.
- [ ] Spawn points tested from **both** teams — colours and positions correct for Blue and Pink.
- [ ] Fixed-spawn map: mod.io page tagged **Map**, **and** both playing and spectator spawns placed. (A `UGCNoLayoutSupport` tag left in the bundle is fine on a Map.)
- [ ] Layout map: mod.io page tagged **Arena**, the reserved rectangle is clear, and you placed no playing spawns.
- [ ] Walls and cover tagged `UGCSolid` or `UGCNoWalk` — put your head in one and confirm the screen fades.
- [ ] Scoreboard uses `Assets/CreatorKit/Materials/UGCScoreboardScreen.mat` and showed a real score during a match.
- [ ] Every placeholder is visible in the Scene view, and no object repeats a `UGC*` tag its parent has.
- [ ] Any area players shouldn't re-enter is sealed — try to walk **and** teleport back in.
- [ ] Joined the room and found the pedestal in front of you; ended a match and the whole room fit in
      the lobby. Eliminated players land in their own team's pit, **every pit has a pedestal you can reach**,
      and nobody in any pit can shoot onto the field.
- [ ] Spawned while standing well off-centre in your play space, next to each spectator area's barrier —
      you stay on the spectator side.
- [ ] Every ad screen, start box and scoreboard is clear of pilasters, posts and trim, and every
      scoreboard reads upright from where players stand.
- [ ] Built all three platform bundles, and all three are on the mod page. Only the Windows one can
      be tested locally.

## How to report on your work

Say what you **measured**, with numbers. Say what you **reasoned** but did not measure. Say what still
needs a headset — real locomotion feel, bot navigation, whether the placeholder swaps actually produced
working objects. If a check did not run, say that, rather than that it passed.

The reference maps in `Assets/CreatorKit/ReferenceMaps/` were all built through exactly this workflow. Open them
and look at how they solve things before inventing your own approach.
