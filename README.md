# YouTube Batch Suite (4-in-1 Downloader)

<div align="center">

<img src="youtube tool/Assets/icon.png" width="128" height="128" alt="YouTube Batch Suite icon" />
<br/><br/>

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-12.0.5-8B5CF6?logo=avalonia&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows&logoColor=white)
![Version](https://img.shields.io/badge/Version-1.1.0-E50914)

**유튜브 링크 입력 한 번으로 동영상(`.mp4`), 오디오(`.mp3`), 포토샵 호환 고화질 썸네일(`.jpg`), 상세 메타데이터(`.txt`) 4종을 한 번에 저장하는 Windows 데스크톱 도구**

</div>

---

## ✨ 핵심 기능

- **4-in-1 일괄 저장**:
  - **동영상 (`.mp4`)**: 최고 화질 비디오 + 오디오 스트림을 병렬 다운로드 후 무손실 스트림 복사(`-c:v copy`, `-movflags +faststart`)로 초고속 병합
  - **오디오 (`.mp3`)**: 오디오 스트림 다운로드 완료 즉시 고음질 MP3(`libmp3lame -q:a 0`) 변환을 병렬 시작
  - **썸네일 (`_thumbnail.jpg`)**: WebP/JPEG 원본을 포토샵 호환 JPEG(품질 100)로 저장하며, YouTube Music 음원처럼 양옆 단색 바가 있는 경우 정사각형 앨범 아트로 자동 크롭
  - **메타데이터 (`_info.txt`)**: 제목, 원본 제목, 채널, 게시일, 재생시간, URL, 전체 설명을 UTF-8 텍스트로 저장
- **재생목록 일괄 지원**: 재생목록 URL을 입력하면 재생목록 이름의 하위 폴더를 만들고 모든 곡을 중복 방지 파일명으로 순차 처리하며, 실패한 곡은 `실패한 곡.txt`에 기록합니다.
- **고속 병렬 파이프라인 & 캐시**: 분석 단계에서 불러온 영상 정보와 변환된 썸네일 바이트를 캐시해 다운로드 시 재요청을 생략하고, 오디오·비디오 스트림을 동시에 다운로드합니다.
- **작업 취소 및 안전한 종료**: 실행 중 언제든 `[⏹ 작업 취소]` 버튼으로 중단할 수 있으며, 창을 닫을 때 실행 중인 `ffmpeg.exe` 프로세스 트리와 임시 파일을 즉시 정리합니다.
- **한국어 로케일 우선**: `ko-KR` / `gl=KR` 쿠키 및 헤더를 적용해 한국어 번역 제목이 있는 영상도 우선 한국어 제목으로 불러옵니다.
- **FFmpeg 자동 준비**: 첫 실행 시 `%LOCALAPPDATA%\YouTubeTool\ffmpeg\` 경로에 FFmpeg 바이너리가 없으면 자동으로 1회 다운로드합니다.

---

## 🚀 빠른 시작

1. `publish\youtube tool.exe` 단독 실행 파일을 실행합니다. (.NET 10 런타임이 내장된 Self-Contained 실행 파일이므로 별도 런타임 설치가 필요하지 않습니다.)
2. 유튜브 동영상 또는 재생목록 URL을 입력하고 `Enter` 키를 누르거나 `[🔍 분석하기]`를 클릭합니다.
3. 필요 시 저장 파일명(제목)과 저장 경로 폴더를 확인·수정한 뒤 `[⚡ 4-in-1 일괄 다운로드 시작]`을 누릅니다.

---

## 🛠️ 소스 빌드 및 EXE 패키징

### 사전 요구사항
- Windows 10/11 x64
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)

### 빌드 명령

```powershell
# Release 빌드
dotnet build "youtube tool.slnx" -c Release

# 단독 실행 파일(SingleFile Self-Contained) 패키징 (.pdb 제외)
dotnet publish "youtube tool/youtube tool.csproj" -c Release -o publish
```

빌드가 완료되면 `publish\youtube tool.exe` 파일 하나만 깔끔하게 생성됩니다.

---

## 📦 주요 사용 라이브러리

- [Avalonia UI](https://avaloniaui.net/) (`12.0.5`)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (`8.4.2`)
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) (`6.6.2`)
- [SkiaSharp](https://github.com/mono/SkiaSharp) (`3.119.4`)
- [Xabe.FFmpeg.Downloader](https://github.com/tomaszzmuda/Xabe.FFmpeg) (`6.0.2`)
