# GrowCam

Desktop app that watches a grow tent with a TP-Link Tapo camera, lets Claude
check the plants once a day and shows the result in a large-print, high-contrast
window – built to be easy to read and use for someone with poor eyesight.

Developed and tested on macOS, deployed on Windows as a normal installed program
(Start menu entry, starts with Windows, keeps running in the tray).

**Features**

- Daily AI check (deficiencies, pests, mould, stress) with plain-language advice
- Grow diary: milestones (sprouted, vegetative, flowering) are detected by the AI and confirmed with one tap
- New-grow wizard with automatic strain lookup on the web
- Camera control: pan/tilt, saved views, fixed "analysis view" (ONVIF)
- Weekly report every Sunday, archive of finished grows
- Font-size switch (A / A+ / A++), big buttons, closing the window keeps it running in the tray
- Self-updates from GitHub releases of this private repo
- Demo mode without camera and API key

The previous Python/web version lives in the branch `python-legacy`.

## Develop on the Mac

```bash
brew install --cask dotnet-sdk   # .NET 10 SDK
brew install ffmpeg              # only needed with a real camera
dotnet run --project src/GrowCam.App
dotnet test GrowCam.slnx
```

Without camera and API key the app runs in demo mode. Data lives in
`~/Library/Application Support/GrowCamData` (override with `GROWCAM_DATA_DIR`).

Building the Windows program on the Mac (for a quick test on the PC):

```bash
dotnet publish src/GrowCam.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/win
```

Copy `out/win/GrowCam.exe` plus an `ffmpeg.exe` next to it to the PC.

## Releases and installation on Windows

GitHub Actions builds every push. A **tag** creates a release with the installer:

```bash
git tag v0.2.0
git push origin v0.2.0
```

The release contains `GrowCam-win-Setup.exe` (installer, includes `ffmpeg.exe`)
and a portable zip. Install once on the PC with the setup – afterwards the app
updates itself.

### Setup on the PC (once)

1. Run `GrowCam-win-Setup.exe`.
2. In GrowCam → **Einstellungen**:
   - **Kamera**: IP of the camera (give it a fixed IP in the router) and the
     *camera account* from the Tapo app (Camera settings → Advanced → Camera account).
   - **KI**: API key from <https://platform.claude.com>.
   - **GitHub-Zugang**: a fine-grained personal access token with *read-only*
     access to *Contents* of this repo (needed because the repo is private).
   - Tick **"GrowCam mit Windows starten"**, then **Speichern**.
3. In the Tapo app: turn **night vision off** (IR images are useless for colour
   analysis); ONVIF/RTSP must be allowed for the camera account.
4. **Kamera** page → aim the camera → **"Diese Ansicht für Fotos merken"**.

Updates are checked every 6 hours (and at start); with *"Updates automatisch
installieren"* they install and restart on their own.

## Architecture

Clean architecture – dependencies point inwards only:

```
src/
  GrowCam.Domain          entities + rules (phases, milestones, expected dates) – no dependencies
  GrowCam.Application     ports (ICamera, IPlantAnalyzer, repositories …) and use cases:
                          GrowService, MonitoringService, CameraService, ReportService,
                          SeedService, ScheduleService
  GrowCam.Infrastructure  adapters: SQLite (Microsoft.Data.Sqlite), file image store,
                          Tapo camera (ffmpeg for RTSP snapshots + ONVIF for pan/tilt),
                          Claude via HttpClient, demo fakes, JSON settings, autostart
  GrowCam.App             Avalonia UI (MVVM with CommunityToolkit.Mvvm), tray icon,
                          composition root, background scheduler, Velopack updater
tests/GrowCam.Tests       xUnit tests for domain, services and AI adapters
```

Patterns: Ports & Adapters, Repository, Composition Root, Proxy + Strategy
(`ConfiguredCamera` / `ConfiguredAi` switch between real and demo adapters when
settings change – no restart needed), MVVM, convention-based ViewLocator.

**Dependencies (lightweight):** Avalonia, CommunityToolkit.Mvvm,
Microsoft.Data.Sqlite, Velopack. No ORM, no AI SDK, no DI container.

## AI usage and cost

| Job | When | Input |
|---|---|---|
| Plant check | daily at the configured time + on demand | today's photo + previous check photo |
| Weekly report | Sundays at the configured time | one photo per day |
| Strain lookup | new-grow wizard | web search (cached per strain) |

Results come back as JSON via forced tool calls, so nothing depends on parsing
free text. Expect a few cents per day.

## Notes

- Everything runs locally on the PC; nothing is reachable from outside.
- Secrets (camera password, API key, GitHub token) are stored in
  `settings.json` in the data folder on the PC, never in the repo.
