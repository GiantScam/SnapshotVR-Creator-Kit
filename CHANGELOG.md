# Changelog

Versions follow semver. **Major**: an asset was removed, renamed or given a new GUID, or a contract
number changed (limits, tags, agent settings), so existing maps may need work. **Minor**: new props,
tools or reference maps. **Patch**: fixes.

## 0.2.0

- **New placeholder: Mirror.** `mirror_placeholder` (tag `UGCMirrorPlaceholder`) becomes the game's
  "Enable Mirror" panel: a 2 × 3 m panel used from its −Z side, placed against a wall. Validate Map
  checks its footprint like the ad screen and start box.
- **Scoreboards must be a Quad.** The score is drawn on every face of the mesh, so a Cube shows it
  on its back, edges and top as well. Validate Map now fails a scoreboard that is not a single flat
  face, faces away from the players, or is upside down. The docs' old advice to use a Cube and point
  +Z at the players is replaced: point a Quad's +Z **away** from them.
- **`UGC/Screen` draws in both eyes on PC VR.** It was missing the instancing/stereo macros, so with
  single-pass instanced rendering the score showed only in the left eye. Quest was unaffected.
- **Nature props:** the bushes now use a convex mesh collider instead of a box, and the trees gain
  convex "Canopy Core" colliders.
- **Reference maps:** The Gym, Dusty and Backyard Woodsball updated with the game's latest
  hand-tuned versions.
- **Room modes documented.** An admin can switch a room to Spooky (the sun goes off) or Jolly
  (snow over 60 × 80 m around the origin); both recolour the teams. See *Light, sky and materials*.

**ProjectSettings.** `TagManager.asset` changed: `UGCMirrorPlaceholder` is appended at the end, and
the legacy `NoWalk` slot becomes `Reserved/12` (use `UGCNoWalk`). Replace your project's
`ProjectSettings/TagManager.asset` with this release's. Tags are stored by position, so a project
whose list does not match builds maps with the wrong tags. No package changes.

## 0.1.0

First release.

- Unity 6000.3.24f1, URP 17.3.0, AI Navigation 2.0.14. Open the project with exactly this Unity
  version; a bundle is tied to the version that built it.
- Placeholders for every gameplay object, bunkers, cover, nature props and materials, under
  `Assets/CreatorKit/`.
- Four reference maps: The Gym, Dusty, Backyard Woodsball and Locomotion Test.
- Tools under the `Snapshot > UGC` menu: Validate Map, Preview NavMesh, Center Map, Map Bundles and
  Upload Maps to mod.io.
- `README.md` is the creator guide. `AGENTS.md` is the same ground written for a coding agent.

**Packages.** The kit's tools need these in `Packages/manifest.json`, and a project made from this
release has them: `com.unity.nuget.newtonsoft-json` 3.2.2, `com.unity.xr.management` 4.6.1,
`com.unity.xr.openxr` 1.18.0 and `com.unity.inputsystem` 1.20.0. OpenXR is switched on for Windows and
Android under *XR Plug-in Management* so that maps are compiled for both eyes; leave it on. The
project also includes `com.unity.pipeline` 0.7.0-exp.1, which lets the Unity CLI (and a coding agent
using it) drive the open editor; the kit's own tools don't need it. A later
release that needs another package will give the exact line to add here.
