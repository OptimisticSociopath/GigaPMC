namespace GigaPMC.Client
{
    internal enum LocomotionPhase { Ground, Fall, Jump, Landing, Excluded }

    // A loss of ground contact is not a loss of horizontal momentum. Keep the
    // movement multiplier across walk/run -> fall -> landing. An explicit jump
    // uses its own setting, even if the first jump frame is still grounded.
    // This never modifies gravity, vertical motion or the native ground flags.
    internal sealed class AirborneMovementPolicy
    {
        private bool hasGroundHistory, jumpEpisode;

        internal void Reset() { hasGroundHistory=false; jumpEpisode=false; }

        internal float Select(LocomotionPhase phase,bool grounded,float movementFactor,
            bool scaleJump,float jumpStrengthPercent)
        {
            if(phase==LocomotionPhase.Excluded)
            { Reset();return 1f; }
            if(phase==LocomotionPhase.Jump)
            { jumpEpisode=true;return JumpFactor(movementFactor,scaleJump,jumpStrengthPercent); }
            if(grounded)
            { hasGroundHistory=true;jumpEpisode=false;return movementFactor; }
            if(jumpEpisode)return JumpFactor(movementFactor,scaleJump,jumpStrengthPercent);
            return hasGroundHistory?movementFactor:1f;
        }

        internal static float JumpFactor(float movementFactor,bool enabled,float strengthPercent)
        {
            if(!enabled||!CharacterMovementMath.Finite(movementFactor)||movementFactor<1f||
               !CharacterMovementMath.Finite(strengthPercent)||strengthPercent<0f||strengthPercent>100f)return 1f;
            return 1f+(movementFactor-1f)*(strengthPercent/100f);
        }
    }
}
