#!/usr/bin/env python3
"""P06 audit metadata source checks only; does not execute .NET or EFT."""
from pathlib import Path
r=Path(__file__).resolve().parents[1]
a=(r/'tools/GameAbiAudit/Program.cs').read_text(encoding='utf-8-sig')
c=(r/'src/Client/Plugin.cs').read_text(encoding='utf-8-sig')
p=(r/'src/Client/GigaPMC.Client.csproj').read_text(encoding='utf-8-sig')
s=(r/'src/Server/Mod.cs').read_text(encoding='utf-8-sig')
checks={
'ABI_CHECK_EXPECTS_201':'Value=="2.0.1"&&candidate.Assembly.Name.Version==new Version(2,0,1,0)' in a,
'ABI_CHECK_IDENTIFIED':'PLUGIN_AND_ASSEMBLY_VERSION_2_0_1' in a,
'ABI_CHECK_NO_OLD_EXPECTATION':'PLUGIN_AND_ASSEMBLY_VERSION_2_0_0' not in a,
'CLIENT_METADATA_201':'[BepInPlugin("com.vargor.gigapmc", "GigaPMC - For Tarkov Enjoyers", "2.0.1")]' in c,
'CLIENT_ASSEMBLY_201':'<Version>2.0.1</Version>' in p,
'SERVER_METADATA_201':'new("2.0.1")' in s,
}
for k,v in checks.items():
    if not v:raise SystemExit('FAIL|'+k)
    print('PASS|'+k)
print('P206_SOURCE_CHECK_PASS|NO_GAMEPLAY_TEST')
