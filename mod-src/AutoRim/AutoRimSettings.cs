using Verse;

namespace AutoRim
{
    public class AutoRimSettings : ModSettings
    {
        public const int DefaultPort = 7789;

        /// <summary>Master kill switch. When false the listener is closed and every command refuses.</summary>
        public bool bridgeEnabled = true;

        public int port = DefaultPort;

        /// <summary>
        /// Autosave before executing a destructive command. Defaults on: several of this user's
        /// saves are permadeath, where an unwanted destructive action cannot be undone.
        /// </summary>
        public bool autosaveBeforeDestructive = true;

        /// <summary>Show an in-game message whenever a destructive command executes.</summary>
        public bool notifyOnDestructive = true;

        /// <summary>Verbose per-request logging. Off by default; noisy.</summary>
        public bool logRequests;

        /// <summary>
        /// Keep the game simulating while its window is not focused.
        ///
        /// Unity halts the whole application when unfocused unless told otherwise, which stops
        /// the main-thread pump — so every bridge request times out the moment you switch to
        /// the chat window. On by default, because that is the entire point of this mod.
        /// </summary>
        public bool keepRunningUnfocused = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref bridgeEnabled, "bridgeEnabled", true);
            Scribe_Values.Look(ref port, "port", DefaultPort);
            Scribe_Values.Look(ref autosaveBeforeDestructive, "autosaveBeforeDestructive", true);
            Scribe_Values.Look(ref notifyOnDestructive, "notifyOnDestructive", true);
            Scribe_Values.Look(ref logRequests, "logRequests", false);
            Scribe_Values.Look(ref keepRunningUnfocused, "keepRunningUnfocused", true);
            base.ExposeData();
        }
    }
}
