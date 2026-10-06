// TakeoverAudioPolicy.cs - LGUMonitorTakeover
//
// #861/#715: the one place that decides whether a loose takeover audio file (AlarmFile,
// VoiceFiles, a local Soundtrack) may be used, and why not when it may not. The host and every
// client run the same rules: the host when it publishes its selection, a client when it accepts
// the published names and hashes. Deliberately free of Unity, BepInEx and Netcode so the exact
// rules the game applies can be driven headlessly.
//
// Also home to the takeover audio timing budget, for the same reason: the numbers the runtime
// waits on are the numbers the tests read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Y4NGZCompany.Core
{
    /// <summary>Why a configured or published takeover audio file was, or was not, accepted.</summary>
    internal enum TakeoverAudioFileStatus
    {
        Accepted,
        /// <summary>Nothing configured. Not a failure.</summary>
        Blank,
        /// <summary>The name is not a valid path on this machine.</summary>
        InvalidPath,
        /// <summary>The name resolves outside the MonitorTakeovers folder.</summary>
        OutsideFolder,
        /// <summary>No such file in the MonitorTakeovers folder.</summary>
        Missing,
        /// <summary>Not an .mp3, .wav or .ogg file name.</summary>
        Unsupported,
        /// <summary>Larger than <see cref="TakeoverAudioPolicy.MaxAudioBytes"/>.</summary>
        TooLarge,
        /// <summary>Longer than the payload field can carry. Rejected, never truncated.</summary>
        NameTooLong,
        /// <summary>The file exists but could not be read or hashed.</summary>
        Unreadable,
        /// <summary>The local file is not byte-identical to the host's, or the host sent no hash.</summary>
        HashMismatch,
        /// <summary>A voice entry beyond the clip count or payload field capacity.</summary>
        OverLimit,
    }

    /// <summary>The verdict on one audio file, with everything a diagnostic line needs.</summary>
    internal readonly struct TakeoverAudioFileCheck
    {
        /// <summary>The config key the name came from: AlarmFile, VoiceFiles or Soundtrack.</summary>
        internal readonly string Setting;
        /// <summary>The trimmed name as configured (host) or as published (client).</summary>
        internal readonly string Name;
        internal readonly TakeoverAudioFileStatus Status;
        /// <summary>Absolute path inside the folder. Set only when accepted.</summary>
        internal readonly string Path;
        /// <summary>Lowercase SHA-256 of the file. Set only when accepted.</summary>
        internal readonly string Hash;
        /// <summary>Size, exception text or limit that explains <see cref="Status"/>.</summary>
        internal readonly string Detail;

        internal TakeoverAudioFileCheck(
            string setting, string name, TakeoverAudioFileStatus status, string path, string hash, string detail)
        {
            Setting = setting ?? string.Empty;
            Name = name ?? string.Empty;
            Status = status;
            Path = path ?? string.Empty;
            Hash = hash ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        internal bool Accepted => Status == TakeoverAudioFileStatus.Accepted;

        /// <summary>
        /// One complete sentence: setting, file, cause and what plays instead. The fallback is
        /// the caller's, because only the caller knows whether other files still play.
        /// </summary>
        internal string Describe(string fallback)
        {
            string reason;
            switch (Status)
            {
                case TakeoverAudioFileStatus.Accepted:
                    reason = "is ready";
                    break;
                case TakeoverAudioFileStatus.Blank:
                    reason = "is blank";
                    break;
                case TakeoverAudioFileStatus.InvalidPath:
                    reason = $"is not a valid file name ({Detail})";
                    break;
                case TakeoverAudioFileStatus.OutsideFolder:
                    reason = "points outside BepInEx/config/Y4NGZCompany/MonitorTakeovers";
                    break;
                case TakeoverAudioFileStatus.Missing:
                    reason = "was not found in BepInEx/config/Y4NGZCompany/MonitorTakeovers of the active profile"
                        + (Detail.Length > 0 ? $" ({Detail})" : string.Empty);
                    break;
                case TakeoverAudioFileStatus.Unsupported:
                    reason = Detail.Length > 0 ? Detail : "is not an MP3, WAV or OGG file";
                    break;
                case TakeoverAudioFileStatus.TooLarge:
                    reason = $"is larger than the 32 MiB limit ({Detail})";
                    break;
                case TakeoverAudioFileStatus.NameTooLong:
                    reason = $"has a name too long to send to other players ({Detail}); rename the file";
                    break;
                case TakeoverAudioFileStatus.Unreadable:
                    reason = $"could not be read ({Detail})";
                    break;
                case TakeoverAudioFileStatus.HashMismatch:
                    reason = Detail.Length > 0 ? Detail : "is not identical to the host's copy";
                    break;
                case TakeoverAudioFileStatus.OverLimit:
                    reason = Detail.Length > 0 ? Detail : "is over the voice file limit";
                    break;
                default:
                    reason = "was rejected";
                    break;
            }

            return string.IsNullOrEmpty(fallback)
                ? $"{Setting} '{Name}' {reason}."
                : $"{Setting} '{Name}' {reason}; {fallback}.";
        }
    }

    /// <summary>
    /// The host's VoiceFiles decision: the files it publishes, index-aligned with their hashes,
    /// and every entry it did not publish with the reason.
    /// </summary>
    internal sealed class TakeoverVoiceSelection
    {
        internal readonly List<TakeoverAudioFileCheck> Accepted = new List<TakeoverAudioFileCheck>();
        internal readonly List<TakeoverAudioFileCheck> Rejected = new List<TakeoverAudioFileCheck>();
        /// <summary>Comma-joined accepted names: the payload's MumbleAudioFiles field.</summary>
        internal string Names = string.Empty;
        /// <summary>Comma-joined hashes, index-aligned with <see cref="Names"/>.</summary>
        internal string Hashes = string.Empty;
    }

    internal static class TakeoverAudioPolicy
    {
        /// <summary>Largest accepted audio file, on host and client alike.</summary>
        internal const long MaxAudioBytes = 32L * 1024L * 1024L;

        /// <summary>Most voice files one takeover publishes.</summary>
        internal const int MaxVoiceClips = 12;

        /// <summary>
        /// Per-name cap for every loose-file name field (alarm, soundtrack, each voice, media).
        /// A longer name is rejected; truncating it could select a different real file.
        /// </summary>
        internal const int MaxFileNameChars = 120;

        /// <summary>Joined VoiceFiles names cap, separators included.</summary>
        internal const int MaxVoiceNamesChars = 1000;

        /// <summary>Joined voice hashes cap, separators included.</summary>
        internal const int MaxVoiceHashesChars = 1000;

        /// <summary>UTF-8 capacity of the FixedString512Bytes name fields.</summary>
        internal const int FileNameFieldUtf8Bytes = 509;

        /// <summary>UTF-8 capacity of the FixedString4096Bytes voice list fields.</summary>
        internal const int VoiceListFieldUtf8Bytes = 4093;

        /// <summary>
        /// The common audio preparation window. No quota takeover starts its sequence sooner than
        /// this after it began, whatever Orbit Delay (or the debug skip-delay path) says; every
        /// peer waits the same time, ready or not, and the audio is decided once at that shared
        /// deadline. What has not loaded by then falls back for that takeover and keeps loading
        /// for the next. The default Orbit Delay of 4 s already covers it.
        /// </summary>
        internal const float PreparationWindowSeconds = 2f;

        /// <summary>
        /// How long a soundtrack played through a VideoPlayer (a cached link, or the takeover
        /// video's own track) has to prepare with a usable audio track before it counts as
        /// failed. Matches the configured-video fallback.
        /// </summary>
        internal const float SoundtrackStartupSeconds = 4f;

        /// <summary>UnityWebRequest timeout for one decode, in whole seconds.</summary>
        internal const int LoadTimeoutSeconds = 60;

        internal static readonly string[] AudioExtensions = { ".mp3", ".wav", ".ogg" };

        /// <summary>Comma list, trimmed, empties dropped. A comma can never be part of a name.</summary>
        internal static string[] SplitList(string value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
            string[] parts = value.Split(',');
            var result = new List<string>(parts.Length);
            for (int index = 0; index < parts.Length; index++)
            {
                string item = parts[index].Trim();
                if (item.Length > 0) result.Add(item);
            }
            return result.ToArray();
        }

        /// <summary>True when the value fits both the character cap and the field's UTF-8 bytes.</summary>
        internal static bool FitsWireField(string value, int maxChars, int maxUtf8Bytes)
        {
            value = value ?? string.Empty;
            return value.Length <= maxChars && Encoding.UTF8.GetByteCount(value) <= maxUtf8Bytes;
        }

        /// <summary>
        /// The takeover folder sandbox. Resolves <paramref name="name"/> inside
        /// <paramref name="root"/>, checks the extension and existence, and reads the length.
        /// Never throws: a path or IO failure is a status, not an exception.
        /// </summary>
        internal static TakeoverAudioFileStatus TryLocate(
            string root, string name, string[] extensions, out string path, out long length, out string detail)
        {
            path = null;
            length = 0L;
            detail = string.Empty;
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return TakeoverAudioFileStatus.Blank;

            string candidate;
            string extension;
            try
            {
                string folder = System.IO.Path.GetFullPath(root ?? string.Empty)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, trimmed));
                if (!candidate.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    return TakeoverAudioFileStatus.OutsideFolder;
                extension = System.IO.Path.GetExtension(candidate) ?? string.Empty;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return TakeoverAudioFileStatus.InvalidPath;
            }

            if (!HasExtension(extensions, extension))
            {
                detail = extension.Length == 0
                    ? "has no file extension; write the full file name, for example voice.mp3"
                    : $"is a '{extension}' file; only {string.Join(", ", extensions ?? Array.Empty<string>())} are supported";
                return TakeoverAudioFileStatus.Unsupported;
            }

            try
            {
                if (!File.Exists(candidate))
                {
                    if (Directory.Exists(candidate)) detail = "that name is a folder";
                    return TakeoverAudioFileStatus.Missing;
                }
                length = new FileInfo(candidate).Length;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return TakeoverAudioFileStatus.Unreadable;
            }

            path = candidate;
            return TakeoverAudioFileStatus.Accepted;
        }

        /// <summary>
        /// Host side: may this configured name be published? Checks the name against the wire
        /// field, the sandbox, the type, the size, and hashes the file.
        /// </summary>
        internal static TakeoverAudioFileCheck CheckHostFile(string root, string setting, string configured)
        {
            string name = (configured ?? string.Empty).Trim();
            if (name.Length == 0)
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.Blank, null, null, null);

            if (!FitsWireField(name, MaxFileNameChars, FileNameFieldUtf8Bytes))
            {
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.NameTooLong, null, null,
                    $"{name.Length} characters, {Encoding.UTF8.GetByteCount(name)} UTF-8 bytes; the limit is {MaxFileNameChars} characters");
            }

            return LocateAndHash(root, setting, name, null);
        }

        /// <summary>
        /// Client side: is the local file with this published name byte-identical to the host's?
        /// A published name without a hash is never trusted.
        /// </summary>
        internal static TakeoverAudioFileCheck VerifyPublishedFile(string root, string setting, string name, string expectedHash)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.Blank, null, null, null);

            string expected = (expectedHash ?? string.Empty).Trim();
            if (expected.Length == 0)
            {
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.HashMismatch, null, null,
                    "arrived without the host's checksum, so it cannot be verified");
            }

            return LocateAndHash(root, setting, name, expected);
        }

        /// <summary>
        /// Host side VoiceFiles: up to <see cref="MaxVoiceClips"/> accepted files in list order,
        /// within the joined name/hash field caps. An entry past a limit is reported as
        /// <see cref="TakeoverAudioFileStatus.OverLimit"/> and is not opened.
        /// </summary>
        internal static TakeoverVoiceSelection SelectHostVoices(string root, string configuredList)
        {
            var selection = new TakeoverVoiceSelection();
            string[] entries = SplitList(configuredList);
            var names = new List<string>(entries.Length);
            var hashes = new List<string>(entries.Length);
            int nameChars = 0;
            int nameBytes = 0;
            int hashChars = 0;
            string closedReason = null;

            for (int index = 0; index < entries.Length; index++)
            {
                string entry = entries[index];
                if (closedReason == null && selection.Accepted.Count >= MaxVoiceClips)
                    closedReason = $"is over the {MaxVoiceClips}-file VoiceFiles limit";
                if (closedReason != null)
                {
                    selection.Rejected.Add(new TakeoverAudioFileCheck(
                        "VoiceFiles", entry, TakeoverAudioFileStatus.OverLimit, null, null, closedReason));
                    continue;
                }

                TakeoverAudioFileCheck check = CheckHostFile(root, "VoiceFiles", entry);
                if (!check.Accepted)
                {
                    selection.Rejected.Add(check);
                    continue;
                }

                int separator = selection.Accepted.Count > 0 ? 1 : 0;
                int nextNameChars = nameChars + check.Name.Length + separator;
                int nextNameBytes = nameBytes + Encoding.UTF8.GetByteCount(check.Name) + separator;
                int nextHashChars = hashChars + check.Hash.Length + separator;
                if (nextNameChars > MaxVoiceNamesChars
                    || nextNameBytes > VoiceListFieldUtf8Bytes
                    || nextHashChars > MaxVoiceHashesChars)
                {
                    closedReason = $"does not fit: the VoiceFiles names sent to players are limited to {MaxVoiceNamesChars} characters in total";
                    selection.Rejected.Add(new TakeoverAudioFileCheck(
                        "VoiceFiles", check.Name, TakeoverAudioFileStatus.OverLimit, null, null, closedReason));
                    continue;
                }

                selection.Accepted.Add(check);
                names.Add(check.Name);
                hashes.Add(check.Hash);
                nameChars = nextNameChars;
                nameBytes = nextNameBytes;
                hashChars = nextHashChars;
            }

            selection.Names = string.Join(",", names);
            selection.Hashes = string.Join(",", hashes);
            return selection;
        }

        /// <summary>
        /// Client side VoiceFiles: verifies each published name against its index-aligned hash.
        /// Never more than <see cref="MaxVoiceClips"/> entries are opened.
        /// </summary>
        internal static List<TakeoverAudioFileCheck> VerifyPublishedVoices(string root, string names, string hashes)
        {
            string[] published = SplitList(names);
            string[] publishedHashes = SplitList(hashes);
            var result = new List<TakeoverAudioFileCheck>(published.Length);
            for (int index = 0; index < published.Length; index++)
            {
                if (index >= MaxVoiceClips)
                {
                    result.Add(new TakeoverAudioFileCheck("VoiceFiles", published[index], TakeoverAudioFileStatus.OverLimit,
                        null, null, $"is over the {MaxVoiceClips}-file VoiceFiles limit"));
                    continue;
                }

                string hash = index < publishedHashes.Length ? publishedHashes[index] : string.Empty;
                result.Add(VerifyPublishedFile(root, "VoiceFiles", published[index], hash));
            }
            return result;
        }

        /// <summary>Lowercase hex SHA-256, or an empty string with the IO error.</summary>
        internal static string ComputeSha256(string path, out string error)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    error = string.Empty;
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return string.Empty;
            }
        }

        /// <summary>
        /// How long a quota takeover waits between starting and its first sequence step: Orbit
        /// Delay, raised to <see cref="PreparationWindowSeconds"/>. Unconditional on purpose. A
        /// floor that depended on this peer's cached selection or readiness would let peers whose
        /// payload is stale or still arriving start at different times.
        /// </summary>
        internal static float PreSequenceDelaySeconds(float orbitDelay)
        {
            float delay = orbitDelay > 0f ? orbitDelay : 0f;
            return delay < PreparationWindowSeconds ? PreparationWindowSeconds : delay;
        }

        private static TakeoverAudioFileCheck LocateAndHash(string root, string setting, string name, string expectedHash)
        {
            TakeoverAudioFileStatus status = TryLocate(root, name, AudioExtensions, out string path, out long length, out string detail);
            if (status != TakeoverAudioFileStatus.Accepted)
                return new TakeoverAudioFileCheck(setting, name, status, null, null, detail);

            if (length > MaxAudioBytes)
            {
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.TooLarge, null, null,
                    $"{length / (1024d * 1024d):F1} MiB");
            }

            string hash = ComputeSha256(path, out string error);
            if (hash.Length == 0)
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.Unreadable, null, null, error);

            if (expectedHash != null && !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.HashMismatch, null, null,
                    "is not identical to the host's copy; every player needs the same file");
            }

            return new TakeoverAudioFileCheck(setting, name, TakeoverAudioFileStatus.Accepted, path, hash, null);
        }

        private static bool HasExtension(string[] extensions, string extension)
        {
            if (extensions == null || string.IsNullOrEmpty(extension)) return false;
            for (int index = 0; index < extensions.Length; index++)
                if (string.Equals(extensions[index], extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
