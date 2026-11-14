using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Xabe.FFmpeg.Downloader;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using youtube_tool.Models;

namespace youtube_tool.Services
{
    public sealed partial class YouTubeService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly YoutubeClient _youtube;
        private readonly object _cacheLock = new();

        private VideoInfoModel? _cachedVideo;
        private (string VideoId, string ThumbnailUrl, byte[] JpegBytes)? _cachedThumbnail;
        private (string PlaylistId, Playlist Playlist, IReadOnlyList<PlaylistVideo> Tracks, VideoInfoModel Info)? _cachedPlaylist;
        private bool _disposed;

        // FFmpeg 바이너리 저장 경로: %LOCALAPPDATA%\YouTubeTool\ffmpeg\
        private static readonly string FFmpegDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YouTubeTool", "ffmpeg");

        private static string FFmpegExePath => Path.Combine(FFmpegDir, "ffmpeg.exe");

        private static readonly SemaphoreSlim FFmpegInitLock = new(1, 1);
        private static volatile bool _ffmpegReady;

        // JPEG 압축으로 바 안쪽 색이 미세하게 흔들리는 것을 허용하는 채널별 오차
        private const int SideBarColorTolerance = 12;
        // 앨범아트 경계 바로 옆은 JPEG 번짐이 있어 판별에서 제외하는 폭(px)
        private const int SideBarEdgeGuard = 4;

        private static readonly Regex InvalidFileNameRegex = BuildInvalidFileNameRegex();

