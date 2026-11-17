using GigaPMC.Client;
using GigaPMC.Server;
using System.Text.Json;

try
{
int checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS|"+label);}
var client=new RaidHpSettings();var server=new VitalitySettings();
string[] parts=["Head","Chest","Stomach","LeftArm","RightArm","LeftLeg","RightLeg"];
int[] expected=[1,5,4,2,2,3,3];
for(int i=0;i<parts.Length;i++)
{
    Check(client.Bonus(2,0,79,parts[i])==expected[i]&&VitalityMath.BonusForPart(2,0,server,79,parts[i])==expected[i],"LEVEL_DEFAULT_"+parts[i]);
    Check(client.Bonus(1,0,79,parts[i])==0,"LEVEL_ONE_NO_BONUS_"+parts[i]);
}
foreach(int level in new[]{1,2,10,79,100})
foreach(int prestige in new[]{0,1,6})
foreach(bool accumulated in new[]{false,true})
{
    client.Multiplicative=server.PrestigeMultiplicative=accumulated;
    Check(parts.All(part=>client.Bonus(level,prestige,100,part)==VitalityMath.BonusForPart(level,prestige,server,100,part)),
        "CLIENT_SERVER_PARITY_"+level+"_"+prestige+"_"+accumulated);
}
Check(client.EveryTen==5&&client.AtMax==15&&client.Prestige==5&&client.ManualCap==79&&client.AutoCap,"OTHER_HP_DEFAULTS_UNCHANGED");
Check(new VitalitySettings().ConfigSchemaVersion==200&&new VitalitySettings{ConfigSchemaVersion=110}.IsValid(),"VERSION_2_AND_OLD_SCHEMA_ACCEPTED");
Check(!new VitalitySettings{ChestHpPerLevel=101}.IsValid()&&!new RaidHpSettings{Head=-1}.IsValid(),"INVALID_HP_RATES_REJECTED");
foreach(var kind in AttributeBonusMath.OrderedKinds)
{
    var rates=AttributeBonusMath.Defaults(kind);bool movement=AttributeBonusMath.IsMovement(kind);
    Check(rates.PerLevelPercent==(movement?.25m:1m)&&rates.EveryTenPercent==(movement?1m:3m)&&
        rates.AtMaxLevelPercent==(movement?5m:10m)&&rates.PrestigeMode==PercentPrestigeMode.Fixed&&
        rates.PrestigeTiers.SequenceEqual(movement?new decimal[]{2,4,6,8,10,12}:new decimal[]{5,8,11,14,17,20}),"OTHER_BONUS_DEFAULTS_UNCHANGED_"+kind);
}
var lease=new AttributeF12RaidLease();object player=new();
lease.Capture(player,new RaidHpSettings(),AttributeBonusMath.Defaults,true,true,true,true,new JumpDistanceSettings(true,50));
lease.Capture(player,new RaidHpSettings{Chest=99},_=>throw new Exception("RECAPTURE_READ_PENDING"),true,true,true,true,new JumpDistanceSettings(false,0));
Check(lease.Jump.Enabled&&lease.Jump.StrengthPercent==50&&lease.Hp().Chest==5,"RAID_SNAPSHOT_IGNORES_PENDING_JUMP_AND_HP_CHANGES");
var copy=lease.Hp();copy.Chest=80;Check(lease.Hp().Chest==5,"HP_SNAPSHOT_DEFENSIVE_COPY");
lease.Clear();Check(!lease.Active&&lease.Jump.Enabled&&lease.Jump.StrengthPercent==100,"RAID_END_CLEARS_JUMP_OVERRIDE");
lease.Capture(new object(),new RaidHpSettings(),AttributeBonusMath.Defaults,true,true,true,true,new JumpDistanceSettings(false,0));
Check(!lease.Jump.Enabled&&lease.Jump.StrengthPercent==0,"NEXT_RAID_APPLIES_PENDING_JUMP_SETTINGS");
foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-1f,101f})
{
    bool rejected=false;try{_=new JumpDistanceSettings(true,bad);}catch(ArgumentOutOfRangeException){rejected=true;}
    Check(rejected,"INVALID_JUMP_SETTING_REJECTED_"+bad);
}
// Exercise production backend file loading in a private temporary test folder.
string temporary=Path.Combine(Path.GetTempPath(),"GigaPMC-settings-test-"+Guid.NewGuid().ToString("N"));
string file=FikaRules.GetSettingsPath(temporary,false);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
try
{
    Check(FikaRules.LoadSettings(temporary,false).ChestHpPerLevel==5,"NEW_BACKEND_DEFAULTS");
    File.WriteAllText(file,"{\"ConfigSchemaVersion\":110,\"ChestHpPerLevel\":3,\"StomachHpPerLevel\":2,\"LeftArmHpPerLevel\":7}");
    var old=FikaRules.LoadSettings(temporary,false);
    Check(old.ChestHpPerLevel==3&&old.StomachHpPerLevel==2&&old.LeftArmHpPerLevel==7,"EXISTING_SCHEMA_110_SETTINGS_NOT_OVERWRITTEN");
    File.WriteAllText(file,"{\"ChestHpPerLevel\":2,\"StomachHpPerLevel\":1,\"BonusHpAtMaxLevel\":12}");
    old=FikaRules.LoadSettings(temporary,false);
    Check(old.ConfigSchemaVersion==200&&old.ChestHpPerLevel==5&&old.StomachHpPerLevel==4&&old.BonusHpAtMaxLevel==15,"PRE_SCHEMA_MIGRATION_MATCHES_CURRENT_DEFAULTS");
    File.WriteAllText(file,"{\"ConfigSchemaVersion\":200,\"ChestHpPerLevel\":17,\"StomachHpPerLevel\":9}");
    old=FikaRules.LoadSettings(temporary,false);
    Check(old.ChestHpPerLevel==17&&old.StomachHpPerLevel==9,"CUSTOM_VERSION_2_VALUES_PRESERVED");
    File.WriteAllText(file,"invalid");Check(FikaRules.LoadSettings(temporary,false).LeftLegHpPerLevel==3,"INVALID_BACKEND_JSON_SAFE_DEFAULTS");
}
finally
{
    // The directory is generated here, and must stay in the OS temp directory.
    string resolved=Path.GetFullPath(temporary),parent=Path.GetFullPath(Path.GetTempPath());
    if(!resolved.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("GigaPMC-settings-test-"))throw new Exception("TEMP_CLEANUP_PATH");
    Directory.Delete(resolved,true);
}
Console.WriteLine("SETTINGS_TESTS_PASS|CHECKS="+checks);
}
catch(Exception ex){Console.Error.WriteLine("SETTINGS_TESTS_FAIL|"+ex);Environment.ExitCode=1;}
