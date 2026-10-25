# Gobmine: materials, inventory, debris and collapse

Project-specific cumulative source patch, built from the uploaded Game.zip,
ProjectSettings.zip, Packages.zip and Mirror.zip, with the earlier debris fixes
merged into the uploaded (older) scripts.

**Status: implemented source and asset patch, NOT Unity-compiled or play-tested.**
No Unity Editor, C# compiler, Mirror Weaver or shader compiler was available here.
The validation reports distinguish Python algorithm models/static checks from the
56 C# test groups that must run inside your Unity project.

## Install into a backed-up project

1. Back up the project AND its files under `Application.persistentDataPath`.
   Use a branch/copy, not the only copy of your game.
2. Close Unity. Extract this archive, then MERGE its `Assets` directory into the
   project root. Overwrite the matching files. Do not delete your existing Game
   folder, rename replacement classes, or keep second copies of them.
3. Keep all included `.meta` files. Existing asset/script GUIDs are preserved.
   This is cumulative: do not install the earlier debris ZIP over this patch.
4. Reopen Unity and resolve every compiler/Weaver error before entering play mode.
   Do not upgrade Unity or packages just to install this patch.
5. Run the validation and isolated-scene steps below before using a valuable save.

The archive contains changed/new assets only, plus matching metadata. It does
not replace ProjectSettings, Packages, Mirror, your full game, or world saves.
`MANIFEST.json` lists payload files and their SHA-256 (excluding itself);
`SOURCE_CHANGES.patch`
shows differences from the uploaded Game folder.

**The Player and Debris prefabs and Menu/DemoLvl scenes are included as replacements
based on your uploaded versions.** If those four assets have changed since the
upload, merge their changes rather than blindly overwriting them. See CHANGELOG.
The expected layout is `Assets/Game`; retain that layout for the editor tools.

## Project configuration used

Read from the uploaded files, not a claim about the latest available versions:

| Component | Uploaded version/configuration |
|---|---|
| Unity | 6000.6.2f1, revision 770e33f6875c |
| Universal Render Pipeline | 17.6.0 |
| Mirror | 96.0.1 |
| Input System | 1.20.0; project input handling is Both |
| Cinemachine | 6.6.0 |
| Test Framework | 1.8.0 |
| Terrain | 16 x 16 x 16 cells per chunk; 0.25 metre spacing |

The upload contains references to URP pipeline/global settings assets that were
not included. Keep the existing pipeline assets in your real project. The new
shader needs URP. Missing uploaded references do not establish that your real
project has missing assets.

## What is implemented

### Terrain identity and air

Density remains the source of terrain shape. Solid samples have negative density;
nonnegative samples are on the empty side. A deterministic material generator
assigns one solid material to each GLOBAL terrain cell. Effective cell queries
return Air (ID 0), Surface (partially cut), or Solid. Surface cells retain their
remaining rock/ore identity. There is no invisible GameObject per empty cell and
an unloaded/uninitialized world is not silently treated as air.

`World.GetCellInfo(globalCell)` returns the state and effective material ID.
`World.IsAirAt(worldPosition)` and `World.GetMaterialAt(worldPosition)` query the
interpolated field. An actual mesh raycast remains authoritative for a mesh hit;
interpolated-field and marching-cubes surfaces can differ in ambiguous cells.

### Stone, copper, silver and gold

Starter definitions live in `Assets/Game/Resources/Gobmine`. Material and item IDs
are stable: Stone 1, Copper Ore 2, Silver Ore 3, Gold Ore 4. Air is 0 and has no
rendered material, item, reward or debris.

Ore deposits are seeded, globally positioned ellipsoids with independently varied
axis radii. They cross chunk boundaries without restarting. Later catalogue rules
win overlaps, so gold takes precedence over copper. They are deposits, not random
loot rolls on each hit. Generation makes NO natural air caves.

