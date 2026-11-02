# GigaPMC - For Tarkov Enjoyers v1.0.0

Author: Vargor / OptimisticSociopath
Target: SPT 4.1.6; Fika backend support is experimental (no completed real two-player raid test).

This is a **source BUILD_ONLY** package. The native Windows 5.1 runner compiles both DLLs using your already-tested SPT references and .NET 10 SDK; when all steps PASS, it generates one release ZIP in Downloads. It does **not** install to C:\SPT or touch PMC profiles.

### Renamed installed paths
- `SPT_Runtime/user/mods/GigaPMC/GigaPMC.Server.dll`
- `BepInEx/plugins/GigaPMC.Client.dll`
- `SPT_Runtime/user/mods/GigaPMC/icon.png`
- `SPT_Runtime/user/mods/GigaPMC/config/fika-server-settings.example.json`

### Identity
- Server and client GUID: `com.vargor.gigapmc`
- Release version **1.0.0**
- Display name `GigaPMC - For Tarkov Enjoyers`
- Author `Vargor / OptimisticSociopath`

**BREAKING INSTALL PATH/GUID CHANGE:** This is NOT a simple overwrite. Backup profiles and config first; migrate existing configs manually, then remove prior installed DLLs and the old server mod folder. Read `MIGRATION_PL_EN.txt` in the generated release before installing. Having old and new server/client DLLs simultaneously may double-apply the profile patch.

All numeric HP calculations and the Fika profile policy remain the same as P09 R2. The change is naming, GUID, assembly names and corresponding config paths; no runtime Fika sync proof is claimed.
