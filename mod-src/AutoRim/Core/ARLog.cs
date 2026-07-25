using System;
using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// Prefixed logging. Everything the mod writes to Player.log goes through here so the
    /// user can find our lines in a log that is otherwise full of vanilla and mod chatter.
    /// </summary>
    public static class ARLog
    {
        public const string Prefix = "[AutoRim]";

        public static void Message(string text) => Log.Message($"{Prefix} {text}");

        public static void Warning(string text) => Log.Warning($"{Prefix} {text}");

        public static void Error(string text) => Log.Error($"{Prefix} {text}");

        /// <summary>
        /// Logs an exception without ever rethrowing. Called from the command execution path,
        /// where an escaping exception would land inside the game's update loop.
        /// </summary>
        public static void Exception(string context, Exception ex)
        {
            Log.Warning($"{Prefix} {context} failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
