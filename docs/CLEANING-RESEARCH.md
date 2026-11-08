# Cleaning research (November 7, 2026)

What was looked at, what was learned, and what Hush did with it. Only public descriptions and documentation were read; **no third-party code or rule file was copied**. Hush's locations come from Windows documentation, the Disk Cleanup handler list on this PC and general knowledge of well-known caches, then were checked against a real machine with the read-only `--scan-report`.

## Tools reviewed

| Tool | License / status | What it offers | What Hush took |
| --- | --- | --- | --- |
| [BleachBit](https://docs.bleachbit.org/doc/winapp2ini.html) | Source under GPL-3 (CleanerML repo GPL-3+). The community `winapp2.ini` has no explicit license and its status is disputed. | Hundreds of per-application cleaners as XML, plus winapp2.ini import. | The *idea* of per-application cache rules only. Neither rule set was imported or copied (license). |
| Microsoft PC Manager | Free Microsoft app, closed source. No official list of what Deep cleanup removes. | A scan that pre-checks only items it considers safe; Storage page with large/duplicate files. | The "pre-check only safe items" behavior (Hush: only Temp and Thumbnails). One reviewer measured about 2.2 GB predicted vs 425 MB freed, so Hush measures real free space before and after a clean and says what the disk reports. |
| [PrivaZer](https://privazer.com/en/) | Free with a paid PRO, closed source; the page does not say which features are free. | Browser data, cookies, histories, jump lists, USB history, free-space wiping, shredder. | Categories for browser history, cookies and usage traces. **Not** adopted: free-space wiping, shredder, USB history, jump lists (jump lists hold pinned items). |
| [Bulk Crap Uninstaller](https://github.com/Klocman/Bulk-Crap-Uninstaller) | Apache-2.0. | Bulk uninstall, leftover and orphan detection. | Nothing copied. Hush already finds only orphaned App Paths and broken shortcuts with a double check (executable and folder both missing). Uninstalling programs is out of scope. |
| Windows Storage Sense and Disk Cleanup | Microsoft. [Storage Sense help](https://support.microsoft.com/en-us/windows/manage-drive-space-with-storage-sense-654f6ada-7bfc-45e5-966b-e24aded96ad5) names Previous Windows installations under Temporary files. Disk Cleanup's handlers live under `HKLM\...\Explorer\VolumeCaches` and are run with `cleanmgr /sageset` and `/sagerun`. | The supported way to remove Windows.old and update leftovers. | Windows.old goes through the "Previous Installations" handler (one handler selected, switch removed afterwards, after UAC and explicit consent). Verified on this PC that the handler exists (`Previous Installations`, `Update Cleanup`, `Temporary Files`, `Thumbnail Cache`, `Windows Error Reporting Files`, `Internet Cache Files`, `D3D Shader Cache`, `Delivery Optimization Files` and others). |
| [Win11Debloat](https://github.com/Raphire/Win11Debloat) | MIT. | App removal, privacy/telemetry tweaks, UI tweaks, Delivery Optimization sharing. Does **not** clean files or caches. | Nothing to take for cleaning. Its privacy tweaks overlap the existing Settings page (Windows policy switches with Undo); app removal and service changes were deliberately not added. |

## Methods adopted (verified, Windows 11, safe)

- Temp, thumbnail, WER, shader (DirectX, NVIDIA, AMD, Intel), browser caches (Edge, Chrome, Firefox, Brave, Vivaldi, Opera), Go/NuGet/npm/pip/Yarn/JetBrains/Visual Studio caches, Epic and Steam web caches.
- Windows log and trace files by extension, aged, never `.evtx`, security or boot logs.
- Windows.old and component cleanup through Microsoft's own tools; clipboard history through the Windows clipboard API (only offered when the user turned history on).
- Browser history (Chromium) and cookies (Chromium, Firefox) as file-level rules with the browser closed; Review and Advanced respectively.

## Methods rejected, and why

- **INetCache as a whole.** It holds Outlook and Word attachment copies, and on this PC it also contains Chromium-style folders (`Default`, `WidevineCdm`, `Safe Browsing`, ...) that belong to an application. Only its legacy `IE` and `Content.IE5` folders are listed.
- Firefox history: `places.sqlite` also stores bookmarks.
- Registry MRU lists, jump lists, Prefetch, Defender data, WMI/driver stores, `System32\LogFiles\WMI`, pnpm store, Spotify storage, Office unsaved-file caches, `.nuget\packages` (offline restore would break).
- `DISM /ResetBase` (makes every installed update permanent).
- Free-space wiping and shredding (not a cleaner's job here, and destructive).

## Caveats

- Per-application paths were confirmed only where the app is installed on the development PC (Brave, NVIDIA, Office, Windows folders); others come from documentation and are harmless if the folder is missing.
- The real Disk Cleanup/DISM/clipboard actions were never run on the development PC; their command lines and gating are covered by tests with fakes and string checks.

## Second pass: what a real PC showed (November 8, 2026)

After a user reported that temp and app junk was still everywhere after cleaning, this PC was surveyed read-only: every folder named cache, temp, tmp, logs, crash and similar under AppData, Roaming and ProgramData over 5 MB (about 150), then compared with Hush's rules. BleachBit's published cleaner definitions (VS Code family, Zen, GitHub Desktop, Android Studio, Adobe, Visual Studio, Office) were read to cross-check which folders such tools treat as cache or logs; the lists in Hush are Hush's own (folder names such as `Code Cache` and `GPUCache` are facts about Chromium, not anyone's expression), and nothing was copied.

**Added:** the temp-age choice (the leftovers in `%TEMP%` were all under the old 2-day limit), Prefetch (Review), CapCut `User Data\Cache`, Office `16.0\Wef`, updater installers (`installer.exe`, `current.blockmap`, `pending` in `<app>-updater` and `*.update` folders), uv and node-gyp caches, Adobe `Analyzer Cache Files`, `SquirrelTemp`, Edge updater logs, and generic discovery of Electron/WebView2/CEF caches, Firefox-family caches (Zen alone was 1 GB), per-app Logs, Temp, Crashpad and CrashDumps (see CLAUDE.md, "App discovery").

**Looked at and deliberately not added:** `ProgramData\Package Cache` (1.7 GB; the installer cache that repair and uninstall need), `Pub\Cache` (holds globally installed Dart tools), Adobe `UXP` and `Motion Graphics Templates` (installed plugins and templates), Office `SolutionPackages` and `OfficeFileCache`, `Notepad++\backup` (unsaved work), `Zen\Profiles\*\storage` and any browser `IndexedDB`/`Local Storage` (site data), `Claude Code`'s `.claude` folder (the user's sessions and memory), Vivaldi's `Application\<version>\Installer`, `Waves Audio\instl\Cache` and similar vendor installer caches (needed for repair), `Windows\SystemTemp`, `WebCache` (Explorer's ESE database), and game launcher content.
