using System.Reflection;
using AutoRim.Core;
using UnityEngine;
using Verse;

namespace AutoRim
{
    /// <summary>
    /// Mod entry point. RimWorld instantiates this once at startup, before any game is loaded.
    /// Nothing here may touch map or game state.
    /// </summary>
    public class AutoRimMod : Mod
    {
        public static AutoRimMod Instance { get; private set; }
        public static AutoRimSettings Settings { get; private set; }

        private string _portBuffer;

        public AutoRimMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AutoRimSettings>();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            ARLog.Message($"v{version} loaded. Bridge {(Settings.bridgeEnabled ? "enabled" : "DISABLED")} on port {Settings.port}.");
        }

        public override string SettingsCategory() => "AutoRim";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled(
                "Bridge enabled",
                ref Settings.bridgeEnabled,
                "Master switch. When off, the local listener is closed and all external commands are refused.");

            _portBuffer ??= Settings.port.ToString();
            listing.TextFieldNumericLabeled("Port (127.0.0.1 only)", ref Settings.port, ref _portBuffer, 1024, 65535);

            listing.Gap();

            bool wasKeepRunning = Settings.keepRunningUnfocused;
            listing.CheckboxLabeled(
                "Keep playing while the window is unfocused",
                ref Settings.keepRunningUnfocused,
                "Required for the assistant to reach the colony while you are typing in another window. "
                + "Without it Unity suspends the game and every request times out.");
            if (Settings.keepRunningUnfocused && !wasKeepRunning) Core.BackgroundRunning.ApplyNow();

            listing.Gap();

            listing.CheckboxLabeled(
                "Autosave before destructive actions",
                ref Settings.autosaveBeforeDestructive,
                "Strongly recommended on permadeath saves. Destructive actions cannot be undone.");

            listing.CheckboxLabeled(
                "Show a message on destructive actions",
                ref Settings.notifyOnDestructive,
                "Displays an in-game message whenever an external command does something irreversible.");

            listing.CheckboxLabeled(
                "Log every request",
                ref Settings.logRequests,
                "Verbose. Useful when debugging the MCP server, noisy otherwise.");

            listing.Gap();
            listing.Label("Changes to the port take effect the next time the bridge starts.");

            listing.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            _portBuffer = null;
        }
    }
}
