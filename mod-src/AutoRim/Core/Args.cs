using System.Collections.Generic;
using AutoRim.Bridge;

namespace AutoRim.Core
{
    /// <summary>
    /// Argument readers for command implementations. Every failure is a BAD_ARGS
    /// CommandException naming the offending field, so the caller can correct it in one turn
    /// instead of guessing.
    /// </summary>
    public static class Args
    {
        public static string RequireString(this JsonValue args, string name)
        {
            var value = args[name].AsString();
            if (string.IsNullOrEmpty(value))
                throw CommandException.BadArgs($"Missing required argument '{name}'.");
            return value;
        }

        public static string OptString(this JsonValue args, string name, string fallback = null)
        {
            var value = args[name].AsString();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        public static int RequireInt(this JsonValue args, string name)
        {
            if (args[name].Type != JsonType.Number)
                throw CommandException.BadArgs($"Missing or non-numeric argument '{name}'.");
            return args[name].AsInt();
        }

        public static int OptInt(this JsonValue args, string name, int fallback) =>
            args[name].Type == JsonType.Number ? args[name].AsInt() : fallback;

        public static int RequireIntInRange(this JsonValue args, string name, int min, int max)
        {
            int value = args.RequireInt(name);
            if (value < min || value > max)
                throw CommandException.BadArgs($"'{name}' must be between {min} and {max} (got {value}).");
            return value;
        }

        public static bool OptBool(this JsonValue args, string name, bool fallback = false) =>
            args[name].Type == JsonType.Bool ? args[name].AsBool() : fallback;

        public static float OptFloat(this JsonValue args, string name, float fallback) =>
            args[name].Type == JsonType.Number ? (float)args[name].AsDouble() : fallback;

        /// <summary>
        /// Reads a list of strings, accepting either a JSON array or a single string for
        /// convenience (callers routinely pass one value where a list is allowed).
        /// </summary>
        public static List<string> RequireStringList(this JsonValue args, string name)
        {
            var result = new List<string>();
            var node = args[name];

            if (node.Type == JsonType.String)
            {
                result.Add(node.AsString());
            }
            else if (node.Type == JsonType.Array)
            {
                foreach (var item in node.Items)
                {
                    var s = item.AsString();
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }

            if (result.Count == 0)
                throw CommandException.BadArgs($"Missing required argument '{name}' (string or array of strings).");
            return result;
        }

        public static List<int> OptIntList(this JsonValue args, string name)
        {
            var result = new List<int>();
            var node = args[name];
            if (node.Type == JsonType.Number) result.Add(node.AsInt());
            else if (node.Type == JsonType.Array)
                foreach (var item in node.Items)
                    if (item.Type == JsonType.Number) result.Add(item.AsInt());
            return result;
        }

        /// <summary>
        /// Standard paging. Callers may omit both; limit is clamped so no command can be
        /// coaxed into returning an unbounded collection.
        /// </summary>
        public static void ReadPaging(this JsonValue args, out int offset, out int limit,
                                      int defaultLimit = 50, int maxLimit = 200)
        {
            offset = System.Math.Max(0, args.OptInt("offset", 0));
            limit = args.OptInt("limit", defaultLimit);
            if (limit <= 0) limit = defaultLimit;
            if (limit > maxLimit) limit = maxLimit;
        }
    }
}
