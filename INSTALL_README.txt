GigaPMC – For Tarkov Enjoyers 2.0.1
====================================

Close Escape from Tarkov and the SPT server first.
Back up the existing GigaPMC client/server DLLs, then extract the full
release ZIP into C:\SPT while retaining the BepInEx/ and SPT_Runtime/
paths. Do not copy the SOURCE ZIP into the game.

Client: BepInEx\plugins\GigaPMC.Client.dll
Server: SPT_Runtime\user\mods\GigaPMC\GigaPMC.Server.dll

The included fika-server-settings.example.json is only an example and
will not replace your existing shared-server HP settings.

Fika host default: 00 Fika > Edit shared HP on this computer = Enabled.
DEV VISION default: Disabled.

IMPORTANT FOR UPGRADES: Existing BepInEx F12 values override defaults.
If the old host-editor value is still Disabled, click Reset beside that
one entry in F12 after installing 2.0.1. If DEV VISION is still Enabled,
click Reset beside its entry. Other F12 values are preserved.

Host HP updates are stored in the local server JSON file with .bak
backups. The five non-HP bonuses remain local to each PMC client.
Changes during a raid apply on the next raid/profile refresh cycle.
