# GigaPMC 2.0.0

Local PMC health and attribute bonuses for the audited SPT 4.1.6 build.

## New in 2.0.0

- Continuous horizontal movement scaling when descending stairs, ramps and slopes, including native falling/landing states after ground contact. This preserves the multiplier without forcing the character onto the ground.
- F12 **11 Jump Distance**: enable jump scaling and choose a strength from 0% to 100%. Defaults: enabled, 100%. A movement bonus of +100% with jump strength 50% produces a horizontal jump multiplier of 1.5. Jump height and gravity inputs remain native. Settings apply next raid.
- Per-level HP defaults: head 1, each arm 2, each leg 3, stomach 4, thorax 5. Milestone/prestige and other bonus defaults are unchanged. Existing saved values are preserved; reset only the seven body-part entries to adopt the new defaults.

Movement still uses one native collision-controller call with bounded native substeps. No direct transform-position writes or post-collision displacement. Extreme requested speeds can be limited, and arbitrary-speed collision safety is not guaranteed. Fika retains the previous server-authoritative HP behavior; local movement bonuses are not activated by Fika clients.

## Install

Close SPT and the game. Replace the previous mod using `GigaPMC-v2.0.0-FULL-SSPTMM.zip`. Keep one client and one server DLL. Do not install the SOURCE-GITHUB archive as a mod.

## Build on Windows (double-click)

1. Download the source ZIP and **extract the entire archive**. Do not try to run a script from inside a ZIP preview.
2. Install .NET SDK **10.0.403** and use the matching SPT 4.1.6 game at `C:\SPT`. Windows PowerShell 5.1 is required. Game DLLs are not included here.
3. **Double-click `BUILD_WINDOWS.cmd`** in the extracted source folder. It starts the build and keeps the window open after success or failure.
4. On failure, open `BUILD_LAST.log` in that folder and the step logs in `artifacts/build-...`. Both are ignored by Git.

Advanced usage from Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\BUILD.ps1 -GameRoot 'C:\SPT'
```

The first restore uses NuGet. Optional `-OfflineFeed` supplies a local feed with netstandard.library.ref 2.1.0. `GAME_REFERENCES_SHA256.csv` verifies the local game libraries. Outputs and step logs go to `artifacts/build-...`. The script builds both DLLs, runs movement/settings tests and a local ABI audit, then packages the mod without installing it.

Client source: `src/Client`. Server source: `src/Server`. Current tests: `src/MovementTests`, `src/SettingsTests`. Build compatibility: `GAME_REFERENCES_SHA256.csv`. Some historical log and Harmony identifiers are intentionally retained for compatibility.


The source archive contains no game binaries, private logs, profile data or obsolete test fixtures. MIT license; see [LICENSE](LICENSE). Full changes: [CHANGELOG](CHANGELOG.md).
