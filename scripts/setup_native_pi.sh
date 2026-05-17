#!/bin/bash
set -e

echo "=== Pipedl Native Installation for Raspberry Pi ==="

# Cleanup stale Microsoft apt repository entries from prior attempts.
# These can fail on newer Debian sqv policy checks and block apt update.
sudo rm -f /etc/apt/sources.list.d/microsoft-prod.list
sudo rm -f /etc/apt/sources.list.d/microsoft*.list
sudo sed -i '/packages\.microsoft\.com/d' /etc/apt/sources.list 2>/dev/null || true

# 1. Install prerequisites
echo ">> Installing .NET, Python, and Playwright dependencies..."
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
    curl \
    wget \
    ca-certificates \
    gnupg \
    python3 \
    python3-pip \
    python3-venv \
    ffmpeg \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    libpango-1.0-0 \
    libcairo2

# Install .NET 10 SDK (direct official tarball; avoids apt sqv/SHA1 policy issues)
echo ">> Installing .NET 10 SDK..."
DOTNET_VERSION="10.0.100"
DOTNET_TARBALL_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_VERSION}/dotnet-sdk-${DOTNET_VERSION}-linux-arm64.tar.gz"
mkdir -p "$HOME/.dotnet"
wget -O "$HOME/dotnet-sdk.tar.gz" "$DOTNET_TARBALL_URL"
tar -xzf "$HOME/dotnet-sdk.tar.gz" -C "$HOME/.dotnet"
rm -f "$HOME/dotnet-sdk.tar.gz"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"

# Install Node.js (needed for Playwright setup)
if ! command -v node >/dev/null; then
    echo ">> Installing Node.js..."
    curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
    sudo apt-get install -y nodejs
fi

# 2. Build the Pipedl application natively
echo ">> Building the .NET application..."
cd ~/scripts/pipedl
dotnet publish src/Pipedl.Worker/Pipedl.Worker.csproj -c Release -o ./publish /p:UseAppHost=true

# 3. Setup Python Virtual Environment for spotdl
echo ">> Setting up Python virtual environment for spotdl..."
python3 -m venv ~/scripts/spotdl-venv
~/scripts/spotdl-venv/bin/pip install --no-cache-dir spotdl

# 4. Setup Playwright locally
echo ">> Downloading Playwright Chromium browser..."
export PLAYWRIGHT_BROWSERS_PATH=$HOME/.cache/ms-playwright
npx --yes playwright@1.59.0 install --with-deps chromium

# 5. Create the wrapper script
echo ">> Creating Pipedl run script..."
cat << 'EOF' > ~/scripts/pipedl/run_pipedl.sh
#!/bin/bash
set -e

# Paths configuration
export DB_PATH=$HOME/scripts/pipedl/data/pipedl.db
export MUSIC_OUTPUT_PATH=/music
export TARGET_USER_ID=mnupea
export RUN_ONCE=1

# Include spotdl and dotnet in path
export PATH="$HOME/scripts/spotdl-venv/bin:$HOME/.dotnet:/usr/bin:$PATH"
export PLAYWRIGHT_BROWSERS_PATH=$HOME/.cache/ms-playwright

# Resolve publish directory (supports explicit -o ./publish or default csproj publish path)
if [ -d "$HOME/scripts/pipedl/publish" ]; then
    PUBLISH_DIR="$HOME/scripts/pipedl/publish"
elif [ -d "$HOME/scripts/pipedl/src/Pipedl.Worker/bin/Release/net10.0/publish" ]; then
    PUBLISH_DIR="$HOME/scripts/pipedl/src/Pipedl.Worker/bin/Release/net10.0/publish"
else
    echo "ERROR: Publish directory not found. Run dotnet publish first."
    exit 1
fi

cd "$PUBLISH_DIR"

if [ -x "./Pipedl.Worker" ]; then
    ./Pipedl.Worker
elif [ -f "./Pipedl.Worker.dll" ]; then
    dotnet ./Pipedl.Worker.dll
else
    echo "ERROR: Pipedl.Worker binary/dll not found in $PUBLISH_DIR"
    exit 1
fi
EOF

chmod +x ~/scripts/pipedl/run_pipedl.sh

# 6. Ensure data directory exists
mkdir -p ~/scripts/pipedl/data

# 7. Setup Systemd Timer (cron equivalent)
echo ">> Configuring systemd timer to daily at 1 AM..."
SCRIPT_PATH="$HOME/scripts/pipedl/run_pipedl.sh"
WORKDIR_PATH="$HOME/scripts/pipedl"

sudo bash -c 'cat << EOF > /etc/systemd/system/pipedl-native.service
[Unit]
Description=Pipedl Native Worker
After=network.target

[Service]
Type=oneshot
User='"$USER"'
ExecStart='"$SCRIPT_PATH"'
WorkingDirectory='"$WORKDIR_PATH"'
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF'

sudo bash -c 'cat << EOF > /etc/systemd/system/pipedl-native.timer
[Unit]
Description=Run Pipedl daily at 1 AM

[Timer]
OnCalendar=*-*-* 01:00:00
Persistent=true

[Install]
WantedBy=timers.target
EOF'

sudo systemctl daemon-reload
sudo systemctl enable --now pipedl-native.timer

echo "=== Installation Complete! ==="
echo "Pipedl will now run natively every day at 1 AM."
echo "You can trigger a manual run with:"
echo "  ~/scripts/pipedl/run_pipedl.sh"
