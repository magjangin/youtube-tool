using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using youtube_tool.Models;
using youtube_tool.Services;

namespace youtube_tool.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        private readonly YouTubeService _youtubeService = new();
        private readonly object _progressLock = new();
        private CancellationTokenSource? _cts;
        private double _lastReportedProgress = -1;
        private bool _disposed;

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
        private string _saveFolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "YouTube_Downloads");

        public MainWindowViewModel()
        {
            if (!Directory.Exists(SaveFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(SaveFolderPath);
                }
                catch
                {
                }
            }
        }

        partial void OnThumbnailImageChanging(Bitmap? oldValue, Bitmap? newValue)
        {
            if (!ReferenceEquals(oldValue, newValue))
                oldValue?.Dispose();
        }

        // 링크를 바꾸면 이전 링크의 제목·썸네일이 남지 않도록 분석 결과를 비움
        partial void OnVideoUrlChanged(string value)
        {
            VideoTitle = string.Empty;
            IsInfoLoaded = false;
            VideoInfo = null;
            ThumbnailImage = null;
        }

        [RelayCommand]
        private async Task FetchInfoAsync()
        {
            if (IsDownloading)
                return;

            if (string.IsNullOrWhiteSpace(VideoUrl))
            {
                ReportStatus("⚠️ 유튜브 URL을 입력해 주세요.");
                return;
            }

            using var cts = BeginOperation();
            try
            {
                IsDownloading = true;
                IsInfoLoaded = false;
                ReportStatus("🔍 동영상 정보를 분석하는 중...");

                var url = VideoUrl.Trim();
                var info = await _youtubeService.GetVideoInfoAsync(url, cts.Token);
                VideoInfo = info;
                VideoTitle = info.Title;

                // 썸네일 비동기 로드 및 캐시 (이미 인코딩된 JPEG를 바이트 캐시에 저장해 다운로드 시 재사용)
                if (!string.IsNullOrEmpty(info.ThumbnailUrl))
                {
                    var jpegBytes = await _youtubeService.GetThumbnailJpegAsync(info.Id, info.ThumbnailUrl, cts.Token);
                    if (jpegBytes != null)
                    {
                        using var ms = new MemoryStream(jpegBytes);
                        ThumbnailImage = new Bitmap(ms);
                    }
                }
                else
                {
                    ThumbnailImage = null;
                }

                IsInfoLoaded = true;
                ReportStatus(info.IsPlaylist
                    ? "💡 재생목록 분석 완료! 제목 칸의 이름으로 폴더를 만들어 곡마다 저장합니다."
                    : "💡 동영상 분석 완료! 아래 [일괄 다운로드 시작] 버튼을 누르세요.");
            }
            catch (OperationCanceledException)
            {
                ReportStatus("⏹ 작업이 취소되었습니다.");
                IsInfoLoaded = false;
            }
            catch (Exception ex)
            {
                ReportStatus($"❌ 동영상 분석 실패: {ex.Message}");
                IsInfoLoaded = false;
            }
            finally
            {
                EndOperation(cts);
                IsDownloading = false;
            }
        }

        [RelayCommand]
        private async Task StartBatchDownloadAsync()
        {
            if (IsDownloading)
                return;

            if (string.IsNullOrWhiteSpace(VideoUrl))
            {
                ReportStatus("⚠️ 유튜브 URL을 입력해 주세요.");
                return;
            }

            using var cts = BeginOperation();
            try
            {
                IsDownloading = true;
                _lastReportedProgress = -1;
                ReportProgress(0);

                var progress = new UiProgressAdapter(ReportProgress);
                var url = VideoUrl.Trim();

                if (YouTubeService.IsPlaylistUrl(url))
                {
                    await _youtubeService.BatchDownloadPlaylistAsync(
                        url,
                        SaveFolderPath,
                        progress,
                        ReportStatus,
                        VideoTitle,
                        cts.Token);
                }
                else
                {
                    await _youtubeService.BatchDownloadAsync(
                        url,
                        SaveFolderPath,
                        progress,
                        ReportStatus,
                        VideoTitle,
                        cancellationToken: cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                ReportStatus("⏹ 작업이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                ReportStatus($"❌ 일괄 다운로드 실패: {ex.Message}");
            }
            finally
            {
                EndOperation(cts);
                IsDownloading = false;
            }
        }

        [RelayCommand]
        public void Cancel()
        {
            var cts = _cts;
            if (cts is { IsCancellationRequested: false })
            {
                cts.Cancel();
                ReportStatus("⏹ 작업 취소 중...");
            }
        }

        [RelayCommand]
        private void OpenFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SaveFolderPath))
                {
                    ReportStatus("⚠️ 저장 폴더 경로를 입력해 주세요.");
                    return;
                }

                if (!Directory.Exists(SaveFolderPath))
                    Directory.CreateDirectory(SaveFolderPath);

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

                ReportStatus($"📂 저장 폴더를 열었습니다: {SaveFolderPath}");
            }
            catch (Exception ex)
            {
                ReportStatus($"❌ 폴더 열기 실패: {ex.Message}");
            }
        }

        private CancellationTokenSource BeginOperation()
        {
            var previous = Interlocked.Exchange(ref _cts, null);
            previous?.Cancel();

            var next = new CancellationTokenSource();
            _cts = next;
            return next;
        }

        private void EndOperation(CancellationTokenSource cts)
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
        }

        private void ReportStatus(string message)
        {
            if (Dispatcher.UIThread.CheckAccess())
                StatusMessage = message;
            else
                Dispatcher.UIThread.Post(() => StatusMessage = message);
        }

        private void ReportProgress(double fraction)
        {
            double percent = Math.Clamp(fraction * 100.0, 0.0, 100.0);
            lock (_progressLock)
            {
                if (percent > 0.0 && _lastReportedProgress > percent)
                    percent = _lastReportedProgress;

                if (percent is > 0.0 and < 100.0 && Math.Abs(percent - _lastReportedProgress) < 0.2)
                    return;

                _lastReportedProgress = percent;
            }

            if (Dispatcher.UIThread.CheckAccess())
                ProgressValue = percent;
            else
                Dispatcher.UIThread.Post(() => ProgressValue = percent);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            var cts = Interlocked.Exchange(ref _cts, null);
            cts?.Cancel();

            ThumbnailImage = null;
            _youtubeService.Dispose();
        }

        private sealed class UiProgressAdapter(Action<double> handler) : IProgress<double>
        {
            public void Report(double value) => handler(value);
        }
    }
}
