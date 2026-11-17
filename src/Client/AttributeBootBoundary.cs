using System;

namespace GigaPMC.Client
{
    /// <summary>
    /// Fault boundary for optional attribute/F12 initialization only.
    /// It NEVER catches failures from the original HP boot or changes HP state.
    /// </summary>
    internal static class AttributeBootBoundary
    {
        internal static bool TryBind(Action bindOptional, Action clearOptional,
            Action<string> info, Action<string> error)
        {
            if (bindOptional == null || clearOptional == null || info == null || error == null)
                throw new ArgumentNullException("P23_BOOT_CALLBACK_MISSING");
            info("[GigaPMC] P23_OPTIONAL_F12_BOOT_BEGIN");
            try
            {
                bindOptional();
                info("[GigaPMC] P23_OPTIONAL_F12_BOOT_PASS|DEV_VISION_CONFIG_BOUND|NO_GAMEPLAY_HOOKS");
                return true;
            }
            catch (Exception ex)
            {
                // Clear partial state; HP boot is deliberately outside this
                // boundary and is allowed to proceed independently.
                try { clearOptional(); }
                catch (Exception cleanupError)
                {
                    error("[GigaPMC] P23_OPTIONAL_CLEANUP_ERROR|" + cleanupError.GetType().Name);
                }
                error("[GigaPMC] P23_OPTIONAL_F12_BOOT_FAIL_CLOSED|HP_BOOT_CONTINUES|" +
                    ex.GetType().Name + "|" + ex.Message);
                return false;
            }
        }
    }
}
