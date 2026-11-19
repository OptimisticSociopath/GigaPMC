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
var onlyPrestige = AttributeBonusMath.Defaults(AttributeBonusKind.CarryWeight);
onlyPrestige.AtMaxLevelEnabled = false;
Check(AttributeCapPolicy.Resolve(client,null,onlyPrestige)==0,"UNKNOWN_CAP_ALLOWED_IF_ATMAX_OFF");
Check(onlyPrestige.TotalPercent(true,1,1,0)==5m,"PMC_L1_PRESTIGE1_WITHOUT_CAP_IS_PLUS_FIVE");
Check(onlyPrestige.TotalPercent(true,1,0,0)==0m,"PMC_L1_PRESTIGE0_IS_ZERO");
bool absentCapRejected=false;
try { AttributeCapPolicy.Resolve(client,null,AttributeBonusMath.Defaults(AttributeBonusKind.CarryWeight)); }
catch(InvalidOperationException) { absentCapRejected=true; }
Check(absentCapRejected,"ATMAX_ENABLED_REQUIRES_REAL_CAP");
Check(AttributeCapPolicy.Resolve(new RaidHpSettings{AutoCap=false,ManualCap=90},null,
    AttributeBonusMath.Defaults(AttributeBonusKind.CarryWeight))==90,"MANUAL_CAP_RETAINED");
Check(new VitalitySettings().ConfigSchemaVersion==200&&new VitalitySettings{ConfigSchemaVersion=110}.IsValid(),"VERSION_2_AND_OLD_SCHEMA_ACCEPTED");
Check(!new VitalitySettings{ChestHpPerLevel=101}.IsValid()&&!new RaidHpSettings{Head=-1}.IsValid(),"INVALID_HP_RATES_REJECTED");
foreach(var kind in AttributeBonusMath.OrderedKinds)
{
    var rates=AttributeBonusMath.Defaults(kind);bool movement=AttributeBonusMath.IsMovement(kind);
    Check(rates.PerLevelPercent==(movement?.25m:1m)&&rates.EveryTenPercent==(movement?1m:3m)&&
        rates.AtMaxLevelPercent==(movement?5m:10m)&&rates.PrestigeMode==PercentPrestigeMode.Fixed&&
        rates.PrestigeTiers.SequenceEqual(movement?new decimal[]{2,4,6,8,10,12}:new decimal[]{5,8,11,14,17,20}),"OTHER_BONUS_DEFAULTS_UNCHANGED_"+kind);
}
// P03 Fika guard: server HP OFF and local attribute lease ON are independent.
var fikaLease=new AttributeF12RaidLease();object fikaOwner=new();
fikaLease.Capture(fikaOwner,
    new RaidHpSettings { Enabled=false,AutoCap=false,ManualCap=79 },
    AttributeBonusMath.Defaults,true,true,true,true,new JumpDistanceSettings(true,100));
Check(fikaLease.Active&&fikaLease.Hp().Enabled==false&&fikaLease.GlobalEnabled&&
    fikaLease.CarryEnabled&&fikaLease.StaminaEnabled&&fikaLease.EffectsEnabled,
    "FIKA_SERVER_HP_OFF_WHILE_LOCAL_ATTRIBUTES_ON");
Check(fikaLease.Rates(AttributeBonusKind.CarryWeight).TotalPercent(
    fikaLease.GlobalEnabled,1,1,fikaLease.Hp().ResolveCap(null))==5m,
    "FIKA_L1_PRESTIGE1_CARRY_PLUS_FIVE");
fikaLease.Capture(fikaOwner,new RaidHpSettings{Enabled=true},
    _=>throw new Exception("MUST_NOT_RESAMPLE_FIKA_F12"),false,false,false,false);
Check(!fikaLease.Hp().Enabled&&fikaLease.CarryEnabled,
    "FIKA_SAME_RAID_F12_CHANGES_DEFERRED");
fikaLease.Clear();
Check(!fikaLease.Active&&!fikaLease.CarryEnabled,"FIKA_RAID_END_CLEARS_LOCAL_EFFECTS");
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
// P09: local Fika-server admin F12 edits use an atomic, strictly validated JSON mirror.
var sharedHp = new RaidHpSettings { Enabled=true, Head=7, Chest=16, Stomach=9,
    LeftArm=8, RightArm=4, LeftLeg=5, RightLeg=2, EveryTen=11, AtMax=31,
    Prestige=9, Multiplicative=true, AutoCap=false, ManualCap=103 };
