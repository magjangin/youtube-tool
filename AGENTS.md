# YouTube Batch Suite 개발 및 아키텍처 인수인계 문서 (`AGENTS.md`)

## 1. 프로젝트 개요 & 기술 스택
- **프로젝트명:** YouTube Batch Suite (`youtube tool`)
- **버전:** `1.1.0`
- **목적:** 유튜브 동영상/재생목록 URL 입력 한 번으로 동영상(`.mp4`), 오디오(`.mp3`), 포토샵 호환 고화질 썸네일(`_thumbnail.jpg`), 메타데이터(`_info.txt`) 4종을 일괄 저장하는 Avalonia UI 데스크톱 앱.
- **기술 스택:**
  - `.NET 10.0` (`net10.0`, `win-x64` Self-Contained SingleFile)
  - `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter` `12.0.5`
  - `CommunityToolkit.Mvvm` `8.4.2`
  - `YoutubeExplode` `6.6.2` (및 `YoutubeExplode.Converter` `6.6.2`)
  - `Xabe.FFmpeg.Downloader` `6.0.2` (최초 1회 `%LOCALAPPDATA%\YouTubeTool\ffmpeg\ffmpeg.exe` 자동 다운로드)
  - `SkiaSharp` `3.119.4` (썸네일 디코딩·유튜브 뮤직 단색 사이드바 자동 크롭·JPEG 100 인코딩)

---

## 2. 설계 의도 & 병목 개선 내역 (v1.1.0 리팩토링)

### 해결한 핵심 병목
1. **분석(미리보기) -> 다운로드 중복 네트워크/디코딩 제거 (`YouTubeService`)**
   - `GetVideoInfoAsync`, `GetPlaylistInfoAsync`, `GetThumbnailJpegAsync` 결과를 스레드 안전 캐시(`_cachedVideo`, `_cachedThumbnail`, `_cachedPlaylist`)에 보관합니다.
   - 직전에 분석한 영상을 바로 일괄 다운로드할 때 `Videos.GetAsync`, 썸네일 HTTP 다운로드, SkiaSharp 크롭/JPEG 재인코딩을 생략하고 `StreamManifest`만 조회합니다.
2. **이중 `GetManifestAsync`·이중 오디오 다운로드·순차 다운로드·불필요한 비디오 재인코딩 제거**
   - 기존에는 `YoutubeExplode.Videos.DownloadAsync`를 `.mp3`용과 `.mp4`용으로 각각 호출해 ① `GetManifestAsync` 2회, ② 오디오 스트림 2회 다운로드, ③ 비디오·오디오 순차 다운로드, ④ MP4 컨테이너 불일치 시 느린 비디오 재인코딩이 발생했습니다.
   - 개선 후에는 단일 `StreamManifest`에서 최적 오디오(`GetAudioOnlyStreams`)와 최적 비디오(`GetVideoOnlyStreams`, `MaxHeight` -> `Framerate` -> `Container.Mp4` -> `Bitrate` 순, 없을 경우 `GetMuxedStreams` 폴백)를 선택해 고유 `.tmp` 파일로 **병렬 다운로드**합니다.
   - 오디오 임시 파일 다운로드가 끝나자마자 비디오 다운로드를 기다리지 않고 `ffmpeg.exe`로 **MP3 변환(`libmp3lame -q:a 0`)을 즉시 병렬 시작**합니다.
   - 비디오+오디오 다운로드가 모두 끝나면 `ffmpeg.exe`로 **무손실 스트림 복사(`-c:v copy`, 오디오 MP4 컨테이너면 `-c:a copy` 아니면 `aac 192k`, `-movflags +faststart`) 병합**을 수행하며, 스트림 복사 실패 시에만 `libx264 -preset veryfast -crf 18`로 자동 폴백합니다.
3. **재생목록 메타데이터 병렬 조회**
   - `Playlists.GetAsync`와 `CollectPlaylistVideosAsync`를 `Task.WhenAll`로 동시 실행합니다.
   - 트랙 다운로드 시에도 `FetchAndCacheVideoInfoAsync`와 `Streams.GetManifestAsync`를 `Task.WhenAll`로 동시 실행합니다.
4. **썸네일 사이드바 감지 메모리 직접 접근 최적화 (`HasUniformSideBars`)**
   - 1280x720 등 썸네일에서 수만~10만 회 발생하던 `SKBitmap.GetPixel` P/Invoke 루프를 `SKPixmap.GetPixelSpan()` 4바이트 픽셀 스팬 직접 순회(`Bgra8888`/`Rgba8888`, 기타 포맷은 `GetPixel` 폴백)로 교체했습니다.
