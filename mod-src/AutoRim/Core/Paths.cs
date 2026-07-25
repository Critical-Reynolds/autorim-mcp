using System;
using System.IO;
using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// AutoRim's own files live next to RimWorld's config, not in the mod folder: the mod
    /// folder is overwritten on every deploy, and Program Files is not a sensible place for
    /// runtime state.
    /// </summary>
    public static class Paths
    {
        private static string _root;

        /// <summary>%LOCALAPPDATA%Low\Ludeon Studios\RimWorld by Ludeon Studios\Config\AutoRim</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                _root = Path.Combine(GenFilePaths.ConfigFolderPath, "AutoRim");
                try
                {
                    Directory.CreateDirectory(_root);
                }
                catch (Exception ex)
                {
                    ARLog.Exception($"creating config directory '{_root}'", ex);
                }
                return _root;
            }
        }

        /// <summary>Shared secret the MCP server presents on every request.</summary>
        public static string TokenFile => Path.Combine(Root, "bridge.token");

        /// <summary>Append-only record of every destructive action taken through the bridge.</summary>
        public static string ActionLogFile => Path.Combine(Root, "actions.log");
    }
}
