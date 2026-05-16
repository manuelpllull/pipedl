#!/usr/bin/env bash
set -euo pipefail

# Installs Docker & Compose (if missing), clones repo to /opt/pipedl and starts compose
REPO_URL="https://github.com/manuelpllull/pipedl.git"
INSTALL_DIR="/opt/pipedl"

echo "Updating apt and installing prerequisites..."
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg lsb-release

if ! command -v docker >/dev/null 2>&1; then
  echo "Installing Docker..."
  curl -fsSL https://get.docker.com | sh
  sudo usermod -aG docker "$(whoami)"
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "Installing docker compose plugin..."
  sudo apt-get install -y docker-compose-plugin
fi

echo "Cloning repository to ${INSTALL_DIR}..."
sudo mkdir -p "${INSTALL_DIR}"
sudo chown "$(whoami)":"$(whoami)" "${INSTALL_DIR}"
if [ -d "${INSTALL_DIR}/.git" ]; then
  echo "Repository already cloned; pulling latest..."
  (cd "${INSTALL_DIR}" && git pull)
else
  git clone "${REPO_URL}" "${INSTALL_DIR}"
fi

echo "Starting docker compose (build) as detached service..."
cd "${INSTALL_DIR}"
docker compose up -d --build

echo "Install complete. Make sure you log out and back in to apply docker group membership if needed."
