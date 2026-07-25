using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AutoRim.Bridge;
using RimWorld;
using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// Enforces the safety tier of every command. Runs on the main thread, inside the
    /// dispatcher pump.
    ///
    /// Safe commands run straight through. Destructive commands refuse without an explicit
    /// confirm flag, returning a preview of the consequences instead; when confirmed they are
    /// autosaved ahead of, announced in game, and written to an audit log.
    /// </summary>
    public static class SafetyGate
    {
        /// <summary>Name of the rolling restore point written before destructive actions.</summary>
        public const string SafetySaveName = "AutoRim-safety";

        private const int AutosaveCooldownSeconds = 60;

        private static readonly Stopwatch SinceStart = Stopwatch.StartNew();
        private static long _lastSafetySaveMs = long.MinValue;

        public static JsonValue Run(ICommand command, JsonValue args)
        {
            if (AutoRimMod.Settings != null && !AutoRimMod.Settings.bridgeEnabled)
                throw new CommandException(ErrorCode.BridgeDisabled,
                    "The AutoRim bridge is disabled.",
                    "Re-enable it in RimWorld under Options > Mod settings > AutoRim.");

            if (command.Tier == SafetyTier.Safe)
                return command.Execute(args);

            return RunDestructive(command, args);
        }

        private static JsonValue RunDestructive(ICommand command, JsonValue args)
        {
            if (!args.OptBool("confirm"))
            {
                JsonValue preview = null;
                if (command is IPreviewable previewable)
                {
                    try
                    {
                        preview = previewable.Preview(args);
                    }
                    catch (CommandException)
                    {
                        // A preview that cannot be built (bad target, nothing matched) should
                        // surface as the underlying problem, not as a confirmation prompt.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ARLog.Exception($"preview for '{command.Name}'", ex);
                    }
                }

                var error = new CommandException(ErrorCode.NeedsConfirm,
                    $"'{command.Name}' is irreversible and was not executed.",
                    "Show the user what this would do, get their agreement, then resend with confirm: true.")
                {
                    Payload = JsonValue.NewObject()
                        .Set("command", command.Name)
                        .Set("preview", preview ?? JsonValue.Null)
                };
                throw error;
            }

            MaybeWriteSafetySave(command.Name);

            var result = command.Execute(args);

            Announce(command.Name, args, result);
            Audit(command.Name, args, result);

            return result;
        }

        /// <summary>
        /// Writes a single rolling restore point. Overwrites itself rather than accumulating
        /// files, so there is always exactly one "just before AutoRim did something" save.
        /// Rate-limited so a batch of confirmed actions does not save repeatedly.
        /// </summary>
        private static void MaybeWriteSafetySave(string commandName)
        {
            if (AutoRimMod.Settings == null || !AutoRimMod.Settings.autosaveBeforeDestructive) return;

            long now = SinceStart.ElapsedMilliseconds;
            if (now - _lastSafetySaveMs < AutosaveCooldownSeconds * 1000L) return;

            try
            {
                GameDataSaveLoader.SaveGame(SafetySaveName);
                _lastSafetySaveMs = now;
                ARLog.Message($"Wrote restore point '{SafetySaveName}' before '{commandName}'.");
            }
            catch (Exception ex)
            {
                // A failed safety save must not block the action the user explicitly confirmed,
                // but they need to know the restore point is not there.
                ARLog.Exception("writing safety save", ex);
                TryMessage($"AutoRim could not write the '{SafetySaveName}' restore point.",
                    MessageTypeDefOf.CautionInput);
            }
        }

        private static void Announce(string commandName, JsonValue args, JsonValue result)
        {
            if (AutoRimMod.Settings == null || !AutoRimMod.Settings.notifyOnDestructive) return;

            string summary = result?["summary"].AsString() ?? commandName;
            TryMessage($"AutoRim: {summary}", MessageTypeDefOf.NeutralEvent);
        }

        private static void TryMessage(string text, MessageTypeDef type)
        {
            try
            {
                Messages.Message(text, type, false);
            }
            catch (Exception ex)
            {
                ARLog.Exception("showing in-game message", ex);
            }
        }

        private static void Audit(string commandName, JsonValue args, JsonValue result)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string summary = result?["summary"].AsString() ?? "(no summary)";
                string line = $"{stamp}\t{commandName}\t{summary}\targs={args}\n";
                File.AppendAllText(Paths.ActionLogFile, line);
            }
            catch (Exception ex)
            {
                ARLog.Exception("writing action log", ex);
            }
        }
    }
}
