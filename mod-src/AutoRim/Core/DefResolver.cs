using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoRim.Bridge;
using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// Turns what a person would say ("steel wall", "granite", "hunting", "simple meal") into
    /// the Def the game actually wants.
    ///
    /// This is the difference between the tools feeling usable and feeling like guesswork.
    /// When a query is ambiguous the resolver refuses and hands back the candidates, so the
    /// caller can disambiguate in one more turn instead of silently acting on the wrong thing.
    /// </summary>
    public static class DefResolver
    {
        private const int ScoreExactDefName = 100;
        private const int ScoreExactLabel = 95;
        private const int ScoreNormalizedEqual = 90;
        private const int ScoreLabelStartsWith = 75;
        private const int ScoreLabelContains = 60;
        private const int ScoreAllTokens = 50;

        /// <summary>How many candidates to show when refusing.</summary>
        private const int MaxCandidates = 8;

        public struct Match<T> where T : Def
        {
            public T Def;
            public int Score;
        }

        /// <summary>
        /// Resolves exactly one def, or throws NOT_FOUND / AMBIGUOUS with candidates attached.
        /// The optional filter narrows the search space, which both avoids false matches and
        /// makes the candidate list useful — "door" should not offer every door-shaped item
        /// when the caller is trying to build one.
        /// </summary>
        public static T Resolve<T>(string query, string argName = "query", Func<T, bool> filter = null)
            where T : Def, new()
        {
            if (string.IsNullOrEmpty(query))
                throw CommandException.BadArgs($"Missing '{argName}'.");

            var matches = Rank(query, filter);

            if (matches.Count == 0)
            {
                var nearest = Nearest(query, MaxCandidates, filter);
                var error = CommandException.NotFound(
                    $"No {FriendlyTypeName<T>()} matches '{query}'.",
                    nearest.Count > 0 ? "Did you mean one of the candidates listed?" : null);
                error.Payload = JsonValue.NewObject().Set("candidates", Describe(nearest));
                throw error;
            }

            int topScore = matches[0].Score;
            var tied = matches.Where(m => m.Score == topScore).ToList();

            if (tied.Count == 1) return tied[0].Def;

            var ambiguous = new CommandException(ErrorCode.Ambiguous,
                $"'{query}' matches {tied.Count} {FriendlyTypeName<T>()} entries equally well.",
                "Resend using one of the exact defName values below.");
            ambiguous.Payload = JsonValue.NewObject()
                .Set("candidates", Describe(tied.Take(MaxCandidates).Select(m => m.Def).ToList()));
            throw ambiguous;
        }

        /// <summary>Resolve, or null when there is no match. For optional arguments.</summary>
        public static T ResolveOrNull<T>(string query, Func<T, bool> filter = null) where T : Def, new()
        {
            if (string.IsNullOrEmpty(query)) return null;
            var matches = Rank(query, filter);
            if (matches.Count == 0) return null;
            int topScore = matches[0].Score;
            return matches.Count(m => m.Score == topScore) == 1 ? matches[0].Def : null;
        }

        /// <summary>Free-text search, ordered best first. Used by the query tool.</summary>
        public static List<T> Search<T>(string query, int limit, Func<T, bool> filter = null) where T : Def, new()
        {
            if (string.IsNullOrEmpty(query))
                return DefDatabase<T>.AllDefsListForReading
                    .Where(d => filter == null || filter(d))
                    .Take(limit)
                    .ToList();

            return Rank(query, filter).Take(limit).Select(m => m.Def).ToList();
        }

        // ---- ranking ----------------------------------------------------------------------

        private static List<Match<T>> Rank<T>(string query, Func<T, bool> filter) where T : Def, new()
        {
            string trimmed = query.Trim();
            string normalizedQuery = Normalize(trimmed);
            string[] queryTokens = normalizedQuery.Split(' ')
                .Where(t => t.Length > 0).ToArray();

            var results = new List<Match<T>>();

            foreach (var def in DefDatabase<T>.AllDefsListForReading)
            {
                if (filter != null && !filter(def)) continue;
                int score = Score(def, trimmed, normalizedQuery, queryTokens);
                if (score > 0) results.Add(new Match<T> { Def = def, Score = score });
            }

            // Best score first; among equals prefer the shorter label, which is almost always
            // the plainer, more expected item ("wall" over "wall (ancient, damaged)").
            results.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                int byLength = LabelOf(a.Def).Length.CompareTo(LabelOf(b.Def).Length);
                if (byLength != 0) return byLength;
                return string.CompareOrdinal(a.Def.defName, b.Def.defName);
            });

            return results;
        }

        private static int Score(Def def, string raw, string normalizedQuery, string[] queryTokens)
        {
            if (string.Equals(def.defName, raw, StringComparison.OrdinalIgnoreCase))
                return ScoreExactDefName;

            string label = LabelOf(def);

            if (label.Length > 0 && string.Equals(label, raw, StringComparison.OrdinalIgnoreCase))
                return ScoreExactLabel;

            string normalizedLabel = Normalize(label);
            string normalizedDefName = Normalize(def.defName);

            if (normalizedLabel == normalizedQuery || normalizedDefName == normalizedQuery)
                return ScoreNormalizedEqual;

            if (normalizedQuery.Length == 0) return 0;

            if (normalizedLabel.StartsWith(normalizedQuery, StringComparison.Ordinal))
                return ScoreLabelStartsWith;

            if (normalizedLabel.Contains(normalizedQuery) || normalizedDefName.Contains(normalizedQuery))
                return ScoreLabelContains;

            if (queryTokens.Length > 1)
            {
                bool allPresent = queryTokens.All(token =>
                    normalizedLabel.Contains(token) || normalizedDefName.Contains(token));
                if (allPresent) return ScoreAllTokens;
            }

            return 0;
        }

        /// <summary>
        /// Loose fallback used only to suggest alternatives after a failed lookup: any def
        /// sharing a word with the query.
        /// </summary>
        private static List<T> Nearest<T>(string query, int limit, Func<T, bool> filter) where T : Def, new()
        {
            var tokens = Normalize(query).Split(' ').Where(t => t.Length >= 3).ToArray();
            if (tokens.Length == 0) return new List<T>();

            return DefDatabase<T>.AllDefsListForReading
                .Where(def => filter == null || filter(def))
                .Where(def =>
                {
                    string haystack = Normalize(LabelOf(def) + " " + def.defName);
                    return tokens.Any(token => haystack.Contains(token));
                })
                .OrderBy(def => LabelOf(def).Length)
                .Take(limit)
                .ToList();
        }

        // ---- helpers ----------------------------------------------------------------------

        public static JsonValue Describe(Def def)
        {
            if (def == null) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("defName", def.defName)
                .Set("label", LabelOf(def));
        }

        public static JsonValue Describe<T>(IEnumerable<T> defs) where T : Def
        {
            var array = JsonValue.NewArray();
            foreach (var def in defs) array.Add(Describe(def));
            return array;
        }

        private static string LabelOf(Def def) => def.label ?? string.Empty;

        private static string FriendlyTypeName<T>() where T : Def
        {
            // "ResearchProjectDef" -> "research project"
            string name = typeof(T).Name;
            if (name.EndsWith("Def", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 3);

            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            return sb.ToString();
        }

        /// <summary>Lowercases and reduces anything non-alphanumeric to single spaces.</summary>
        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length);
            bool lastWasSpace = false;
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            return sb.ToString().Trim();
        }
    }
}
