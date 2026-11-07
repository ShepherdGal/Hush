# Publishing Hush updates

Only publish when the user asks. Only change the version when the user says so (see CLAUDE.md).

## Repository

The updater checks GitHub `ShepherdGal/Hush` (UpdateService.Repository) for the latest stable release with a `Hush.exe` asset. The check and download are anonymous, so in-app updates only work for other people while the repository (or at least its releases) is public; while it is private the app says updates cannot be checked right now. Making it public is the owner's decision. Never put a token in Git, the executable, a release, settings, logs or chat.

Status as of November 7, 2026: the project folder is a fresh Git repository with `origin` set to `https://github.com/ShepherdGal/Hush.git`, but nothing has been pushed. The old GitHub repository (with its previous history and release) must be deleted or renamed away by the owner first, then the new empty `Hush` repository created and pushed:

1. GitHub, old repository, Settings, Danger Zone: Delete this repository (or rename it to something else if the old history should be kept).
2. GitHub, New repository: name `Hush`, **public** (required for anonymous in-app updates), with no README, .gitignore or license so it starts empty.
3. In the project folder: `git push -u origin main`.
4. When releasing: `git tag v0.7`, `git push origin v0.7`, then create a release for that tag and attach `artifacts/Hush/Hush.exe` (and the zip).

## Release steps

1. Confirm the version in Hush.csproj with the user; update ReleaseNotes.cs, Docs/CHANGELOG.md and Docs/HANDOFF.md.
2. Run `Tests/Verify.ps1` (use `-Configuration Release` if a preview locks the Verification build).
3. Run `Build.ps1` (framework-dependent; needs .NET 10 Desktop Runtime) or `Build.ps1 -Standalone`.
4. Copy reviewed source/docs into `_publish/repository` (never bin/obj/artifacts/Backups/settings/logs) and commit.
5. Tag `vX.Y` or `vX.Y.Z` and create a non-prerelease GitHub release with plain-language notes.
6. Attach the exact `Hush.exe` from `artifacts/Hush` (required for in-app updates) and `Hush-windows-x64.zip` for manual downloads. Never replace a published tag's binary; publish a new version instead.

## How in-app updates work

- About → Check for updates reads the latest stable release anonymously (no sign-in, no Authorization header, no GitHub wording in the UI). The old "Connect GitHub" step and its credential-helper code were removed by request.
- Download & install (one click, with a live progress bar) verifies size, GitHub SHA-256 digest, source repo and that the file is an x64 `Hush` executable with the expected version, then hands over to the updater without a second confirmation.
- The helper waits for the app to exit, swaps in the new exe with a rollback copy (`Hush.previous-*.exe`), restarts, and rolls back if the new app does not report ready within 15 seconds. Staging lives in `%LOCALAPPDATA%\Hush\Updates`.
- Only the published single-file app in a writable folder can self-update. The app is unsigned; SHA-256 checks the download against GitHub, not an Authenticode signature.
