using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core
{
    internal sealed class YoutubeVideoStreams
    {
        internal string VideoUrl;
    }

    /// <summary>
    /// Downloads an authored YouTube page locally into a cached, Unity-compatible
    /// MP4 so every peer can obtain the same video without distributing it in the
    /// modpack. Ported from Y4NGZFlyingTV's resolver (#661) with its own cache
    /// directory; the takeover prefetches configured links in the background and
    /// plays only from this cache at takeover time.
    /// </summary>
    internal static class YoutubeVideoResolver
    {
        private const string CacheFormatVersion = "screen-fill-v1";
        private const int MaximumSourceLength = 4096;
        private const int MaximumLoggedErrorLength = 400;
        private const int MinimumBinaryLength = 100 * 1024;
        private const int MinimumVideoLength = 64 * 1024;
        private const long MaximumVideoBytes = 256L * 1024L * 1024L;
        private const long FfmpegArchiveLength = 54681842L;
        private const long FfmpegExecutableLength = 134163456L;
        private const string UnityCompatibleFormat =
            "bestvideo[ext=mp4][height<=480][vcodec^=avc1]+bestaudio[ext=m4a]/" +
            "best[ext=mp4][height<=480][vcodec^=avc1][acodec^=mp4a]";
        private const string FfmpegArchiveUrl =
            "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v6.1/ffmpeg-6.1-win-64.zip";
        private const string FfmpegArchiveSha256 =
            "B0FB4BCEF9D4B5F7A77D2E4854F80D4CE3E43809BC29FD1F97CAA1B467F96993";
        private const string FfmpegExecutableSha256 =
            "BA242553F0FF60AD788069D5D376C1B4F7A2F3A3566416E0ED950CA7920DA5FA";

        private static readonly Regex CropRectanglePattern = new Regex(
            @"crop=(?<width>\d+):(?<height>\d+):(?<x>\d+):(?<y>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex FrameSizePattern = new Regex(
            @"\bs:(?<width>\d{2,5})x(?<height>\d{2,5})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly TimeSpan BinaryRefreshInterval = TimeSpan.FromHours(24d);
        private static readonly TimeSpan FailedRefreshRetryInterval = TimeSpan.FromHours(1d);
        private static readonly TimeSpan ResolvedUrlCacheLifetime = TimeSpan.FromMinutes(10d);
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3d);
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim BinaryGate = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, CachedResolution> CachedUrls =
            new Dictionary<string, CachedResolution>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Task<YoutubeVideoStreams>> InFlightResolutions =
            new Dictionary<string, Task<YoutubeVideoStreams>>(StringComparer.Ordinal);
        private static readonly HttpClient DownloadClient = CreateDownloadClient();

        private static DateTime _nextBinaryRefreshUtc;
        private static string _validatedFfmpegPath;

        private sealed class CachedResolution
        {
            internal YoutubeVideoStreams Streams;
            internal DateTime ExpiresUtc;
        }

        private sealed class ProcessResult
        {
            internal int ExitCode;
            internal string StandardOutput;
            internal string StandardError;
            internal bool TimedOut;
        }

        private sealed class CropRegion
        {
            internal int SourceWidth;
            internal int SourceHeight;
            internal int Width;
            internal int Height;
            internal int X;
            internal int Y;
        }

        internal static bool IsYoutubeUrl(string value)
        {
            return TryNormalizeYoutubeUrl(value, out _);
        }

        /// <summary>
        /// Synchronous cache probe: true only when the configured link has already
        /// been resolved into a valid cached MP4 on this machine.
        /// </summary>
        internal static bool TryGetCachedVideo(string value, out string path)
        {
            path = null;
            if (!TryNormalizeYoutubeUrl(value, out string normalizedUrl))
                return false;
            string cacheKey = ComputeTextSha256(CacheFormatVersion + "\n" + normalizedUrl);
            string candidate = Path.Combine(Path.Combine(GetToolCacheRoot(), "videos"), cacheKey + ".mp4");
            if (!IsValidCachedVideo(candidate))
                return false;
            path = candidate;
            return true;
        }

        /// <summary>
        /// Fire-and-forget background resolution so the video is cached before the
        /// takeover needs it. Safe to call repeatedly: in-flight and cached links
        /// are deduplicated inside <see cref="ResolveAsync"/>.
        /// </summary>
        internal static void Prefetch(string value)
        {
            if (!IsYoutubeUrl(value) || TryGetCachedVideo(value, out _))
                return;
            _ = ResolveAsync(value);
        }

        internal static Task<YoutubeVideoStreams> ResolveAsync(string value)
        {
            if (!TryNormalizeYoutubeUrl(value, out string normalizedUrl))
                return Task.FromResult<YoutubeVideoStreams>(null);

            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                if (CachedUrls.TryGetValue(normalizedUrl, out CachedResolution cached)
                    && cached.ExpiresUtc > now)
                {
                    return Task.FromResult(cached.Streams);
                }

                CachedUrls.Remove(normalizedUrl);

                if (InFlightResolutions.TryGetValue(normalizedUrl, out Task<YoutubeVideoStreams> existing))
                    return existing;

                var completion = new TaskCompletionSource<YoutubeVideoStreams>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                InFlightResolutions[normalizedUrl] = completion.Task;
                _ = ResolveAndPublishAsync(normalizedUrl, completion);
                return completion.Task;
            }
        }

        internal static bool TryNormalizeYoutubeUrl(string value, out string normalizedUrl)
        {
            normalizedUrl = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim();
            if (trimmed.Length > MaximumSourceLength
                || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !uri.IsDefaultPort
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            bool shortHost = host == "youtu.be";
            bool youtubeHost = host == "youtube.com"
                || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                || host == "youtube-nocookie.com"
                || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);

            if ((!shortHost && !youtubeHost) || !HasRecognizedVideoPath(uri, shortHost))
                return false;

            var normalized = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1,
                Fragment = string.Empty,
            };
            normalizedUrl = normalized.Uri.AbsoluteUri;
            return normalizedUrl.Length <= MaximumSourceLength;
        }

        private static async Task ResolveAndPublishAsync(
            string normalizedUrl,
            TaskCompletionSource<YoutubeVideoStreams> completion)
        {
            YoutubeVideoStreams resolvedStreams = null;
            try
            {
                resolvedStreams = await ResolveCoreAsync(normalizedUrl).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                TakeoverBootstrap.Log?.LogWarning($"[MonitorTakeover] YouTube resolution failed: {GetSafeError(e.Message)}");
            }

            lock (Gate)
            {
                InFlightResolutions.Remove(normalizedUrl);
                if (resolvedStreams != null)
                {
                    CachedUrls[normalizedUrl] = new CachedResolution
                    {
                        Streams = resolvedStreams,
                        ExpiresUtc = DateTime.UtcNow + ResolvedUrlCacheLifetime,
                    };
                }
            }

            completion.TrySetResult(resolvedStreams);
        }

        private static async Task<YoutubeVideoStreams> ResolveCoreAsync(string normalizedUrl)
        {
            string videoDirectory = Path.Combine(GetToolCacheRoot(), "videos");
            Directory.CreateDirectory(videoDirectory);
            string cacheKey = ComputeTextSha256(CacheFormatVersion + "\n" + normalizedUrl);
            string cachedVideoPath = Path.Combine(videoDirectory, cacheKey + ".mp4");
            if (IsValidCachedVideo(cachedVideoPath))
            {
                TakeoverBootstrap.Log?.LogInfo($"[MonitorTakeover] Using cached YouTube video {GetSafeSourceLabel(normalizedUrl)}.");
                return new YoutubeVideoStreams { VideoUrl = cachedVideoPath };
            }

            TryDeleteFile(cachedVideoPath);
            DeleteWorkingVideoFiles(videoDirectory, cacheKey);

            string binaryPath = await EnsureYtDlpAsync().ConfigureAwait(false);
            string ffmpegPath = await EnsureFfmpegAsync().ConfigureAwait(false);
            string workingVideoPath = Path.Combine(videoDirectory, cacheKey + ".working.mp4");
            string normalizedVideoPath = Path.Combine(videoDirectory, cacheKey + ".working.fill.mp4");
            TakeoverBootstrap.Log?.LogInfo(
                $"[MonitorTakeover] Downloading and caching YouTube video {GetSafeSourceLabel(normalizedUrl)}.");

            try
            {
                ProcessResult result = await RunYtDlpAsync(
                    binaryPath,
                    ffmpegPath,
                    workingVideoPath,
                    normalizedUrl).ConfigureAwait(false);
                if (result.TimedOut)
                {
                    TakeoverBootstrap.Log?.LogWarning("[MonitorTakeover] yt-dlp timed out while caching the configured video.");
                    return null;
                }

                if (result.ExitCode != 0)
                {
                    TakeoverBootstrap.Log?.LogWarning(
                        $"[MonitorTakeover] yt-dlp exited with code {result.ExitCode}: {GetSafeError(result.StandardError)}");
                    return null;
                }

                if (!IsValidCachedVideo(workingVideoPath))
                {
                    TakeoverBootstrap.Log?.LogWarning(
                        "[MonitorTakeover] yt-dlp completed, but the merged MP4 was missing or invalid.");
                    return null;
                }

                string completedVideoPath = workingVideoPath;
                CropRegion crop = await DetectEncodedBordersAsync(ffmpegPath, workingVideoPath).ConfigureAwait(false);
                if (crop != null)
                {
                    TakeoverBootstrap.Log?.LogInfo(
                        $"[MonitorTakeover] Removing encoded video borders: crop {crop.Width}x{crop.Height} " +
                        $"at {crop.X},{crop.Y}, then fill {crop.SourceWidth}x{crop.SourceHeight}.");
                    ProcessResult normalizeResult = await RunProcessAsync(
                        ffmpegPath,
                        BuildFillFrameArguments(workingVideoPath, normalizedVideoPath, crop)).ConfigureAwait(false);
                    if (normalizeResult.TimedOut)
                    {
                        TakeoverBootstrap.Log?.LogWarning(
                            "[MonitorTakeover] FFmpeg timed out while removing encoded video borders; using the original frame.");
                    }
                    else if (normalizeResult.ExitCode != 0 || !IsValidCachedVideo(normalizedVideoPath))
                    {
                        TakeoverBootstrap.Log?.LogWarning(
                            $"[MonitorTakeover] FFmpeg could not remove encoded video borders; using the original frame: " +
                            GetSafeError(normalizeResult.StandardError));
                    }
                    else
                    {
                        completedVideoPath = normalizedVideoPath;
                    }
                }

                File.Move(completedVideoPath, cachedVideoPath);
                TakeoverBootstrap.Log?.LogInfo(
                    $"[MonitorTakeover] YouTube video cached as a screen-filling local H.264/AAC MP4 " +
                    $"({new FileInfo(cachedVideoPath).Length / 1024L / 1024L} MiB).");
                return new YoutubeVideoStreams { VideoUrl = cachedVideoPath };
            }
            finally
            {
                DeleteWorkingVideoFiles(videoDirectory, cacheKey);
            }
        }

        private static string GetToolCacheRoot()
        {
            string cacheRoot = string.IsNullOrWhiteSpace(Paths.CachePath)
                ? Path.GetDirectoryName(typeof(YoutubeVideoResolver).Assembly.Location)
                : Paths.CachePath;
            string toolRoot = Path.Combine(cacheRoot ?? string.Empty, "Y4NGZMonitorTakeover");
            Directory.CreateDirectory(toolRoot);
            return toolRoot;
        }

        private static async Task<string> EnsureYtDlpAsync()
        {
            await BinaryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string binaryDirectory = GetToolCacheRoot();

                string binaryName = GetYtDlpBinaryName();
                string binaryPath = Path.Combine(binaryDirectory, binaryName);
                DateTime now = DateTime.UtcNow;
                bool exists = File.Exists(binaryPath)
                    && new FileInfo(binaryPath).Length >= MinimumBinaryLength;
                bool fresh = exists
                    && now - File.GetLastWriteTimeUtc(binaryPath) <= BinaryRefreshInterval;

                if (fresh || (exists && now < _nextBinaryRefreshUtc))
                {
                    EnsureExecutablePermission(binaryPath);
                    return binaryPath;
                }

                TakeoverBootstrap.Log?.LogInfo(exists
                    ? "[MonitorTakeover] Refreshing the cached yt-dlp executable."
                    : "[MonitorTakeover] Downloading yt-dlp for first YouTube playback.");

                try
                {
                    string downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + binaryName;
                    byte[] payload = await DownloadClient.GetByteArrayAsync(downloadUrl).ConfigureAwait(false);
                    if (payload == null || payload.Length < MinimumBinaryLength)
                        throw new InvalidDataException("The yt-dlp download was unexpectedly small.");

                    string temporaryPath = binaryPath + ".download";
                    try
                    {
                        File.WriteAllBytes(temporaryPath, payload);
                        File.Copy(temporaryPath, binaryPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }

                    EnsureExecutablePermission(binaryPath);
                    _nextBinaryRefreshUtc = now + BinaryRefreshInterval;
                    return binaryPath;
                }
                catch (Exception e)
                {
                    if (!exists)
                        throw new IOException("yt-dlp could not be downloaded and no cached copy is available.", e);

                    _nextBinaryRefreshUtc = now + FailedRefreshRetryInterval;
                    TakeoverBootstrap.Log?.LogWarning(
                        $"[MonitorTakeover] Could not refresh yt-dlp; using the cached executable: {GetSafeError(e.Message)}");
                    EnsureExecutablePermission(binaryPath);
                    return binaryPath;
                }
            }
            finally
            {
                BinaryGate.Release();
            }
        }

        private static async Task<string> EnsureFfmpegAsync()
        {
            string systemPath = FindExecutableOnPath(IsWindowsPlatform() ? "ffmpeg.exe" : "ffmpeg");
            if (!string.IsNullOrEmpty(systemPath))
                return systemPath;

            await BinaryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrEmpty(_validatedFfmpegPath) && File.Exists(_validatedFfmpegPath))
                    return _validatedFfmpegPath;

                if (!IsWindowsPlatform())
                {
                    throw new PlatformNotSupportedException(
                        "FFmpeg was not found on PATH. Install FFmpeg so yt-dlp can merge YouTube video and audio streams.");
                }

                string toolRoot = GetToolCacheRoot();
                string ffmpegDirectory = Path.Combine(toolRoot, "ffmpeg-6.1");
                Directory.CreateDirectory(ffmpegDirectory);
                string ffmpegPath = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
                if (IsVerifiedFile(ffmpegPath, FfmpegExecutableLength, FfmpegExecutableSha256))
                {
                    _validatedFfmpegPath = ffmpegPath;
                    return ffmpegPath;
                }

                TryDeleteFile(ffmpegPath);
                string archivePath = Path.Combine(toolRoot, "ffmpeg-6.1-win-64.zip.download");
                string executableDownloadPath = ffmpegPath + ".download";
                TryDeleteFile(archivePath);
                TryDeleteFile(executableDownloadPath);
                TakeoverBootstrap.Log?.LogInfo(
                    "[MonitorTakeover] FFmpeg was not found on PATH; downloading the verified 52 MiB playback helper once.");

                try
                {
                    await DownloadFileAsync(FfmpegArchiveUrl, archivePath).ConfigureAwait(false);
                    if (!IsVerifiedFile(archivePath, FfmpegArchiveLength, FfmpegArchiveSha256))
                        throw new InvalidDataException("The downloaded FFmpeg archive failed integrity verification.");

                    using (var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false))
                    {
                        ZipArchiveEntry ffmpegEntry = null;
                        for (int i = 0; i < archive.Entries.Count; i++)
                        {
                            ZipArchiveEntry candidate = archive.Entries[i];
                            if (string.Equals(candidate.FullName, "ffmpeg.exe", StringComparison.Ordinal)
                                && candidate.Length == FfmpegExecutableLength)
                            {
                                ffmpegEntry = candidate;
                                break;
                            }
                        }

                        if (ffmpegEntry == null)
                            throw new InvalidDataException("The verified FFmpeg archive did not contain the expected executable.");

                        using (Stream input = ffmpegEntry.Open())
                        using (var output = new FileStream(
                            executableDownloadPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            81920,
                            useAsync: true))
                        {
                            await input.CopyToAsync(output).ConfigureAwait(false);
                        }
                    }

                    if (!IsVerifiedFile(executableDownloadPath, FfmpegExecutableLength, FfmpegExecutableSha256))
                        throw new InvalidDataException("The extracted FFmpeg executable failed integrity verification.");

                    File.Move(executableDownloadPath, ffmpegPath);
                    _validatedFfmpegPath = ffmpegPath;
                    return ffmpegPath;
                }
                finally
                {
                    TryDeleteFile(archivePath);
                    TryDeleteFile(executableDownloadPath);
                }
            }
            finally
            {
                BinaryGate.Release();
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath)
        {
            using (HttpResponseMessage response = await DownloadClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }
        }

        private static async Task<ProcessResult> RunYtDlpAsync(
            string binaryPath,
            string ffmpegPath,
            string outputPath,
            string normalizedUrl)
        {
            return await RunProcessAsync(
                binaryPath,
                BuildYtDlpArguments(ffmpegPath, outputPath, normalizedUrl)).ConfigureAwait(false);
        }

        private static async Task<ProcessResult> RunProcessAsync(string binaryPath, string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The media helper did not start.");

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                Task waitTask = Task.Run(() => process.WaitForExit());
                Task finishedTask = await Task.WhenAny(waitTask, Task.Delay(ProcessTimeout)).ConfigureAwait(false);

                if (finishedTask != waitTask)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch
                    {
                    }

                    await Task.WhenAny(
                        Task.WhenAll(waitTask, outputTask, errorTask),
                        Task.Delay(TimeSpan.FromSeconds(2d))).ConfigureAwait(false);

                    return new ProcessResult
                    {
                        ExitCode = -1,
                        StandardOutput = GetCompletedTaskResult(outputTask),
                        StandardError = GetCompletedTaskResult(errorTask),
                        TimedOut = true,
                    };
                }

                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = outputTask.Result,
                    StandardError = errorTask.Result,
                    TimedOut = false,
                };
            }
            finally
            {
                process.Dispose();
            }
        }

        private static async Task<CropRegion> DetectEncodedBordersAsync(
            string ffmpegPath,
            string videoPath)
        {
            ProcessResult result = await RunProcessAsync(
                ffmpegPath,
                BuildCropDetectionArguments(videoPath)).ConfigureAwait(false);
            if (result.TimedOut)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    "[MonitorTakeover] FFmpeg timed out while checking the video frame for encoded borders.");
                return null;
            }

            if (result.ExitCode != 0)
            {
                TakeoverBootstrap.Log?.LogWarning(
                    $"[MonitorTakeover] FFmpeg could not inspect the video frame for encoded borders: " +
                    GetSafeError(result.StandardError));
                return null;
            }

            string diagnostics = result.StandardError ?? string.Empty;
            Match frameSizeMatch = FrameSizePattern.Match(diagnostics);
            MatchCollection cropMatches = CropRectanglePattern.Matches(diagnostics);
            if (!frameSizeMatch.Success || cropMatches.Count == 0)
                return null;

            Match cropMatch = cropMatches[cropMatches.Count - 1];
            if (!TryParsePositiveInt(frameSizeMatch.Groups["width"].Value, out int sourceWidth)
                || !TryParsePositiveInt(frameSizeMatch.Groups["height"].Value, out int sourceHeight)
                || !TryParsePositiveInt(cropMatch.Groups["width"].Value, out int cropWidth)
                || !TryParsePositiveInt(cropMatch.Groups["height"].Value, out int cropHeight)
                || !TryParseNonNegativeInt(cropMatch.Groups["x"].Value, out int cropX)
                || !TryParseNonNegativeInt(cropMatch.Groups["y"].Value, out int cropY))
            {
                return null;
            }

            if (cropWidth > sourceWidth
                || cropHeight > sourceHeight
                || cropX > sourceWidth - cropWidth
                || cropY > sourceHeight - cropHeight
                || cropWidth < sourceWidth / 5
                || cropHeight < sourceHeight / 5)
            {
                return null;
            }

            bool materialBorder = sourceWidth - cropWidth >= 8 || sourceHeight - cropHeight >= 8;
            if (!materialBorder)
                return null;

            return new CropRegion
            {
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                Width = cropWidth,
                Height = cropHeight,
                X = cropX,
                Y = cropY,
            };
        }

        private static string BuildCropDetectionArguments(string videoPath)
        {
            string nullOutput = IsWindowsPlatform() ? "NUL" : "/dev/null";
            return "-hide_banner -loglevel info -i " + QuoteProcessArgument(videoPath) + " "
                + "-t 12 -vf "
                + QuoteProcessArgument("fps=2,cropdetect=limit=24:round=2:reset=0,showinfo") + " "
                + "-an -f null " + QuoteProcessArgument(nullOutput);
        }

        private static string BuildFillFrameArguments(
            string inputPath,
            string outputPath,
            CropRegion crop)
        {
            string filter = $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}," +
                $"scale={crop.SourceWidth}:{crop.SourceHeight}:flags=lanczos,setsar=1";
            return "-y -hide_banner -loglevel error -i " + QuoteProcessArgument(inputPath) + " "
                + "-map 0:v:0 -map 0:a:0? -vf " + QuoteProcessArgument(filter) + " "
                + "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p "
                + "-c:a copy -movflags +faststart " + QuoteProcessArgument(outputPath);
        }

        private static bool TryParsePositiveInt(string value, out int parsed)
        {
            return int.TryParse(value, out parsed) && parsed > 0;
        }

        private static bool TryParseNonNegativeInt(string value, out int parsed)
        {
            return int.TryParse(value, out parsed) && parsed >= 0;
        }

        private static string BuildYtDlpArguments(
            string ffmpegPath,
            string outputPath,
            string normalizedUrl)
        {
            return "--ignore-config --no-playlist --no-progress --socket-timeout 15 --retries 2 "
                + "--fragment-retries 2 --max-filesize 256M --merge-output-format mp4 "
                + "--ffmpeg-location " + QuoteProcessArgument(ffmpegPath) + " "
                + "--format " + QuoteProcessArgument(UnityCompatibleFormat) + " "
                + "--output " + QuoteProcessArgument(outputPath)
                + " -- " + QuoteProcessArgument(normalizedUrl);
        }

        private static string QuoteProcessArgument(string value)
        {
            if (value == null)
                return "\"\"";

            var output = new StringBuilder(value.Length + 2);
            output.Append('"');
            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    output.Append('\\', backslashes * 2 + 1);
                    output.Append('"');
                    backslashes = 0;
                    continue;
                }

                output.Append('\\', backslashes);
                backslashes = 0;
                output.Append(character);
            }

            output.Append('\\', backslashes * 2);
            output.Append('"');
            return output.ToString();
        }

        private static string GetCompletedTaskResult(Task<string> task)
        {
            return task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3d),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"Y4NGZMonitorTakeover/{Y4NGZCompany.Bootstrap.TakeoverBootstrap.PLUGIN_VERSION}");
            return client;
        }

        private static string GetYtDlpBinaryName()
        {
            if (IsWindowsPlatform())
                return "yt-dlp.exe";

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.MacOSX:
                    return "yt-dlp_macos";
                case PlatformID.Unix:
                    return "yt-dlp";
                default:
                    throw new PlatformNotSupportedException("yt-dlp is not available for this operating system.");
            }
        }

        private static bool IsWindowsPlatform()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            return platform == PlatformID.Win32NT
                || platform == PlatformID.Win32S
                || platform == PlatformID.Win32Windows
                || platform == PlatformID.WinCE;
        }

        private static string FindExecutableOnPath(string executableName)
        {
            string pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
                return null;

            string[] pathEntries = pathValue.Split(Path.PathSeparator);
            for (int i = 0; i < pathEntries.Length; i++)
            {
                string directory = pathEntries[i].Trim().Trim('"');
                if (directory.Length == 0)
                    continue;

                try
                {
                    string candidate = Path.Combine(directory, executableName);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool IsValidCachedVideo(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MinimumVideoLength || info.Length > MaximumVideoBytes)
                    return false;

                var header = new byte[12];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Read(header, 0, header.Length) != header.Length)
                        return false;
                }

                return header[4] == (byte)'f'
                    && header[5] == (byte)'t'
                    && header[6] == (byte)'y'
                    && header[7] == (byte)'p';
            }
            catch
            {
                return false;
            }
        }

        private static bool IsVerifiedFile(string path, long expectedLength, string expectedSha256)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    && info.Length == expectedLength
                    && string.Equals(ComputeFileSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeTextSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ToHex(sha.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static void DeleteWorkingVideoFiles(string videoDirectory, string cacheKey)
        {
            string[] matches = Directory.GetFiles(videoDirectory, cacheKey + ".working*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < matches.Length; i++)
                TryDeleteFile(matches[i]);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void EnsureExecutablePermission(string binaryPath)
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix
                && Environment.OSVersion.Platform != PlatformID.MacOSX)
            {
                return;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = "+x " + QuoteProcessArgument(binaryPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("chmod did not start.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException($"chmod exited with code {process.ExitCode}.");
            }
            finally
            {
                process.Dispose();
            }
        }

        private static bool HasRecognizedVideoPath(Uri uri, bool shortHost)
        {
            string path = uri.AbsolutePath.TrimEnd('/');
            if (shortHost)
                return path.Length > 1;

            if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
                return HasQueryParameter(uri.Query, "v");

            return path.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/live/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/v/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/clip/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasQueryParameter(string query, string expectedName)
        {
            if (string.IsNullOrEmpty(query))
                return false;

            string[] fields = query.TrimStart('?').Split('&');
            for (int i = 0; i < fields.Length; i++)
            {
                int separator = fields[i].IndexOf('=');
                string name = separator >= 0 ? fields[i].Substring(0, separator) : fields[i];
                string value = separator >= 0 ? fields[i].Substring(separator + 1) : string.Empty;
                if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetSafeSourceLabel(string normalizedUrl)
        {
            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri uri))
                return "from the configured link";

            return uri.IdnHost + uri.AbsolutePath;
        }

        private static string GetSafeError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "no diagnostic was returned";

            string[] lines = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string safe = lines.Length > 0 ? lines[lines.Length - 1].Trim() : message.Trim();
            if (safe.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "yt-dlp reported an HTTP or URL-related failure (URL omitted)";
            }

            if (safe.Length > MaximumLoggedErrorLength)
                safe = safe.Substring(0, MaximumLoggedErrorLength) + "...";

            return safe;
        }
    }
}
