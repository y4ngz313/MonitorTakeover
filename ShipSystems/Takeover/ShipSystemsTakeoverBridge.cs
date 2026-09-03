// ShipSystemsTakeoverBridge.cs — #611 task 1.3.
//
// Handing the monitor wall to the takeover, and giving it back.
//
// This used to resolve "Y4NGZCompany.ShipSystems.Layout.ShipSystemsApi, Y4NGZCompany" and probe
// for an overload taking a Texture, falling back to a no-arg call "for compatibility with an
// older split ShipSystems module". Both halves of that are gone: the string names the monolith
// and cannot survive the split, and the overload probe is now a contract member with a defined
// shape. The Texture-versus-no-Texture choice lives in the provider, where the engine types
// are, so this side no longer reflects to find out what the other side can accept.

using UnityEngine;
using Y4NGZCore.Modules.ShipSystems;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class ShipSystemsTakeoverBridge
    {
        private static bool _takeoverActive;

        /// <summary>
        /// Tells the ship's own monitor content to stand down and hands it the texture the
        /// takeover is painting. Idempotent while a takeover is running.
        ///
        /// <para>The <c>_takeoverActive</c> latch is this side's, not the sink's: it is what
        /// makes <see cref="EndExternalMonitorTakeover"/> safe to call from every teardown path
        /// including the ones that never started a takeover.</para>
        /// </summary>
        internal static void BeginExternalMonitorTakeover(Texture takeoverTexture)
        {
            if (_takeoverActive) return;
            _takeoverActive = true;
            ShipSystemsServices.BeginExternalMonitorTakeover(takeoverTexture);
        }

        internal static void UpdateExternalMonitorTakeoverTexture(Texture takeoverTexture)
        {
            if (!_takeoverActive || takeoverTexture == null) return;
            ShipSystemsServices.UpdateExternalMonitorTakeoverTexture(takeoverTexture);
        }

        internal static void EndExternalMonitorTakeover()
        {
            if (!_takeoverActive) return;
            _takeoverActive = false;
            ShipSystemsServices.EndExternalMonitorTakeover();
        }
    }
}
