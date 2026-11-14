using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SkiaSharp;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Converter;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using youtube_tool.Models;

namespace youtube_tool.Services
{
    public class YouTubeService
    {
        private readonly HttpClient _httpClient;
        private readonly YoutubeClient _youtube;

        public YouTubeService()
        {
            _httpClient = CreateOptimizedHttpClient();
            _youtube = new YoutubeClient(_httpClient);
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
            catch { }

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

        // FFmpeg 바이너리 저장 경로: %LOCALAPPDATA%\YouTubeTool\ffmpeg\
        private static readonly string FFmpegDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YouTubeTool", "ffmpeg");

        private static string FFmpegExePath => Path.Combine(FFmpegDir, "ffmpeg.exe");

        private static bool _ffmpegReady = false;

        /// <summary>
        /// FFmpeg 바이너리가 없으면 자동으로 다운로드합니다.
        /// </summary>
        private async Task EnsureFFmpegAsync(Action<string> statusCallback)
        {
            if (_ffmpegReady && File.Exists(FFmpegExePath))
                return;

            if (!Directory.Exists(FFmpegDir))
                Directory.CreateDirectory(FFmpegDir);

            if (!File.Exists(FFmpegExePath))
            {
                statusCallback("⬇️ FFmpeg를 처음 다운로드 중입니다 (약 60~80MB, 한 번만 실행됩니다)...");
                FFmpeg.SetExecutablesPath(FFmpegDir);
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpegDir);
                statusCallback("✅ FFmpeg 다운로드 완료!");
            }

            FFmpeg.SetExecutablesPath(FFmpegDir);
            _ffmpegReady = true;
        }

        /// <summary>
        /// 재생목록 전용 링크인지 확인합니다. watch?v=...&amp;list=... 처럼 영상이 지정된 링크는 기존처럼 영상 1개로 처리합니다.
        /// </summary>
        public static bool IsPlaylistUrl(string url) =>
            VideoId.TryParse(url) is null && PlaylistId.TryParse(url) is not null;

