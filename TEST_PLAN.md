# Unity test checklist

Run on a project/saves backup. No steps below have been executed in Unity here.
Use the isolated scene initially: it has a separate save slot and seed 1729.

## Import and editor checks

- Confirm no duplicate classes, compiler errors or Mirror Weaver errors.
- Run Validate Scene and Prefabs. Check the actual Shader Inspector for errors and
  verify URP is active in the selected quality tier.
- Run All Regression Tests. Expected groups: 25 debris, then 31 materials.
  Read the full Console output; a summary from this package is not their result.
- Create the isolated mining scene. Save it, enter play mode, click Host.
- Confirm terrain, player camera, local controls and inventory HUD appear. The
  uploaded player controller is not replaced with a physics character controller.

## Material and reward behaviour

1. Mine stone. The shape changes, stone rubble appears and Stone total increases.
2. Continue cutting the same empty space. No further rewards should be earned.
3. Mine toward gold near (12.125,-1.375,0.375) in the default seed-1729 test world.
   The exposed surface and HUD should show Gold Ore; direct cuts credit Gold Ore.
4. Repeat small partial cuts: fractional rewards accumulate rather than being
   discarded per swing. Cutting near an interface can credit multiple materials.
5. Inspect a deposit across a chunk boundary, unload/reload the area and reconnect.
   Ore identity and excavation must persist. Empty cells must not become stone.
6. Let ordinary mining rubble expire: inventory stays unchanged. Walking through
   that cosmetic rubble must not grant the same resource a second time.

## Collapse and pickup behaviour

1. Isolate a small stone body, including below Y=0 and across a chunk boundary.
   All its disconnected terrain should eventually be removed.
2. Isolate an ore-bearing body. It should produce matching cosmetic debris AND
   collectible resource bundles. Only the bundles carry unclaimed resource value.
3. Keep a resource bundle uncollected beyond 25 seconds. Cosmetic debris can vanish;
   resource value must remain. Walk near it to collect once.
4. Approach a pickup through a solid wall: it should not be collected through it.
5. Place tunnels close vertically and thin the separator from each side. Verify
   thickness threshold, follow-on collapses and that thick supported layers survive.
6. Mine rapidly while a large structural pass is still running, then stop. Work
   must continue until resolved; inspect any remaining supported geometry.
7. Watch ore-pickup physics for penetration, tunnelling or violent overlap with
   rubble. Inspect positions near chunk seams and recently rebuilt colliders.

## Networking

- Run a host plus a separate client, then a dedicated server plus two clients.
  A host-only run is insufficient.
- Mine from both players. Only the correct miner receives direct rewards, and both
  peers see the same edits, ore surfaces, collapses and rubble materials.
- Join during sustained mining/collapse and after many edits. Check the final
  terrain against the server, not just whether a player successfully connects.
- Leave/re-enter an interest-management region. Terrain must not retain old holes
  or miss revisions. Keep World's NetworkIdentity Force Shown.
- Collect the same resource with two nearby players. Its value must enter only one
  inventory. Check that remote pickup transforms follow server physics.
- Disable only local input/camera behaviours on remote players; do not disable the
  server's mining command component. Use one miner component per player.

## Persistence and failure cases

- Use Save Running World Now, stop normally, restart: excavation and ore layout
  persist; uncollected resources remain recoverable.
- Disconnect with inventory: its value becomes world recovery bundles, not silently
  assigned to a new/claimed account ID. This is intentional in this patch.
- Save while holding inventory and restart: those saved totals become recovery
  bundles. Confirm totals are conserved, not both restored and dropped.
- In a disposable save copy, corrupt the primary after two successful saves. Verify
  backup recovery. Never corrupt your actual save to test this.
- In a disposable copy, change a generation/yield setting and attempt to load:
  expect a clear rules-mismatch error, not a newly overwritten world.
- Test the eventual target build platform's replacement/backup filesystem support.

## Performance evidence to capture

Profile frame time, allocations, mesh rebuilds, structural backlog, debris count,
resource views and network bandwidth during sustained mining and a large collapse.
Budgets prevent unlimited work in some paths but do not guarantee a frame rate.
Bring down expensive settings only after recording a representative test.

For a failure, provide the full Console stack trace, host/client/dedicated role,
exact scene/save settings, the action sequence and a short video for visual faults.
