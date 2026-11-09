# Hush 0.9 plan

Written November 9, 2026 from the user's 0.9 wish list. Each item says what is true today (checked in the code), what 0.9 will do, and how we know it is done. **Built November 9, 2026: every item below is done (see the CHANGELOG "0.9 work" and the HANDOFF for what could not be verified on this PC).** The version number stays 0.8.3 until the user says to change it.

The rules these items needed changed are already rewritten in CLAUDE.md and AGENTS.md (November 9, 2026), so a builder can follow this plan without hitting an old rule. The user's latest direction still wins over anything here.

---

## Phase 1 — Install and basics (small, high impact)

### 0. [done] Administrator mode: ask once, then always run as administrator
**User direction (November 9); rules already updated in CLAUDE.md/AGENTS.md.** Hush asks once and, if the user agrees, always runs elevated, so it can clean Windows\Temp, Prefetch, trace logs, Windows Update files and everything in items 8–13.

- **The question:** on first run, right after the Terms are accepted, one popup in the app's own look: "Let Hush run as administrator?" with a short line on why (system Temp, Prefetch, logs and Windows Update files can only be cleaned as administrator) and what does not change (Hush still shows everything first and asks before cleaning). Action on the left (*Run as administrator*), *Not now* on the right. The answer is stored as `Preferences.RunAsAdmin` (true/false), so it is asked only once. Settings has a switch to change it later. Existing 0.8 users see the question once on their first 0.9 start.
- **How "always" works:**
  - *Installed copy (recommended path):* when the user agrees, Hush moves the installed copy to `C:\Program Files\Hush` (admin-only folder) and registers one scheduled task, `Hush (administrator)`, "run with highest privileges" for this user, pointing at that exe. The Start menu and desktop shortcuts start the task, so there is **one** UAC prompt (the moment the user agrees) and no prompt on each start after that. The exe must live in an admin-only folder: a task pointing at a user-writable folder would let any program the user runs replace the exe and get admin silently.
  - *Portable copy (run from a zip or a user folder):* Hush relaunches itself elevated with Windows' own prompt on every start (Windows does not allow skipping UAC for a user-writable exe safely). The popup says so and suggests installing to avoid the prompt.
  - Turning the switch off removes the task, moves shortcuts back to a normal start, and the next start is not elevated.
