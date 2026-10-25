# Validation report

## Result

**29 portable Python model/static groups passed (13 debris, 16 material/asset).**
No Unity Editor, C# compiler, shader compiler or Mirror Weaver was available.
None of the 56 provided C# editor groups has been executed here.

These checks used a merged copy of the uploaded Game assets plus the cumulative
patch. The downloadable patch contains only changed/new assets and metadata.

## Executed checks

### Debris models and source checks

- 300 randomized connectivity fields against independent support oracle
- 78 supported-cache line regressions
- Disconnected islands at seven heights, including below zero
- 150001-sample island resumes across work budgets and fully collapses
- All 50 roof samples detected from upper and lower tunnel edits
- True roof boundaries, interpolated thickness, one-sided air and disable option
- 150 concurrent-removal/follow-up-pass connectivity regressions
- 57 analytic planar volumes/centroids, full/empty cells and partial cut accounting
- 3600 clipped tetrahedra checked against independent convex-hull geometry
- 4000 removal-only volume transitions never create material
- 201 negative/positive grid divisions and all shared-boundary owners
- Support-ray proof rejects an enclosed 125-sample body
- C# delimiter/source-path checks and unchanged original Unity GUID metadata

### Materials, resources and asset wiring

- Four stable material/item IDs, reserved Air=0 and positive configured yields
- 1800 seeded global-cell queries are order-independent; all ores occur; seed changes deposits
- Unity test fixtures: rare-ore overlap priority, zero chance and vertical limits
- 500 negative/chunk-boundary round trips retain global ore identity
- 5000 fixed-point deposit membership comparisons against geometric oracle
- All 256 air/surface/solid corner states and negative point interpolation
- 4000 accounting transitions conserve resource value without double-crediting debris
- Fractional/negative/mixed resource buckets, moving piles and exactly-once consumption model
- Save snapshots and disconnect spill conserve inventory plus uncollected value
- Save binary field sizes/checksum corruption model and atomic-replace/backup source paths
- Unity YAML parses without duplicate keys or missing local components/parents
- No duplicate asset GUIDs; all new resource references resolve; all three Mirror asset IDs match
- Debris server authority, owner-only inventory, pickup body/trigger and scene registrations
- Owned mining, server raycasts/cooldown, pickup distance/walls and contiguous late-join source paths
- C# lexical delimiters/error tokens and editor-only boundaries (not compilation)
- All eight starter materials reference the URP shader and correct world/object projection

## Asset inspection

The new resource asset GUID references resolve to the supplied game assets, Unity
built-ins, or the uploaded Mirror NetworkIdentity script. The original player and
debris GUIDs/asset IDs are preserved. The new pickup ID is consistent with the
GUID hashing used by the uploaded Mirror version. YAML validation is not a Unity
import: it cannot prove shader validity, engine serialization compatibility or
actual runtime component behaviour.

Existing URP pipeline/global settings referenced by ProjectSettings were not
included in the uploads. Package assets are also expected to be resolved by the
user's own Unity Package Manager. Do not use the uploaded subset as a claim that
the full project is broken or as a complete standalone project.

## Explicitly not executed

C# compilation; Mirror Weaver; Unity C# test commands; shader compilation and
rendering; physics/play mode; live host/client or dedicated-server replication;
late-join behaviour; target-platform file replacement; performance profiling.

Some model groups also inspect corresponding C# source paths, but transcription
and integration differences remain possible. Passing these checks is evidence
about individual algorithms and configuration, not proof that the full game runs.

See TEST_PLAN.md for the next checks inside the real Unity project.
