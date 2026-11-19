#!/usr/bin/env python3
"""Offline source contract smoke checks. NOT a C# build or runtime proof."""
from pathlib import Path
p=Path(__file__).resolve().parents[1]
def file(name): return (p/name).read_text(encoding="utf-8-sig")
def check(ok,label):
    if not ok: raise SystemExit("FAIL|"+label)
    print("PASS|"+label)
server=file("src/Server/Mod.cs")
menu=file("src/Client/MenuHealthSync.cs")
raid=file("src/Client/RaidEntryPatch.cs")
client=file("src/Client/Plugin.cs")
check("if (!settings.Enabled) return;" in server,"SERVER_HP_OFF_NO_WRITE")
check("if (!settings.Enabled) { log?.LogInfo" in menu,"MENU_HP_OFF_NO_WRITE")
check("if (!settings.Enabled) { log?.LogInfo" in raid,"RAID_HP_OFF_NO_WRITE")
check('"2.0.1")' in client,"CLIENT_VERSION")
check('new("2.0.1")' in server,"SERVER_VERSION")
check("P201_NATIVE_EFFECT_RETRY" in client,"BOUNDED_RETRY_HOOK")
check("pendingBackendF12Save=true" in client,"F12_BACKEND_DEFER")
check("P37_MENU_NATIVE_WEIGHT_UNCHANGED_ZERO_PERCENT" in file("src/Client/MenuCarryHook.cs"),"NO_FALSE_ZERO_APPLIED")
check("ServerLevelCapCache.Read()" in file("src/Client/StaminaCapacityHooks.cs"),"SHARED_CAP_STAMINA")
check("ServerLevelCapCache.Read()" in file("src/Client/CarryCapacityHooks.cs"),"SHARED_CAP_CARRY")
check("ServerLevelCapCache.Read()" in file("src/Client/NativeAttributeEffects.cs"),"SHARED_CAP_EFFECTS")
check("ServerLevelCapCache.Read()" in file("src/Client/AttributeDevVision.cs"),"SHARED_CAP_MENU")
cs='\n'.join(x.read_text(encoding='utf-8-sig') for x in (p/'src').rglob('*.cs'))
check("SkillDistribution" not in cs and "skilldistribution" not in cs.lower(),"NO_SKILL_DISTRIBUTION_CODE_REFERENCE")
check('BepInDependency("com.fika.core"' in client,"PRESERVED_FIKA_SOFT_DEPENDENCY")
effects=file("src/Client/NativeAttributeEffects.cs")
check("P201_RATE_UNAVAILABLE|KIND=" in effects and "p201RateRetryNeeded=retryRate" in effects,
    "ADS_MELEE_MOVEMENT_INDEPENDENT_PERCENT_FAILURE")
check("P201_MOVEMENT_BRIDGE_FAILED" in effects and "ADS_MELEE_SNAPSHOTS_PRESERVED" in effects,
    "MOVEMENT_BRIDGE_FAILURE_PRESERVES_ADS_MELEE")
check(client.count('No unnecessary write on attribute-only F12 edits.')==1,
    "NO_DUPLICATE_BACKEND_JSON_READ")
print("P201_SOURCE_CHECK_PASS|NOT_BUILD_PASS|NOT_GAMEPLAY_PASS")
