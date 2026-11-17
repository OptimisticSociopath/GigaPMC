using System;
using System.Collections.Generic;

namespace GigaPMC.Client
{
    // P28 R2: SINGLE-RAID snapshot of all F12 settings that influence gameplay.
    // An F12 edit during a raid changes the pending BepInEx values ONLY;
    // gameplay continues to use these immutable (defensive-copy) rates.
    // This class is pure C#: independently exercised by .NET offline tests.
    internal sealed class AttributeF12RaidLease
    {
        private object player;
        private bool seenWorld;
        private int consecutiveMissingWithMenu;
        private const int ConfirmedExitSamples = 3;
        private RaidHpSettings hp;
        private Dictionary<AttributeBonusKind, AttributeBonusRates> rates;
        private bool globalEnabled, carryEnabled, staminaEnabled, effectsEnabled;

        internal bool Active => player != null && hp != null && rates != null;
        internal bool GlobalEnabled => Active && globalEnabled;
        internal bool CarryEnabled => Active && carryEnabled;
        internal bool StaminaEnabled => Active && staminaEnabled;
        internal bool EffectsEnabled => Active && effectsEnabled;
        private JumpDistanceSettings jump=JumpDistanceSettings.Default;
        internal JumpDistanceSettings Jump => jump;
        internal bool Owns(object localPlayer) => Active && ReferenceEquals(player, localPlayer);

        private static RaidHpSettings CopyHp(RaidHpSettings x)
        {
            return new RaidHpSettings {
                Enabled=x.Enabled, Head=x.Head, Chest=x.Chest, Stomach=x.Stomach,
                LeftArm=x.LeftArm, RightArm=x.RightArm,
                LeftLeg=x.LeftLeg, RightLeg=x.RightLeg,
                EveryTen=x.EveryTen, AtMax=x.AtMax, Prestige=x.Prestige,
                Multiplicative=x.Multiplicative, AutoCap=x.AutoCap, ManualCap=x.ManualCap
            };
        }

        internal void Capture(object localPlayer, RaidHpSettings hpNow,
            Func<AttributeBonusKind, AttributeBonusRates> readRates,
            bool isEnabled, bool carry, bool stamina, bool effects,JumpDistanceSettings jumpSettings=null)
        {
            if (localPlayer == null)
                throw new InvalidOperationException("P29_LOCAL_PMC_REQUIRED");
            // Same local PMC: never consult even an invalid pending F12 edit.
            // Re-entrant Player.Init must preserve the already certified lease.
            if (Owns(localPlayer)) return;
            if (hpNow == null || !hpNow.IsValid() || readRates == null)
                throw new InvalidOperationException("P28_R2_RAID_F12_CAPTURE_INVALID");
            // Prepare everything first. On a malformed F12 setting, keep the
            // previous valid raid lease intact; NEVER partially replace it.
            var pending = new Dictionary<AttributeBonusKind, AttributeBonusRates>();
            foreach (var kind in AttributeBonusMath.OrderedKinds)
            {
                var value = readRates(kind);
                if (value == null || !value.IsValid())
                    throw new InvalidOperationException("P28_R2_F12_RATE_INVALID_" + kind);
                pending.Add(kind, value.Copy());
            }
            player = localPlayer;
            hp = CopyHp(hpNow);
            rates = pending;
            jump=jumpSettings??JumpDistanceSettings.Default;
            globalEnabled = isEnabled;
            carryEnabled = isEnabled && carry;
            staminaEnabled = isEnabled && stamina;
            effectsEnabled = isEnabled && effects;
            seenWorld = false;
            consecutiveMissingWithMenu = 0;
        }

        internal RaidHpSettings Hp()
        {
            if (!Active) throw new InvalidOperationException("P28_R2_F12_NOT_CAPTURED");
            return CopyHp(hp);
        }
        internal AttributeBonusRates Rates(AttributeBonusKind kind)
        {
            if (!Active || !rates.TryGetValue(kind,out var rate))
                throw new InvalidOperationException("P28_R2_F12_RATE_NOT_CAPTURED_" + kind);
            return rate.Copy();
        }

        // A missing MainPlayer is NOT proof that a raid ended: Unity loading
        // transitions can temporarily report null. Require three consecutive
        // observations AFTER the same local player was seen, and an independent
        // verified MAIN MENU witness from the already-existing HP menu observer.
        // No wall-clock timers, no guess about GameWorld lifetime.
        internal bool ObserveWorldPlayer(object localWorldPlayer, bool verifiedOutOfRaidMenu)
        {
            if (!Active) return false;
            if (ReferenceEquals(player,localWorldPlayer))
            {
                seenWorld = true;
                consecutiveMissingWithMenu = 0;
                return false;
            }
            if (!seenWorld || !verifiedOutOfRaidMenu)
            {
                consecutiveMissingWithMenu = 0;
                return false;
            }
            if (++consecutiveMissingWithMenu < ConfirmedExitSamples) return false;
            Clear();
            return true;
        }
        internal void Clear()
        {
            player=null; hp=null; rates=null; seenWorld=false;
            consecutiveMissingWithMenu=0;
            globalEnabled=carryEnabled=staminaEnabled=effectsEnabled=false;
            jump=JumpDistanceSettings.Default;
        }
    }
}
