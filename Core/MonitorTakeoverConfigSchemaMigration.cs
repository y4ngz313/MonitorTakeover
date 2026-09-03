using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Y4NGZCore.Configuration;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// #714: rewrites <c>com.y4ngz.company.monitortakeover.cfg</c> from the numbered sections it
    /// inherited from the pre-split shared file into plainly named ones.
    ///
    /// <para>The numeric prefixes and the repeated "Quota Progression" / "Monitor Takeover" words
    /// existed only to order these sections inside the monolith's <c>com.y4ngz.company.cfg</c>.
    /// This plugin has owned its own file since #614, so both carry no information.</para>
    ///
    /// <para><b>Why this is a migration and not an edit.</b> BepInEx keys a
    /// <see cref="ConfigEntry{T}"/> by (section, key), so renaming a section produces a brand-new
    /// definition bound to its default: a customized value would be silently replaced on the first
    /// launch after the rename. This pass therefore runs once, before the first <c>Bind</c>, moves
    /// every value across, purges the orphaned numbered sections, and stamps a schema version so
    /// the second and third launches are byte-identical no-ops.</para>
    ///
    /// <para><b>Ordering against the split importer.</b>
    /// <see cref="MonitorTakeoverConfigMigration.MigrateConfigSafely"/> runs
    /// <c>SplitConfigImporter.ImportOnceSafely</c> first and this pass second. The importer's map
    /// now targets the plain sections directly, so a profile coming straight from a pre-split
    /// monolith lands in the new layout without this pass having to touch anything; this pass then
    /// finds no numbered sections and only stamps. A profile that ran an older build of this
    /// plugin has numbered sections in the destination and always carries the importer's
    /// <c>LegacyConfigImportVersion</c> stamp (the importer writes it unconditionally on the run
    /// that created those sections), so the importer returns immediately and this pass has the
    /// file to itself. The two therefore never write the same definition on the same launch, and
    /// the "an existing new-layout value wins" rule below is never the arbiter of a live value.
    /// </para>
    /// </summary>
    internal static class MonitorTakeoverConfigSchemaMigration
    {
        internal const int CurrentSchemaVersion = 1;

        // Mirrors src/Y4NGZCompany/Core/Y4NGZCompanyConfigMigration.cs so the two generated
        // config files carry the same stamp under the same heading and read alike.
        internal const string MigrationSection = "00 - Migration";
        internal const string MigrationVersionKey = "ConfigSchemaVersion";

        internal const string GeneralSection = "General";
        internal const string MediaSection = "Media";
        internal const string AudioSection = "Audio";
        internal const string PresentationSection = "Presentation";
        internal const string RewardsCeremonySection = "Rewards Ceremony";
        internal const string DiagnosticsSection = "Diagnostics";

        internal const string LegacyQuotaSectionPrefix = "70 - Quota Progression - Quota ";
        internal const string LegacyQuotaSharedSection = "70 - Quota Progression - Shared";
        internal const string LegacyTakeoverSharedSection = "71 - Monitor Takeover - Shared";
        internal const string LegacyPresentationSection = "72 - Monitor Takeover - Presentation";
        internal const string LegacyEndingSection = "73 - Monitor Takeover - Ending";
        internal const string LegacyDiagnosticsSection = "79 - Monitor Takeover - Diagnostics";

        /// <summary>Plain section for a milestone quota number, e.g. 3 -> "Quota 3".</summary>
        internal static string QuotaSection(int quota) =>
            "Quota " + quota.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The bucket each key of the old <c>71 - Monitor Takeover - Shared</c> section belongs to.
        /// That one section had grown to carry the master toggles, the media surface, the loose
        /// audio overrides, two dialogue-randomization flags and the whole rewards ceremony.
        /// </summary>
        private static readonly Dictionary<string, string> TakeoverSharedBuckets =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Enabled"] = GeneralSection,
                ["Takeover Duration"] = GeneralSection,
                ["Orbit Delay"] = GeneralSection,

                ["DefaultMediaFile"] = MediaSection,
                ["LoopMedia"] = MediaSection,
                ["EnableMediaAudio"] = MediaSection,
                ["UseMediaLengthAsDuration"] = MediaSection,

                ["MumbleAudioFiles"] = AudioSection,
                ["AlarmAudioFile"] = AudioSection,

                ["RandomizeDialogue"] = PresentationSection,
                ["RandomizeWithoutReplacement"] = PresentationSection,

                ["EnableUnlockAnnouncement"] = RewardsCeremonySection,
                ["UnlockAnnouncementSeconds"] = RewardsCeremonySection,
                ["AnnounceOnBothMonitors"] = RewardsCeremonySection,
                ["RewardsCeremonyEnabled"] = RewardsCeremonySection,
                ["CeremonyMirrorBothMonitors"] = RewardsCeremonySection,
                ["CeremonyTypewriterSecondsPerChar"] = RewardsCeremonySection,
                ["CeremonyLineConfirmSting"] = RewardsCeremonySection,
                ["CeremonyRestoreStatic"] = RewardsCeremonySection
            };

        /// <summary>
        /// The old <c>72 - Monitor Takeover - Presentation</c> section. Everything stays
        /// presentation except Mumble Volume, which is the takeover speech level and belongs with
        /// the other two audio-override keys.
        /// </summary>
        private static readonly Dictionary<string, string> PresentationBuckets =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Mumble Volume"] = AudioSection,

                ["Typewriter Speed"] = PresentationSection,
                ["Hide HUD"] = PresentationSection,
                ["Dim Lights"] = PresentationSection,
                ["Light Dim Intensity"] = PresentationSection,
                ["Override Additional Monitors"] = PresentationSection,
                ["Additional Monitor Name Tokens"] = PresentationSection,
                ["Mask Man Tail Buffer"] = PresentationSection
            };

        /// <summary>
        /// Maps one numbered (section, key) to its plain destination, or null when the section is
        /// not one of ours. Shared by this pass and by the split importer's own map so a value
        /// arriving from a pre-split file and a value already sitting in the destination land in
        /// exactly the same place.
        /// </summary>
        internal static SplitConfigTarget MapNumbered(string section, string key)
        {
            if (section == null || key == null)
                return null;

            switch (section)
            {
                case LegacyQuotaSharedSection:
                    // ConfiguredMilestones was a runtime-written bookkeeping key, never a setting.
                    return key == "ConfiguredMilestones"
                        ? null
                        : new SplitConfigTarget(GeneralSection, key);

                case LegacyTakeoverSharedSection:
                    return TakeoverSharedBuckets.TryGetValue(key, out string sharedTarget)
                        ? new SplitConfigTarget(sharedTarget, key)
                        // An unrecognized key in a section we own is far more likely to be a key
                        // this build has not learned about yet than junk, so it follows the
                        // section's centre of gravity rather than being dropped.
                        : new SplitConfigTarget(GeneralSection, key);

                case LegacyPresentationSection:
                    return PresentationBuckets.TryGetValue(key, out string presentationTarget)
                        ? new SplitConfigTarget(presentationTarget, key)
                        : new SplitConfigTarget(PresentationSection, key);

                case LegacyDiagnosticsSection:
                    return new SplitConfigTarget(DiagnosticsSection, key);
            }

            int quota = ParseLegacyQuotaNumber(section);
            return quota > 0 ? new SplitConfigTarget(QuotaSection(quota), key) : null;
        }

        /// <summary>
        /// True for every numbered section this plugin owned, including
        /// <c>73 - Monitor Takeover - Ending</c>. The ending is listed here on purpose: #713
        /// removed the feature, <see cref="MapNumbered"/> deliberately maps none of its keys, and
        /// membership in this set is what makes the purge below delete them instead of leaving
        /// eleven orphans in a file whose whole point is that it reads plainly.
        /// </summary>
        internal static bool IsLegacyOwnedSection(string section)
        {
            switch (section)
            {
                case LegacyQuotaSharedSection:
                case LegacyTakeoverSharedSection:
                case LegacyPresentationSection:
                case LegacyEndingSection:
                case LegacyDiagnosticsSection:
                    return true;
            }

            return ParseLegacyQuotaNumber(section) > 0;
        }

        /// <summary>Returns the 1-9 milestone number of a "70 - Quota Progression - Quota NN" section, else 0.</summary>
        private static int ParseLegacyQuotaNumber(string section)
        {
            if (section == null || !section.StartsWith(LegacyQuotaSectionPrefix, StringComparison.Ordinal))
                return 0;

            string suffix = section.Substring(LegacyQuotaSectionPrefix.Length);
            return suffix.Length == 2
                && char.IsDigit(suffix[0])
                && char.IsDigit(suffix[1])
                && int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quota)
                    ? quota
                    : 0;
        }

        internal static void MigrateSafely(ConfigFile config, ManualLogSource log)
        {
            try
            {
                Migrate(config, log);
            }
            catch (Exception ex)
            {
                // The message of an IO exception commonly carries the user's absolute profile
                // path; the type is enough to diagnose and keeps machine-local detail out of logs.
                log?.LogError(
                    $"Monitor Takeover config schema migration did not finish ({ex.GetType().Name}); "
                    + "startup continues on the current config.");
                try { config?.Reload(); }
                catch (Exception reloadEx)
                {
                    log?.LogWarning(
                        "Config reload after a failed Monitor Takeover schema migration also failed: "
                        + reloadEx.GetType().Name);
                }
            }
        }

        private static void Migrate(ConfigFile config, ManualLogSource log)
        {
            string path = Path.Combine(Paths.ConfigPath, MonitorTakeoverConfigMigration.DestinationFileName);
            Dictionary<ConfigDefinition, string> values = ReadConfigValues(path);
            if (GetSchemaVersion(values) >= CurrentSchemaVersion)
                return;

            var normalized = new Dictionary<ConfigDefinition, string>();
            foreach (KeyValuePair<ConfigDefinition, string> pair in values)
            {
                if (!IsLegacyOwnedSection(pair.Key.Section))
                    normalized[pair.Key] = pair.Value;
            }

            int migrated = 0;
            int removed = 0;
            foreach (KeyValuePair<ConfigDefinition, string> pair in values)
            {
                if (!IsLegacyOwnedSection(pair.Key.Section))
                    continue;

                SplitConfigTarget target = MapNumbered(pair.Key.Section, pair.Key.Key);
                if (target == null)
                {
                    // The retired ending keys and the runtime bookkeeping key land here.
                    removed++;
                    continue;
                }

                var definition = new ConfigDefinition(target.Section, target.Key);
                if (normalized.ContainsKey(definition))
                {
                    removed++;
                    continue;
                }

                normalized[definition] = pair.Value;
                migrated++;
            }

            normalized[new ConfigDefinition(MigrationSection, MigrationVersionKey)] =
                CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);

            WriteConfigValuesAtomic(path, normalized);
            config?.Reload();
            log?.LogInfo(
                $"Monitor Takeover config schema v{CurrentSchemaVersion} migration: "
                + $"moved={migrated}, dropped={removed}. Values already in the new sections were kept.");
        }

        private static int GetSchemaVersion(Dictionary<ConfigDefinition, string> values)
        {
            return values.TryGetValue(new ConfigDefinition(MigrationSection, MigrationVersionKey), out string raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
                    ? version
                    : 0;
        }

        private static void WriteConfigValuesAtomic(string path, Dictionary<ConfigDefinition, string> values)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = path
                + $".pre-schema-v{CurrentSchemaVersion}-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + ".bak";
            try
            {
                WriteConfigValues(tempPath, values);
                if (!File.Exists(path))
                {
                    File.Move(tempPath, path);
                    return;
                }

                try
                {
                    File.Replace(tempPath, path, backupPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithRollback(tempPath, path, backupPath);
                }
                catch (IOException)
                {
                    ReplaceWithRollback(tempPath, path, backupPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static void ReplaceWithRollback(string tempPath, string path, string backupPath)
        {
            File.Copy(path, backupPath, false);
            string rollbackPath = path + ".rollback-" + Guid.NewGuid().ToString("N");
            File.Move(path, rollbackPath);
            try
            {
                File.Move(tempPath, path);
                File.Delete(rollbackPath);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(rollbackPath, path);
                throw;
            }
        }

        private static void WriteConfigValues(string path, Dictionary<ConfigDefinition, string> values)
        {
            var sections = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<ConfigDefinition, string> pair in values)
            {
                string section = pair.Key.Section ?? string.Empty;
                if (!sections.TryGetValue(section, out SortedDictionary<string, string> entries))
                {
                    entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    sections[section] = entries;
                }
                entries[pair.Key.Key] = pair.Value;
            }

            using (var writer = new StreamWriter(path, false))
            {
                writer.WriteLine("## Y4NGZ Monitor Takeover configuration. Legacy section names were normalized automatically.");
                foreach (KeyValuePair<string, SortedDictionary<string, string>> section in sections)
                {
                    writer.WriteLine();
                    writer.Write('[');
                    writer.Write(section.Key);
                    writer.WriteLine(']');
                    foreach (KeyValuePair<string, string> entry in section.Value)
                    {
                        writer.Write(entry.Key);
                        writer.Write(" = ");
                        writer.WriteLine(entry.Value);
                    }
                }
            }
        }

        private static Dictionary<ConfigDefinition, string> ReadConfigValues(string path)
        {
            var values = new Dictionary<ConfigDefinition, string>();
            if (!File.Exists(path))
                return values;

            string section = null;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;

                if (line[0] == '[')
                {
                    section = line[line.Length - 1] == ']'
                        ? line.Substring(1, line.Length - 2).Trim()
                        : null;
                    continue;
                }

                if (section == null)
                    continue;

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                string key = line.Substring(0, equalsIndex).Trim();
                if (key.Length == 0)
                    continue;

                values[new ConfigDefinition(section, key)] = line.Substring(equalsIndex + 1).Trim();
            }

            return values;
        }
    }
}