- **Same person check:** if the elevated Hush runs as a different Windows account than the signed-in user (a standard user typed an admin's password), user-folder cleaning (AppData, Recent, browser caches) would hit the wrong profile. Hush then cleans only system locations elevated and says so; user locations stay with the signed-in user's account.
- **Everything else stays:** scan is still read-only, every delete re-verifies, logs, confirmation clicks, the Never-touch list and the Defender/restore-point rules are unchanged. Being admin only changes what is reachable.
- **Side effects to handle:** the updater writes into Program Files (fine, it is elevated); single-instance (item 2) must find a running Hush across elevation levels; the Process page can now end elevated apps (item 17); "Restart as administrator" buttons disappear when already elevated.
- **Tests:** fixture folders and `HKCU\Software\Hush.TestFixtures\<GUID>`; a fake task scheduler and fake elevation probe. Never register a real task or move the real app on this PC.

**Done when:** first run asks once; after *Run as administrator* the installed Hush starts elevated from the Start menu with no further prompt; Windows\Temp and Prefetch files show up in a scan; turning the switch off undoes the task.

### 1. [built; clean-VM test pending] "This PC needs .NET 10" on other computers
**Today: not fixed.** `scripts/Build.ps1` builds framework-dependent by default (`-Standalone` is off), so a PC without the .NET 10 Desktop Runtime shows Windows' runtime error, and `WHICH FILE.txt` only says "needs the .NET 10 Desktop Runtime".

**0.9:** ship the self-contained build as the normal release. The runtime goes inside `Hush-Portable.exe` / `Hush-Setup.exe`, so there is nothing to install and nothing to explain.
- Cost: the download grows from a few MB to roughly 60–70 MB (trimming is not safe for WPF, so we use compression in the single file instead). **The user accepted this (November 9).**
- Rejected alternative: a small launcher that downloads the runtime from Microsoft. It needs admin rights, an internet connection and a second program to maintain, and it can still fail halfway. Simpler is safer here.
- Updater: check that the download progress, SHA-256 check and the single rollback copy (`Hush.previous.exe`) still work with the larger file.

**Done when:** a clean Windows 10 and 11 VM with no .NET installed opens Hush from the zip with no prompts; Build.ps1 default is self-contained; `WHICH FILE.txt`, README and RELEASING.md no longer mention the runtime.

### 2. [done] Only one Hush at a time
**Today: not handled.** There is no single-instance check, so every double-click opens another window.

**0.9:** a named mutex per user session (`Local\Hush-<user SID>`). A second start does not open a window; it asks the running Hush to come to the front (restore it if minimized) and exits. Setup and uninstall modes get their own mutex so the installer can still replace the running copy after it closes. The "Restart as administrator" path hands over cleanly (the old one releases the mutex before the elevated one starts).

**Done when:** starting Hush five times quickly gives one window that comes to the front; restart-as-admin and the updater still relaunch correctly. Test with a child process, never by killing the user's Hush.

### 3. [done] Hush logo too bright while the window is active
**Today:** the logo looks brighter while Hush has focus and normal once the desktop is clicked, so something reacts to window activation (an `IsActive` trigger, the active caption/glow, or the icon frame Windows picks for the active title). To find in 0.9 and set to the calm (inactive) look in both states.

**Done when:** preview PNGs of the active and inactive window show the same logo in Dark and Light.

### 4. [done] Hover on the main Scan button
Give the gauge's Scan button the same hover as the other glowing buttons: the existing glow gently gets stronger over 280 ms. No scaling (global rule).

### 5. [done] Tell the user what "Clean" cleans after a scan
After a scan, Clean only removes the green (Safe) items. The clean confirmation from Home will say this in one line, e.g. "Cleans only the green, recommended items. Use Take a look to choose more." Same wording rules as today: no speed claims.

### 6. [done] Actions button chevron
The chevron on Settings > Actions turns upside down when the menu opens and back when it closes (click the button again, pick an action, Escape or click elsewhere all close it). The rotation uses the shared 280 ms easing from Motion.cs.

### 7. [done] "Other Browsers" goes last
Move `BrowserOther` to the end of `ReviewCategory.Definitions` so it is the last row on Review (it stays Review/orange and opt-in). People with many tabs open are least likely to tick it by accident.

---

## Phase 2 — Cleaning more, and cleaning what we already find

### 8. [done] Why the system folders "aren't cleaned" — the main finding
Most folders on the list **are already Hush targets**: `AppData\Local\Temp`, `Windows\Temp`, `Windows\Prefetch`, NuGet caches, Windows event trace logs (`Diagnosis\ETLLogs`, `Logs\SIH`, Update `.etl`), Recent items, Clipboard history, Thumbnails and Defender logs. They come back nearly empty for three reasons:

1. **Hush runs without administrator rights** (`app.manifest` is `asInvoker`). Every location under `C:\Windows` and `ProgramData` is `DeletableOnly`, so as a normal user Hush lists only the few files that user may delete — usually none in `Windows\Temp`, `Prefetch`, ETL logs, Update downloads.
2. **Age limits:** Temp 1 day, Prefetch 1 day, ETL 7 days, Update 14 days, NuGet 14 days. A freshly used PC has little that old.
3. **Files in use:** `thumbcache_*.db` is held by Explorer, many Temp files by running apps; these are always skipped.

**0.9:** solved by **administrator mode (item 0)**. Elevated, the `DeletableOnly` check lets those files through, and every delete is still re-verified and logged. While administrator mode is off, the scan note says how much it could not reach and offers to turn it on.

Per folder:
| Folder | Today | 0.9 |
|---|---|---|
| `AppData\Local\Temp` | Safe, 1 day | Safe, **any age** (see item 9), in-use files skipped |
| `Windows\Temp` | Safe, 1 day, needs admin to see most files | Safe, any age, through the admin step |
| `Windows\Prefetch` | Review, 1 day, admin | Review, any age, admin step. Stays Review: the next launch of each program is a little slower |
| NuGet | `v3-cache`, `plugins-cache`, `Cache`, 14 days | add `%USERPROFILE%\.nuget\packages` as a **separate Review row** ("NuGet packages — re-downloaded on the next build, offline builds fail until then") |
| Windows event trace logs | 7–14 days, admin | 1 day, admin step. Never `Logs\CBS` in use, never Security/System event logs (`.evtx`) |
| Windows recent access | Recent `*.lnk`, Review | unchanged rule; check why the user saw none (count shown only via `CountedByItems`) |
| Clipboard | only offered when Win+V history is on | also offer "Clear the current clipboard" when history is off |
| System usage traces | Recent + Office recent | unchanged. Explorer search and Run history (registry MRU lists) stay out: **the user said no (November 9)** |

### 9. [done] Remove Settings > Temporary files; always find files of any age
**Rule changed (November 9).** Delete the Temporary files card, `Preferences.TempAge` and `MainWindow.TempAge.cs`. Temp targets use age zero; the safety that stays is "a file in use is always skipped" (the exclusive-handle delete already guarantees this) plus the delete-time re-check. The `HeldBytes` "too new" split in the meter tooltip and the Boost popup goes away because nothing is held back any more. Update CLAUDE.md's "Temporary Files age" paragraph and the tests that encode the 1-day default.

Honest note for the user: a program can keep a temp file closed between uses (installers mid-update, a few editors). With no age limit Hush can remove such a file; the program then usually recreates it. That is why Windows' Storage Sense uses 1 day. The user has chosen any age; the plan follows that.

### 10. [done] Thumbnails
Thumbnail cache is already Safe, but Explorer keeps the `thumbcache_*.db` files open, so they are skipped. 0.9 runs Windows' own **Disk Cleanup "Thumbnails" handler** (the same way Previous Windows Installations already runs through Microsoft's handler), which can clear the cache properly. Explorer is never closed or restarted by Hush.

