using System;
namespace GigaPMC.Client
{
    // Stable local-player observations only. No EFT skill descriptor, stamina
    // baseline, global buff table or numerical strength/endurance formula.
    internal sealed class LocalTrialAuthority
    {
        private object world,player,profile,physical;
        private decimal percent;
        private int samples;
        internal int Samples => samples;
        internal bool Observe(object candidateWorld,object candidatePlayer,
            object candidateProfile,object candidatePhysical,decimal candidatePercent)
        {
            if(candidateWorld==null || candidatePlayer==null || candidateProfile==null ||
                candidatePhysical==null || candidatePercent<=0m || candidatePercent>100000m)
            { Reset(); return false; }
            bool same=ReferenceEquals(world,candidateWorld) &&
                ReferenceEquals(player,candidatePlayer) &&
                ReferenceEquals(profile,candidateProfile) &&
                ReferenceEquals(physical,candidatePhysical) && percent==candidatePercent;
            if(!same)
            {
                world=candidateWorld;player=candidatePlayer;profile=candidateProfile;
                physical=candidatePhysical;percent=candidatePercent;samples=1;
                return false;
            }
            if(samples<3) samples++;
            return samples==3;
        }
        internal void Reset()
        { world=player=profile=physical=null;percent=0m;samples=0; }
    }
}
