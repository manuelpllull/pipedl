# pipedl

> A self-hosted, headless Spotify playlist syncer designed to run on a **Raspberry Pi** alongside [Navidrome](https://www.navidrome.org/).  
> It scrapes your Spotify playlists via Playwright, diffs them against a local SQLite database, and downloads new tracks with [SpotDL](https://github.com/spotDL/spotify-downloader) — all on a cron schedule you control.

---

## ✨ Features

| Feature | Details |
|---|---|
| **Two-step scraping** | User profile → playlist URLs → track URLs, all via headless Chromium (Playwright) |
| **Infinite scroll support** | Handles Spotify's lazy-loaded playlist pages automatically |
| **Hash-diff engine** | SHA-1 checksum + `HashSet` diff keeps DB in sync: only inserts new tracks, archives removed ones |
| **SQLite + Dapper** | Lightweight persistence with WAL mode — safe for concurrent read/write on the Pi |
| **SpotDL integration** | Invokes `spotdl` as a subprocess; outputs to `/music/navidrome/{playlist}/{title}.{ext}` |
| **Navidrome scan trigger** | Optionally calls Navidrome's REST API to refresh the library after a download batch |
| **Quartz.NET scheduler** | Cron expression in `appsettings.json` — no separate task runner needed |
| **Semaphore concurrency** | Configurable parallelism (`MaxConcurrentPlaylists`) keeps CPU/RAM usage Pi-friendly |
| **Graceful Playwright fallback** | Falls back to HTTP+regex if the headless browser isn't available |

---

## 🏗️ Architecture

```
Pipedl/
├── src/
│   ├── Pipedl.Domain/           # Pure domain logic
│   │   └── Entities/
│   │       ├── Playlist.cs       # Entity + PlaylistHelpers (checksum, diff)
│   │       └── Track.cs
│   │
│   ├── Pipedl.Infrastructure/   # SQLite/Dapper, DB factory
│   │   └── DbConnectionFactory.cs
│   │
│   └── Pipedl.Worker/           # .NET Worker Service (the runnable host)
│       ├── Features/
│       │   ├── SyncUserPlaylists/   # Slice: scrape user → update DB
│       │   ├── SyncSinglePlaylist/  # Slice: diff a single playlist
│       │   └── DownloadSongs/       # Slice: invoke spotdl
│       ├── PlaylistScraper.cs    # Playwright two-step scraper
│       ├── SyncJob.cs            # Quartz IJob entry point
│       └── Program.cs            # Host bootstrap + RUN_ONCE mode
│
├── Dockerfile                    # Multi-stage build (SDK → runtime + Chromium + spotdl)
├── docker-compose.yml            # Compose file with named volumes & env-var config
├── .specs/                       # Architecture & domain specifications
└── Pipedl.sln
```

The project follows a **Vertical Slice** organization for the application layer, with a shared Clean Architecture core for Domain and Infrastructure.

---

## 🛠️ Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 10.0+ | `~/.dotnet/dotnet` if installed via script |
| [SpotDL](https://github.com/spotDL/spotify-downloader) | Latest | `pip install spotdl` |
| [Playwright browsers](https://playwright.dev/docs/intro) | Chromium | See install step below |
| Node.js | 18+ | Only needed to install Playwright browsers once |

> **Running with Docker?** You only need Docker — no .NET SDK, Node.js, or SpotDL required on the host. See [Docker](#-docker) below.

---

## 🚀 Quick Start

### 1. Clone

```bash
git clone https://github.com/your-username/syncify.git
cd syncify
```

### 2. Build

```bash
~/.dotnet/dotnet build Pipedl.sln -c Release
```

### 3. Install Playwright browsers (once)

```bash
# Inside the Worker output directory
cd src/Pipedl.Worker
npx playwright@1.59.0 install chromium
```

### 4. Run a one-shot scrape (no download)

```bash
# Prints all playlists and their tracks for a given Spotify user ID
env PLAYWRIGHT_BROWSERS_PATH=$HOME/Library/Caches/ms-playwright \
  RUN_ONCE=1 \
  ~/.dotnet/dotnet bin/Release/net10.0/Pipedl.Worker.dll mnupea
```

### 5. Run as a scheduled service

Edit `src/Pipedl.Worker/appsettings.json`:

```json
{
  "Pipedl": {
    "TargetUserId": "user",
    "CronExpression": "0 0 3 * * ?",
    "MaxConcurrentPlaylists": 2,
    "MusicOutputPath": "/music/navidrome",
    "NavidromeUrl": "http://localhost:4533",
    "NavidromeUser": "admin",
    "NavidromePassword": "your-password"
  }
}
```

Then start the worker:

```bash
env PLAYWRIGHT_BROWSERS_PATH=$HOME/Library/Caches/ms-playwright \
  ~/.dotnet/dotnet bin/Release/net10.0/Syncify.Worker.dll
```

---

## 🐳 Docker

Docker is the easiest deployment path — Chromium, SpotDL, and ffmpeg are all baked into the image.

### Build & run (scheduler mode)

```bash
# First time or after code changes
docker compose up -d --build

# Subsequent starts
docker compose up -d
```

### One-shot scrape (print playlists + tracks, no downloads)

```bash
docker compose run --rm -e RUN_ONCE=1 pipedl
```

### Tail logs

```bash
docker compose logs -f pipedl
```

### Configuration

All settings are passed as environment variables in `docker-compose.yml`:

| Variable | Default | Description |
|---|---|---|
| `TARGET_USER_ID` | `mnupea` | Spotify user ID to sync |
| `CRON_EXPRESSION` | `0 0 0 * * ?` | Quartz cron (midnight daily) |
| `DB_PATH` | `/data/pipedl.db` | SQLite file path inside the container |
| `MUSIC_OUTPUT_PATH` | `/music` | SpotDL download root inside the container |
| `RUN_ONCE` | *(unset)* | Set to `1` to scrape once and exit |
| `NAVIDROME_URL` | *(unset)* | Optional: trigger library scan after download |
| `NAVIDROME_USER` | *(unset)* | Navidrome admin username |
| `NAVIDROME_PASSWORD` | *(unset)* | Navidrome admin password |

### Volumes

| Volume | Mount | Purpose |
|---|---|---|
| `pipedl-data` | `/data` | SQLite database — persists across restarts |
| `pipedl-music` | `/music` | Downloaded music files |

You can bind-mount host directories instead of named volumes if you prefer direct access to the files:

```yaml
volumes:
  - /srv/pipedl/db:/data
  - /srv/music:/music
```

### Running alongside Navidrome (same Compose stack)

Uncomment the `networks` section in `docker-compose.yml` and point `NAVIDROME_URL` at the service name:

```yaml
services:
  navidrome:
    image: deluan/navidrome:latest
    volumes:
  - pipedl-music:/music:ro   # shared read-only
    networks:
      - media

  syncify:
    # ...
    environment:
      NAVIDROME_URL: http://navidrome:4533
    networks:
      - media

networks:
  media:
```

### Raspberry Pi (ARM64)

The base images (`mcr.microsoft.com/dotnet/runtime:10.0` and `mcr.microsoft.com/dotnet/sdk:10.0`) ship multi-arch manifests that include `linux/arm64`. Docker on a Pi 4/5 will pull the correct layer automatically:

```bash
docker compose up -d --build
```

No cross-compilation or separate Dockerfile needed.

---

```sql
CREATE TABLE Playlists (
    SpotifyId    TEXT PRIMARY KEY,
    Name         TEXT,
    Owner        TEXT,
    LastSyncedAt TEXT,
    Checksum     TEXT,
    IsActive     INTEGER DEFAULT 1
);

CREATE TABLE Tracks (
    SpotifyId  TEXT PRIMARY KEY,
    PlaylistId TEXT REFERENCES Playlists(SpotifyId),
    Title      TEXT,
    Artist     TEXT,
    Album      TEXT,
    Duration   INTEGER,
    Downloaded INTEGER DEFAULT 0
);

CREATE TABLE SyncLogs (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedAt   TEXT,
    FinishedAt  TEXT,
    TracksAdded INTEGER,
    TracksRemoved INTEGER,
    Status      TEXT
);
```

SQLite is configured with **WAL (Write-Ahead Logging)** mode so Navidrome reads and Syncify writes never block each other.

---

## ⚙️ How the Diff Engine Works

```
DB (existing)    Scraped (new)
   [A, B, C]       [B, C, D, E]
        │                │
        └──── Diff ───────┘
              │       │
           INSERT   DELETE/ARCHIVE
           [D, E]      [A]
```

1. Load all `SpotifyId`s for a playlist from SQLite into a `HashSet<string>`.
2. Stream scraped track IDs from Playwright.
3. `scraped.ExceptWith(existing)` → tracks to **INSERT**.
4. `existing.ExceptWith(scraped)` → tracks to **DELETE** (set `IsActive = 0`).
5. Recompute playlist `Checksum` (SHA-1 of sorted track IDs).
6. Bulk `ExecuteAsync` via Dapper.

---

## 📁 Download Pipeline

```
New tracks in DB
      │
      ▼
DownloadHandler
      │
      ├─ spotdl https://open.spotify.com/track/{id}
      │         --output "/music/navidrome/{playlist}/{title}.{ext}"
      │
      └─ On exit code 0:
            Mark track Downloaded = 1 in DB
            Trigger Navidrome library scan (optional)
```

---

## 🧪 Running Tests

```bash
~/.dotnet/dotnet test src/Pipedl.Tests/Pipedl.Tests.csproj --logger "console;verbosity=detailed"
```

Current test coverage:
- `PlaylistHelpers.ComputeChecksum` — deterministic, order-independent
- `PlaylistHelpers.DiffTracks` — correct insert/delete sets
- Edge cases: null input, empty collections

---

## 🔧 Configuration Reference

| Key | Default | Description |
|---|---|---|
| `TargetUserId` | *(required)* | Spotify username to sync |
| `CronExpression` | `0 0 3 * * ?` | Quartz cron (3 AM daily) |
| `MaxConcurrentPlaylists` | `2` | Semaphore limit for Pi |
| `MusicOutputPath` | `/music/navidrome` | SpotDL output root |
| `NavidromeUrl` | *(optional)* | Trigger library scan after download |
| `NavidromeUser` | *(optional)* | Navidrome admin username |
| `NavidromePassword` | *(optional)* | Navidrome admin password |

---

## 🖥️ Raspberry Pi Deployment

```bash
# Publish a self-contained ARM64 binary
~/.dotnet/dotnet publish src/Pipedl.Worker/Pipedl.Worker.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -o ./publish

# Copy to Pi
scp -r ./publish pi@raspberrypi:/opt/pipedl

# On the Pi — create a systemd service
sudo nano /etc/systemd/system/pipedl.service
```

```ini
[Unit]
Description=Pipedl Spotify Playlist Syncer
After=network.target

[Service]
WorkingDirectory=/opt/pipedl
ExecStart=/opt/pipedl/Pipedl.Worker
Restart=always
RestartSec=10
Environment=PLAYWRIGHT_BROWSERS_PATH=/home/pi/.cache/ms-playwright
User=pi

[Install]
WantedBy=multi-user.target
```

```bash
sudp systemctl enable pipedl
sudo systemctl start pipedl
sudo journalctl -u pipedl -f
```

---

## 🤝 Contributing

1. Fork the repo and create a feature branch: `git checkout -b feat/my-feature`
2. Make your changes, add tests where appropriate.
3. Run `dotnet test` and ensure all pass.
4. Submit a Pull Request with a clear description.

---

## 📄 License

```
MIT License

Copyright (c) 2026 Manuel

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