### 11. [done] Microsoft Defender Antivirus files
Today Hush cleans only Defender's diagnostic logs. 0.9 adds Disk Cleanup's own **"Microsoft Defender Antivirus"** handler as a Review system tool: it removes Defender's non-critical files (old definition backups and temporary files) the way Microsoft designed. Protection history, quarantine, scan history and current definitions stay untouched (the existing test keeps enforcing that).

### 12. [done] "Windows Update Cleanup" / temp files fail to delete on some PCs
First collect facts: ask for `%LOCALAPPDATA%\Hush\Logs` from an affected PC. Likely causes, in order:
1. Not elevated (see item 8) — files are listed from a cached Disk Cleanup estimate but cannot be removed.
2. The Windows Update service or BITS holds files in `SoftwareDistribution\Download` while an update is in progress.
3. Disk Cleanup's "Windows Update Cleanup" handler only works when run as admin and can take many minutes; a timeout may report it as failed.

0.9 fixes: show *why* a file was skipped (in use / needs admin / changed since scan) in the result, run the handler elevated with a longer time limit and a "still working" state, and if an update is installing, skip the Update category with a clear note instead of failing. Hush will not stop Windows Update services to force deletes.

### 13. [done] WinSxS — is it safe to delete, and how?
**Short answer: never delete anything in `C:\Windows\WinSxS` by hand.** It is the component store; most of what Explorer shows as its size are hard links to files Windows is using right now, so the real extra size is much smaller than the folder looks. Deleting files there can break Windows Update, Settings and repair.

