# GrowCam

Lightweight grow tent monitor: a TP-Link Tapo camera takes photos, Claude looks
at them once a day, and a large-print web app shows the result – built to be
easy to read and use for someone with poor eyesight.

**Features**

- Daily AI check (deficiencies, pests, mould, stress) with plain-language advice
- Grow diary: milestones (sprouted, vegetative, flowering) are detected by the AI and confirmed with one tap
- New-grow wizard with automatic strain lookup on the web
- Camera control (pan/tilt, saved views, fixed "analysis view")
- Weekly report every Sunday
- Archive of finished grows
- Self-updating from release tags of this private repo
- Font-size switch, high contrast, big buttons, works without JavaScript

**Lightweight by design:** 4 small dependencies (Starlette, Uvicorn, Jinja2,
python-multipart) + optional `pytapo`, SQLite from the standard library, no
build step, no frontend framework, ~25 lines of JavaScript.

## Try it (demo mode, no hardware)

```bash
python -m venv .venv
.venv\Scripts\activate            # Linux/macOS: source .venv/bin/activate
pip install -r requirements.txt
python -m growcam
```

Open <http://localhost:8080>. Without camera and API key the app uses a demo
image and sample AI answers.

## Setup on the Windows PC

### 1. Install the tools (once)

```powershell
winget install Python.Python.3.12 Git.Git Gyan.FFmpeg NSSM.NSSM
```

Open a **new** PowerShell afterwards so the tools are on `PATH`.

### 2. Deploy key (read-only access to this private repo)

```powershell
mkdir C:\GrowCam-key
ssh-keygen -t ed25519 -C growcam-pc -f C:\GrowCam-key\id_ed25519
# press Enter twice (no passphrase)
Get-Content C:\GrowCam-key\id_ed25519.pub
```

GitHub → repo **Settings → Deploy keys → Add deploy key**, paste the public key,
leave *Allow write access* **unchecked**.

### 3. Clone

```powershell
cd C:\
git -c core.sshCommand="ssh -i C:/GrowCam-key/id_ed25519" clone git@github.com:502Proxy/GrowCam.git GrowCam
move C:\GrowCam-key C:\GrowCam\.deploy     # git-ignored, used by the updater
cd C:\GrowCam
```

### 4. Configure

```powershell
copy .env.example .env
notepad .env
```

| Setting | Where to get it |
|---|---|
| `TAPO_HOST` | Camera IP – give it a fixed IP in the router |
| `TAPO_RTSP_USER` / `TAPO_RTSP_PASSWORD` | Tapo app → camera → Settings → Advanced → **Camera account** |
| `TAPO_CLOUD_PASSWORD` | Password of your Tapo app login (only for moving the camera) |
| `ANTHROPIC_API_KEY` | <https://platform.claude.com> → API keys |

In the Tapo app also turn **night vision off** (IR images are useless for the
colour analysis) and optionally the status LED.

### 5. Install the service

In an **admin** PowerShell:

```powershell
cd C:\GrowCam
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This creates `.venv`, installs a Windows service *GrowCam* (starts with
Windows), a scheduled task *GrowCam Updater* (every 15 min) and a firewall
rule for the home network. The script prints the address to open on phone/tablet.

Then: open the app → **Kamera** → aim the camera → **"Diese Ansicht für Fotos merken"**.

## Releasing an update

The PC only installs **tagged releases**, so you can push to `main` freely:

```bash
git tag v0.2.0
git push origin v0.2.0
```

Within 15 minutes the updater fetches the tag, installs requirements and
restarts the service (rolls back if installing fails). With *"Updates
automatisch installieren"* switched off, the app shows a button instead.

## Architecture

Clean architecture – dependencies point inwards only:

```
growcam/
  domain/models.py           entities + rules (phases, milestones, expected dates) – no I/O
  application/ports.py       interfaces (Camera, PlantAnalyzer, repositories, …)
  application/services.py    use cases: GrowService, MonitoringService, CameraService,
                             ReportService, SeedService
  infrastructure/            adapters: SQLite, file image store, Tapo (ffmpeg + pytapo),
                             Claude (urllib), demo fakes, updater bridge
  web/                       Starlette routes + Jinja templates (server-rendered)
  scheduler.py               asyncio loop: hourly photos, daily check, Sunday report
  container.py               composition root (the only place that picks adapters)
scripts/update.py            standalone self-updater (stdlib only)
scripts/install.ps1          Windows installer
```

Design patterns used: Ports & Adapters (camera/AI/storage are swappable),
Repository, Composition Root / dependency injection, Strategy (demo vs. real
adapters), Post/Redirect/Get in the web layer.

Data lives in `data/` (SQLite DB, photos, logs) and is never touched by updates.

## AI usage and cost

| Job | When | Model input |
|---|---|---|
| Plant check | daily at `ANALYSIS_TIME` + on demand | today's photo + previous check photo |
| Weekly report | Sundays at `REPORT_TIME` | one photo per day |
| Strain lookup | new-grow wizard | web search (cached per strain) |

Structured results come from forced tool calls (JSON schema), so parsing never
depends on free text. Expect a few cents per day.

## Security

The app has no login – keep it inside the home network. **Do not** forward the
port in the router. Secrets live only in `.env` and `.deploy/` (both git-ignored).

## Development

```bash
pip install -r requirements-dev.txt
pytest
```
