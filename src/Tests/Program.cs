using GigaPMC.Server;
using System.IO;
using System.Text.Json;
var settings = new VitalitySettings();
if (!settings.IsValid()) throw new Exception("invalid defaults");
var cases = new (int level, int prestige, int bonus, int total)[]
{
    (1,0,0,440),(9,0,8,496),(10,0,14,538),(19,0,23,601),
    (20,0,29,643),(70,0,104,1168),(78,0,112,1224),(79,0,125,1315),
    (1,1,5,475),(1,2,10,510),(1,3,15,545),(10,2,24,608),(79,2,135,1385)
};
foreach (var c in cases)
{
    var actual = VitalityMath.Bonus(c.level, c.prestige, settings);
    if (actual != c.bonus || 440 + 7*actual != c.total)
        throw new Exception($"TEST_FAILED L{c.level} P{c.prestige}: +{actual} total {440+7*actual} expected +{c.bonus}, {c.total}");
}
settings.BonusHpEvery10Levels = 10;
if (VitalityMath.Bonus(79,0,settings)!=160) throw new Exception("TEST_FAILED custom +10");
settings.MaxPmcLevel = 100;
if (VitalityMath.Bonus(79,0,settings)!=148) throw new Exception("TEST_FAILED max level setting");
Console.WriteLine("PASS|P10_MATH|" + (cases.Length+2) + "_CASES");
Console.WriteLine("PASS|P06_PRESTIGE|RESET_TO_L1_BONUS_5_PER_PRESTIGE");
Console.WriteLine("PASS|P06_MAXLVL|BONUS_AT_79_ONLY_BY_DEFAULT");


// Fika policy regression tests: shared server authority and per-player isolation.
var tmp = Path.Combine(Path.GetTempPath(), "GigaPMC_Fika_P09_" + Guid.NewGuid().ToString("N"));
try
{
    var mods = Path.Combine(tmp, "user", "mods");
    var serverConfigDir = Path.Combine(mods, "GigaPMC", "config");
    Directory.CreateDirectory(serverConfigDir);
    if (FikaRules.IsFikaBackend(tmp, Array.Empty<string>()))
        throw new Exception("TEST_FAIL FIKA_FALSE_POSITIVE");

    File.WriteAllText(Path.Combine(serverConfigDir, "f12-overrides.json"),
        "{\"BonusHpPerLevel\":99,\"BonusHpPerPrestige\":90}");
    var solo = FikaRules.LoadSettings(tmp, false);
    if (solo.BonusHpPerLevel != 99) throw new Exception("TEST_FAIL SOLO_F12_OVERRIDE");

    Directory.CreateDirectory(Path.Combine(mods, "fika-server"));
    if (!FikaRules.IsFikaBackend(tmp, Array.Empty<string>())) throw new Exception("TEST_FAIL FIKA_DETECTION");
    var initialFika = FikaRules.LoadSettings(tmp, true);
    if (initialFika.BonusHpPerLevel != 1 || initialFika.BonusHpPerPrestige != 5)
        throw new Exception("TEST_FAIL FIKA_IGNORES_GUEST_F12");

    File.WriteAllText(Path.Combine(serverConfigDir, "fika-server-settings.json"),
        "{\"BonusHpPerLevel\":2,\"BonusHpPerPrestige\":7}");
    var shared = FikaRules.LoadSettings(tmp, true);
    if (shared.BonusHpPerLevel != 2 || shared.BonusHpPerPrestige != 7)
        throw new Exception("TEST_FAIL FIKA_SERVER_CONFIG");

    // Two distinct human profiles: no global level/prestige cache.
    var playerA = VitalityMath.Bonus(4, 0, shared);
    var playerB = VitalityMath.Bonus(10, 2, shared);
    if (playerA != 6 || playerB != (9*2 + 5 + 14))
        throw new Exception("TEST_FAIL FIKA_TWO_PROFILES");

    if (FikaRules.ReadPrestige(null) != 0 || FikaRules.ReadPrestige(3) != 3)
        throw new Exception("TEST_FAIL PRESTIGE_NULL");
    bool badPrestigeFailed = false;
    try { FikaRules.ReadPrestige(-1); } catch (InvalidDataException) { badPrestigeFailed = true; }
    if (!badPrestigeFailed) throw new Exception("TEST_FAIL NEGATIVE_PRESTIGE_ACCEPTED");

    File.WriteAllText(Path.Combine(serverConfigDir, "fika-server-settings.json"), "{invalid json");
    if (FikaRules.LoadSettings(tmp,true).BonusHpPerLevel != 1)
        throw new Exception("TEST_FAIL INVALID_SETTINGS");
    Console.WriteLine("PASS|P10_FIKA_RULES|7_CASES|NO_GAME_WRITE");
    Console.WriteLine("PASS|P10_GUEST_F12_IGNORED_BY_SHARED_BACKEND");
    Console.WriteLine("PASS|P10_PRESTIGE_NULL_AS_ZERO");
}
finally
{
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
}