The safe ways, all Microsoft's own:
1. **Look first:** `DISM /Online /Cleanup-Image /AnalyzeComponentStore` shows the real reclaimable size and whether cleanup is recommended.
2. **Component cleanup (Hush already has it, Advanced):** `DISM /Online /Cleanup-Image /StartComponentCleanup` removes superseded versions of updated components. Windows also does this by itself every few weeks.
3. **`/ResetBase` (deeper):** also removes the ability to uninstall any update installed so far. It frees more, cannot be undone, and Hush currently never runs it.

0.9: add the "Analyze" step so the component cleanup row shows the real reclaimable size before the user decides. **The user approved `/ResetBase` (November 9):** it becomes its own red (Advanced) system-tool row, "Deep component cleanup", next to the normal component cleanup. Never selected by default, the consent checkbox is required, and the description says plainly: "Updates installed so far can no longer be uninstalled. This cannot be undone." It runs only through DISM, elevated, with a long time limit and a "still working" state. Hush still never deletes WinSxS files itself.

---

## Phase 3 — Layout

### 14. [done] Review files: three color groups, then the categories
The Review page first shows **three cards: Green (Safe), Amber (Review), Red (Advanced)**, each with its total size, number of items and a selected count. Clicking one opens its categories (Temporary Files, Thumbnails… for Green) in the same card style and animations as today, with the shared Back button returning to the three groups. Only the layout changes: category cards, checkboxes, the Advanced consent, virtualization and the `BulkCollection` reset rule stay as they are. A group with nothing found is hidden. CLAUDE.md now describes this layout (rule changed November 9).

### 15. [done] Settings: fewer, fuller cards
Today each section feels like a separate card per row, which wastes space (Process most of all). 0.9 packs each Settings section into **two cards side by side** (stacking to one column when the window is narrow) and moves every remaining description into the `HelpMark` "?" popup, as already done for Windows choices. Process becomes a compact list inside one card. Keeps the Info card rules (at most eleven texts).

### 16. [done] Progress bars that fill
Replace the travelling bar (Update check) and the sweep (Boost) with **one bar that fills from empty to full**, smoothly eased:
- Update download and Boost cleaning: real progress (bytes, files).
- Update check and "Freeing memory": no real progress exists, so the bar fills over the stage's minimum time (3.5 s check, `BoostStageMinimum`) and jumps to full the moment the answer arrives. It never sits at "full" before the work is really done.
Shared control in `UpdateActivity.cs`/Motion.cs so every loading line looks the same; capped frame rate, render-thread animation.

### 17. [done] Process list misses apps (Media Player, PC Manager…)
**Cause found in `ProcessManager.cs`:**
- **Store apps** (Media Player, Photos, Calculator…): their window belongs to `ApplicationFrameHost`, which is on the protected list, so the app's own process never "has a window" and is hidden.
- **Apps that run elevated or minimize to the tray** (Microsoft PC Manager): no visible top-level window in our session, or the path cannot be read without admin, so they are filtered out.

