using System;
using System.IO;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using Verse;
using Verse.Steam;

namespace AutoRim.Commands
{
    /// <summary>
    /// Diagnostics for Steam Workshop publishing.
    ///
    /// RimWorld hides the upload button rather than explaining why it is unavailable, and the
    /// conditions behind it are not surfaced anywhere in the UI. Rather than guess from what is
    /// missing on screen, this reports each condition the game actually checks in
    /// ModMetaData.CanToUploadToWorkshop: not official, sourced from the Mods folder, and not
    /// possibly authored by somebody else.
    /// </summary>
    public class WorkshopStatusCommand : CommandBase
    {
        public override string Name => "control.workshop_status";
        public override string Description =>
            "Reports whether this mod can be uploaded to the Steam Workshop, and which condition is blocking it if not.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            string packageId = args.OptString("packageId", "autorim.mcp");

            var meta = ModLister.AllInstalledMods
                .FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

            if (meta == null)
                throw CommandException.NotFound($"No installed mod with packageId '{packageId}'.",
                    "It must be present in RimWorld's Mods folder and detected at startup.");

            var result = JsonValue.NewObject()
                .Set("packageId", meta.PackageId)
                .Set("name", meta.Name)
                .Set("rootDir", meta.RootDir?.FullName ?? "")
                .Set("active", meta.Active)
                .Set("steamInitialized", SteamInitialized());

            // The three conditions RimWorld actually tests, reported individually.
            bool official = meta.Official;
            var source = meta.Source;
            bool sourceOk = source == ContentSource.ModsFolder;

            var conditions = JsonValue.NewObject()
                .Set("notOfficial", !official)
                .Set("sourceIsModsFolder", sourceOk)
                .Set("source", source.ToString())
                .Set("versionCompatible", meta.VersionCompatible);

            bool canUpload;
            try
            {
                canUpload = meta.CanToUploadToWorkshop();
            }
            catch (Exception ex)
            {
                ARLog.Exception("checking CanToUploadToWorkshop", ex);
                canUpload = false;
                conditions.Set("checkThrew", ex.Message);
            }

            result.Set("canUploadToWorkshop", canUpload);
            result.Set("conditions", conditions);

            // Assets the Workshop listing needs.
            string previewPath = null;
            try
            {
                previewPath = meta.GetWorkshopPreviewImagePath();
            }
            catch (Exception)
            {
            }

            string publishedIdFile = meta.RootDir != null
                ? Path.Combine(meta.RootDir.FullName, "About", "PublishedFileId.txt")
                : null;

            var assets = JsonValue.NewObject()
                .Set("previewImagePath", previewPath ?? "")
                .Set("previewImageExists", !string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
                .Set("alreadyPublished", publishedIdFile != null && File.Exists(publishedIdFile));

            if (publishedIdFile != null && File.Exists(publishedIdFile))
            {
                try
                {
                    assets.Set("publishedFileId", File.ReadAllText(publishedIdFile).Trim());
                }
                catch (Exception)
                {
                }
            }

            result.Set("assets", assets);

            if (!canUpload)
            {
                string reason =
                    official ? "RimWorld treats this as an official mod, which cannot be uploaded."
                    : !sourceOk ? $"The mod was loaded from {source}, but upload requires ContentSource.ModsFolder. " +
                                  "A copy under the Steam Workshop directory takes precedence over the one in Mods."
                    : "The mod may have been authored by a different Steam account (a PublishedFileId from someone else is present).";

                result.Set("blockedBecause", reason);
            }

            result.Set("summary", canUpload
                ? "Ready to upload: the button appears in the mod info panel of the Mods screen."
                : "Cannot upload. See blockedBecause.");

            return result;
        }

        private static bool SteamInitialized()
        {
            try
            {
                return SteamManager.Initialized;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
