using System;
namespace GigaPMC.Client
{
    // GameWorld.MainPlayer is the selected local player; never sample bots,
    // Scavs or a mismatched explicit IsYourPlayer result.
    internal static class LocalPmcGate
    {
        internal static bool IsAuthorized(bool mainPlayerPresent, bool? isAi,
            bool? isYourPlayer, string side, bool verifiedEftPlayer,
            bool verifiedEftProfile)
        {
            return mainPlayerPresent && isAi == false && isYourPlayer != false &&
                verifiedEftPlayer && verifiedEftProfile &&
                (string.Equals(side,"Bear",StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(side,"Usec",StringComparison.OrdinalIgnoreCase));
        }
    }
}
