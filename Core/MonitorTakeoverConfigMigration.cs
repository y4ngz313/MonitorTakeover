using BepInEx.Configuration;
using BepInEx.Logging;
using Y4NGZCore.Configuration;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// Imports quota progression and monitor takeover settings that predate the DLL split, then
    /// brings the resulting file up to the current config schema.
    /// </summary>
    internal static class MonitorTakeoverConfigMigration
    {
        internal const string DestinationFileName = "com.y4ngz.company.monitortakeover.cfg";

        internal static void MigrateConfigSafely(ConfigFile config, ManualLogSource log)
        {
            SplitConfigImporter.ImportOnceSafely(
                config,
                log,
                "Y4NGZMonitorTakeover",
                DestinationFileName,
                new SplitConfigImportSource(
                    "com.y4ngz.company.cfg",
                    MapLegacy,
                    IsPotentiallyOwnedLegacySection),
                new SplitConfigImportSource(
                    "com.lguhud.contracthud.cfg",
                    MapLegacy,
                    IsPotentiallyOwnedLegacySection),
                new SplitConfigImportSource(
                    "com.y4ngz.lgumonitortakeover.cfg",
                    MapLegacy,
                    IsPotentiallyOwnedLegacySection));

            // #714: second, and only ever second. The importer above appends values into the
            // PLAIN sections this build binds, so a pre-split profile is already in the new
            // layout by the time this runs and receives the schema stamp plus the #861 key
            // renames (the importer's map still speaks the v1 key names). A profile that
            // ran an older split build still has the numbered sections in the destination; the
            // importer refuses to touch it (its own stamp is present) and this pass moves the
            // values across, drops the numbered sections, and backs the old file up.
            MonitorTakeoverConfigSchemaMigration.MigrateSafely(config, log);
        }

        /// <summary>
        /// The numbered sections as they appear in a PRE-SPLIT source file, mapped straight to
        /// the plain sections this build binds. The numbered names survive here as source
        /// spellings only: nothing this plugin writes uses them any more.
        /// </summary>
        private static SplitConfigTarget MapCanonical(string section, string key)
        {
            return MonitorTakeoverConfigSchemaMigration.MapNumbered(section, key);
        }

        private static SplitConfigTarget MapLegacy(string section, string key)
        {
            SplitConfigTarget canonical = MapCanonical(section, key);
            if (canonical != null)
                return canonical;

            if (section == "Settings")
            {
                // The pre-fold monolith's flat [Settings] block. The three master toggles are
                // General; Mumble Volume is the takeover speech level and joins the audio
                // overrides; everything else it carried is presentation.
                string targetSection;
                switch (key)
                {
                    case "Enabled":
                    case "Takeover Duration":
                    case "Orbit Delay":
                        targetSection = MonitorTakeoverConfigSchemaMigration.GeneralSection;
                        break;
                    case "Mumble Volume":
                        targetSection = MonitorTakeoverConfigSchemaMigration.AudioSection;
                        break;
                    default:
                        targetSection = MonitorTakeoverConfigSchemaMigration.PresentationSection;
                        break;
                }

                string targetKey = key == "Override Fifth Monitor"
                    ? "Override Additional Monitors"
                    : key;
                return new SplitConfigTarget(targetSection, targetKey);
            }

            if (section == "Diagnostics"
                && (key == "Verbose Logging" || key == "Dump Monitor Hierarchy On Orbit"))
            {
                return new SplitConfigTarget(MonitorTakeoverConfigSchemaMigration.DiagnosticsSection, key);
            }

            return null;
        }

        // #713: the monolith's [Ending] section is deliberately absent here, and
        // MapLegacy maps neither it nor the split's own [73 - Monitor Takeover - Ending]. The
        // quota ending was removed, so those keys map to nothing; an old file that still carries
        // them imports cleanly and the ending keys are counted as ignored rather than migrated.
        // (The ending section IS in IsLegacyOwnedSection, because the schema pass has to purge
        // it from a destination an older split build wrote; that list answers a different
        // question than this one.)
        private static bool IsPotentiallyOwnedLegacySection(string section)
        {
            return MonitorTakeoverConfigSchemaMigration.IsLegacyOwnedSection(section)
                || section == "Settings"
                || section == "Diagnostics";
        }
    }
}
