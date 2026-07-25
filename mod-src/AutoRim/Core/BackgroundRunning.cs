using System;
using UnityEngine;
using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// Keeps RimWorld simulating while its window is in the background.
    ///
    /// Unity suspends the application when it loses focus unless Application.runInBackground is
    /// set. That suspends the main-thread pump too, so every bridge request times out the
    /// instant the player alt-tabs to talk to the assistant — which is exactly when they are
    /// using this mod.
    ///
    /// RimWorld exposes the same thing as Prefs.RunInBackground and re-applies its own
    /// preference at various points, so setting the Unity flag alone does not stick. Both are
    /// set, and the value is re-checked periodically rather than once, because opening the
    /// options screen can put it back.
    /// </summary>
    public static class BackgroundRunning
    {
        /// <summary>Frames between checks. Cheap enough to be invisible, often enough to catch a reset.</summary>
        private const int CheckIntervalFrames = 120;

        private static int _framesSinceCheck = CheckIntervalFrames;
        private static bool _loggedOnce;

        /// <summary>Called once at startup.</summary>
        public static void ApplyNow()
        {
            var settings = AutoRimMod.Settings;
            if (settings == null || !settings.keepRunningUnfocused) return;

            Apply(logIfChanged: true);
        }

        /// <summary>Called every frame from the game component; does real work rarely.</summary>
        public static void Tick()
        {
            var settings = AutoRimMod.Settings;
            if (settings == null || !settings.keepRunningUnfocused) return;

            if (++_framesSinceCheck < CheckIntervalFrames) return;
            _framesSinceCheck = 0;

            Apply(logIfChanged: false);
        }

        private static void Apply(bool logIfChanged)
        {
            try
            {
                bool changed = false;

                if (!Application.runInBackground)
                {
                    Application.runInBackground = true;
                    changed = true;
                }

                if (!Prefs.RunInBackground)
                {
                    // Writing the game's own preference is what makes this survive; the Unity
                    // flag on its own gets overwritten when RimWorld reapplies prefs.
                    Prefs.RunInBackground = true;
                    changed = true;
                }

                if (changed && (logIfChanged || !_loggedOnce))
                {
                    _loggedOnce = true;
                    ARLog.Message("Enabled 'run in background' so the colony keeps simulating while the window is unfocused.");
                }
            }
            catch (Exception ex)
            {
                ARLog.Exception("applying run-in-background", ex);
            }
        }

        /// <summary>Current state, for control.bridge_status and the settings screen.</summary>
        public static bool IsActive
        {
            get
            {
                try
                {
                    return Application.runInBackground && Prefs.RunInBackground;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
