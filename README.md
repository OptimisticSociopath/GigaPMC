# GigaPMC – For Tarkov Enjoyers 2.0.1

**Author:** Vargor / OptimisticSociopath  
**Game:** SPT 4.1.6  
**Optional co-op:** Fika 2.4.6 (shared SPT server)  
**Version:** 2.0.1

GigaPMC provides level-, milestone- and Prestige-based PMC health and attribute bonuses.

## Included features

- Seven configurable HP gains: head, chest, stomach, left/right arms and left/right legs.
- Extra HP at every tenth level and at the maximum PMC level.
- Additive or multiplicative Prestige HP bonuses.
- Five individually configurable local PMC bonuses: carrying weight, body stamina, movement speed, ADS endurance and melee damage.
- Movement Speed with native collision handling, controlled sprint and horizontal jump-distance scaling; movement and jump settings are frozen during a raid and apply on the next raid.
- Menu/Hideout bonus readouts and live menu health refresh when the native Health panel is available.
- One native-event-driven raid lifecycle. No periodic GameWorld/player scans.
- Support for Prestige Perks; Skill Distribution is not a required dependency.

## Fika configuration

**Shared HP is calculated by the SPT/Fika server.** A local server administrator can change the shared HP settings using F12 without manually editing JSON:

`00 Fika > 02 Edit shared HP on this computer (SPT server host)`

The default is **Enabled** on the local server installation. The mod checks for the local server DLL and `fika-server` directory; remote clients cannot write server HP settings through F12. Local host edits update `SPT_Runtime/user/mods/GigaPMC/config/fika-server-settings.json` with a `.bak` backup. Changes made during a raid take effect on the next profile/raid cycle.

**The other five bonus tracks are controlled per local PMC client**, not globally pushed from the host. Every player using GigaPMC has their own F12 attribute settings. The general Enabled key on a Fika client controls those local attributes, not the shared server HP toggle.

`99 GigaPMC Developer > 01 DEV VISION - Enable` defaults to **Disabled**.

**Upgrading from a previous 2.0.1 candidate:** BepInEx preserves explicitly saved F12 values. If an earlier build saved the host HP editor as Disabled, press **Reset** on that setting to adopt the new Enabled default. Likewise press Reset for DEV VISION if your old local configuration still has it Enabled. These resets do not reset the seven HP sliders or the other bonus settings.

## Installation

Close the game and SPT server. Extract `GigaPMC-v2.0.1-FULL-READY.zip` into the SPT root, keeping the archive's `BepInEx/` and `SPT_Runtime/` folder structure. Replace the previous GigaPMC client and server DLLs after backing them up. The ZIP includes only `fika-server-settings.example.json`; it does **not** replace an existing server `fika-server-settings.json`.

- Client: `BepInEx/plugins/GigaPMC.Client.dll`
- Server: `SPT_Runtime/user/mods/GigaPMC/GigaPMC.Server.dll`

Install the same release on the game installations where local bonus functionality is wanted, and install the server component on the shared SPT backend.

## Build from complete source (Windows)

Requires .NET SDK 10.0.403 and the matching SPT 4.1.6 game at `C:\SPT`. Windows PowerShell 5.1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\BUILD.ps1 -GameRoot 'C:\SPT'
```

`BUILD.ps1` validates game assembly hashes, cleans all five projects, recompiles client/server/tests, runs 69 Movement checks, 89 Settings checks and 51 ABI checks, verifies the new DLL and ZIP entries, and exports the complete release ZIP to `%USERPROFILE%\Downloads`.

**Source folders:** `src/Client`, `src/Server`, `src/MovementTests`, `src/SettingsTests`. The `tools` directory contains retained regression/ABI checks. Internal diagnostic IDs are kept stable to avoid disrupting functional code. See `CHANGELOG.md`.

Licensed under MIT; see `LICENSE`.
