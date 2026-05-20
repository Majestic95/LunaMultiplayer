# Breakage analysis — Player auto-updater workstream

Branch: `feature/auto-updater` (off `master` @ 6515e006).
Worktree: `F:/luna-multiplayer-updater/` (sibling to the per-agency worktree).

This workstream ships a player-facing Windows updater exe for the Majestic95 fork.
Three pieces land sequentially as separate commits:

- **Piece B** — Neuter the existing in-game UpdateHandler (this commit, FIRST).
- **Piece C step 1** — Template `LunaMultiplayer.version` from `Scripts/build-release.ps1`.
- **Piece A** — Add `Tools/PlayerUpdater/` (the new Windows exe).
- **Piece C step 2** — Publish the updater exe in the release zip set.

Piece B ships first because: (a) it is load-bearing — without it, the new exe and the in-game popup fight each other; (b) it is small and reviewable on its own; (c) the existing `new Version(tag_name)` parser at `LmpClient/Utilities/UpdateHandler.cs:23` throws on every fork tag (`v0.31.0-per-agency-private-7` is not a `System.Version` literal), so we are removing a code path that is already silently failing in production.

---

## Piece B — Scope

Remove the in-game "update available" surface entirely. Players will use the standalone exe (Piece A) for updates.

**Files to DELETE (load-bearing):**

| Path | Why |
|---|---|
| `LmpClient/Utilities/UpdateHandler.cs` | The coroutine that polls `RepoConstants.ApiLatestGithubReleaseUrl` (upstream) every session. `new Version(tag_name)` throws on fork tags — already broken. |
| `LmpClient/Windows/Update/UpdateWindow.cs` | The IMGUI window that opens when `UpdateHandler` sets `UpdateWindow.Singleton.Display = true`. |
| `LmpClient/Windows/Update/UpdateDrawer.cs` | Partner draw routine for UpdateWindow. |
| `LmpClient/Localization/Structures/UpdateWindowText.cs` | Localization strings used only by UpdateWindow. |
| `LmpClient/Localization/XML/<Language>/UpdateWindowText.xml` × 14 languages on disk (12 in csproj + Czech + Finnish that exist on disk but are not in csproj) | XML payloads for UpdateWindowText. |

**Files to MODIFY:**

| Path | Edit |
|---|---|
| `LmpClient/MainSystem.cs:213` | Remove the `else { StartCoroutine(UpdateHandler.CheckForUpdates()); }` branch entirely. The if-else collapses to a single if. |
| `LmpClient/Utilities/DisclaimerDialog.cs:24` | Remove the `MainSystem.Singleton.StartCoroutine(UpdateHandler.CheckForUpdates());` call inside the Accept handler. The disclaimer flow keeps everything else (DisclaimerAccepted=true, Enabled=true, SaveSettings). |
| `LmpClient/Localization/LocalizationContainer.cs` | Remove `UpdateWindowText` field (line 30), `LoadWindowTexts(language, ref UpdateWindowText)` call (line 91), `LunaXmlSerializer.WriteToXmlFile(UpdateWindowText, ...)` call (line 123). |
| `LmpClient/LmpClient.csproj` | Remove 4 `<Compile Include="...">` lines (UpdateHandler.cs, UpdateWindowText.cs, UpdateDrawer.cs, UpdateWindow.cs) + 12 `<Content Include="...UpdateWindowText.xml">` lines. |

**No additions.** Piece B is purely subtractive.

---

## Why this is safe (Piece B)