var sharedJson = FikaHpHostConfig.ToJson(sharedHp);
Check(FikaHpHostConfig.TryParse(sharedJson,out var parsedHp) &&
    parsedHp.Head==7 && parsedHp.Chest==16 && parsedHp.Stomach==9 &&
    parsedHp.LeftArm==8 && parsedHp.RightArm==4 && parsedHp.LeftLeg==5 &&
    parsedHp.RightLeg==2 && parsedHp.EveryTen==11 && parsedHp.AtMax==31 &&
    parsedHp.Prestige==9 && parsedHp.Multiplicative && !parsedHp.AutoCap &&
    parsedHp.ManualCap==103,"P09_FIKA_HP_JSON_ALL_F12_FIELDS_ROUNDTRIP");
Check(FikaHpHostConfig.TryParse("{\"ConfigSchemaVersion\":200,\"HeadHpPerLevel\":4}",out var partialHp) &&
    partialHp.Head==4 && partialHp.Chest==5,"P09_FIKA_HP_JSON_PARTIAL_USES_SERVER_DEFAULTS");
Check(!FikaHpHostConfig.TryParse("{\"HeadHpPerLevel\":4,\"HeadHpPerLevel\":7}",out _),
    "P09_FIKA_HP_JSON_DUPLICATE_KEY_REJECTED");
Check(!FikaHpHostConfig.TryParse("{\"ChestHpPerLevel\":101}",out _),
    "P09_FIKA_HP_JSON_OUT_OF_RANGE_REJECTED");
Check(!FikaHpHostConfig.TryParse("{\"Enabled\":false,\"Hidden\":true}",out _),
    "P09_FIKA_HP_JSON_UNKNOWN_KEY_REJECTED");
Check(!FikaHpHostConfig.TryParse("{\"Enabled\":tru}",out _),
    "P09_FIKA_HP_JSON_INVALID_BOOLEAN_REJECTED");
Check(!FikaHpHostConfig.TryParse("{\"Enabled\":true}garbage",out _),
    "P09_FIKA_HP_JSON_TRAILING_BYTES_REJECTED");
Check(FikaHpHostConfig.TryParse("{\"ConfigSchemaVersion\":110}",out _) &&
    FikaHpHostConfig.TryParse("{\"ConfigSchemaVersion\":0}",out _),
    "P09_FIKA_HP_JSON_SCHEMA_110_ACCEPTED");
var hostTest = Path.Combine(Path.GetTempPath(),"GigaPMC-p09-host-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(hostTest);
try {
    string hostFile=Path.Combine(hostTest,"fika-server-settings.json");
    Check(FikaHpHostConfig.TryRead(hostFile,out var absentHp,out _) && absentHp.Chest==5,
        "P09_FIKA_HP_ABSENT_FILE_SERVER_DEFAULTS");
    FikaHpHostConfig.WriteAtomic(hostFile,sharedHp);
    Check(FikaHpHostConfig.TryRead(hostFile,out var diskHp,out _) && diskHp.Chest==16 &&
        diskHp.ManualCap==103,"P09_FIKA_HP_FILE_ATOMIC_WRITE_READBACK");
    sharedHp.Chest=17;
    FikaHpHostConfig.WriteAtomic(hostFile,sharedHp);
    Check(FikaHpHostConfig.TryRead(hostFile,out diskHp,out _) && diskHp.Chest==17 &&
        File.Exists(hostFile+".bak"),"P09_FIKA_HP_FILE_ATOMIC_REPLACE_BACKUP");
}
finally {
    if (!Path.GetFileName(hostTest).StartsWith("GigaPMC-p09-host-",StringComparison.Ordinal) ||
        !Path.GetFullPath(hostTest).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))
        throw new Exception("P09_TEMP_GUARD");
    Directory.Delete(hostTest,true);
}
Console.WriteLine("P09_FIKA_HP_HOST_JSON_TESTS_PASS");
Console.WriteLine("SETTINGS_TESTS_PASS|CHECKS="+checks);
}
catch(Exception ex){Console.Error.WriteLine("SETTINGS_TESTS_FAIL|"+ex);Environment.ExitCode=1;}
