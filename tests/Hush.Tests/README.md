# Verification

Run `scripts/Verify.ps1` from PowerShell. It uses the separate `Verification` build configuration, so testing does not overwrite the executable of an open Release app.

The script runs the full safety and UI suite, then deliberately fails a UI assertion in a separate test process. It verifies that the failure is reported on standard error, the process returns exit code 1, and the test UI shuts down. Successful tests return 0. This prevents an assertion failure from escaping to Windows as an unhandled CLR exception and displaying an Application Error dialog.

Both modes create and delete only synthetic fixtures. The deliberate failure mode never runs cleanup.

To inspect the controlled failure manually after a build:

```powershell
.\tests\Hush.Tests\bin\Verification\net10.0-windows10.0.19041.0\Hush.Tests.exe --verify-failure-path
# Expected: detailed FAIL output and exit code 1, with no crash dialog.
```

`Hush.Tests.exe` is a developer test runner. The user application is `artifacts/Hush-Portable.exe`; the portable ZIP contains the app, the terms and the license.

`Hush.Tests.exe --benchmark` is a synthetic-only performance run (20,000 files, 420 directories, a unique `.fixtures-*` folder that is removed afterwards). It prints scan time (best of 3), allocations, clean time and memory. Use it to compare engine changes before and after; it is not part of `scripts/Verify.ps1` because timings vary by machine.

`Hush.Tests.exe --scan-report` is a read-only scan of the real PC with the default targets. It prints, per category, item count, size and how many items would be selected by default, plus the largest groups. It never calls Clean and writes nothing; use it to confirm new locations exist on a real machine. Categories needing administrator rights (Update, Delivery, Setup, Windows Logs, Installer Leftovers, system dumps) show 0 unless it is run elevated.

For synthetic WPF review previews, set `HUSH_PREVIEW_DIRECTORY` to a writable output folder before running `scripts/Verify.ps1`. The suite exports both themes at default and minimum sizes using fixture paths only. The application does not save these previews during normal use.

`Hush.Tests.exe --verify-setup` (part of scripts/Verify.ps1) exercises the installer and uninstaller against temp folders and a `HKCU\Software\Hush.TestFixtures\<GUID>` registry key only: verified copy, shortcuts, Settings > Apps entry, upgrade, locked and fake files, uninstall safety and both setup windows. It never installs or removes Hush on the real PC.

`Hush.Tests.exe --verify-smart-boost` (part of `scripts/Verify.ps1`) drives the real Settings controls: Smart Boost starts with no timer, resource indicators sample only while Settings is open, the startup list writes only the StartupApproved flag in a fixture registry key (with Undo), Automatic Boost asks for consent and records it, the theme change keeps the Smart Boost settings, low-resource mode restores Hush, and Quick Boost ends in the confirmation without deleting anything.

`Hush.Tests.exe --stress-scan [files]` (not part of `scripts/Verify.ps1`; about two minutes at the default 250,000 files) creates a synthetic fixture above the old 150,000-file limit, then checks that a scan lists every file exactly once with monotonic progress, a count-only scan matches, cancellation returns partial results quickly, the review list stays virtualized, selecting everything is instant, and a clean removes exactly the listed files with two audit lines per file. It prints scan/clean speed and memory per file.
