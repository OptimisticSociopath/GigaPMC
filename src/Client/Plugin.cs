using System;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace GigaPMC.Client
{
    [BepInPlugin("com.vargor.gigapmc", "GigaPMC - For Tarkov Enjoyers", "2.0.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> isEnabled;
        private ConfigEntry<int> eachTen, atMax, prestige, maxLevel;
        private ConfigEntry<int> head, chest, stomach, leftArm, rightArm, leftLeg, rightLeg;
        private ConfigEntry<bool> prestigeMultiplicative, autoMaxLevel;
        private ConfigEntry<bool> jumpDistanceScaling;
        private ConfigEntry<float> jumpDistanceStrength;
        internal bool RaidJumpScalingEnabled => raidF12.Jump.Enabled;
        internal float RaidJumpScalingStrength => raidF12.Jump.StrengthPercent;
        private bool isFika;
        private AttributeBonusConfig attributeBonusConfig;
        private ConfigEntry<bool> p09ExperimentalRuntime;
        private ConfigEntry<bool> devVision;
        private ConfigEntry<bool> p24CarryTrial;
        private ConfigEntry<bool> p26StaminaTrial;
        private ConfigEntry<bool> p27EffectsTrial;
        private static Plugin devVisionOwner;
        private readonly AttributeF12RaidLease raidF12 = new AttributeF12RaidLease();
        private int p29RaidEntryFrame = -1;

        private void Awake()
        {
            Logger.LogInfo("[GigaPMC] VERSION="+CharacterMovementBridge.Build);
            // Fika is server-authoritative; client F12 must not pretend to
            // change the shared remote backend's HP.
            isFika = Chainloader.PluginInfos.ContainsKey("com.fika.core");
            if (isFika)
            {
                Config.Bind("00 Fika", "Configuration authority",
                    "Shared SPT/Fika server admin",
                    new ConfigDescription("Fika clients cannot change shared PMC HP through F12. " +
                        "The server administrator edits GigaPMC/config/fika-server-settings.json.",
                        null, new ConfigurationManagerAttributes { ReadOnly = true }));
                Logger.LogInfo("[GigaPMC] Fika client: server owns all health scaling settings.");
                return;
            }

            string serverDir = Path.Combine(Paths.GameRootPath, "SPT_Runtime", "user", "mods", "GigaPMC");
            string previousOverrides = Path.Combine(serverDir, "config", "f12-overrides.json");
            LegacySettingsReader old;
            try { old = LegacySettingsReader.FromFiles(Config.ConfigFilePath, previousOverrides); }
            catch (Exception ex)
            {
                Logger.LogWarning("[GigaPMC] Legacy settings could not be read: " + ex.Message);
                old = new LegacySettingsReader("", "");
            }

            isEnabled = Config.Bind("01 General", "Enabled",
                true,
                "Enable GigaPMC for your PMC only. Never modifies Scavs, AI PMCs or bosses.");

            const string parts = "02 Body Part HP";
            head = BindPart(parts, "01 Head HP per level", 1, "head");
            chest = BindPart(parts, "02 Chest HP per level", 5, "thorax (chest)");
            stomach = BindPart(parts, "03 Stomach HP per level", 4, "stomach");
            leftArm = BindPart(parts, "04 Left arm HP per level", 2, "left arm");
            rightArm = BindPart(parts, "05 Right arm HP per level", 2, "right arm");
            leftLeg = BindPart(parts, "06 Left leg HP per level", 3, "left leg");
            rightLeg = BindPart(parts, "07 Right leg HP per level", 3, "right leg");

            jumpDistanceScaling=Config.Bind("11 Jump Distance", "01 Scale jump distance with movement speed",true,
                "Enable horizontal jump-distance scaling from the GigaPMC movement bonus. " +
                "Applies to walking and sprinting jumps; jump height and gravity stay native. Changes apply next raid.");
            jumpDistanceStrength=Config.Bind("11 Jump Distance", "02 Jump distance scaling strength (%)",100f,
                new ConfigDescription("0% = no GigaPMC jump-distance bonus. 100% = full movement-speed bonus. " +
                    "Example: movement +100% and strength 50% gives jump horizontal speed +50%. " +
                    "Collisions and native jump mechanics still determine the actual distance. Changes apply next raid.",
                    new AcceptableValueRange<float>(0f,100f)));

            eachTen = Config.Bind("03 Milestone Bonuses", "Bonus HP every 10 levels",
                5,
                new ConfigDescription("Extra HP for EACH body part at level 10, 20, 30, etc. " +
                    "Example: +5 gives +15 HP to each body part at level 30 (three milestones).",
                    new AcceptableValueRange<int>(0, 1000)));
            atMax = Config.Bind("03 Milestone Bonuses", "Bonus HP at maximum level",
                15,
                new ConfigDescription("One-time extra HP to EACH body part at the real game's maximum PMC level. " +
                    "Default +15; if another mod raises the max level to 100, the bonus applies at 100, not 79.",
                    new AcceptableValueRange<int>(0, 1000)));

            prestige = Config.Bind("04 Prestige", "Bonus HP per prestige",
                5,
                new ConfigDescription("Base HP reward per prestige for EACH body part. " +
                    "The amount you set here is used by BOTH Additive and Multiplicative modes. " +
                    "Example with base +5 at prestige 6: Additive +30; Multiplicative +105 HP per part.",
                    new AcceptableValueRange<int>(0, 1000)));
            prestigeMultiplicative = Config.Bind("04 Prestige", "Prestige Multiplicative",
                false,
                "OFF = Additive: base x prestige (e.g. 5 x 6 = 30 HP per part). " +
                "ON = Multiplicative: base x prestige x (prestige+1)/2 (e.g. 5 x 6 x 7/2 = 105). " +
                "Only the PRESTIGE bonus changes; level and milestone bonuses always add normally.");

            autoMaxLevel = Config.Bind("05 Level Rules", "Automatically detect game maximum PMC level",
                true,
                "ON (recommended): read the real SPT max level from the experience table. " +
                "OFF: use the manual maximum level below.");
            maxLevel = Config.Bind("05 Level Rules", "Maximum PMC level (manual override)",
                79,
                new ConfigDescription("Used ONLY if automatic max-level detection is OFF. " +
                    "Example: set to 100 to award the max-level +15 at level 100.",
                    new AcceptableValueRange<int>(1, 1000)));

            // Migration assigns values only AFTER binding with canonical defaults.
            // This preserves BepInEx's Reset button and the 2.0 defaults while
            // retaining explicit values already saved in the current config.
            MigrateBool(old, isEnabled, "01 General", "Enabled", "01 General", "Enabled");
            MigrateInt(old, head, parts, "01 Head HP per level", "02 Body Part HP", "HeadHpPerLevel", 0, 100, null, "Head HP per level");
            MigrateInt(old, chest, parts, "02 Chest HP per level", "02 Body Part HP", "ChestHpPerLevel", 0, 100, 2, "Chest HP per level");
            MigrateInt(old, stomach, parts, "03 Stomach HP per level", "02 Body Part HP", "StomachHpPerLevel", 0, 100, 1, "Stomach HP per level");
            MigrateInt(old, leftArm, parts, "04 Left arm HP per level", "02 Body Part HP", "LeftArmHpPerLevel", 0, 100, null, "Left arm HP per level");
            MigrateInt(old, rightArm, parts, "05 Right arm HP per level", "02 Body Part HP", "RightArmHpPerLevel", 0, 100, null, "Right arm HP per level");
            MigrateInt(old, leftLeg, parts, "06 Left leg HP per level", "02 Body Part HP", "LeftLegHpPerLevel", 0, 100, null, "Left leg HP per level");
            MigrateInt(old, rightLeg, parts, "07 Right leg HP per level", "02 Body Part HP", "RightLegHpPerLevel", 0, 100, null, "Right leg HP per level");
            MigrateInt(old, eachTen, "03 Milestone Bonuses", "Bonus HP every 10 levels", "02 HP Bonuses", "BonusHpEvery10Levels", 0, 1000);
            MigrateInt(old, atMax, "03 Milestone Bonuses", "Bonus HP at maximum level", "02 HP Bonuses", "BonusHpAtMaxLevel", 0, 1000, 12);
            MigrateInt(old, prestige, "04 Prestige", "Bonus HP per prestige", "02 HP Bonuses", "BonusHpPerPrestige", 0, 1000);
            MigrateBool(old, prestigeMultiplicative, "04 Prestige", "Prestige Multiplicative", "02 HP Bonuses", "PrestigeMultiplicative");
            MigrateBool(old, autoMaxLevel, "05 Level Rules", "Automatically detect game maximum PMC level", "03 Level Rules", "AutoDetectMaxPmcLevel");
            MigrateInt(old, maxLevel, "05 Level Rules", "Maximum PMC level (manual override)", "03 Level Rules", "MaxPmcLevel", 1, 1000);

            // P23: optional attribute bootstrap is fault-isolated from the
            // existing HP boot. A failed F12 attribute key must NEVER prevent
            // RaidEntryPatch / MenuHealthSync from initializing.
            bool attributesReady = AttributeBootBoundary.TryBind(
                () =>
                {
                    attributeBonusConfig = AttributeBonusConfig.Bind(Config);
                    devVision = Config.Bind("99 GigaPMC Developer", "01 DEV VISION - Enable", false,
                        "Shows ONLY real numeric results measured by executing native EFT hooks. " +
                        "In menu unavailable results are shown as dashes. " +
                        "No percentages or IL projections are presented as applied stats.");
                    devVisionOwner = this;
                    Logger.LogInfo("[GigaPMC] P32_DEV_VISION_CONFIG_BOUND|DEFAULT_OFF|FIVE_BONUSES|READ_ONLY");
                },
                () =>
                {
                    attributeBonusConfig = null;
                    p09ExperimentalRuntime = null;
                    devVision = null;
                    devVisionOwner = null;
                    p24CarryTrial = null;
                    p26StaminaTrial = null;
                    p27EffectsTrial = null;
                },
                message => Logger.LogInfo(message),
                message => Logger.LogError(message));
            if (attributesReady)
            {
                // Still safe-off by the P10 hard authority gate. Never let an
                // optional diagnostic hook installer interrupt HP startup.
                try { AttributeRuntime.TryInstall(this, Logger); }
                catch (Exception ex)
                {
                    Logger.LogError("[GigaPMC] P23_OPTIONAL_HOOK_INSTALL_ERROR|HP_BOOT_CONTINUES|" +
                        ex.GetType().Name + "|" + ex.Message);
                }
            }
            Logger.LogInfo("[GigaPMC] P32_FIVE_BONUSES_ENABLED_BY_DEFAULT|5_F12_SECTIONS|ONE_GLOBAL_MOVEMENT|NO_EXTRA_RUNTIME_SWITCHES");
            Logger.LogInfo("[GigaPMC] P23_HP_INIT_REACHED|ATTRIBUTES_READY=" + attributesReady);

            Config.SettingChanged += SettingsChanged;
            SaveSettings();
            RaidEntryPatch.TryInstall(this, Logger);
            MenuHealthSync.TryInstall(this, Logger);
            MenuEventBridge.TryInstall(this, Logger);
            LiveAbiAudit.Report(Logger);
            RaidLifecycleHook.TryInstall(this, Logger);
            MenuCarryHook.TryInstall(this, Logger);
            HideoutStaminaBridge.TryInstall(this, Logger);
            Logger.LogInfo("[GigaPMC] P23_HP_INIT_ATTEMPTS_COMPLETED|ATTRIBUTES_READY=" + attributesReady +
                "|P32_GAMEPLAY_NOT_YET_CERTIFIED");
            Logger.LogInfo("[GigaPMC] P32_RAID_CAPTURE_FALLBACK_ARMED|GAMEWORLD_MAINPLAYER|NO_HP_POST_INIT_WRITE|GAMEPLAY_NOT_YET_CERTIFIED");
            if (!File.Exists(Path.Combine(serverDir, "GigaPMC.Server.dll")))
                Logger.LogWarning("[GigaPMC] Server DLL not found. F12 values alone cannot change PMC HP.");
            Logger.LogInfo("[GigaPMC] v2.0.0: HP defaults updated; downhill continuity and configurable horizontal jump scaling.");
        }

        private static void MigrateInt(LegacySettingsReader old, ConfigEntry<int> entry,
            string newSection, string key, string previousSection, string jsonKey,
            int min, int max, int? previousDefault = null, string previousKey = null)
        {
            if (old.ReadCfg(newSection, key) != null) return; // New setting already wins.
            int migrated = old.Int(previousSection, previousKey ?? key, jsonKey, entry.Value,
                min, max, previousDefault);
            // Pre-final v1.1.0 used a different section. Prefer a real legacy
            // CFG value over a JSON fallback that may have been saved earlier.
            if (old.ReadCfg(previousSection, previousKey ?? key) == null &&
                old.ReadCfg("04 Body Part Scaling", previousKey ?? key) != null)
                migrated = old.Int("04 Body Part Scaling", previousKey ?? key,
                    jsonKey, entry.Value, min, max, previousDefault);
            if (migrated != entry.Value) entry.Value = migrated;
        }

        private static void MigrateBool(LegacySettingsReader old, ConfigEntry<bool> entry,
            string newSection, string key, string previousSection, string jsonKey)
        {
            if (old.ReadCfg(newSection, key) != null) return;
            bool migrated = old.Bool(previousSection, key, jsonKey, entry.Value);
            if (migrated != entry.Value) entry.Value = migrated;
        }

        private ConfigEntry<int> BindPart(string section, string label, int value, string part)
            => Config.Bind(section, label, value, new ConfigDescription(
                "Extra MAX HP per level ABOVE level 1 for the " + part + ". " +
                "Example: +3 HP/level gives +9 HP at PMC level 4. " +
                "Each body part is independent. Milestone and prestige bonuses add separately.",
                new AcceptableValueRange<int>(0, 100),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        private void SettingsChanged(object sender, SettingChangedEventArgs args)
        {
            if (!isFika)
            {
                SaveSettings();
                // Live menu refresh. In-raid changes are pending and must
                // never attempt to mutate an in-session HP controller.
                if (!raidF12.Active) MenuHealthSync.Queue();
                if (!raidF12.Active) MenuCarryHook.Queue();
                AttributeDevVision.Invalidate();
                if (!raidF12.Active) P40R3RefreshMenuBonusSnapshot();
                if (!raidF12.Active) HideoutStaminaBridge.OnMenuF12Changed();
            }
        }

        private void Update()
        {
            if (!isFika)
            {
                // P40 receives GameWorld.OnGameStarted and caches its instance.
                // No periodic search for the game world or map name checks.
                // P40: no periodic Unity world discovery or map-name classification.
                // P40 R3: native events own start, end and player readiness.
                // No session lifecycle polling in Update.
                MenuHealthSync.Tick();
                // P38: retired expensive periodic Resources.FindObjectsOfTypeAll UI probe.
                // The live menu hook has its own bounded, low-frequency refresh.
                MenuCarryHook.Tick(this);
                AttributeDevVision.Tick(this);
                // P40 R2: no recurring F12/profile/percent sampling in Update.
                // Native EFT methods invoke the cached-effect Harmony hooks.
                // Each session snapshot is built exactly once when the PMC lease is captured.
            }
        }

        private void LateUpdate()
        {
            if(!isFika) WorldSpeedProbe.Tick();
        }
        private void OnGUI()
        {
            if (!isFika && P19DevVisionEnabled) AttributeDevVision.Draw();
        }

        internal void P25Log(string status)
        {
            Logger.LogInfo("[GigaPMC] P25_" + status + "|READ_ONLY|NO_GAMEPLAY_CERTIFICATION");
        }

        // Trial opt-ins and every configured percentage are latched ONCE at
        // local Player.Init, before raid HP is applied. A change to F12 during
        // the raid is saved for the NEXT raid, never applied mid-raid.
        internal bool P33InRaid => raidF12.Active;
        internal void P33Log(string detail) => Logger.LogInfo("[GigaPMC] P33_" + detail);
        internal void P35Log(string detail) => Logger.LogInfo("[GigaPMC] P35_" + detail);
        internal bool P29IsCapturedLocalPlayer(object player) => !isFika && raidF12.Owns(player);
        // P32: the only user-facing switches are the 5 bonus Enabled keys.
        // Trial integration is automatic on a captured local PMC, never on bots.
        internal bool P24CarryTrialEnabled => !isFika && raidF12.CarryEnabled;
        internal bool P26StaminaTrialEnabled => !isFika && raidF12.StaminaEnabled;
        internal bool P27EffectsTrialEnabled => !isFika && raidF12.EffectsEnabled;
        internal bool P19DevVisionEnabled => devVision != null && devVision.Value;
        internal static int P19CurrentLevelCap(int? detectedCap)
        {
            if (devVisionOwner == null) throw new InvalidOperationException("P19_PLUGIN_NOT_READY");
            return devVisionOwner.CurrentRaidSettings().ResolveCap(detectedCap);
        }

        internal bool P09ExperimentalIntegrationEnabled => !isFika && p09ExperimentalRuntime != null && p09ExperimentalRuntime.Value;
        internal bool P09GlobalEnabled => raidF12.Active ? raidF12.GlobalEnabled :
            isEnabled != null && isEnabled.Value;
        internal AttributeBonusRates P09BonusRates(AttributeBonusKind kind) =>
            raidF12.Active ? raidF12.Rates(kind) : attributeBonusConfig.Snapshot(kind);

        // Called only after EFT confirms an explicitly local PMC Player.Init.
        internal bool CaptureP28RaidF12(object localPlayer)
        {
            try
            {
                // Even a direct Player.Init callback must not freeze F12 in Hideout.
                // Only an observed native GameWorld.OnGameStarted callback may arm
                // a lease. Hideout GameWorld must never freeze F12.
                if (!RaidLifecycleHook.IsAuthenticatedWorldPlayer(localPlayer)) return false;
                bool alreadyOwned = raidF12.Owns(localPlayer);
                if (alreadyOwned) return true; // no pending F12 reads for same PMC
                var jumpSettings=new JumpDistanceSettings(jumpDistanceScaling?.Value??true,
                    jumpDistanceStrength?.Value??100f);
                raidF12.Capture(localPlayer, LiveRaidSettings(),
                    kind => attributeBonusConfig == null
                        ? DisabledDefaults(kind) : attributeBonusConfig.Snapshot(kind),
                    isEnabled != null && isEnabled.Value,
                    true, true, true,jumpSettings); // One enable in each of the 5 bonus sections
                p29RaidEntryFrame = UnityEngine.Time.frameCount;
                p42RaidHudPercents=p40R3MenuLevel>0 ?
                    (decimal[])p40R3MenuPercents.Clone() : null;
                NativeEvidence.Reset();
                MovementEndpointEvidence.Reset();
                Logger.LogInfo("[GigaPMC] P33_F12_RAID_CAPTURE|LOCAL_PMC|5_BONUSES|" +
                    "F12_SNAPSHOT|HP_FROZEN|PENDING_F12_DEFERRED");
                return raidF12.Owns(localPlayer);
            }
            catch (Exception ex)
            {
                // Same-PMC recapture never reads pending F12. A new PMC with
                // invalid F12 MUST NOT inherit the previous player's lease.
                if (!raidF12.Owns(localPlayer))
                {
                    raidF12.Clear();
                    p42RaidHudPercents=null;
                    p29RaidEntryFrame = -1;
                }
                Logger.LogError("[GigaPMC] P29_F12_CAPTURE_FAIL_CLOSED|SKIP_RAID_HP_APPLY|" +
                    ex.GetType().Name);
                return false;
            }
        }
        private static AttributeBonusRates DisabledDefaults(AttributeBonusKind kind)
        {
            var result = AttributeBonusMath.Defaults(kind);
            result.Enabled = false;
            return result;
        }

        // P40: event-only native lifecycle. No scene names, periodic searches,
        // or repeated GetAssemblies() calls in Update().
        internal void P40OnNativeGameStarted(object world)
        {
            object player = AttributeMemberReader.Read(world,"MainPlayer");
            if (player == null) return;
            bool validPlayer=false;
            for (Type t=player.GetType();t!=null;t=t.BaseType)
                if (t.FullName=="EFT.Player" && t.Assembly.GetName().Name=="Assembly-CSharp")
                { validPlayer=true; break; }
            object profile=AttributeMemberReader.Read(player,"Profile");
            object info=AttributeMemberReader.Read(profile,"Info");
            string side=(AttributeMemberReader.Read(profile,"Side") ??
                AttributeMemberReader.Read(info,"Side"))?.ToString();
            object ai=AttributeMemberReader.Read(player,"IsAI");
            object your=AttributeMemberReader.Read(player,"IsYourPlayer");
            if (!LocalPmcGate.IsAuthorized(player!=null,ai as bool?,your as bool?,
                    side,validPlayer,profile?.GetType().FullName=="EFT.Profile")) return;
            // A second native callback for the SAME PMC must not resnapshot
            // or reinstall a previously captured raid's native hooks.
            bool alreadyCaptured = raidF12.Owns(player);
            if (CaptureP28RaidF12(player) && !alreadyCaptured)
            {
                // Snapshot all five effect tracks once. The F12 raid lease is
                // already immutable; never resample level/prestige mid-raid.
                CarryCapacityHooks.ArmOnce(this, Logger);
                StaminaCapacityHooks.ArmOnce(this, Logger);
                NativeAttributeEffects.ArmOnce(this, Logger);
                Logger.LogInfo("[GigaPMC] P40_R2_RAID_EFFECTS_ARMED_ONCE|LOCAL_PMC|F12_FROZEN|NO_BONUS_POLLING");
                Logger.LogInfo("[GigaPMC] P40_NATIVE_RAID_CAPTURE|ON_GAME_STARTED|LOCAL_PMC|NO_WORLD_SCAN");
            }
        }

        // Fired by the same native MAIN MENU operations already used for HP.
        // A new, post-raid, offline-health-controller witness releases F12 exactly once.
        internal void P40R3AfterNativeMenuEvent()
        {
            if(isFika) return;
            if(raidF12.Active)
            {
                if(!MenuHealthSync.ConfirmedOutOfRaidMenuAfter(p29RaidEntryFrame)) return;
                // Unlike the old three-sample poll, an authenticated native menu
                // transition is a single positive exit event, not missing frames.
                raidF12.Clear();
                p42RaidHudPercents=null;
                p29RaidEntryFrame=-1;
                CarryCapacityHooks.OnRaidEnded();
                StaminaCapacityHooks.OnRaidEnded();
                NativeAttributeEffects.OnRaidEnded();
                HideoutStaminaBridge.OnRaidExit();
                RaidLifecycleHook.OnVerifiedMenuReturn();
                NativeEvidence.Reset();
                MovementEndpointEvidence.Reset();
                Logger.LogInfo("[GigaPMC] P40_R3_RAID_EXIT_NATIVE_MENU_WITNESS|F12_RELEASED_ONCE|NO_POLL");
                MenuHealthSync.Queue();
                MenuWeightProbe.Reset();
                MenuCarryHook.Queue();
                AttributeDevVision.Invalidate();
            }
            P40R3RefreshMenuBonusSnapshot();
        }

        // Profile Level/Prestige is available in the HP menu without a GameWorld.
        // Cache the FIVE configured bonus percentages when the menu/profile is
        // observed, and on F12 edits. These are CONFIG values, not fake native
        // Body Stamina/ADS/Melee/Movement measurements for DEV VISION.
        private readonly decimal[] p40R3MenuPercents = new decimal[5];
        private int p40R3MenuLevel=-1, p40R3MenuPrestige=-1;
        private int p42LastLoggedMenuLevel=-1, p42LastLoggedMenuPrestige=-1;
        // The five known percentages are a CONFIG snapshot, not measurements.
        // Frozen once at raid capture; never refreshed from pending F12 changes.
        private decimal[] p42RaidHudPercents;
        private void P40R3RefreshMenuBonusSnapshot()
        {
            if(raidF12.Active || attributeBonusConfig==null) return;
            try
            {
                int level, prestige, cap;
                object profile;
                if(!AttributeDevVision.TryReadMenuPmc(out level,out prestige,out cap,out profile)) return;
                var kinds=new[]{AttributeBonusKind.CarryWeight,AttributeBonusKind.BodyStamina,
                    AttributeBonusKind.AdsEndurance,AttributeBonusKind.MeleeDamage,AttributeBonusKind.SprintSpeed};
                bool changed=level!=p40R3MenuLevel || prestige!=p40R3MenuPrestige;
                for(int i=0;i<kinds.Length;i++)
                {
                    var rates=attributeBonusConfig.Snapshot(kinds[i]);
                    decimal percent=rates.TotalPercent(isEnabled.Value,level,prestige,cap);
                    if(p40R3MenuPercents[i]!=percent) changed=true;
                    p40R3MenuPercents[i]=percent;
                }
                p40R3MenuLevel=level;p40R3MenuPrestige=prestige;
                if(changed)
                {
                    // A slider may fire dozens of ValueChanged callbacks.
                    // HUD invalidation is cheap; repeated disk logging is not.
                    if(p42LastLoggedMenuLevel!=level || p42LastLoggedMenuPrestige!=prestige)
                    {
                        p42LastLoggedMenuLevel=level;
                        p42LastLoggedMenuPrestige=prestige;
                        Logger.LogInfo("[GigaPMC] P40_R3_FIVE_F12_MENU_VALUES_READY|L"+level+
                            "|P"+prestige+"|NATIVE_VALUES_NOT_INVENTED");
                    }
                    AttributeDevVision.Invalidate();
                }
            }
            catch(Exception ex)
            {
                Logger.LogWarning("[GigaPMC] P40_R3_MENU_SNAPSHOT_UNAVAILABLE|"+ex.GetType().Name);
            }
        }

        // HUD reads ONLY data stored by profile-load/F12 or raid-capture events.
        // No profile reflection, disk I/O or native EFT calculations inside Update.
        internal bool P42TryHudPercents(out decimal[] percents)
        {
            percents=null;
            if(isFika || attributeBonusConfig==null) return false;
            if(raidF12.Active)
            {
                if(p42RaidHudPercents==null) return false;
                percents=(decimal[])p42RaidHudPercents.Clone();
                return true;
            }
            if(p40R3MenuLevel<1) return false;
            percents=(decimal[])p40R3MenuPercents.Clone();
            return true;
        }

        // Real menu-profile bonus; not an invented native stamina value.
        // The separate P41 native capacity getter is the only writer to HUD.
        internal bool P41TryMenuBodyStaminaPercent(out decimal percent)
        {
            percent=0m;
            if(isFika || raidF12.Active || attributeBonusConfig==null ||
                isEnabled==null || !isEnabled.Value) return false;
            int currentLevel,currentPrestige,currentCap;
            object currentProfile;
            if(!AttributeDevVision.TryReadMenuPmc(out currentLevel,
                out currentPrestige,out currentCap,out currentProfile)) return false;
            P40R3RefreshMenuBonusSnapshot();
            if(p40R3MenuLevel!=currentLevel ||
                p40R3MenuPrestige!=currentPrestige) return false;
            percent=p40R3MenuPercents[1];
            return percent>=0m && percent<=100000m;
        }

        internal RaidHpSettings CurrentRaidSettings()
        {
            return raidF12.Active ? raidF12.Hp() : LiveRaidSettings();
        }
        private RaidHpSettings LiveRaidSettings()
        {
            return new RaidHpSettings {
                Enabled = isEnabled.Value,
                Head = head.Value, Chest = chest.Value, Stomach = stomach.Value,
                LeftArm = leftArm.Value, RightArm = rightArm.Value,
                LeftLeg = leftLeg.Value, RightLeg = rightLeg.Value,
                EveryTen = eachTen.Value, AtMax = atMax.Value,
                Prestige = prestige.Value, Multiplicative = prestigeMultiplicative.Value,
                AutoCap = autoMaxLevel.Value, ManualCap = maxLevel.Value
            };
        }

        private static string Bool(ConfigEntry<bool> entry) => entry.Value ? "true" : "false";

        private void SaveSettings()
        {
            if (isFika) return;
            try
            {
                string folder = Path.Combine(Paths.GameRootPath, "SPT_Runtime", "user", "mods", "GigaPMC", "config");
                if (!Directory.Exists(folder))
                {
                    Logger.LogWarning("[GigaPMC] SPT backend GigaPMC/config directory not found: cannot sync F12 changes.");
                    return;
                }
                string file = Path.Combine(folder, "f12-overrides.json");
                string json = "{\n"
                    + "  \"ConfigSchemaVersion\": 200,\n"
                    + "  \"Enabled\": " + Bool(isEnabled) + ",\n"
                    + "  \"HeadHpPerLevel\": " + head.Value + ",\n"
                    + "  \"ChestHpPerLevel\": " + chest.Value + ",\n"
                    + "  \"StomachHpPerLevel\": " + stomach.Value + ",\n"
                    + "  \"LeftArmHpPerLevel\": " + leftArm.Value + ",\n"
                    + "  \"RightArmHpPerLevel\": " + rightArm.Value + ",\n"
                    + "  \"LeftLegHpPerLevel\": " + leftLeg.Value + ",\n"
                    + "  \"RightLegHpPerLevel\": " + rightLeg.Value + ",\n"
                    + "  \"BonusHpEvery10Levels\": " + eachTen.Value + ",\n"
                    + "  \"BonusHpAtMaxLevel\": " + atMax.Value + ",\n"
                    + "  \"BonusHpPerPrestige\": " + prestige.Value + ",\n"
                    + "  \"PrestigeMultiplicative\": " + Bool(prestigeMultiplicative) + ",\n"
                    + "  \"AutoDetectMaxPmcLevel\": " + Bool(autoMaxLevel) + ",\n"
                    + "  \"MaxPmcLevel\": " + maxLevel.Value + "\n}\n";
                string temp = file + ".tmp";
                string backup = file + ".bak";
                File.WriteAllText(temp, json);
                if (File.Exists(file))
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(temp, file, backup);
                }
                else File.Move(temp, file);
            }
            catch (Exception ex)
            {
                Logger.LogError("[GigaPMC] Cannot write F12 settings for the local SPT backend: " + ex.Message);
            }
        }
    }

    internal sealed class ConfigurationManagerAttributes
    {
        public bool? ShowRangeAsPercent;
        public bool? ReadOnly;
        public Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
    }
}


