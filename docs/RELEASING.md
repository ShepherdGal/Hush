# Publishing Hush updates

Only publish when the user asks. Only change the version when the user says so (see CLAUDE.md).

## Repository

The updater checks GitHub `ShepherdGal/Hush` (UpdateService.Repository) for the latest stable release with a `Hush.exe` asset. The check and download are anonymous, so in-app updates only work for other people while the repository (or at least its releases) is public; while it is private the app says updates cannot be checked right now. Making it public is the owner's decision. Never put a token in Git, the executable, a release, settings, logs or chat.

Status as of November 7, 2026: the old repository was deleted by the owner and https://github.com/ShepherdGal/Hush was created as a **public** repository from this project folder (`origin`, branch `main`). Releases `v0.7` and `v0.8` are published (v0.8 on November 7, 2026, same three assets); v0.7 is published with `Hush-Setup.exe`, `Hush.exe` and `Hush-windows-x64.zip`. GitHub CLI (`gh`) is installed and signed in on this PC, so a release is: commit, `git tag -a vX.Y -m "Hush X.Y"`, `git push origin vX.Y`, then `gh release create vX.Y artifacts\Hush-Setup.exe artifacts\Hush\Hush.exe artifacts\Hush-windows-x64.zip --repo ShepherdGal/Hush --verify-tag --title "..." --notes-file notes.md --latest`. Always publish only when the user asks, and verify the asset digests afterwards.

## Release steps

1. Confirm the version in Hush.csproj with the user; update ReleaseNotes.cs, docs/CHANGELOG.md and docs/HANDOFF.md.
2. Run `scripts/Verify.ps1` (use `-Configuration Release` if a preview locks the Verification build).
3. Run `scripts/Build.ps1` (framework-dependent; needs .NET 10 Desktop Runtime) or `scripts/Build.ps1 -Standalone`.
4. Commit the reviewed source and docs in the project folder (`.gitignore` already keeps bin/obj/artifacts/Backups/settings/logs and binaries out).
5. Tag `vX.Y` or `vX.Y.Z` and create a non-prerelease GitHub release with plain-language notes.
6. Attach the exact `Hush.exe` from `artifacts/Hush` (the portable app; required for in-app updates), `Hush-Setup.exe` from `artifacts` (the installer: the same program, which opens setup because of its name) and `Hush-windows-x64.zip` for manual downloads. Never replace a published tag's binary; publish a new version instead.

## How in-app updates work

- About → Check for updates reads the latest stable release anonymously (no sign-in, no Authorization header, no GitHub wording in the UI). The old "Connect GitHub" step and its credential-helper code were removed by request.
- Download & install (one click, with a live progress bar) verifies size, GitHub SHA-256 digest, source repo and that the file is an x64 `Hush` executable with the expected version, then hands over to the updater without a second confirmation.
- The helper waits for the app to exit, swaps in the new exe with a rollback copy (`Hush.previous-*.exe`), restarts, and rolls back if the new app does not report ready within 15 seconds. Staging lives in `%LOCALAPPDATA%\Hush\Updates`.
- Only the published single-file app in a writable folder can self-update. The app is unsigned; SHA-256 checks the download against GitHub, not an Authenticode signature.
- The installer is per user and writes only inside the user's profile (program folder, Start menu/desktop shortcuts and the Hush uninstall entry under HKCU). The installed copy is a normal `Hush.exe` in a writable folder, so in-app updates work for it too; after an update the Settings > Apps version label is refreshed on the next start. Updates only ever look for the asset named `Hush.exe`, so the extra `Hush-Setup.exe` asset does not interfere.
