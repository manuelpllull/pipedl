# SpotDL & Navidrome Integration

## Pipeline

1. Detect pending tracks from active playlists where `tracks.downloaded = 0`.
2. Download each pending track with SpotDL:
    - `spotdl download <spotify-track-url> --output <music-output-path>`
3. On successful download:
    - mark `downloaded = 1`
    - set `downloaded_at`
    - update `title`, `artist`, `album`, `duration` from SpotDL metadata (`spotdl save ... --save-file -`)
4. Metadata backfill pass:
    - for already-downloaded active tracks with placeholder/missing metadata (`title = spotify_id`, empty artist/album, or `duration = 0`), call `spotdl save` and update DB fields without re-downloading audio files.

## SpotDL Binary Resolution

Runtime resolves SpotDL executable in this order:
1. `SPOTDL_PATH`
2. nearest `./.venv-spotdl/bin/spotdl` walking upward from current working directory
3. `spotdl` from `PATH`

## Navidrome

Navidrome can monitor the output folder directly. An explicit API-triggered scan remains optional.