        private async Task<VideoInfoModel> GetPlaylistInfoAsync(string url)
        {
            var playlist = await _youtube.Playlists.GetAsync(url);
            var tracks = await _youtube.Playlists.GetVideosAsync(url);
            var author = playlist.Author?.ChannelTitle;

            return new VideoInfoModel
            {
                Id = playlist.Id,
                Title = playlist.Title,
                Channel = string.IsNullOrEmpty(author) ? $"{tracks.Count}곡" : $"{author} · {tracks.Count}곡",
                Duration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration?.Ticks ?? 0)),
                ThumbnailUrl = playlist.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                Url = playlist.Url,
                Description = playlist.Description
            };
        }

        public async Task<VideoInfoModel> GetVideoInfoAsync(string url)
        {
            if (IsPlaylistUrl(url))
                return await GetPlaylistInfoAsync(url);

            var video = await _youtube.Videos.GetAsync(url);
            var highestThumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty;

            return new VideoInfoModel
            {
                Id = video.Id,
                Title = video.Title,
                Channel = video.Author.ChannelTitle,
                Duration = video.Duration,
                ThumbnailUrl = highestThumb,
                Url = video.Url,
                Description = video.Description
            };
        }

        /// <summary>
        /// 재생목록의 모든 곡을 재생목록 이름의 하위 폴더에 4종 세트로 저장합니다.
        /// 한 곡이 실패해도 멈추지 않고 다음 곡으로 넘어가며, 실패한 곡은 폴더의 목록 파일에 남깁니다.
        /// </summary>
        public async Task BatchDownloadPlaylistAsync(string url, string outputFolder, IProgress<double> progress, Action<string> statusCallback, string? customTitle = null)
        {
            statusCallback("재생목록 정보를 조회하는 중...");
            var playlist = await _youtube.Playlists.GetAsync(url);
            var tracks = await _youtube.Playlists.GetVideosAsync(url);
            if (tracks.Count == 0)
            {
                statusCallback("⚠️ 재생목록에서 받을 수 있는 곡을 찾지 못했습니다.");
                return;
            }

            var folderTitle = !string.IsNullOrWhiteSpace(customTitle) ? customTitle.Trim() : playlist.Title;
            var playlistFolder = Path.Combine(outputFolder, SanitizeFileName(folderTitle));
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedTracks = new List<PlaylistVideo>();

            for (int i = 0; i < tracks.Count; i++)
            {
                int index = i;
                string prefix = $"[{i + 1}/{tracks.Count}] ";
                var trackProgress = new Progress<double>(p => progress.Report((index + p) / tracks.Count));

                bool saved = false;
                try
                {
                    saved = await BatchDownloadAsync(tracks[i].Url, playlistFolder, trackProgress, msg => statusCallback(prefix + msg), usedNames: usedNames);
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
                await File.WriteAllLinesAsync(Path.Combine(playlistFolder, "실패한 곡.txt"), lines, Encoding.UTF8);
            }
            catch
            {
                // 목록 파일을 못 써도 아래 상태 메시지로 실패 수는 알림
            }

            statusCallback($"⚠️ 완료: {tracks.Count - failedTracks.Count}곡 저장, {failedTracks.Count}곡 실패 ('실패한 곡.txt' 참고)");
        }

        /// <param name="usedNames">재생목록처럼 한 폴더에 여러 곡을 저장할 때 같은 제목의 덮어쓰기를 막기 위한 사용 중인 파일명 집합</param>
        /// <returns>오디오와 동영상을 모두 저장했으면 true</returns>
        public async Task<bool> BatchDownloadAsync(string url, string outputFolder, IProgress<double> progress, Action<string> statusCallback, string? customTitle = null, ISet<string>? usedNames = null)
        {
            string? firstError = null;

            // 0. FFmpeg 준비
            await EnsureFFmpegAsync(statusCallback);

            statusCallback("영상 정보를 조회하는 중...");
            var video = await _youtube.Videos.GetAsync(url);
            var titleToUse = !string.IsNullOrWhiteSpace(customTitle) ? customTitle.Trim() : video.Title;
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
            string infoPath  = $"{basePath}_info.txt";

            // 1. 썸네일 다운로드 (WebP 등 포맷을 포토샵 호환 표준 고화질 JPEG로 변환)
            statusCallback("1/4: 썸네일 다운로드 및 포토샵 호환 JPEG 변환 중...");
            try
            {
                var thumbUrl = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url;
                if (!string.IsNullOrEmpty(thumbUrl))
                {
                    var thumbBytes = await _httpClient.GetByteArrayAsync(thumbUrl);

                    byte[]? jpegBytes = null;
                    try
                    {
                        jpegBytes = EncodeThumbnailJpeg(thumbBytes);
                    }
                    catch
                    {
                        // 변환 실패 시 원본 바이트를 그대로 저장
                    }

                    await File.WriteAllBytesAsync(thumbPath, jpegBytes ?? thumbBytes);
                }
            }
            catch (Exception ex)
            {
                statusCallback($"썸네일 다운로드 실패 (건너뜀): {ex.Message}");
            }
            progress.Report(0.1);

            // 2. 메타데이터 텍스트 파일 저장
            statusCallback("2/4: 메타데이터 정보 저장 중...");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"제목: {titleToUse}");
                if (titleToUse != video.Title)
                {
                    sb.AppendLine($"유튜브 원본 제목: {video.Title}");
                }
                sb.AppendLine($"채널: {video.Author.ChannelTitle}");
                sb.AppendLine($"게시일: {video.UploadDate:yyyy-MM-dd}");
                sb.AppendLine($"재생시간: {video.Duration}");
                sb.AppendLine($"URL: {video.Url}");
                sb.AppendLine(new string('-', 50));
                sb.AppendLine("설명:");
                sb.AppendLine(video.Description);
                await File.WriteAllTextAsync(infoPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                statusCallback($"메타데이터 저장 실패 (건너뜀): {ex.Message}");
            }
            progress.Report(0.2);

            // 3. 오디오 (MP3) 다운로드
            statusCallback("3/4: 오디오(MP3) 추출 및 다운로드 중...");
            try
            {
                var audioProgress = new Progress<double>(p => progress.Report(0.2 + (p * 0.3)));
                try
                {
                    // FFmpeg 경로를 지정하여 MP3로 변환 저장
                    await _youtube.Videos.DownloadAsync(
                        url,
                        audioPath,
                        builder => builder
                            .SetContainer("mp3")
                            .SetFFmpegPath(FFmpegExePath),
                        audioProgress);
                }
                catch
                {
                    // fallback: raw 오디오 스트림 직접 다운로드
                    var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id);
                    var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                    if (audioStreamInfo != null)
                    {
                        string rawAudioPath = $"{basePath}.{audioStreamInfo.Container.Name}";
                        await _youtube.Videos.Streams.DownloadAsync(audioStreamInfo, rawAudioPath, audioProgress);
                        File.Move(rawAudioPath, audioPath, overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                firstError ??= $"오디오: {ex.Message}";
                statusCallback($"오디오 다운로드 에러: {ex.Message}");
            }
            progress.Report(0.5);

            // 4. 비디오 (MP4) 최고화질 다운로드 — FFmpeg로 VideoOnly + AudioOnly 병합
            statusCallback("4/4: 동영상(MP4) 최고화질 다운로드 중... (1080p FFmpeg 병합)");
            try
            {
                var videoProgress = new Progress<double>(p => progress.Report(0.5 + (p * 0.5)));
                try
                {
                    // FFmpeg 경로를 명시하여 최고화질 VideoOnly+AudioOnly 병합
                    await _youtube.Videos.DownloadAsync(
                        url,
                        videoPath,
                        builder => builder
                            .SetContainer("mp4")
                            .SetFFmpegPath(FFmpegExePath),
                        videoProgress);
                }
                catch (Exception innerEx)
                {
                    // fallback: muxed 스트림 (최대 480p)
                    statusCallback($"4/4: FFmpeg 병합 중 오류 발생 ({innerEx.Message}), 기본 MP4 스트림으로 재시도 중...");
                    var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id);
                    var muxedStreamInfo = streamManifest.GetMuxedStreams().GetWithHighestVideoQuality();
                    if (muxedStreamInfo != null)
                    {
                        await _youtube.Videos.Streams.DownloadAsync(muxedStreamInfo, videoPath, videoProgress);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                firstError ??= $"동영상: {ex.Message}";
                statusCallback($"❌ 비디오 다운로드 실패: {ex.Message}");
            }

            progress.Report(1.0);
            statusCallback(firstError == null
                ? "✅ 썸네일, 메타데이터, 오디오(MP3), 동영상(MP4) 일괄 다운로드 완료!"
                : $"⚠️ 일부 파일 저장 실패 - {firstError}");
            return firstError == null;
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

        // JPEG 압축으로 바 안쪽 색이 미세하게 흔들리는 것을 허용하는 채널별 오차
        private const int SideBarColorTolerance = 12;
        // 앨범아트 경계 바로 옆은 JPEG 번짐이 있어 판별에서 제외하는 폭(px)
        private const int SideBarEdgeGuard = 4;

        /// <summary>
        /// 양옆이 같은 단색 바이면 가운데 정사각형을 잘라 반환하고, 아니면 null을 반환합니다.
        /// URL이 아닌 픽셀로 판별하므로 유튜브 뮤직 링크의 16:9 뮤직비디오 썸네일은 잘리지 않습니다.
        /// </summary>
        private static SKBitmap? CropSideBarsToSquare(SKBitmap bitmap)
        {
            int barWidth = (bitmap.Width - bitmap.Height) / 2;
            if (barWidth <= SideBarEdgeGuard * 2)
                return null;

            var barColor = bitmap.GetPixel(barWidth / 2, bitmap.Height / 2);
            int scanWidth = barWidth - SideBarEdgeGuard;
            int rightBarStart = bitmap.Width - scanWidth;

            for (int y = 0; y < bitmap.Height; y += 2)
            {
                for (int x = 0; x < scanWidth; x += 2)
                {
                    if (!IsNearColor(bitmap.GetPixel(x, y), barColor) ||
                        !IsNearColor(bitmap.GetPixel(rightBarStart + x, y), barColor))
                    {
                        return null;
                    }
                }
            }

            var square = new SKBitmap();
            var center = SKRectI.Create(barWidth, 0, bitmap.Height, bitmap.Height);
            if (bitmap.ExtractSubset(square, center))
                return square;

            square.Dispose();
            return null;
        }

        private static bool IsNearColor(SKColor a, SKColor b) =>
            Math.Abs(a.Red - b.Red) <= SideBarColorTolerance &&
            Math.Abs(a.Green - b.Green) <= SideBarColorTolerance &&
            Math.Abs(a.Blue - b.Blue) <= SideBarColorTolerance;

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}])", invalidChars);
            var sanitized = Regex.Replace(fileName, invalidRegStr, "_").Trim('.', ' ');

            // Windows MAX_PATH (260자) 제한 방지를 위해 파일명 길이 80자로 제한
            if (sanitized.Length > 80)
            {
                sanitized = sanitized.Substring(0, 80).TrimEnd('.', ' ');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "YouTube_Video" : sanitized;
        }
    }
}
