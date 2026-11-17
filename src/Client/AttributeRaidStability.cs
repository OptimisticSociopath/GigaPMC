using System;

namespace GigaPMC.Client
{
    // P16 pure, offline-tested lifecycle model. There is NO runtime poller,
    // injected observer, Harmony hook, and NO read of local user profiles.
    internal sealed class AttributeRaidStability
    {
        private object currentPlayer;
        private object currentScene;
        private string currentProfile;
        private int stableSamples;
        internal bool Ready => stableSamples >= 3;
        internal int StableSamples => stableSamples;

        internal bool Observe(object player, object scene, string profileIdentity,
            bool explicitlyLocalPmc, bool fullyInitialized)
        {
            if (player == null || scene == null || string.IsNullOrWhiteSpace(profileIdentity) ||
                !explicitlyLocalPmc || !fullyInitialized)
            { Reset(); return false; }
            if (!ReferenceEquals(player,currentPlayer) ||
                !ReferenceEquals(scene,currentScene) ||
                !string.Equals(profileIdentity,currentProfile,StringComparison.Ordinal))
            {
                currentPlayer=player;
                currentScene=scene;
                currentProfile=profileIdentity;
                stableSamples=1;
                return false;
            }
            if (stableSamples < 3) stableSamples++;
            return Ready;
        }

        internal void Reset()
        {
            currentPlayer=null;
            currentScene=null;
            currentProfile=null;
            stableSamples=0;
        }
    }
}