| Material | Hardness multiplier | Item units per removed cubic metre |
|---|---:|---:|
| Stone | 1.00 | 8 |
| Copper Ore | 1.35 | 32 |
| Silver Ore | 1.65 | 24 |
| Gold Ore | 2.00 | 16 |

These are initial gameplay values, not geology or a balanced economy. Rewards are
ore, not automatically refined bars. Every partially removed cell contributes to
its own material total. Fractional item amounts accumulate; whole quantities are
available through `PlayerInventory.GetWholeQuantity`.

The starter catalogue generates ore throughout its configured vertical range,
not only below Y=0. Edit the min/max CELL Y values to constrain deposits; at 0.25m,
a cell coordinate of -40 corresponds to approximately -10m. Hardness affects the
brush conservatively using the hardest material touching a density sample.
It does not make ore immune to connectivity or thin-ceiling collapse.

### Rendering

A chunk stays one mesh/collider. Surface triangles are grouped into submeshes by
cell material. Buried material interfaces do not produce internal visible faces;
ore is exposed when terrain is excavated.

`Shaders/TerrainOreURP.shader` and eight materials supply procedural rock and
metal-vein starter appearances. An optional triplanar rock albedo texture can be
assigned without mesh UVs. Terrain uses world-space projection; moving rubble
uses object-space projection. These are starter art, not finished production
textures. Shader compilation/lighting must be tested in the real URP project.

### Mining, inventory and debris

A client sends an aim direction through its owned mining component. The server
uses its configured tool, raycasts, checks reach/cooldown, and queues the edit with
the miner's identity. It does not accept a client-provided reward amount or item ID.
The uploaded client-authoritative player movement is retained; this is not a
complete movement/teleport anti-cheat system.

* Direct mining: credit the miner's inventory for actual removed material and
  create matching COSMETIC rubble. That rubble is not collectible a second time.
* Collapse (or an accepted edit whose miner has disconnected): transfer removed
  resources into server-owned, persistent resource bundles, plus cosmetic rubble.
* Cutting air: no extra material or inventory reward.

Removal accounting uses the existing six-tetrahedra approximation of the density
cell. It is monotone/volume-based, not exact fracture geometry. Numerical changes
at or below 1e-7 unit-cell volume are ignored as round-off.

`OrePickup` is the physical, collectible VIEW of a resource record. It falls on the
server, synchronizes position, and can be collected by a nearby player when there
is no terrain wall between them. Pickup collection is automatic within the trigger
and server reach checks. It is not assigned exclusively to the triggering miner.
There is no resource expiration timer. Up to 128 nearby pickup views are active by
default; other resource values remain stored and become visible as capacity allows.

Cosmetic debris remains pooled, capped at 512 active objects, with a 25-second
lifetime. Deferred cosmetic rubble waits in a queue; it does not earn new rewards
when spawned. Cosmetics and their pending queue are not saved across sessions.
Resource value is separate and is saved. Debris is grouped prefab rubble, not an
exact fractured copy of the original ore shape.

A small replaceable `MiningInventoryHUD` on the player displays the targeted
material and owned fractional inventory totals. It is a diagnostic HUD, not a
complete inventory/crafting/equipment interface. Turn off `showHUD` to hide it.

### Collapse, shared samples and networking

The earlier canonical density store and resumable structural checks are included.
Disconnected samples are not automatically anchored below Y=0 or beyond a size
threshold. Thin rock layers are measured between air boundaries from both directions;
minCeilingThickness is 2 (about 0.5m at the provided spacing). Work proceeds over
multiple frames and edits/collapses queue follow-up checks.

Chunk boundaries, unloaded data and server colliders use the same density store.
Clients receive actual revisioned density changes. Joining clients receive the
seed, catalogue rules signature and a baseline snapshot, then contiguous changes.
A detected revision gap requests another baseline instead of accepting an incomplete
terrain history. Hosts and dedicated servers use the same authoritative edit path.

This solver assumes infinite solid rock minus the original rectangular spawn room
and removal-only edits. Procedural natural caves, adding/placing terrain and finite
world boundaries need a different support proof and are NOT added by this patch.

