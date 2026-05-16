#!/bin/bash
set -e

echo "=== Pipedl Native Installation for Raspberry Pi ==="

# 1. Install prerequisites
echo ">> Installing .NET, Python, and Playwright dependencies..."
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
    curl \
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

# Install .NET 10 SDK
echo ">> Installing .NET 10 SDK..."
# Create an installation directory on the large partition
mkdir -p $HOME/.dotnet
export DOTNET_ROOT=$HOME/.dotnet
export DOTNET_INSTALL_DIR=$HOME/.dotnet
# Force the installer to write temp files to the home directory instead of /tmp 
export TMPDIR=$HOME/tmp
mkdir -p $TMPDIR
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir $HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet

# Install Node.js (needed for Playwright setup)
if ! command -v node >/dev/null; then
    echo ">> Installing Node.js..."
    curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
    sudo apt-get install -y nodejs
fi

# 2. Build the Pipedl application natively
echo ">> Building the .NET application..."
cd ~/scripts/pipedl
$HOME/.dotnet/dotnet publish src/Pipedl.Worker/Pipedl.Worker.csproj -c Release -o ./publish /p:UseAppHost=true

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
# Paths configuration
export DB_PATH=$HOME/scripts/pipedl/data/pipedl.db
export MUSIC_OUTPUT_PATH=/music
export TARGET_USER_ID=mnupea
export RUN_ONCE=1

# Include spotdl and dotnet in path
export PATH="$HOME/scripts/spotdl-venv/bin:$HOME/.dotnet:$PATH"
export PLAYWRIGHT_BROWSERS_PATH=$HOME/.cache/ms-playwright

# Run the app
cd $HOME/scripts/pipedl/publish
./Pipedl.Worker
EOF

chmod +x ~/scripts/pipedl/run_pipedl.sh

# 6. Ensure data directory exists
mkdir -p ~/scripts/pipedl/data

# 7. Setup Systemd Timer (cron equivalent)
echo ">> Configuring systemd timer to daily at 1 AM..."
SCRIPT_PATH="$HOME/scripts/pipedl/run_pipedl.sh"
PUBLISH_PATH="$HOME/scripts/pipedl/publish"

sudo bash -c 'cat << EOF > /etc/systemd/system/pipedl-native.service
[Unit]
Description=Pipedl Native Worker
After=network.target

[Service]
Type=oneshot
User='"$USER"'
ExecStart='"$SCRIPT_PATH"'
WorkingDirectory='"$PUBLISH_PATH"'
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
