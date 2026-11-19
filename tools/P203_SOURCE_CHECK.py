#!/usr/bin/env python3
"""Source-only safety contracts for P03. Never claims a C# build or gameplay test."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
read=lambda p:(root/p).read_text(encoding='utf-8-sig')
plugin=read('src/Client/Plugin.cs')
bridge=read('src/Client/FikaLocalBridge.cs')
health=read('src/Client/MenuHealthSync.cs')
raid=read('src/Client/RaidLifecycleHook.cs')
checks={
 'FIKA_NO_EARLY_RETURN_ONLY_CONFIG': 'InitializeFikaLocalAttributes();' in plugin and 'Config.Bind("00 Fika", "01 Configuration authority (v2.0.1)"' in plugin,
 'FIKA_CARRY_F12_BOUND': 'attributeBonusConfig=AttributeBonusConfig.Bind(Config)' in plugin,
 'FIKA_HP_BACKEND_OWNED': 'Enabled=false, AutoCap=false' in plugin and 'if(owner!=null && owner.IsFikaClient && !owner.CanEditFikaHostHp) return;' in health,
 'FIKA_NONHOST_BACKEND_WRITES_DISABLED': 'if (isFika) return false;' in plugin and 'if(!isFika) pendingBackendF12Save=true;' in plugin,
 'P09_HOST_HP_EXPLICIT_OPTIN': 'FikaHostFileAvailable()' in plugin and 'if(!CanEditFikaHostHp) return false;' in plugin and 'FikaHpHostConfig.WriteAtomic' in plugin,
 'FIKA_OPTIONAL_BRIDGE': 'Fika.Core.Main.GameMode.BaseGameController' in bridge and 'SetLocalPlayer' in bridge,
 'FIKA_OWNER_AUTHENTICATED': 'P203_FIKA_OWNER_AUTHENTICATED' in raid and 'ReferenceEquals(AttributeMemberReader.Read(world,"MainPlayer"),fikaCandidatePlayer)' in raid,
 'FIKA_LOCAL_PMC_ONLY': 'IsYourPlayer' in bridge and 'IsAI' in bridge and 'IsHeadless' in bridge and 'IsSpectator' in bridge,
 'FIKA_FROZEN_F12': 'raidF12.Capture(localPlayer, LiveRaidSettings(),' in plugin and 'FikaLocalBonusesAllowed' in plugin,
 'FIKA_MANUAL_CAP': 'Fika maximum PMC level (local bonuses only)' in plugin and 'fikaManualLevelCap?.Value ?? 79' in plugin,
 'FIKA_NO_NEW_WORLD_POLL': 'Resources.FindObjectsOfTypeAll' not in bridge and 'FindObjectOfType' not in bridge and 'Update()' not in bridge,
 'NO_SKILL_DISTRIBUTION_DEP': all('SkillDistribution' not in read(p) and 'using SkillDistribution' not in read(p) for p in [str(x.relative_to(root)) for x in (root/'src').rglob('*.cs')]),
 'NO_FIKA_DLL_REFERENCE': all('Reference Include="Fika' not in x.read_text() and 'using Fika.' not in x.read_text() for x in (root/'src').rglob('*.csproj')) and 'using Fika.' not in bridge,
 'DEFAULT_GIGAPMC_ON': '"01 General", "Enabled", true' in plugin and '"Enable local PMC attribute bonuses (experimental)", true' in plugin,
 'FIKA_REGRESSION_TESTS': 'FIKA_SERVER_HP_OFF_WHILE_LOCAL_ATTRIBUTES_ON' in read('src/SettingsTests/Program.cs'),
}
for label,passed in checks.items():
 if not passed: raise SystemExit('FAIL|'+label)
 print('PASS|'+label)
print('P203_SOURCE_CHECK_PASS|NOT_BUILD_PASS|NOT_FIKA_GAMEPLAY_PASS')
