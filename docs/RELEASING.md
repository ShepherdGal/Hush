# Publishing Hush updates

Only publish when the user asks. Only change the version when the user says so (see CLAUDE.md).

## Repository

The updater checks GitHub `ShepherdGal/Hush` (UpdateService.Repository) for the latest stable release with a `Hush-Portable.zip` asset (GitHub does not accept `.exe` uploads; the updater unpacks `Hush-Portable.exe` from the zip after verifying the zip's SHA-256). The check and download are anonymous, so in-app updates only work for other people while the repository (or at least its releases) is public; while it is private the app says updates cannot be checked right now. Making it public is the owner's decision. Never put a token in Git, the executable, a release, settings, logs or chat.

Status as of November 7, 2026: the old repository was deleted by the owner and https://github.com/ShepherdGal/Hush was created as a **public** repository from this project folder (`origin`, branch `main`). Releases `v0.7` and `v0.8` are published (v0.8 on November 7, 2026, same three assets); v0.7 is published with `Hush-Setup.exe`, `Hush.exe` and `Hush-windows-x64.zip`. GitHub CLI (`gh`) is installed and signed in on this PC, so a release is: commit, `git tag -a vX.Y -m "Hush X.Y"`, `git push origin vX.Y`, then `gh release create vX.Y "artifacts\Hush-Portable.zip#Hush-Portable.zip - portable (no install)" "artifacts\Hush-Setup.zip#Hush-Setup.zip - installer" "artifacts\LICENSE#LICENSE - MIT license" "artifacts\Hush-icon.png#Hush-icon.png - app icon" --repo ShepherdGal/Hush --verify-tag --title "..." --notes-file notes.md --latest` (the text after `#` is only the label people see; the file names stay exactly `Hush-Portable.zip` and `Hush-Setup.zip`, which the in-app updater and the README's `releases/latest/download/...` links rely on). Always publish only when the user asks, and verify the asset digests afterwards.

## Release steps

1. Confirm the version in Hush.csproj with the user; update ReleaseNotes.cs, docs/CHANGELOG.md and docs/HANDOFF.md.
2. Run `scripts/Verify.ps1` (use `-Configuration Release` if a preview locks the Verification build).
3. Run `scripts/Build.ps1` (self-contained and compressed, about 60-70 MB; nothing to install on the user's PC). `-FrameworkDependent` makes a small build for local tests only.
4. Commit the reviewed source and docs in the project folder (`.gitignore` already keeps bin/obj/artifacts/Backups/settings/logs and binaries out).
5. Tag `vX.Y` or `vX.Y.Z` and create a non-prerelease GitHub release with plain-language notes.
6. Start the release notes with the "Which file?" block below, then the changes. Attach the exact `Hush-Portable.zip` from `artifacts` (the portable app; required for in-app updates) and `Hush-Setup.zip` (the installer: the same program, which opens setup because of its name). Never upload the loose `.exe` files (GitHub refuses them). Never replace a published tag's binary; publish a new version instead.

## How in-app updates work

- About → Check for updates reads the latest stable release anonymously (no sign-in, no Authorization header, no GitHub wording in the UI). The old "Connect GitHub" step and its credential-helper code were removed by request.
- Download & install (one click, with a live progress bar) verifies size, GitHub SHA-256 digest, source repo and that the file is an x64 `Hush` executable with the expected version, then hands over to the updater without a second confirmation.
- The helper waits for the app to exit, swaps in the new exe with a rollback copy (`Hush.previous-*.exe`), restarts, and rolls back if the new app does not report ready within 15 seconds. Staging lives in `%LOCALAPPDATA%\Hush\Updates`.
- Only the published single-file app in a writable folder can self-update. The app is unsigned; SHA-256 checks the download against GitHub, not an Authenticode signature.
- The installer is per user and writes only inside the user's profile (program folder, Start menu/desktop shortcuts and the Hush uninstall entry under HKCU). The installed copy is a normal `Hush.exe` in a writable folder, so in-app updates work for it too; after an update the Settings > Apps version label is refreshed on the next start. Updates only ever look for the asset named `Hush-Portable.zip`, so the extra `Hush-Setup.zip` asset does not interfere. Versions up to 0.8.1 looked for an asset named `Hush.exe`, so they cannot update to a release that only has zips; they need one manual download of the new zip.

## Release notes template

Every release body starts with this, so people can find the right download at a glance:

```
**Which file?**
- **Hush-Portable.zip** - portable. Unzip, run Hush-Portable.exe; nothing is installed.
- **Hush-Setup.zip** - installer. Unzip, run Hush-Setup.exe. For your account only, no administrator rights.
You only need one. Needs Windows 10/11 64-bit; nothing else to install. Use at your own risk (see docs/TERMS.md).

In brief
- (at most five one-line bullets in plain words, for example "Cleans system Temp files". The app shows this section in its update popup under "What's new in this version"; keep it short.)

What's new
- ... (the long notes stay on the release page)
```

Also update `src/Hush/Updates/ReleaseNotes.cs` (`Current`) with the same short bullets for the version being released: the About window shows it for the installed version.

## One place for the files on the build PC

`scripts/Build.ps1` writes `Hush-Portable.exe`, `Hush-Setup.exe`, `Hush-Portable.zip`, `Hush-Setup.zip` and WHICH FILE.txt (plus LICENSE and the icon) into `artifacts\` and removes the old nested `artifacts\Hush\` folder. Test builds create many extra `Hush.exe` copies under `src\...\bin` and `tests\...\bin`; `scripts/Tidy.ps1` lists them and (with `-Apply`) removes them, skipping any folder with a running program in it. The app itself keeps a single rollback copy (`Hush.previous.exe`) after an update and removes finished update staging under `%LOCALAPPDATA%\Hush\Updates` on its next start.