using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using youtube_tool.Models;
using youtube_tool.Services;

namespace youtube_tool.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly YouTubeService _youtubeService = new();
        private readonly HttpClient _httpClient = new();

        [ObservableProperty]
        private string _videoUrl = string.Empty;

        [ObservableProperty]
        private string _statusMessage = "유튜브 동영상 주소를 입력하고 [분석하기] 버튼을 누르세요.";

        [ObservableProperty]
        private double _progressValue = 0;

        [ObservableProperty]
        private bool _isDownloading = false;

        [ObservableProperty]
        private bool _isInfoLoaded = false;

        [ObservableProperty]
        private VideoInfoModel? _videoInfo;

        [ObservableProperty]
        private string _videoTitle = string.Empty;

        [ObservableProperty]
        private Bitmap? _thumbnailImage;

        [ObservableProperty]
        private string _saveFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "YouTube_Downloads");

        public MainWindowViewModel()
        {
            if (!Directory.Exists(SaveFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(SaveFolderPath);
                }
                catch { }
            }
        }

        // 링크를 바꾸면 이전 링크의 제목이 파일명·폴더명으로 쓰이지 않도록 분석 결과를 비움
        partial void OnVideoUrlChanged(string value)
        {
            VideoTitle = string.Empty;
            IsInfoLoaded = false;
        }

        [RelayCommand]
        private async Task FetchInfoAsync()
        {
            if (string.IsNullOrWhiteSpace(VideoUrl))
            {
                StatusMessage = "⚠️ 유튜브 URL을 입력해 주세요.";
                return;
            }

            try
            {
                IsDownloading = true;
                StatusMessage = "🔍 동영상 정보를 분석하는 중...";
                IsInfoLoaded = false;

                VideoInfo = await _youtubeService.GetVideoInfoAsync(VideoUrl.Trim());
                VideoTitle = VideoInfo.Title;

                // 썸네일 비동기 비트맵 로드
                if (!string.IsNullOrEmpty(VideoInfo.ThumbnailUrl))
                {
                    var bytes = await _httpClient.GetByteArrayAsync(VideoInfo.ThumbnailUrl);
                    using var ms = new MemoryStream(YouTubeService.EncodeThumbnailJpeg(bytes) ?? bytes);
                    ThumbnailImage = new Bitmap(ms);
                }

                IsInfoLoaded = true;
                StatusMessage = YouTubeService.IsPlaylistUrl(VideoUrl.Trim())
                    ? "💡 재생목록 분석 완료! 제목 칸의 이름으로 폴더를 만들어 곡마다 저장합니다."
                    : "💡 동영상 분석 완료! 아래 [일괄 다운로드 시작] 버튼을 누르세요.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ 동영상 분석 실패: {ex.Message}";
                IsInfoLoaded = false;
            }
            finally
            {
                IsDownloading = false;
            }
        }

        [RelayCommand]
        private async Task StartBatchDownloadAsync()
        {
            if (string.IsNullOrWhiteSpace(VideoUrl))
            {
                StatusMessage = "⚠️ 유튜브 URL을 입력해 주세요.";
                return;
            }

            try
            {
                IsDownloading = true;
                ProgressValue = 0;

                var progress = new Progress<double>(p =>
                {
                    ProgressValue = p * 100;
                });

                var url = VideoUrl.Trim();
                if (YouTubeService.IsPlaylistUrl(url))
                {
                    await _youtubeService.BatchDownloadPlaylistAsync(url, SaveFolderPath, progress, msg => StatusMessage = msg, VideoTitle);
                }
                else
                {
                    await _youtubeService.BatchDownloadAsync(url, SaveFolderPath, progress, msg => StatusMessage = msg, VideoTitle);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ 일괄 다운로드 실패: {ex.Message}";
            }
            finally
            {
                IsDownloading = false;
            }
        }

        [RelayCommand]
        private void OpenFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SaveFolderPath))
                {
                    StatusMessage = "⚠️ 저장 폴더 경로를 입력해 주세요.";
                    return;
                }

                if (!Directory.Exists(SaveFolderPath))
                {
                    Directory.CreateDirectory(SaveFolderPath);
                }

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{SaveFolderPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SaveFolderPath,
                        UseShellExecute = true
                    });
                }

                StatusMessage = $"📂 저장 폴더를 열었습니다: {SaveFolderPath}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ 폴더 열기 실패: {ex.Message}";
            }
        }
    }
}