- **`IWindow` discovery is reflection-based.** [`LmpClient/Windows/WindowsHandler.cs:24`](../../LmpClient/Windows/WindowsHandler.cs) scans `Assembly.GetExecutingAssembly().GetLoadableTypes()` for IWindow implementers. Deleting `UpdateWindow.cs` removes the type entirely — there is no explicit registration to unwire.
- **The polling coroutine never produced useful output on this fork.** `RepoConstants.ApiLatestGithubReleaseUrl` returns `LunaMultiplayer/LunaMultiplayer/releases/latest`, and the fork is at protocol 0.31.0; upstream's "latest" is older. The popup told players to "upgrade" to a fork-incompatible build. Plus the parser throws on the fork's own tag format, so the path is structurally dead today.
- **No callers outside the two known sites.** Verified via `Grep` for `UpdateHandler|UpdateWindow|UpdateDrawer|UpdateWindowText` across the entire worktree — only 22 hits, all enumerated in the modification list above.
- **Localization is opt-in per-window**, not a fail-on-missing-text contract — removing `UpdateWindowText` from the `LoadWindowTexts` chain does not break the other 17 window-text structures' loading.
- **No tests reference any of the deleted symbols.** `LmpClientTest/` has no UpdateHandler/UpdateWindow cases.

---

## Edge cases (Piece B)

- **Players who upgrade with stale localization XML on disk.** `GameData/LunaMultiplayer/Localization/<lang>/UpdateWindowText.xml` will linger on player installs that updated from an older build. Harmless — nothing reads those files after Piece B lands; `LocalizationContainer.LoadLanguage` no longer asks for `UpdateWindowText`. The new updater (Piece A) will sweep them out on the next overlay (per-file overlay removes files present in old install but not in new zip — wait, no, the algorithm only OVERWRITES files present in BOTH zip and install; old XML files not in the new zip are LEFT alone). So the stale XML stays on disk forever as harmless dead bytes. Not worth a one-time cleanup pass; the file is ~1 KB per language.
- **Players who never accepted the disclaimer.** The disclaimer dialog now skips the update-check step on Accept. No behavioural change otherwise — the gateway to LMP gameplay is unchanged.
- **`LmpVersioning.CurrentVersion` reads.** UpdateHandler compared `LmpVersioning.CurrentVersion > latestVersion`. `LmpVersioning.CurrentVersion` is still consumed by other paths (handshake, master-server registration) and stays untouched.

---

## Test plan (Piece B)

- **`dotnet build LmpClient/LmpClient.csproj -c Release` must succeed** with no NEW warnings vs the pre-edit baseline. Existing 4× MSB3245 + 2× NU1701 + 1× CS0169 warnings are pre-existing per CLAUDE.md and stay untouched.
- **Soak validation only.** No automated test surface exists for UpdateHandler — the path is KSP-coroutine bound. Player smoke test: start KSP with the new LmpClient.dll, accept disclaimer (if first run), confirm no "update available" popup appears, confirm no exceptions in `KSP.log` mentioning `UpdateHandler` / `UpdateWindow` / `System.Version`.

---

## Piece A scope (forward-looking, for the next commit)