0.9: map an `ApplicationFrameHost` window to the app process it hosts (its child window's process), list tray-only apps from a short rule (has a notification-area icon or a visible window in the last scan), and show elevated apps with "Needs administrator to end" instead of hiding them. End stays exactly as safe as now (exact processes, start time re-checked, close politely first, log).

### 21. [done] "There is a new update!" next to the logo
When Hush opens it checks for an update quietly in the background (the same anonymous release check, no prompt, no download). If a newer version exists, a small **green** text (`GoodBrush`, upright, the shared small font size, never italic) appears to the right of the Hush wordmark in the top-left: **"There is a new update!"**. It fades in (320 ms) and has the usual text hover (color eases, no scaling). Clicking it opens the update popup, which runs the check (with the filling bar from item 16) and then asks "Update Hush to X?" as today; nothing downloads before that consent. No text appears when Hush is up to date, the check fails or there is no internet. The background check runs once per start and never slows startup.

### 22. [done] Update popup: status under the version, then short release notes
New order inside the update popup / About card:
1. **Version 0.8.3**
2. Directly below it, the status line: the filling search bar while checking, then "You're up to date" (green), "Hush X is available" (accent, semibold) or the error, and the *Update now* button.
3. Below that, **What's new in this version**: the notes for the installed release, compact. At most about five one-line bullets, no paragraphs, no technical detail (for example "Cleans system Temp files", "One Hush window at a time"). The release notes template in RELEASING.md gets a short "In brief" section that the app shows; the long notes stay on the release page.

---

## Phase 4 — System changes

### 18. [done] Restore point before big changes
Before applying Windows choices (the ShutUp10-style Actions or several switches at once), the confirmation popup offers **"Create a restore point first"** (on by default). Created through Windows' System Restore API, which needs administrator rights.
- If System Protection is off on drive C:, the popup says so and offers to turn it on (admin) or continue without.
- Windows allows only one restore point per 24 hours by default; if one was made recently, Hush says it will rely on that one rather than failing silently.
- Hush never deletes restore points (existing rule).

### 19. [done] Turn off Windows Update (red warning)
**Rule changed (CLAUDE.md updated November 9).** The user chose to switch updates **off completely** when a user picks it. 0.9 adds **"Turn off Windows Update"** as a red (Advanced) Windows choice in its own *Windows Update* topic:
- Off means off: the Windows Update policies (`NoAutoUpdate`, no automatic download or install), the `wuauserv`, `UsoSvc`, `WaaSMedicSvc` and `BITS` service start types set to Disabled, and the Update Orchestrator scheduled tasks disabled. `WaaSMedicSvc` is protected by Windows; if its start type cannot be changed, the result says exactly what was and wasn't changed. Never claim success that wasn't verified.
- Every original value (policy, service start type, task state) is saved before the change; Undo puts them all back and restarts the services. Undo all changes includes it.
- Red, opt-in, needs the Advanced consent checkbox, and the warning says plainly: "Your PC stops getting security fixes and Defender updates through Windows Update until you turn this back on."
- Not part of *Apply only recommended* or the amber action. *Apply all settings* includes it only after its own consent checkbox.
- While it is on, Settings shows a lasting red line with a one-click *Turn Windows Update back on*.
- Tests use the registry fixture root and a fake service/task controller; never change the real services on this PC.

### 20. [done] Ideas from WPD (the telemetry blocker)
WPD (by WPD Team) offers three groups. What fits Hush:
- **Privacy settings** (group policies, services and scheduled tasks for telemetry): compare its list against `docs/WINDOWS-SETTINGS.md` and add the ones we lack, with backups and Undo like today. Scheduled tasks and services would be a new kind of Windows choice (CLAUDE.md says Smart Boost never changes them; CLAUDE.md now allows them on the Privacy page with backups and Undo).
- **Firewall blocking of Microsoft telemetry IPs:** powerful but risky (lists go stale, can break Windows Update, Store, Defender cloud protection). Proposed as a later, separate red feature if the user wants it, not in 0.9.
- **Store app uninstaller:** fits the "startup/app management" direction; a candidate for 0.10.
Research only in 0.9 unless the user picks items; no code copied from WPD.

---

## Order of work and checks

1. Phase 1 (item 0 administrator mode first, then the rest), then a build on a clean VM for item 1.
2. Items 8 and 9 before the other cleaning items, because they change what every later item finds.
3. Phases 3 and 4.

For every item: `scripts/Verify.ps1` passes; engine changes also run `--benchmark` and `--stress-scan`; UI changes are checked on preview PNGs in both themes; new delete/system-change logic gets fixture tests (never real cleanup, restore points, update pauses or ended apps on this PC). CLAUDE.md, HANDOFF.md and CHANGELOG.md are updated with each rule change.

## Decisions (answered by the user, November 9, 2026)
1. Bigger (~60–70 MB) self-contained download so no .NET install is needed: **yes**.
2. Explorer search / Run history (registry MRU lists) in usage traces: **no**.
3. DISM `/ResetBase` as a red option: **yes** (item 13).
4. Windows Update: **switch it off completely** when the user chooses it (item 19).
5. Administrator mode: Hush asks once; if the user agrees it always runs as administrator (item 0).
