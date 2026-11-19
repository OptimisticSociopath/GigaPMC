# GigaPMC changelog

## 2.0.1

- Fika host: full F12 management of seven-part shared HP, milestone, Prestige and level-cap settings, with server JSON readback and `.bak` backups.
- Host-only shared HP editor is now **Enabled by default** on a verified local SPT/Fika backend. Remote clients remain read-only.
- DEV VISION is **Disabled by default**; existing manually saved BepInEx overrides are preserved until reset.
- Local PMC attributes remain individually configurable: carrying weight, stamina, movement speed, ADS endurance and melee damage.
- Preserves the verified movement-binding implementation, controlled collision motion and horizontal jump scaling, as well as all HP and bonus calculations.
- The complete Windows build now forces a clean rebuild, verifies regressions and package content, and copies the release ZIP to Downloads.
- Consolidated the public source archive: removed old patch-stage notes; retained functional regression tests, ABI checks and stable diagnostic IDs.

## 2.0.0

- Introduced local PMC attribute bonuses, HP growth and bonuses by level, milestone and Prestige, F12 configuration and menu readouts.