New project `Tools/PlayerUpdater/` (net10.0-windows, WinForms, self-contained or framework-dependent). Single button "Check for Update" → fetch latest release from **Majestic95/LunaMultiplayer ONLY** (no fork-vs-upstream switch — hardcoded to the user's repo) → present version + release notes + per-file overlay plan + backup path → install → rollback available.

**Key architectural decisions (locked from spec v2):**

- Per-file overlay (not folder-MOVE) — preserves `GameData/LunaMultiplayer/Data/settings.xml` ([SettingsReadSaveHandler.cs:11](../../LmpClient/Systems/SettingsSys/SettingsReadSaveHandler.cs)) and `GameData/LunaMultiplayer/Flags/` ([MainSystem.cs:559](../../LmpClient/MainSystem.cs)).
- Backups OUTSIDE `GameData/` — `%LOCALAPPDATA%/LunaMultiplayer/PlayerUpdater/backups/<install-hash>/<timestamp>/`. (KSP's GameDatabase does NOT skip dot-prefixed folders; a `.lmp-backup-…/Plugins/LmpClient.dll` inside `GameData/` would cause duplicate-assembly conflicts.)
- KSP detection chain: Steam libraryfolders.vdf → CKAN registry → GOG → last-used → manual file picker. Verify `GameData/Squad/` exists before accepting any path.
- Refuse on Program Files installs that fail a probe-write rather than silently elevating. Clear message to the player.
- Channel detection by reading `GameData/LunaMultiplayer/LunaMultiplayer.version` (after Piece C step 1 makes that file useful). Defaults the dropdown to the player's current channel.
- Cross-channel switch (stability ↔ per-agency) triggers a protocol-incompatibility confirmation dialog before proceeding.
- SHA-256 verification via the `digest` field on the GitHub Asset (verified present on v7 release: `sha256:d7c5f3fa…`). Warn-not-refuse if digest absent (older releases).
- Single-instance Mutex with `Global\` prefix so an elevated + non-elevated process pair still collides.

**Edge cases the implementation must handle:**

- KSP.exe running → refuse with retry-when-closed UX.
- Antivirus / Steam holding file locks → retry-with-backoff on sharing-violation IOException up to ~5s before declaring failure.
- Partial-extract recovery: detect `.lmp-staging/` or partial backup manifest on next launch, offer "previous install was interrupted — restore from backup?".
- Disk space < 3× zip size → refuse with required-free-space.
- GitHub rate-limit 403 → parse `X-RateLimit-Reset`, surface to player.

**Test surface:**

- New `Tools/PlayerUpdater.Tests/` (MSTest, net10.0) for pure helpers: `VersionParser`, `BackupManager.PlanBackup`, `ZipInstaller.PlanOverlay`, `KspDetector.ParseLibraryFoldersVdf`. No integration test surface (the disk + network operations have no test harness).

---

## Piece C scope (forward-looking)

**Step 1 (lands BEFORE Piece A so the version file is useful at parse time):**

`Scripts/build-release.ps1` accepts a new `-ReleaseTag` parameter. At staging time it generates `LunaMultiplayer.version` with:

```json
{
  "NAME":     "Luna Multiplayer (Majestic95 fork)",
  "GITHUB":   { "USERNAME": "Majestic95", "REPOSITORY": "LunaMultiplayer" },
  "VERSION":  { "MAJOR": <parsed>, "MINOR": <parsed>, "PATCH": <parsed> },
  "TAG":      "<release tag verbatim>",
  "CHANNEL":  "<stable | private | per-agency-private>",
  "REVISION": <int or null for stable>
}
```

The static `LunaMultiplayer.version` file on disk becomes a fallback / template; the build script rewrites it before zipping.

**Step 2:**

Build script publishes the PlayerUpdater exe in two flavours alongside the existing 5 zips:
- `LunaMultiplayer-Updater-win-x64-Release.exe` (~5 MB framework-dependent — requires .NET 10 Desktop Runtime on the player machine)
- `LunaMultiplayer-Updater-win-x64-selfcontained-Release.zip` (~70 MB, no runtime dep)

`LMP Readme.txt` updated to point new players at the exe.

---

## Cross-cutting risk: SmartScreen + UAC

- Unsigned `.exe` from GitHub → SmartScreen "Windows protected your PC" on first run. Document in release notes with bypass steps. Authenticode cert (~$200/yr) deferred until cohort scale demands it.
- Steam default location is `C:\Program Files (x86)\Steam\…` → write to `GameData/` requires elevation. Updater detects via probe-write at startup and refuses with clear instructions if the path is read-only. No silent UAC prompt; players must opt in by relaunching as admin.

---

## Rollback plan

Piece B is the only purely-subtractive change in the workstream. If a regression is found post-merge, `git revert <Piece B commit>` restores UpdateHandler + UpdateWindow + the localization references in a single commit. Piece A is purely additive (a new project under `Tools/`) and Piece C is additive (new params + new publish step in the build script) — both also cleanly revertible.
