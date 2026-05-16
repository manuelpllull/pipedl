#!/usr/bin/env bash
set -euo pipefail

# Try to detect a running Navidrome service and check music mount
NAV_URL=${NAVIDROME_URL:-http://localhost:4533}

echo "Checking Navidrome at ${NAV_URL}"
if curl -fsS "${NAV_URL}/api/version" >/dev/null 2>&1; then
  echo "Navidrome appears reachable at ${NAV_URL}"
  curl -fsS "${NAV_URL}/api/version" | jq -r '.' || true
else
  echo "Navidrome not reachable at ${NAV_URL} — check service or set NAVIDROME_URL env to the correct address"
fi

# Check music mount path from docker-compose or default
if [ -f docker-compose.yml ]; then
  echo "Found docker-compose.yml — searching for music volume mapping..."
  grep -n "pipedl-music" docker-compose.yml || true
fi

echo "Check /music path on host (if running navidrome locally):"
if [ -d /music ] || [ -d /srv/music ] || [ -d /var/media ]; then
  echo "Potential music directories present:"; ls -ld /music /srv/music /var/media 2>/dev/null || true
else
  echo "No obvious music mount found at /music, /srv/music, or /var/media. Check your compose volumes or Navidrome config."
fi
