# Darkroom

Windows 바탕화면 위젯. 위젯은 HTML/CSS/JS로 만들고(WebView2), 호스트(C# .NET 8)가 투명 창, 바탕화면 고정, 데이터, 테마를 맡는다.

## 설치

[Releases](https://github.com/chosikdongmul/darkroom/releases)에서 받는다.

- `Darkroom-Setup-<버전>.exe` — 설치 파일. 관리자 권한 없이 현재 사용자에게 설치된다. 새 버전이 나오면 앱 안에서 업데이트할 수 있다.
- `Darkroom.exe` — 설치 없이 실행하는 단일 파일.

Windows 10/11 x64, Microsoft Edge WebView2 런타임(Windows 11은 기본 포함)이 필요하다. 코드 서명이 없어 처음 실행할 때 SmartScreen 경고가 뜰 수 있다.

## 기능

- 바탕화면 아이콘 위에 고정(Win+D에도 유지), 드래그와 스냅
- 색 테마 8가지, 글꼴 조합 7가지(모두 내장)
- 사진 폴더로 Windows 배경화면 바꾸기
- 위젯: 노출값 시계(지금 해 위치·날씨에 맞는 노출), 골든아워, 필름 카운터, 히스토그램(배경화면 기준), 필름 프레임, 시스템, 배터리, 해, 날씨, 사진, 지금 재생 중

트레이 아이콘을 누르면 Manager가 열린다. 설정과 로그는 `%APPDATA%\Darkroom`에 있다.

## 빌드

```powershell
dotnet build Darkroom.sln
.\src\Darkroom.App\bin\Debug\net8.0-windows10.0.19041.0\Darkroom.exe
dotnet test tests/Darkroom.Core.Tests

.\build\publish.ps1          # dist\Darkroom.exe
.\build\make-installer.ps1   # dist\Darkroom-Setup-<버전>.exe (Inno Setup 6 필요)
```

개발 빌드는 저장소의 `widgets/`, `sdk/`, `themes/`를 바로 읽고, 파일을 저장하면 해당 위젯만 다시 불러온다. 다른 폴더를 쓰려면 `--root <폴더>`. Debug 빌드에서는 위젯에서 F12로 DevTools를 연다.

| 경로 | |
|---|---|
| `src/Darkroom.Core` | Data Bus, Provider 실행기, 모델(widget.json, 테마, 레이아웃, 상태) |
| `src/Darkroom.Providers` | 내장 데이터 (time, system, battery, sun, weather, photos, media) |
| `src/Darkroom.App` | 위젯 창, WebView2, 트레이, Manager, 업데이트 |
| `sdk/` | 위젯에 주입되는 `darkroom.js`, 내장 글꼴 |
| `widgets/`, `themes/`, `layouts/` | 위젯(폴더 하나에 위젯 하나), 테마, 레이아웃 |

## 위젯 만들기

`widgets/my-widget/widget.json`:

```json
{
  "id": "me.my-widget",
  "name": "My Widget",
  "version": "0.1.0",
  "entry": "index.html",
  "size": { "width": 360, "height": 140, "inset": 12 },
  "windowMode": "desktop",
  "subscribe": ["time.now"],
  "permissions": [],
  "settings": {
    "big": { "type": "bool", "default": false, "label": "크게" }
  }
}
```

- `size`: 내장 위젯은 폭 360, `inset` 12(그림자용 투명 여백)로 맞춰 쌓았을 때 가장자리가 나란하다.
- `windowMode`: `desktop` · `bottom` · `normal` · `topmost`
- `subscribe`: 받을 데이터 키. `system.*`처럼 네임스페이스 단위도 된다.
- `permissions`: `network`가 없으면 외부 요청(fetch, WebSocket, WebRTC)이 막힌다. `launch`가 없으면 `open` Action으로 프로그램을 열 수 없다.
- 설정 type: `bool` · `number` · `string` · `choice`. Manager가 설정 화면을 만들어 준다.

```js
const off = darkroom.subscribe("time.now", (v) => { /* v.epochMs */ });
darkroom.settings;
darkroom.onSettingsChanged((s) => {});
darkroom.theme;                 // CSS에서는 var(--dr-bg) 등
darkroom.onThemeChanged((t) => {});
darkroom.action({ type: "open", target: "C:\\Photos" });   // open | toggleWidget | loadLayout | setSetting | media
darkroom.setSetting("big", true);
```

`data-drag` 속성이 있는 요소를 끌면 위젯이 움직인다(Ctrl+드래그는 어디서나, Shift는 스냅 없이).

CSS 변수: `--dr-bg` `--dr-fg` `--dr-muted` `--dr-faint` `--dr-accent` `--dr-inset` `--dr-plot` `--dr-gold` `--dr-blue` `--dr-radius` `--dr-shadow`, 글꼴 `--dr-font-ui` `--dr-font-num` `--dr-num-weight` `--dr-num-tracking` `--dr-num-scale` `--dr-num-style` `--dr-num-variation`.

| 키 | 값 |
|---|---|
| `time.now` | `{ epochMs, iso, offsetMinutes }` |
| `system.cpu` `system.ram` `system.net` `system.disks` | 사용률, 메모리, 네트워크 속도, 디스크 |
| `battery.status` | `{ hasBattery, percent, charging, pluggedIn, remainingMinutes, saver }` |
| `sun.today` `sun.tomorrow` `sun.position` `sun.phase` | 일출·일몰·골든/블루아워, 고도·방위 |
| `weather.current` `weather.daily` | Open-Meteo (`network` 권한 필요) |
| `wallpaper.current` `wallpaper.histogram` | 지금 배경화면의 이름·EXIF·히스토그램 |
| `photos.current` `photos.histogram` `photos.stats` | 사진 폴더의 사진, 히스토그램, 촬영 통계 |
| `media.nowPlaying` `media.artwork` | 재생 중인 미디어 |

내장 글꼴은 모두 SIL Open Font License 1.1이다(`sdk/fonts/LICENSES.md`).
