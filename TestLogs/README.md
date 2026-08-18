# SteamP2PFriends Test Archive Helpers

`TestLogs` is the repository-local, append-only evidence root for future runtime test cases. The migrated scripts derive their output directory from this folder:

```text
TestLogs/
  artifacts/<CaseId>/
    roles/<Host|Client>/<start|finish>.json
    configs/<Host|Client>/com.yu80rice.steamp2pfriends.cfg
    evidence-summary.json
```

`artifacts` is intentionally ignored by Git. Scripts and the empty-directory marker are versioned; CFG snapshots are local evidence and must be copied to a controlled audit archive before sharing.

## One-click entry

Double-click `SteamP2PFriends-TestArchive.bat` only when an additional CFG-and-hash record is useful. `START` requires Unturned to be closed and records the deployed plugin identity and exact `com.yu80rice.steamp2pfriends.cfg` used by that endpoint. It fails closed unless both `VerboseDiagnostics` and `RouteDiagnostics` are `false`. `FINISH` again requires the game to be closed and confirms that the deployed DLL and configuration did not change after `START`. `VERIFY` rechecks the dual-endpoint DLL SHA-256 match and each CFG snapshot/archive SHA-256. It never reads, copies, or evaluates `LogOutput.log` or `Player.log`.

## Required order

Use the one-click entry on both roles. `Host` and `Client` evidence must end up under the same `<CaseId>` directory before verification. For a second computer or VM, use a deliberately configured shared `TestLogs/artifacts` directory, or transfer the complete Client role subdirectories without overwriting any existing destination file.

1. On both endpoints, set `[Debug] VerboseDiagnostics=false` and `RouteDiagnostics=false`, then select `START` while Unturned is closed. This records the deployed plugin SHA-256 and immutable CFG copy.
2. Launch Unturned, then execute exactly one test route for the case.
3. Exit Unturned completely on each endpoint and select `FINISH`. No game log is copied or parsed.
4. Merge both roles and select `VERIFY` once. `evidence-summary.json` records the plugin and CFG identity verdict and is never overwritten; a failed Case remains immutable and is never overwritten.

## Scope

These helpers are optional archive aids. They do not prove P2P gameplay, replace manual deployment or manual log archiving, or act as a release gate.

## beta.2 example

Use a fresh Case ID for each route and direction. The current release is `v0.2.3.61-beta.2` with SHA-256 `3031C999138E850AED61636032B1580FAFBC6DC35B2F1F3D673262C43C67FC89`.

The separate `Initialize-TestCase.ps1` -> `Archive-TestLogs.ps1` -> `Verify-TestCase.ps1` chain remains available for optional raw Host/Client log archiving. `Archive-TestLogs.ps1` rejects execution while Unturned is running. Its pre/post snapshots are independent diagnostic material only and do not affect `evidence-summary.json` or the CFG-and-hash verdict.

## Provenance

This toolkit is adapted from the previously used phase-6 archive chain: case initialization, pre/post log copies, screenshots, P2P save fingerprints, and manifest verification. It intentionally does not retain the old scripts with fixed dates, fixed `E:` paths, or `Stage6A`-specific log parsing.
