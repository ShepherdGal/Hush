# Windows settings

How the Settings page's Windows choices work in Hush 0.7. Read with CLAUDE.md. This documents the current five choices and their safeguards; the catalog can be expanded (more policies, services, scheduled tasks, app removal, etc.) when the user asks. New tweaks should keep the confirmation + Undo pattern where Windows allows it. No actual Windows policy was applied during verification.

## Supported choices

The catalog in WindowsSettings.cs is a list of known entries; UI and history contain catalog IDs, never executable commands or caller-supplied registry destinations. All entries use REG_DWORD. User entries use HKCU, Widgets uses the 64-bit HKLM view. No service/task/package/security changes are included.

| Choice | Scope and exact value | Effect and limits |
| --- | --- | --- |
| Limit optional diagnostic data | HKCU `Software\Policies\Microsoft\Windows\DataCollection`, `AllowTelemetry=1` | Requests required-only Windows diagnostics for this user. Required data remains; other apps have independent controls. Existing stricter values are never weakened. |
| Stop personalized offers from diagnostic data | HKCU `Software\Policies\Microsoft\Windows\CloudContent`, `DisableTailoredExperiencesWithDiagnosticData=1` | Prevents diagnostic-data personalization of Windows content; suggestions can remain. This does not itself reduce collection. |
| Hide suggestions in Windows Settings | HKCU `Software\Policies\Microsoft\Windows\CloudContent`, `DisableWindowsSpotlightOnSettings=1` | Removes suggested Settings content on Enterprise/Education/IoT Enterprise; disabled on Pro. |
| Turn off location for this user | HKCU `Software\Policies\Microsoft\Windows\LocationAndSensors`, `DisableLocation=1` | Stops Windows location access for this user. Maps/location-dependent features can be affected. IP-based website estimates remain possible. Separate impact disclosure. |
| Turn off Windows Widgets | HKLM `SOFTWARE\Policies\Microsoft\Dsh`, `AllowNewsAndInterests=0` | Disallows Widgets across users, including taskbar content. No uninstall. Requires an already elevated app. Separate impact disclosure. |

Microsoft documents diagnostic policy in both user/device scope and uses the stricter configured setting. See [System/AllowTelemetry](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system#allowtelemetry) and [diagnostic configuration guidance](https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization). Personalization and Settings-suggestion scope/edition mappings come from [Experience policies](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience). Location uses the user [Sensors policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-sensors#disablelocation-1); Widgets uses the stable [AllowNewsAndInterests policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests#allownewsandinterests), not the newer Insider-only alternatives. Local Windows 25H2 ADMX definitions were also inspected to verify DWORD mappings.

## Availability and policy ownership

Conservative supported build range: Windows 11 21H2–25H2, builds 22000–26200 inclusive, documented Pro-family/Enterprise/Education/IoT Enterprise editions. Unknown editions/builds and Home are read-only. This is compatibility gating, not a promise that an old Windows release remains in Microsoft servicing. Additional versions require a new support review. The suggestions choice requires Enterprise-family/Education/IoT.

These support gates disable new adjustments. Undo can still restore tracked original values after an OS edition/build change when the account, expected values, management/device-policy checks and required administrator access permit safe restoration.

Domain join, Entra/work-account connection or active MDM enrollment causes read-only operation. Native detection failures also fail closed. [NetGetJoinInformation](https://learn.microsoft.com/en-us/windows/win32/api/lmjoin/nf-lmjoin-netgetjoininformation) and [NetGetAadJoinInformation](https://learn.microsoft.com/en-us/windows/win32/api/lmjoin/nf-lmjoin-netgetaadjoininformation) supply join information; enrollment state is inspected locally. This is conservative detection, not a comprehensive policy-management inventory. Group Policy/MDM can refresh or override values later.

An existing untracked value of any type is read-only, even if it matches the requested setting. A machine value blocks the corresponding user choice. The app never adopts or replaces someone else's policy; unsupported types remain untouched. Consequently every original value created by the present catalog is absent. History records that absence exactly; it does not invent a Windows default or change an underlying user preference. Existing external values keep their data/type unchanged because no write is attempted.

Widgets needs an already elevated app for Apply and Undo. Hush does not request elevation, change permissions, restart Explorer, force sign-out/reboot, disable security or delete packages. Each confirmation explains scope and impact. Feature refresh can require the user's later sign-out/restart. The UI says policy values were saved and verified; it cannot prove that Windows components have refreshed.

## Confirmation, durability and recovery

Checkboxes stage desired values only. Review captures an immutable expected/target plan. The dedicated modal lists the changes and effects; only its Apply/Undo action runs the backend. Cancel/Escape does not write. A confirmed uncheck restores only that tracked choice's original state; Undo changes restores all tracked choices. There is no blanket Restore Defaults that could overwrite another configuration.

Before mutation, the backend locks its local history and rechecks every expected value, account, support condition and permission requirement. It checks again before each write. Account-bound pending originals are flushed to `%LOCALAPPDATA%/Hush/Windows settings/windows-settings.json` before the first mutation. Writes are flushed/read back; history commits through a fresh temporary file and same-directory atomic rename. No arbitrary paths or restoration values are accepted from history; invalid schema, owner, IDs, duplicate entries, oversized data, non-absent originals and linked history paths are rejected. The file lock serializes Hush instances using that history directory.

On failure, rollback restores only attempted values that still match Hush's target, retaining pending history even if rollback succeeds. A new instance can review Undo to reconcile target/original values and clear the recovery state. A different current value, newly detected management/device policy or missing administrator access blocks restoration rather than overwriting it. Read/Refresh never performs automatic recovery. Corrupt/foreign history is retained and disables changes; do not delete it automatically to unblock writing.

Registry operations use optimistic expected-value checks, not an OS-wide atomic transaction. Another tool can race or later replace the same policy. Readback confirms stored registry data at that moment, not effective Windows behavior. Preserve this distinction in future UI/tests/release claims.

## Verification rule

`--verify-windows-settings` is mandatory in scripts/Verify.ps1. Native production probes are read-only. Every test write, including simulated machine policies, is mapped to one freshly generated `HKCU\Software\Hush.TestFixtures\GUID` root; it never invokes production registry Write. The test checks the root prefix/GUID before removing it. File history lives only inside the unique UI fixture. No real policy change, installed-app self-update or user cleanup is part of verification.
