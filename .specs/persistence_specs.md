# SQLite & Dapper Implementation
## Database Schema (SQLite):

```sql
CREATE TABLE Playlists (
    spotify_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    owner TEXT NOT NULL,
    last_synced_at TEXT,
    checksum TEXT,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE Tracks (
    spotify_id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    artist TEXT,
    album TEXT,
    duration INTEGER NOT NULL DEFAULT 0,
    downloaded INTEGER NOT NULL DEFAULT 0,
    downloaded_at TEXT,
    last_seen_at TEXT
);

CREATE TABLE playlist_tracks (
    playlist_id TEXT NOT NULL,
    track_id TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    PRIMARY KEY (playlist_id, track_id),
    FOREIGN KEY (playlist_id) REFERENCES playlists(spotify_id),
    FOREIGN KEY (track_id) REFERENCES tracks(spotify_id)
);
```

## Dapper Logic:

- Use `SqliteConnection` with normalized connection strings (unsupported keys removed).
- Ensure SQLite file and parent directory are created automatically for file-based databases.
- Relative SQLite paths are resolved from app base directory.

Connection configuration precedence used by the worker host:
1. `ConnectionStrings__Default`
2. `DB_PATH` (wrapped as `Data Source=...`)
3. fallback `pipedl.db`

The "Inference" engine will use `Dictionary<string, Track>` to map existing DB records for O(1) lookups during the diffing phase.