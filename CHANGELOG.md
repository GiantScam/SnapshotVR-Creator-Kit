# Changelog

Versions follow semver. **Major**: an asset was removed, renamed or given a new GUID, or a contract
number changed (limits, tags, agent settings), so existing maps may need work. **Minor**: new props,
tools or reference maps. **Patch**: fixes.

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
