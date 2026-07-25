using System;
using AutoRim.Bridge;
using AutoRim.Core;
using UnityEngine;
using Verse;

namespace AutoRim
{
    /// <summary>
    /// Runs once after defs have loaded and before the main menu appears. This is the right
    /// place to bring up the bridge: commands can resolve defs from here on, and /health
    /// answers even while the user is still at the menu.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AutoRimStartup
    {
        static AutoRimStartup()
        {
            try
            {
                CommandRegistry.Initialize();

                var settings = AutoRimMod.Settings;
                if (settings == null)
                {
                    ARLog.Warning("Settings unavailable at startup; bridge not started.");
                    return;
                }

                BackgroundRunning.ApplyNow();

                if (settings.bridgeEnabled) HttpBridge.Start(settings.port);
                else ARLog.Message("Bridge is disabled in mod settings; not listening.");

                Application.quitting += OnQuitting;
            }
            catch (Exception ex)
            {
                // A throw here would take the whole mod down. Log and leave the game playable.
                ARLog.Exception("startup", ex);
            }
        }

        private static void OnQuitting()
        {
            Dispatcher.DrainAndFail("RimWorld is shutting down.");
            HttpBridge.Stop();
        }
    }
}