## Saves and the important inventory persistence choice

Default new save: `world_materials_v1.dat`, plus `.bak`, under
`Application.persistentDataPath`. Each snapshot contains the seed, dimensions,
material/generator rules signature, density changes and recoverable resources.
Writes use a temporary file, flushed content, a checksum and replacement/backup.
Actual platform filesystem behaviour still needs the supplied Unity test.

Autosave is every 60 seconds plus a normal server shutdown save. A crash can roll
back changes since the last successful save. There is no claim of crash-proof or
multi-file database persistence. Snapshotting terrain and resource totals together
avoids intentionally saving one without the other.

**No authenticated persistent player account system was present in the upload.**
Therefore carried inventory is NOT automatically restored to a claimed player ID.
On disconnect it becomes recoverable world resource bundles. Autosave/shutdown
snapshots include carried items as recovery bundles at player positions; after a
restart those bundles are in the world, not in the joining player's inventory.
Other nearby players can collect them. Do not deploy this as private per-account
inventory persistence without adding authenticated identities and an ownership policy.

When no material save exists, the normal scene may import existing density edits
from `world_save.dat`. That old file is read, not overwritten by the new World.
The remaining terrain receives the new seeded material distribution; previous
excavation is not retroactively rewarded. Back up the old save before migration.

Existing material save dimensions and rules must match the catalogue. Changing
IDs, ordering, deposit settings, hardness or yields requires a new save slot or
an explicit migration (not supplied). Art/name changes can retain the save. Do not
edit gameplay definitions while a world is running. A mismatch/corrupt material
save fails closed rather than silently generating and overwriting a new world.
When deliberately making a new world, use another `materialSaveFileName`, or back
up/move BOTH the primary and its .bak. Disable legacy import for a truly fresh slot.

## Run inside Unity

From the menu, run:

1. `Tools > Gobmine > Materials > Validate Scene and Prefabs`
2. `Tools > Gobmine > Materials > Run All Regression Tests`
3. `Tools > Gobmine > Materials > Create Isolated Mining Test Scene`

The first checks the actual imported assets and reports shader messages. The second
runs 25 debris plus 31 material C# groups, not a simulated result from this package.
The third creates `Assets/Game/Scene/GobmineMaterialTest.unity`, with the real player,
KCP network manager/HUD, world, light, game state and debris pool. Enter play mode
and click Host. It uses seed 1729 and its OWN `gobmine_material_TEST_v1.dat` save;
it does not import or overwrite the normal world save. Scene creation prompts
before replacing an existing test scene.

For that seed and unchanged defaults, the portable generator model locates example
ore cells at these world-space cell-centre positions:

| Ore | Position (metres) |
|---|---|
| Gold | (12.125, -1.375, 0.375), near the east spawn-room wall |
| Copper | (-1.125, 9.625, -4.125), above the room |
| Silver | (1.625, -6.625, 6.875), below the room |

These are algorithm-model coordinates, not visually verified screenshots. Mine
toward them rather than teleporting into solid rock. The patch does not rewrite
your movement controller or add gravity/character collision to it.

Read `TEST_PLAN.md` for host/client, late-join, dedicated-server, save/restart and
collapse scenarios. Build both peers from the same code/assets. Copy the complete
Console error and stack trace if compilation, an editor group or play mode fails.

## Validation here

29 portable model/static groups passed: 13 debris and 16 material/asset groups.
Those include independent connectivity/volume comparisons, seeded deposits,
resource-accounting models, YAML/reference checks and lexical C# checks. They are
NOT substitutes for compiling C#, shader compilation, Mirror weaving, real physics,
play mode or profiling. Full details and raw reports are in `Validation/`.

The optional Python validators are intended to run against the MERGED project
snapshot (place `Validation` beside `Assets`), not this changed-files-only archive
by itself. Install Python dependencies from `Validation/requirements.txt` there.
