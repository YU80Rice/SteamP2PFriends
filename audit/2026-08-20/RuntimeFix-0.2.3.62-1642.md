# SteamP2PFriends Runtime Fix Report - 0.2.3.62

## 1. Scope and root cause

Scope: fix only the Steam P2P graphical listen-host case where a remote client is in a different object region and can pass through ordinary static level objects such as sofas, cabinets, and tables. No P2P connection, protocol, lobby, authentication, or release behavior was changed.

The supplied host/client capture proves that the P2P session connected successfully, but the prior collision implementation logged `native object not ready; using vanilla collision path: NullReferenceException` before its remote-region coverage record. The observed collision failure therefore remained reproducible despite its coverage bookkeeping.

U3-SDK source establishes the engine behavior:

- `U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/LevelObjects.cs:1018-1065` updates object regions only from `Player.LocalPlayer.movement.onRegionUpdated`, with `OBJECT_REGIONS = 3` in the normal build.
- `U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/LevelObject.cs:1062-1077` disables a regular object's root GameObject outside the local active region and then separately disables renderers. Root deactivation disables its colliders.
- `U3-SDK/Assets/Runtime/Assembly-CSharp/Unturned/Level/LevelObject.cs:688-690`, `715-717`, and `1052-1059` identify decals with `transform.Find("Decal")` and give decal/NPC objects different root-activation semantics.

Thus, refreshing region bookkeeping alone cannot repair collision. The repair must restore the root object only after the vanilla method has finished, while leaving the vanilla renderer state unchanged.

## 2. Traceability matrix

| Requirement | Implementation | Evidence |
| --- | --- | --- |
| Keep ordinary static collision around a remote P2P client | `Patches/LevelObjectRemoteCollisionPatch.cs`, `UpdateActiveAndRenderersEnabled_Postfix` | `Priority.Last` postfix restores an inactive root only in remote collision coverage and only when it has a Collider. |
| Do not render remote regions for the host | Same postfix | U3-SDK has already run `SetRenderersEnabled(false)` before the postfix calls `SetActive(true)`. |
| Use the remote player's current region | `TryGetRemotePlayerRegion` | Uses `steamPlayer.player.transform.position` and `Regions.tryGetCoordinate`, matching the U3-SDK position-derived region flow. |
| Exclude decal/NPC and condition-disabled paths | Same postfix | Explicit NPC return, same-source `transform.Find("Decal")` return, `canDamageRubble` condition gate, and collider gate. |
| Clear coverage deterministically | `SteamP2PFriendsPlugin.cs`, `Host/HostManager.cs`, `RemoveRemotePlayer`, `ResetAll` | Disconnect, start, abort, stop, and unload paths reset/reconcile coverage. |
| Diagnose registration and runtime behavior | `SteamP2PFriendsPlugin.cs` and patch logging | Registration is verified by Harmony owner/method identity; bounded coverage/root-reactivation logs identify the active path. |

## 3. Changed files

- `Patches/LevelObjectRemoteCollisionPatch.cs` (new): P2P listen-host-only remote-region tracking plus `Priority.Last` root-reactivation postfix.
- `SteamP2PFriendsPlugin.cs`: register and self-check the two postfixes; remove remote coverage on disconnect and reset it on unload.
- `Host/HostManager.cs`: reset collision coverage during P2P start, abort, and stop.
- `SteamP2PFriends.csproj`: compile the new patch.

### Core logic diff

```diff
+ [HarmonyPriority(Priority.Last)]
+ LevelObject.UpdateActiveAndRenderersEnabled postfix:
+   if (P2P listen-host && object lies in a remote player's OBJECT_REGIONS coverage
+       && conditions are met && object is neither NPC nor Decal
+       && root is inactive && a Collider exists)
+       rootGameObject.SetActive(true);
```

The actual renderer toggle remains the preceding U3-SDK call; this patch does not call `SetRenderersEnabled(true)`.

## 4. Build verification

Command:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe' 'SteamP2PFriends.csproj' /t:Rebuild /p:Configuration=Release /v:minimal /nologo
```

Result: PASS, `0 errors / 0 warnings`.

Artifact:

- `bin/Release/SteamP2PFriends.dll`
- SHA-256: `2FC58A382E9B7E86ED2EC202001CD6A7574509FB55394CAF5459007B53EBABFC`

## 5. Independent audit gate

Round 1 found a blocker: a root reactivation postfix could re-enable a decal path whose renderer behavior is root-activation based in U3-SDK. The blocker was fixed by checking `transform.Find("Decal")` using `ReferenceEquals` Unity-native-null-safe handling and returning before root activation.

Round 2 final audit: PASS. No blockers. The auditor confirmed the same-source decal exclusion, P2P-host-only double gate, root-only activation ordering, renderer preservation, transform-position region calculation, and session cleanup paths.

## 6. Runtime acceptance gate

This report is a static/build/audit PASS only. The supplied 16:13 diagnostic archives predate the final DLL and do not prove this new hash at runtime.

Before declaring the defect fixed, deploy the final DLL to both endpoints, verify the SHA-256 above, and capture fresh paired UMM archives while testing:

1. Host and client first stand in the same region and confirm a known sofa/cabinet/table collides normally.
2. Keep the host in place. Move the client more than three object regions away to another known furnishing and attempt to walk into it from several sides.
3. Keep the client in that remote region while the host changes local region. Verify collision remains, then verify no unintended distant object rendering appears on the host.
4. Disconnect the client and re-enter the former remote region as the host. Confirm normal vanilla visibility/collision behavior returns and no `LevelObjectCollision` warning/error is logged.

Required host-log evidence: the patch registration `OK`, one coverage change with `remotePlayers=1`, bounded `root reactivated` records, and no `root reactivation skipped` or `coverage predicate skipped` line.

## 7. Final conclusion

The old field-dependent collision attempt has been replaced by a U3-SDK-derived root/reactivation repair that preserves renderer disabling and excludes decal/NPC/condition-disabled paths. It is suitable for controlled two-machine P2P verification, but it is not yet a runtime-proven or published release.
