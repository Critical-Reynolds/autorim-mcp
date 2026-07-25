using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// Main-thread-only helpers for reaching the current game. Never call these from the
    /// socket thread.
    /// </summary>
    public static class GameState
    {
        public static bool IsPlaying =>
            Current.ProgramState == ProgramState.Playing && Current.Game != null;

        /// <summary>The map a command acts on when none is named: the one on screen.</summary>
        public static Map CurrentMap => Find.CurrentMap;

        public static Map RequireMap()
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new CommandException(ErrorCode.NoGame, "No map is active.",
                    "Load a colony and make sure a map is on screen.");
            return map;
        }

        /// <summary>
        /// Resolves an optional 'map' argument (map index as shown by colony.snapshot), falling
        /// back to the current map.
        /// </summary>
        public static Map ResolveMap(int? index)
        {
            if (!index.HasValue) return RequireMap();

            var maps = Find.Maps;
            if (index.Value < 0 || index.Value >= maps.Count)
                throw CommandException.NotFound($"No map with index {index.Value}. There are {maps.Count}.");
            return maps[index.Value];
        }
    }
}