5. **UI 스레드 스로틀링 및 비트맵 누수 제거 (`MainWindowViewModel`)**
   - `System.Progress<double>` 대신 `UiProgressAdapter`를 사용해 `0.2%` 미만 변화량(0%·100% 제외)은 `Dispatcher.UIThread.Post` 자체를 생략하고 `_progressLock`으로 역전을 방지합니다.
   - `OnThumbnailImageChanging`에서 이전 `Bitmap`을 `Dispose()`해 비관리 메모리 누수를 막고, `MainWindowViewModel` 자체가 `IDisposable`을 구현해 창 종료 시 `HttpClient`·`CancellationTokenSource`·`Bitmap`을 정리합니다.

### 안정성·정합성 수정
- **원시 오디오 확장자 강제 변경 버그 제거:** MP3 변환 실패 시 `.webm`/`.mp4` 원본 파일을 `.mp3`로 `File.Move`해 깨진 파일이 남던 폴백을 제거했습니다.
- **FFmpeg 직접 프로세스 제어 (`RunFfmpegAsync`):** `ProcessStartInfo.ArgumentList`로 한글/공백/특수문자 경로 이스케이프 오류를 원천 차단하고, `CancellationToken` 등록으로 취소·창 종료 시 `process.Kill(entireProcessTree: true)`로 고아 프로세스를 즉시 정리합니다.
- **스레드 안전 FFmpeg 초기화 (`EnsureFFmpegAsync`):** `SemaphoreSlim` 락으로 동시 초기화를 방지합니다.
- **Windows 예약 파일명 방어 (`SanitizeFileName`):** 컴파일된 `InvalidFileNameRegex`와 `[GeneratedRegex]` `ReservedDeviceNameRegex`(`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`)로 잘못된 파일명을 방지합니다.
- **UX 강화:** URL 입력창 `Enter` 키 바인딩(`FetchInfoCommand`), 작업 중 입력 잠금, `[⏹ 작업 취소]` 버튼 및 `MainWindow.OnClosing` 정리 추가.

---

## 3. 시도했다가 실패했거나 피한 것들 (Dead Ends)
- **`YoutubeExplode.Converter`의 `DownloadAsync` 2회 호출 방식:** 내부적으로 매번 `StreamManifest`를 다시 받아오고 오디오를 2번 내려받으며 순차 실행하므로, `YoutubeExplode.Videos.Streams.DownloadAsync` + 직접 `ffmpeg.exe` 병합 파이프라인으로 대체했습니다.
- **FFmpeg 실패 시 원시 스트림 `File.Move(..., ".mp3")` 폴백:** WebM/Opus 또는 MP4/AAC 바이트를 `.mp3` 확장자로 옮기면 재생/편집기가 손상 파일로 인식하며, MP4 스트림일 경우 출력 파일과 의미가 충돌합니다.
- **`<DebugType>none</DebugType>`만으로 네이티브 `.pdb` 제거 시도:** `SkiaSharp.NativeAssets.Win32` 및 `HarfBuzzSharp.NativeAssets.Win32` NuGet 패키지가 `publish/` 폴더에 `libSkiaSharp.pdb`·`libHarfBuzzSharp.pdb`(약 100MB)를 복사합니다. `youtube tool.csproj`에 `<Target Name="StripPublishPdbs" AfterTargets="ComputeFilesToPublish">`를 추가해 `.pdb`를 `ResolvedFileToPublish`에서 제거했습니다.

---

## 4. 핵심 파일 구성
- `youtube tool/Models/VideoInfoModel.cs`: 영상/재생목록 메타데이터 모델 (`UploadDate`, `IsPlaylist` 포함)
- `youtube tool/Services/YouTubeService.cs`: YouTube 조회/캐시, 병렬 스트림 다운로드, FFmpeg MP3/MP4 처리, SkiaSharp 썸네일 크롭/인코딩
- `youtube tool/ViewModels/MainWindowViewModel.cs`: MVVM 상태·명령(`FetchInfoCommand`, `StartBatchDownloadCommand`, `CancelCommand`, `OpenFolderCommand`), `IDisposable`, UI 스레드 마샬링·진행률 스로틀링
- `youtube tool/MainWindow.axaml` / `MainWindow.axaml.cs`: 앱 UI, 창/헤더 아이콘(`/Assets/icon.ico`, `/Assets/icon.png`), 폴더 선택기, `OnClosing` 취소/정리
- `youtube tool/Assets/icon.png` & `icon.ico`: 1024x1024 앱 아이콘 및 멀티 해상도(16~256px) ICO

---

## 5. 빌드 및 배포

```powershell
# Release 빌드
dotnet build "youtube tool.slnx" -c Release

# 단일 독립 실행 파일(Self-Contained SingleFile) 배포
dotnet publish "youtube tool/youtube tool.csproj" -c Release -o publish
```

- 산출물 위치: `publish\youtube tool.exe` (네이티브 `.pdb` 없이 단독 실행 파일 1개만 생성)
