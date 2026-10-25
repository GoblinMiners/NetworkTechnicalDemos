# Changes relative to the uploaded Game.zip

## Cumulative script replacements

- World.cs: canonical density edits, queued structural/cascade work, per-material
  removal accounting, owned miner identity, server collision rebuilds, shared chunk
  boundaries, revisioned late-join snapshots and explicit gap recovery.
- Chunk.cs: material-grouped marching-cubes submeshes and triangle material lookup.
- VoxelMiner.cs / PlayerMiner.cs: owned server-validated mining requests carrying
  direction only; server-side tool settings and shared inventory cooldown.
- PlayerInventory.cs: owner-synchronized item-ID/double quantities, accumulated
  fractional rewards and disconnect resource preservation.
- PlayerMovement.cs / PlayerNetworkRegistration.cs: retained earlier debris patch
  readiness/server registration fixes. This is not a character-controller rewrite.
- DebrisPool.cs / DebrisCleanup.cs: network spawn/unspawn pooling and material-aware
  rubble; no competing pooled-object destruction timer.
- WorldSaver.cs: retained earlier legacy save read/recovery implementation. The new
  World writes MaterialWorldPersistence instead and does not overwrite legacy data.

## New helpers and assets

Canonical density, structural solver, volume model, material definitions/catalogue,
deterministic deposits, air queries, material mesh builder, WorldMaterials partial,
server mining validation, persistent resource store, network physics ore pickups,
versioned checksummed saves, a small inventory/target HUD, editor diagnostics,
25 debris tests and 31 material tests. Four item/terrain definitions, eight starter
materials, an OrePickup prefab and a procedural/triplanar URP shader are supplied.

## Existing prefab/scene changes

Debris.prefab:
- Add DebrisCleanup with standalone lifetime disabled (pool owns lifetime).
- NetworkTransformReliable becomes ServerToClient, not ClientToServer.
- Use the starter Stone debris material; layer becomes Ignore Raycast.
- Preserve original prefab GUID, NetworkIdentity asset ID, body and mesh collider.

Player.prefab:
- Preserve original prefab GUID, player setup, camera and movement settings.
- PlayerInventory synchronization becomes Owner; assign four item assets.
- Add the small MiningInventoryHUD. Only existing VoxelMiner remains active.

DemoLvl.unity:
- Set thin ceiling threshold to 2 voxel spacings (about 0.5m at current spacing).
- Assign the material catalogue and OrePickup prefab to World.
- Force-show World for consistent terrain observation.
- Debris pool: warm 80, lifetime 25s, max active 512.

Menu.unity:
- Register the new OrePickup network prefab.
- Explicitly assign the existing KCP transport to the network manager.

No ProjectSettings, package versions, Mirror source or original world save are
replaced. Existing .meta contents are preserved for existing assets.
