using System;
using System.IO;
using AutoRim.Bridge;
using AutoRim.Core;
using RimWorld;
using Verse;

namespace AutoRim.Commands
{
    public class BridgeStatusCommand : CommandBase
    {
        public override string Name => "control.bridge_status";
        public override string Description => "Bridge health: listening state, port, queue depth, whether a game is loaded.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            var settings = AutoRimMod.Settings;
            var result = JsonValue.NewObject()
                .Set("listening", HttpBridge.Running)
                .Set("port", HttpBridge.Port)
                .Set("gameLoaded", GameState.IsPlaying)
                .Set("queueDepth", Dispatcher.QueueDepth)
                .Set("commandCount", CommandRegistry.Count)
                .Set("autosaveBeforeDestructive", settings?.autosaveBeforeDestructive ?? false)
                .Set("notifyOnDestructive", settings?.notifyOnDestructive ?? false)
                .Set("runsWhileUnfocused", BackgroundRunning.IsActive);

            if (!string.IsNullOrEmpty(HttpBridge.LastError))
                result.Set("lastError", HttpBridge.LastError);

            return result;
        }
    }

    /// <summary>
    /// Kill switch. Deliberately reachable through the bridge so the assistant can shut itself
    /// out; re-enabling requires the in-game settings panel.
    /// </summary>
    public class DisableBridgeCommand : CommandBase
    {
        public override string Name => "control.disable_bridge";
        public override string Description => "Stops the bridge. Re-enable in Options > Mod settings > AutoRim.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            if (AutoRimMod.Settings != null)
            {
                AutoRimMod.Settings.bridgeEnabled = false;
                AutoRimMod.Instance?.WriteSettings();
            }

            // The listener has to outlive this response, so close it on the next frame rather
            // than pulling the socket out from under the reply we are about to send.
            LongEventHandler.ExecuteWhenFinished(HttpBridge.Stop);

            return JsonValue.NewObject()
                .Set("listening", false)
                .Set("summary", "AutoRim bridge disabled.");
        }
    }

    /// <summary>
    /// Toggles whether the game keeps simulating with its window in the background. Exposed as
    /// a command because the symptom — every request timing out the moment you alt-tab — is
    /// otherwise hard to attribute.
    /// </summary>
    public class SetRunInBackgroundCommand : CommandBase
    {
        public override string Name => "control.set_run_in_background";
        public override string Description =>
            "Keeps the colony simulating while the RimWorld window is unfocused. Needed for the bridge to work while you type elsewhere.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            bool enabled = args.OptBool("enabled", true);

            if (AutoRimMod.Settings != null)
            {
                AutoRimMod.Settings.keepRunningUnfocused = enabled;
                AutoRimMod.Instance?.WriteSettings();
            }

            if (enabled)
            {
                BackgroundRunning.ApplyNow();
            }
            else
            {
                try
                {
                    Prefs.RunInBackground = false;
                    UnityEngine.Application.runInBackground = false;
                }
                catch (Exception ex)
                {
                    ARLog.Exception("disabling run-in-background", ex);
                }
            }

            return JsonValue.NewObject()
                .Set("enabled", enabled)
                .Set("active", BackgroundRunning.IsActive)
                .Set("summary", enabled
                    ? "The colony will keep running while the window is unfocused."
                    : "The game will pause when its window loses focus. Bridge requests will time out while it is in the background.");
        }
    }

    public class SetSpeedCommand : CommandBase
    {
        public override string Name => "control.set_speed";
        public override string Description => "Sets game speed: paused, normal, fast, superfast, ultrafast.";

        public override JsonValue Execute(JsonValue args)
        {
            string requested = args.RequireString("speed").Trim().ToLowerInvariant();

            TimeSpeed speed;
            switch (requested)
            {
                case "paused": case "pause": case "0": speed = TimeSpeed.Paused; break;
                case "normal": case "1": speed = TimeSpeed.Normal; break;
                case "fast": case "2": speed = TimeSpeed.Fast; break;
                case "superfast": case "3": speed = TimeSpeed.Superfast; break;
                case "ultrafast": case "ultra": case "4": speed = TimeSpeed.Ultrafast; break;
                default:
                    throw CommandException.BadArgs($"Unknown speed '{requested}'.",
                        "Use one of: paused, normal, fast, superfast, ultrafast.");
            }

            Find.TickManager.CurTimeSpeed = speed;

            return JsonValue.NewObject()
                .Set("speed", speed.ToString().ToLowerInvariant())
                .Set("paused", Find.TickManager.Paused)
                .Set("summary", $"Game speed set to {speed}.");
        }
    }

    /// <summary>
    /// Writes a named save. The name is always forced into an "AutoRim-" namespace so this
    /// command can never overwrite one of the player's own saves — which on a permadeath run
    /// would be unrecoverable.
    /// </summary>
    public class SaveCommand : CommandBase
    {
        public override string Name => "control.save";
        public override string Description => "Saves the game to an AutoRim-prefixed slot. Cannot overwrite player saves.";

        public const string Prefix = "AutoRim-";

        public override JsonValue Execute(JsonValue args)
        {
            string requested = args.OptString("name", "manual");
            string sanitized = Sanitize(requested);
            if (sanitized.Length == 0)
                throw CommandException.BadArgs("'name' contained no usable characters.");

            string fileName = sanitized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? sanitized
                : Prefix + sanitized;

            GameDataSaveLoader.SaveGame(fileName);

            return JsonValue.NewObject()
                .Set("file", fileName)
                .Set("summary", $"Saved as '{fileName}'.");
        }

        private static string Sanitize(string raw)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (Array.IndexOf(invalid, c) >= 0) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public class NotifyCommand : CommandBase
    {
        public override string Name => "control.notify";
        public override string Description => "Shows a message in game. Useful for telling the player what was just done.";

        public override JsonValue Execute(JsonValue args)
        {
            string text = args.RequireString("text");
            string kind = args.OptString("type", "neutral").ToLowerInvariant();

            MessageTypeDef type;
            switch (kind)
            {
                case "positive": type = MessageTypeDefOf.PositiveEvent; break;
                case "negative": type = MessageTypeDefOf.NegativeEvent; break;
                case "threat": type = MessageTypeDefOf.ThreatSmall; break;
                case "caution": type = MessageTypeDefOf.CautionInput; break;
                default: type = MessageTypeDefOf.NeutralEvent; break;
            }

            Messages.Message(text, type, false);

            return JsonValue.NewObject().Set("summary", $"Showed message: {text}");
        }
    }
}
