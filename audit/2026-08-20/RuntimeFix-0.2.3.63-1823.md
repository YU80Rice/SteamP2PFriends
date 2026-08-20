# SteamP2PFriends Runtime Fix Release Report - v0.2.3.63-beta.2

## 1. Problem and release scope

This release packages one targeted Steam P2P listen-host repair: when the host and a remote client are in different object regions, the client could pass through ordinary static `LevelObject` scene objects such as sofas, cabinets, and tables.

The repair adds `LevelObjectRemoteCollisionPatch`. It tracks remote-player object-region coverage on the P2P host and, after U3-SDK has applied its normal visibility update, reactivates only eligible collider-bearing static-object roots. Vanilla renderer disabling remains unchanged. NPCs, decal objects, condition-disabled paths, and roots without a Collider are excluded.

The following remain outside this release scope: trees, bushes, collectible resources, animals, vehicles, complete remote object/barricade/structure interaction, and save persistence.

## 2. Traceability

| Requirement | Implementation | Result |
| --- | --- | --- |
| Preserve ordinary static-object collision around remote P2P clients | `Patches/LevelObjectRemoteCollisionPatch.cs` | `Priority.Last` postfix restores only an inactive collider-bearing root under remote coverage. |
| Preserve vanilla far-region rendering | Same postfix | Does not call the renderer-enable path. |
| Follow remote player region changes | `LevelObjects.Update` postfix plus `TryGetRemotePlayerRegion` | Uses remote `SteamPlayer` position and U3-SDK region coordinates. |
| Clear transient coverage | `Host/HostManager.cs`, `SteamP2PFriendsPlugin.cs` | Reset on session start, abort, stop, unload, and remote disconnect. |
| Publish only a valid BepInEx package | `publish/v0.2.3.63-beta.2/SteamP2PFriends-v0.2.3.63-beta.2.zip` | Independently verified. |

## 3. Build and test verification

Commands:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' 'SteamP2PFriends.csproj' /t:Rebuild /p:Configuration=Release /v:minimal /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' 'WhitelistTests\SteamP2PFriends.WhitelistTests.csproj' /t:Rebuild /p:Configuration=Release /v:minimal /nologo
& '.\WhitelistTests\bin\Release\SteamP2PFriends.WhitelistTests.exe'
```

- Plugin build: `0 errors / 0 warnings`.
- Test-project build: `0 errors / 0 warnings`.
- Automated tests: `269 / 269 PASS`.

Final DLL:

- Version: `0.2.3.63`
- MVID: `95be2803-f795-4603-9c29-08b15f29ee88`
- Size: `922112` bytes
- SHA-256: `B89D6039E033EDE2FE566D3CA2C033153CFF2B27BA14CEAEF626490C3CF05042`

## 4. Runtime evidence boundary

The collision behavior was exercised in paired UMM archives `UMM-诊断包_20260820_172007` (client) and `UMM-诊断包_20260820_172032` (host): the client remained in Alberton while the host moved to Airport, and the client could no longer pass through the tested furniture. The host log records successful registration, `remotePlayers=1 activeRegions=49`, twelve root reactivations, and coverage removal on disconnect.

That capture used the pre-versioning collision candidate SHA-256 `2FC58A382E9B7E86ED2EC202001CD6A7574509FB55394CAF5459007B53EBABFC`. `v0.2.3.63-beta.2` changes only assembly/plugin version metadata and release documentation on top of the verified collision code. The developer has declared that the release DLL was manually installed on the test endpoints and has accepted final SHA-256 `B89D6039E033EDE2FE566D3CA2C033153CFF2B27BA14CEAEF626490C3CF05042` as the deployment-identity archive in place of a new automatic paired hash snapshot. This attests deployment provenance only; it does not expand the runtime acceptance scope.

## 5. Independent audit gate

| Audit round | Verdict | Evidence |
| --- | --- | --- |
| 1 | FAIL | `v0.2.3.63` archive did not yet exist, so package structure and internal hash could not be verified. |
| 2 | PASS | Archive contains exactly `BepInEx/plugins/SteamP2PFriends.dll`; entry size `922112` bytes and SHA-256 match the final DLL. |

Package:

- ZIP: `SteamP2PFriends-v0.2.3.63-beta.2.zip`
- ZIP SHA-256: `7F82B0948BB04A8677673D74054E96E965F8CDB83C824573AA73D42411A041FF`
- Internal DLL SHA-256: `B89D6039E033EDE2FE566D3CA2C033153CFF2B27BA14CEAEF626490C3CF05042`

## 6. Conclusion

The source, documentation, build, automated tests, package structure, and independent release audit pass. The package is ready for the `v0.2.3.63-beta.2` GitHub Beta release. The release notes must describe furniture-style static `LevelObject` collision as verified behavior and must not claim untested tree/resource/animal/vehicle or full remote-interaction coverage.
