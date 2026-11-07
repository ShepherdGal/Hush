# Verification

Run `Tests/Verify.ps1` from PowerShell. It uses the separate `Verification` build configuration, so testing does not overwrite the executable of an open Release app.

The script runs the full safety and UI suite, then deliberately fails a UI assertion in a separate test process. It verifies that the failure is reported on standard error, the process returns exit code 1, and the test UI shuts down. Successful tests return 0. This prevents an assertion failure from escaping to Windows as an unhandled CLR exception and displaying an Application Error dialog.

Both modes create and delete only synthetic fixtures. The deliberate failure mode never runs cleanup.

To inspect the controlled failure manually after a build:

```powershell
.\Tests\bin\Verification\net10.0-windows\Hush.Tests.exe --verify-failure-path
# Expected: detailed FAIL output and exit code 1, with no crash dialog.
```

`Hush.Tests.exe` is a developer test runner. The user application is `artifacts/Hush/Hush.exe`; the portable ZIP contains only the app and its README.

`Hush.Tests.exe --benchmark` is a synthetic-only performance run (20,000 files, 420 directories, a unique `.fixtures-*` folder that is removed afterwards). It prints scan time (best of 3), allocations, clean time and memory. Use it to compare engine changes before and after; it is not part of `Verify.ps1` because timings vary by machine.

`Hush.Tests.exe --scan-report` is a read-only scan of the real PC with the default targets. It prints, per category, item count, size and how many items would be selected by default, plus the largest groups. It never calls Clean and writes nothing; use it to confirm new locations exist on a real machine. Categories needing administrator rights (Update, Delivery, Setup, Windows Logs, Installer Leftovers, system dumps) show 0 unless it is run elevated.

For synthetic WPF review previews, set `HUSH_PREVIEW_DIRECTORY` to a writable output folder before running `Verify.ps1`. The suite exports both themes at default and minimum sizes using fixture paths only. The application does not save these previews during normal use.