        [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ReservedDeviceNameRegex();

        private static Regex BuildInvalidFileNameRegex()
        {
            var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            return new Regex($"([{invalidChars}]*\\.+$)|([{invalidChars}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        public YouTubeService()
        {
            _httpClient = CreateOptimizedHttpClient();
            _youtube = new YoutubeClient(_httpClient);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _httpClient.Dispose();
        }

        /// <summary>
        /// 한국어 로케일(ko-KR) 및 한국 지역 쿠키를 기본 탑재한 HttpClient를 생성합니다.
        /// 이를 통해 유튜브의 다국어 번역 제목 중 한국어 제목을 최우선으로 받아옵니다.
        /// </summary>
        private static HttpClient CreateOptimizedHttpClient()
        {
            var cookieContainer = new CookieContainer();
            try
            {
                // 유튜브에 한국어 로케일 및 한국 지역 설정 쿠키 주입
                cookieContainer.Add(new Uri("https://www.youtube.com"), new Cookie("PREF", "hl=ko&gl=KR"));
            }
            catch
            {
            }

            var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                AutomaticDecompression = DecompressionMethods.All
            };

            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            return client;
        }

        /// <summary>
        /// FFmpeg 바이너리가 없으면 스레드 안전하게 한 번만 다운로드합니다.
        /// </summary>
        private static async Task EnsureFFmpegAsync(Action<string> statusCallback, CancellationToken cancellationToken)
        {
            if (_ffmpegReady && File.Exists(FFmpegExePath))
                return;

            await FFmpegInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ffmpegReady && File.Exists(FFmpegExePath))
                    return;

                Directory.CreateDirectory(FFmpegDir);

                if (!File.Exists(FFmpegExePath))
                {
                    statusCallback("⬇️ FFmpeg를 처음 다운로드 중입니다 (약 60~80MB, 한 번만 실행됩니다)...");
                    await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpegDir)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    statusCallback("✅ FFmpeg 다운로드 완료!");
                }

                if (File.Exists(FFmpegExePath))
                    _ffmpegReady = true;
            }
            finally
            {
                FFmpegInitLock.Release();
            }
        }

        /// <summary>
        /// 재생목록 전용 링크인지 확인합니다. watch?v=...&amp;list=... 처럼 영상이 지정된 링크는 기존처럼 영상 1개로 처리합니다.
        /// </summary>
        public static bool IsPlaylistUrl(string url) =>
            VideoId.TryParse(url) is null && PlaylistId.TryParse(url) is not null;

        private async Task<IReadOnlyList<PlaylistVideo>> CollectPlaylistVideosAsync(string url, CancellationToken cancellationToken)
        {
            var list = new List<PlaylistVideo>();
            await foreach (var video in _youtube.Playlists.GetVideosAsync(url, cancellationToken).ConfigureAwait(false))
            {
                list.Add(video);
            }
            return list;
        }

        private async Task<VideoInfoModel> GetPlaylistInfoAsync(string url, CancellationToken cancellationToken)
        {
            var playlistTask = _youtube.Playlists.GetAsync(url, cancellationToken).AsTask();
            var tracksTask = CollectPlaylistVideosAsync(url, cancellationToken);
            await Task.WhenAll(playlistTask, tracksTask).ConfigureAwait(false);

            var playlist = playlistTask.Result;
            var tracks = tracksTask.Result;
            var author = playlist.Author?.ChannelTitle;

            var model = new VideoInfoModel
            {
                Id = playlist.Id,
                Title = playlist.Title,
                Channel = string.IsNullOrEmpty(author) ? $"{tracks.Count}곡" : $"{author} · {tracks.Count}곡",
                Duration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration?.Ticks ?? 0)),
                ThumbnailUrl = playlist.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                Url = playlist.Url,
                Description = playlist.Description,
                IsPlaylist = true
            };

            lock (_cacheLock)
            {
                _cachedPlaylist = (playlist.Id.Value, playlist, tracks, model);
            }

            return model;
        }

        public async Task<VideoInfoModel> GetVideoInfoAsync(string url, CancellationToken cancellationToken = default)
        {
            if (IsPlaylistUrl(url))
                return await GetPlaylistInfoAsync(url, cancellationToken).ConfigureAwait(false);

            var videoId = VideoId.Parse(url);
            return await FetchAndCacheVideoInfoAsync(videoId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<VideoInfoModel> FetchAndCacheVideoInfoAsync(VideoId videoId, CancellationToken cancellationToken)
        {
            var video = await _youtube.Videos.GetAsync(videoId, cancellationToken).ConfigureAwait(false);
            var highestThumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty;

            var model = new VideoInfoModel
            {
                Id = video.Id,
                Title = video.Title,
                Channel = video.Author.ChannelTitle,
                Duration = video.Duration,
                UploadDate = video.UploadDate,
                ThumbnailUrl = highestThumb,
                Url = video.Url,
                Description = video.Description,
                IsPlaylist = false
            };

            lock (_cacheLock)
            {
                _cachedVideo = model;
            }

            return model;
        }

        /// <summary>
        /// 썸네일을 다운로드하고 표준 JPEG로 변환한 뒤 캐시합니다. 이후 다운로드 단계에서 재사용됩니다.
        /// </summary>
        public async Task<byte[]?> GetThumbnailJpegAsync(string videoIdOrKey, string thumbnailUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(thumbnailUrl))
                return null;

            lock (_cacheLock)
            {
                if (_cachedThumbnail is { } cached &&
                    (string.Equals(cached.ThumbnailUrl, thumbnailUrl, StringComparison.Ordinal) ||
                     (!string.IsNullOrEmpty(videoIdOrKey) && string.Equals(cached.VideoId, videoIdOrKey, StringComparison.Ordinal))))
                {
                    return cached.JpegBytes;
                }
            }

            var rawBytes = await _httpClient.GetByteArrayAsync(thumbnailUrl, cancellationToken).ConfigureAwait(false);
            byte[] finalBytes;
            try
            {
                finalBytes = EncodeThumbnailJpeg(rawBytes) ?? rawBytes;
            }
            catch
            {
                finalBytes = rawBytes;
            }

            lock (_cacheLock)
            {
                _cachedThumbnail = (videoIdOrKey, thumbnailUrl, finalBytes);
            }

            return finalBytes;
        }

        /// <summary>
        /// 재생목록의 모든 곡을 재생목록 이름의 하위 폴더에 4종 세트로 저장합니다.
        /// 한 곡이 실패해도 멈추지 않고 다음 곡으로 넘어가며, 실패한 곡은 폴더의 목록 파일에 남깁니다.
        /// </summary>
        public async Task BatchDownloadPlaylistAsync(
            string url,
            string outputFolder,
            IProgress<double> progress,
            Action<string> statusCallback,
            string? customTitle = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Playlist playlist;
            IReadOnlyList<PlaylistVideo> tracks;

            var parsedPlaylistId = PlaylistId.TryParse(url);
            bool cacheHit = false;
            playlist = null!;
            tracks = Array.Empty<PlaylistVideo>();

            if (parsedPlaylistId is not null)
            {
                lock (_cacheLock)
                {
                    if (_cachedPlaylist is { } cached && string.Equals(cached.PlaylistId, parsedPlaylistId.Value, StringComparison.Ordinal))
                    {
                        playlist = cached.Playlist;
                        tracks = cached.Tracks;
                        cacheHit = true;
                    }
                }
            }

            if (!cacheHit)
            {
                statusCallback("재생목록 정보를 조회하는 중...");
                var playlistTask = _youtube.Playlists.GetAsync(url, cancellationToken).AsTask();
                var tracksTask = CollectPlaylistVideosAsync(url, cancellationToken);
                await Task.WhenAll(playlistTask, tracksTask).ConfigureAwait(false);
                playlist = playlistTask.Result;
                tracks = tracksTask.Result;
            }

            if (tracks.Count == 0)
            {
                statusCallback("⚠️ 재생목록에서 받을 수 있는 곡을 찾지 못했습니다.");
                return;
            }

            await EnsureFFmpegAsync(statusCallback, cancellationToken).ConfigureAwait(false);

            var folderTitle = !string.IsNullOrWhiteSpace(customTitle) ? customTitle.Trim() : playlist.Title;
            var playlistFolder = Path.Combine(outputFolder, SanitizeFileName(folderTitle));
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedTracks = new List<PlaylistVideo>();

            for (int i = 0; i < tracks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int index = i;
                string prefix = $"[{i + 1}/{tracks.Count}] ";
                var trackProgress = new CallbackProgress<double>(p => progress.Report((index + p) / tracks.Count));

                bool saved = false;
                try
                {
                    saved = await BatchDownloadAsync(
                        tracks[i].Url,
                        playlistFolder,
                        trackProgress,
                        msg => statusCallback(prefix + msg),
                        usedNames: usedNames,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    statusCallback($"{prefix}❌ 건너뜀: {ex.Message}");
                }

                if (!saved)
                    failedTracks.Add(tracks[i]);
            }

            progress.Report(1.0);

            if (failedTracks.Count == 0)
            {
                statusCallback($"✅ 재생목록 {tracks.Count}곡 일괄 다운로드 완료!");
                return;
            }

            try
            {
                Directory.CreateDirectory(playlistFolder);
                var lines = failedTracks.Select(t => $"{t.Title}\t{t.Url}");
                await File.WriteAllLinesAsync(Path.Combine(playlistFolder, "실패한 곡.txt"), lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 목록 파일을 못 써도 아래 상태 메시지로 실패 수는 알림
            }

            statusCallback($"⚠️ 완료: {tracks.Count - failedTracks.Count}곡 저장, {failedTracks.Count}곡 실패 ('실패한 곡.txt' 참고)");
        }

        /// <param name="usedNames">재생목록처럼 한 폴더에 여러 곡을 저장할 때 같은 제목의 덮어쓰기를 막기 위한 사용 중인 파일명 집합</param>
        /// <returns>오디오와 동영상을 모두 저장했으면 true</returns>
        public async Task<bool> BatchDownloadAsync(
            string url,
            string outputFolder,
            IProgress<double> progress,
            Action<string> statusCallback,
            string? customTitle = null,
            ISet<string>? usedNames = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 0. FFmpeg 준비
            await EnsureFFmpegAsync(statusCallback, cancellationToken).ConfigureAwait(false);

            var videoId = VideoId.Parse(url);
            VideoInfoModel? cachedInfo = null;
            lock (_cacheLock)
            {
                if (_cachedVideo != null && string.Equals(_cachedVideo.Id, videoId.Value, StringComparison.Ordinal))
                    cachedInfo = _cachedVideo;
            }

            statusCallback(cachedInfo != null
                ? "스트림 목록을 조회하는 중..."
                : "영상 정보와 스트림 목록을 동시에 조회하는 중...");

            VideoInfoModel videoInfo;
            StreamManifest streamManifest;
            if (cachedInfo != null)
            {
                videoInfo = cachedInfo;
                streamManifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var infoTask = FetchAndCacheVideoInfoAsync(videoId, cancellationToken);
                var manifestTask = _youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken).AsTask();
                await Task.WhenAll(infoTask, manifestTask).ConfigureAwait(false);
                videoInfo = infoTask.Result;
                streamManifest = manifestTask.Result;
            }

            progress.Report(0.05);

            var titleToUse = !string.IsNullOrWhiteSpace(customTitle) ? customTitle.Trim() : videoInfo.Title;
            var safeTitle = SanitizeFileName(titleToUse);
            if (usedNames != null)
                safeTitle = ReserveUniqueName(safeTitle, usedNames);

            if (!Directory.Exists(outputFolder))
            {
                try
                {
                    Directory.CreateDirectory(outputFolder);
                }
                catch (Exception ex)
                {
                    statusCallback($"❌ 저장 폴더 생성 실패 ({outputFolder}): {ex.Message}");
                    return false;
                }
            }

            string basePath = Path.Combine(outputFolder, safeTitle);
            string videoPath = $"{basePath}.mp4";
            string audioPath = $"{basePath}.mp3";
            string thumbPath = $"{basePath}_thumbnail.jpg";
            string infoPath = $"{basePath}_info.txt";

            // 단일 StreamManifest에서 최적 오디오/비디오 스트림 선택
            var audioOnlyStream = streamManifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ThenByDescending(s => s.Container == Container.Mp4)
                .FirstOrDefault();

            var videoOnlyStream = SelectBestVideoStream(streamManifest.GetVideoOnlyStreams());

            MuxedStreamInfo? muxedFallback = null;
            if (audioOnlyStream == null || videoOnlyStream == null)
            {
                muxedFallback = SelectBestVideoStream(streamManifest.GetMuxedStreams());
            }

            IStreamInfo? effectiveAudioStream = (IStreamInfo?)audioOnlyStream ?? muxedFallback;
            IStreamInfo? effectiveVideoStream = (IStreamInfo?)videoOnlyStream ?? muxedFallback;
            bool sharedMuxedSource = ReferenceEquals(effectiveAudioStream, effectiveVideoStream) && effectiveAudioStream != null;

            string tempToken = Guid.NewGuid().ToString("N");
            string? audioTempPath = effectiveAudioStream != null ? $"{basePath}.audio.{tempToken}.{effectiveAudioStream.Container.Name}.tmp" : null;
            string? videoTempPath = effectiveVideoStream != null
                ? (sharedMuxedSource ? audioTempPath : $"{basePath}.video.{tempToken}.{effectiveVideoStream.Container.Name}.tmp")
                : null;

            string? firstError = null;
            bool mp3Completed = false;
            bool mp4Completed = false;

            try
            {
                statusCallback("1/4·2/4: 썸네일·메타데이터 저장 및 오디오·비디오 스트림 병렬 다운로드 중...");

                var thumbTask = SaveThumbnailAsync(videoInfo, thumbPath, statusCallback, cancellationToken);
                var infoTask = SaveMetadataAsync(videoInfo, titleToUse, infoPath, statusCallback, cancellationToken);

                long audioTotalBytes = Math.Max(1L, effectiveAudioStream?.Size.Bytes ?? 0L);
                long videoTotalBytes = sharedMuxedSource ? 0L : Math.Max(1L, effectiveVideoStream?.Size.Bytes ?? 0L);
                long combinedBytes = Math.Max(1L, (effectiveAudioStream != null ? audioTotalBytes : 0L) + (effectiveVideoStream != null && !sharedMuxedSource ? videoTotalBytes : 0L));
                long audioDoneBytes = 0;
                long videoDoneBytes = 0;

                void ReportDownloadProgress()
                {
                    double fraction = (double)(Interlocked.Read(ref audioDoneBytes) + Interlocked.Read(ref videoDoneBytes)) / combinedBytes;
                    progress.Report(0.05 + Math.Clamp(fraction, 0.0, 1.0) * 0.75);
                }

                Task audioDownloadTask = effectiveAudioStream != null && audioTempPath != null
                    ? _youtube.Videos.Streams.DownloadAsync(
                        effectiveAudioStream,
                        audioTempPath,
                        new CallbackProgress<double>(p =>
                        {
                            Interlocked.Exchange(ref audioDoneBytes, (long)(Math.Clamp(p, 0.0, 1.0) * audioTotalBytes));
                            ReportDownloadProgress();
                        }),
                        cancellationToken).AsTask()
                    : Task.CompletedTask;

                Task videoDownloadTask = !sharedMuxedSource && effectiveVideoStream != null && videoTempPath != null
                    ? _youtube.Videos.Streams.DownloadAsync(
                        effectiveVideoStream,
                        videoTempPath,
                        new CallbackProgress<double>(p =>
                        {
                            Interlocked.Exchange(ref videoDoneBytes, (long)(Math.Clamp(p, 0.0, 1.0) * videoTotalBytes));
                            ReportDownloadProgress();
                        }),
                        cancellationToken).AsTask()
                    : Task.CompletedTask;

                // 오디오 다운로드가 끝나는 즉시 (비디오 다운로드를 기다리지 않고) MP3 변환을 병렬 시작
                Task mp3Task = Task.Run(async () =>
                {
                    if (effectiveAudioStream == null || audioTempPath == null)
                    {
                        firstError ??= "오디오: 다운로드 가능한 오디오 스트림이 없습니다.";
                        statusCallback("❌ 오디오 스트림을 찾지 못했습니다.");
                        return;
                    }

                    try
                    {
                        await audioDownloadTask.ConfigureAwait(false);
                        statusCallback("3/4: 오디오(MP3) 변환 중...");
                        await ConvertToMp3Async(audioTempPath, audioPath, cancellationToken).ConfigureAwait(false);
                        mp3Completed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        firstError ??= $"오디오: {ex.Message}";
                        statusCallback($"❌ 오디오 처리 실패: {ex.Message}");
                    }
                }, cancellationToken);

                // 비디오+오디오 다운로드 완료 후 스트림 복사로 MP4 병합 (재인코딩 생략)
                Task mp4Task = Task.Run(async () =>
                {
                    if (effectiveVideoStream == null || videoTempPath == null)
                    {
                        firstError ??= "동영상: 다운로드 가능한 비디오 스트림이 없습니다.";
                        statusCallback("❌ 비디오 스트림을 찾지 못했습니다.");
                        return;
                    }

                    try
                    {
                        await Task.WhenAll(audioDownloadTask, videoDownloadTask).ConfigureAwait(false);
                        progress.Report(0.82);
                        statusCallback("4/4: 동영상(MP4) 초고속 무손실 병합 중...");
                        await MuxToMp4Async(
                            videoTempPath,
                            sharedMuxedSource ? null : audioTempPath,
                            effectiveAudioStream?.Container == Container.Mp4,
                            videoPath,
                            cancellationToken).ConfigureAwait(false);
                        mp4Completed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        firstError ??= $"동영상: {ex.Message}";
                        statusCallback($"❌ 비디오 처리 실패: {ex.Message}");
                    }
                }, cancellationToken);

                await Task.WhenAll(thumbTask, infoTask, mp3Task, mp4Task).ConfigureAwait(false);

                progress.Report(1.0);
                statusCallback(firstError == null
                    ? "✅ 썸네일, 메타데이터, 오디오(MP3), 동영상(MP4) 일괄 다운로드 완료!"
                    : $"⚠️ 일부 파일 저장 실패 - {firstError}");
                return firstError == null;
            }
            finally
            {
                TryDeleteFile(audioTempPath);
                if (!sharedMuxedSource)
                    TryDeleteFile(videoTempPath);

                if (cancellationToken.IsCancellationRequested)
                {
                    if (!mp3Completed) TryDeleteFile(audioPath);
                    if (!mp4Completed) TryDeleteFile(videoPath);
                }
            }
        }

        private async Task SaveThumbnailAsync(
            VideoInfoModel videoInfo,
            string thumbPath,
            Action<string> statusCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(videoInfo.ThumbnailUrl))
                    return;

                var thumbBytes = await GetThumbnailJpegAsync(videoInfo.Id, videoInfo.ThumbnailUrl, cancellationToken).ConfigureAwait(false);
                if (thumbBytes != null)
                    await File.WriteAllBytesAsync(thumbPath, thumbBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                statusCallback($"썸네일 다운로드 실패 (건너뜀): {ex.Message}");
            }
        }

        private static async Task SaveMetadataAsync(
            VideoInfoModel videoInfo,
            string titleToUse,
            string infoPath,
            Action<string> statusCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"제목: {titleToUse}");
                if (!string.Equals(titleToUse, videoInfo.Title, StringComparison.Ordinal))
                    sb.AppendLine($"유튜브 원본 제목: {videoInfo.Title}");
                sb.AppendLine($"채널: {videoInfo.Channel}");
                if (videoInfo.UploadDate.HasValue)
                    sb.AppendLine($"게시일: {videoInfo.UploadDate.Value:yyyy-MM-dd}");
                sb.AppendLine($"재생시간: {videoInfo.Duration}");
                sb.AppendLine($"URL: {videoInfo.Url}");
                sb.AppendLine(new string('-', 50));
                sb.AppendLine("설명:");
                sb.AppendLine(videoInfo.Description);
                await File.WriteAllTextAsync(infoPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                statusCallback($"메타데이터 저장 실패 (건너뜀): {ex.Message}");
            }
        }

        private static async Task ConvertToMp3Async(string audioTempPath, string audioOutputPath, CancellationToken cancellationToken)
        {
            var args = new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-i", audioTempPath,
                "-vn", "-c:a", "libmp3lame", "-q:a", "0",
                "-threads", Environment.ProcessorCount.ToString(),
                audioOutputPath
            };

            var (exitCode, stdErr) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"FFmpeg MP3 변환 실패 (종료 코드 {exitCode}): {TrimError(stdErr)}");
        }

        private static T? SelectBestVideoStream<T>(IEnumerable<T> streams) where T : IVideoStreamInfo =>
            streams
                .OrderByDescending(s => s.VideoQuality.MaxHeight)
                .ThenByDescending(s => s.VideoQuality.Framerate)
                .ThenByDescending(s => s.Container == Container.Mp4)
                .ThenByDescending(s => s.Bitrate)
                .FirstOrDefault();

        private static List<string> BuildMuxBaseArgs(string videoTempPath, string? audioTempPath)
        {
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-i", videoTempPath
            };

            if (audioTempPath != null)
            {
                args.AddRange(["-i", audioTempPath, "-map", "0:v:0", "-map", "1:a:0"]);
            }
            else
            {
                args.AddRange(["-map", "0:v:0", "-map", "0:a:0?"]);
            }

            return args;
        }

        private static async Task MuxToMp4Async(
            string videoTempPath,
            string? audioTempPath,
            bool audioIsMp4Container,
            string videoOutputPath,
            CancellationToken cancellationToken)
        {
            var copyArgs = BuildMuxBaseArgs(videoTempPath, audioTempPath);
            copyArgs.AddRange(["-c:v", "copy", "-c:a", audioIsMp4Container ? "copy" : "aac"]);
            if (!audioIsMp4Container)
                copyArgs.AddRange(["-b:a", "192k"]);
            copyArgs.AddRange([
                "-movflags", "+faststart",
                "-threads", Environment.ProcessorCount.ToString(),
                videoOutputPath
            ]);

            var (exitCode, _) = await RunFfmpegAsync(copyArgs, cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
                return;

            // 스트림 복사 실패 시 호환성 있는 H.264 + AAC로 자동 재시도
            var encodeArgs = BuildMuxBaseArgs(videoTempPath, audioTempPath);
            encodeArgs.AddRange([
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                "-threads", Environment.ProcessorCount.ToString(),
                videoOutputPath
            ]);

            var (retryExit, retryErr) = await RunFfmpegAsync(encodeArgs, cancellationToken).ConfigureAwait(false);
            if (retryExit != 0)
                throw new InvalidOperationException($"FFmpeg MP4 병합 실패 (종료 코드 {retryExit}): {TrimError(retryErr)}");
        }

        private static async Task<(int ExitCode, string StdErr)> RunFfmpegAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FFmpegExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("FFmpeg 프로세스를 시작할 수 없습니다.");

            using var ctr = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            });

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);

            return (process.ExitCode, stdErrTask.Result);
        }

        private static string TrimError(string stdErr)
        {
            if (string.IsNullOrWhiteSpace(stdErr))
                return "알 수 없는 FFmpeg 오류";
            var trimmed = stdErr.Trim();
            return trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed;
        }

        private static void TryDeleteFile(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string ReserveUniqueName(string name, ISet<string> usedNames)
        {
            var candidate = name;
            for (int n = 2; !usedNames.Add(candidate); n++)
                candidate = $"{name} ({n})";
            return candidate;
        }

        /// <summary>
        /// 썸네일을 표준 JPEG로 변환합니다. 유튜브 뮤직 음원처럼 양옆이 단색 바인 썸네일은 가운데 정사각형만 남깁니다.
        /// 디코딩할 수 없는 데이터면 null을 반환합니다.
        /// </summary>
        public static byte[]? EncodeThumbnailJpeg(byte[] source)
        {
            using var decoded = SKBitmap.Decode(source);
            if (decoded == null)
                return null;

            using var square = CropSideBarsToSquare(decoded);
            using var image = SKImage.FromBitmap(square ?? decoded);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);
            return jpeg.ToArray();
        }

        /// <summary>
        /// 양옆이 같은 단색 바이면 가운데 정사각형을 잘라 반환하고, 아니면 null을 반환합니다.
        /// URL이 아닌 픽셀로 판별하므로 유튜브 뮤직 링크의 16:9 뮤직비디오 썸네일은 잘리지 않습니다.
        /// </summary>
        private static SKBitmap? CropSideBarsToSquare(SKBitmap bitmap)
        {
            int barWidth = (bitmap.Width - bitmap.Height) / 2;
            if (barWidth <= SideBarEdgeGuard * 2)
                return null;

            int scanWidth = barWidth - SideBarEdgeGuard;
            int rightBarStart = bitmap.Width - scanWidth;

            if (!HasUniformSideBars(bitmap, barWidth, scanWidth, rightBarStart))
                return null;

            var square = new SKBitmap();
            var center = SKRectI.Create(barWidth, 0, bitmap.Height, bitmap.Height);
            if (bitmap.ExtractSubset(square, center))
                return square;

            square.Dispose();
            return null;
        }

        private static bool HasUniformSideBars(SKBitmap bitmap, int barWidth, int scanWidth, int rightBarStart)
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap != null &&
                pixmap.BytesPerPixel == 4 &&
                (bitmap.ColorType == SKColorType.Bgra8888 || bitmap.ColorType == SKColorType.Rgba8888))
            {
                var span = pixmap.GetPixelSpan();
                int rowBytes = pixmap.RowBytes;
                int refIdx = (bitmap.Height / 2) * rowBytes + (barWidth / 2) * 4;
                byte refC0 = span[refIdx];
                byte refC1 = span[refIdx + 1];
                byte refC2 = span[refIdx + 2];

                for (int y = 0; y < bitmap.Height; y += 2)
                {
                    int rowStart = y * rowBytes;
                    for (int x = 0; x < scanWidth; x += 2)
                    {
                        int leftIdx = rowStart + x * 4;
                        int rightIdx = rowStart + (rightBarStart + x) * 4;
                        if (!IsNearChannels(span[leftIdx], span[leftIdx + 1], span[leftIdx + 2], refC0, refC1, refC2) ||
                            !IsNearChannels(span[rightIdx], span[rightIdx + 1], span[rightIdx + 2], refC0, refC1, refC2))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            var barColor = bitmap.GetPixel(barWidth / 2, bitmap.Height / 2);
            for (int y = 0; y < bitmap.Height; y += 2)
            {
                for (int x = 0; x < scanWidth; x += 2)
                {
                    if (!IsNearColor(bitmap.GetPixel(x, y), barColor) ||
                        !IsNearColor(bitmap.GetPixel(rightBarStart + x, y), barColor))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool IsNearChannels(byte a0, byte a1, byte a2, byte b0, byte b1, byte b2) =>
            Math.Abs(a0 - b0) <= SideBarColorTolerance &&
            Math.Abs(a1 - b1) <= SideBarColorTolerance &&
            Math.Abs(a2 - b2) <= SideBarColorTolerance;

        private static bool IsNearColor(SKColor a, SKColor b) =>
            Math.Abs(a.Red - b.Red) <= SideBarColorTolerance &&
            Math.Abs(a.Green - b.Green) <= SideBarColorTolerance &&
            Math.Abs(a.Blue - b.Blue) <= SideBarColorTolerance;

        private static string SanitizeFileName(string fileName)
        {
            var sanitized = InvalidFileNameRegex.Replace(fileName, "_").Trim('.', ' ');

            // Windows MAX_PATH (260자) 제한 방지를 위해 파일명 길이 80자로 제한
            if (sanitized.Length > 80)
                sanitized = sanitized[..80].TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(sanitized))
                return "YouTube_Video";

            if (ReservedDeviceNameRegex().IsMatch(sanitized))
                sanitized = $"_{sanitized}";

            return sanitized;
        }

        private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
        {
            public void Report(T value) => callback(value);
        }
    }
}
