#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="/opt/pipedl"
cd "${INSTALL_DIR}"

echo "Running one-shot (RUN_ONCE=1) via docker compose..."
docker compose run --rm -e RUN_ONCE=1 pipedl

echo "Done. To view logs: docker compose logs -f pipedl"
