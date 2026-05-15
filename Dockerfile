# ─────────────────────────────────────────────
# Stage 1 – build
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies first (layer-cache friendly)
COPY src/Syncify.Domain/Syncify.Domain.csproj          src/Syncify.Domain/
COPY src/Syncify.Infrastructure/Syncify.Infrastructure.csproj src/Syncify.Infrastructure/
COPY src/Syncify.Worker/Syncify.Worker.csproj           src/Syncify.Worker/
RUN dotnet restore src/Syncify.Worker/Syncify.Worker.csproj

# Copy everything else and publish
COPY . .
RUN dotnet publish src/Syncify.Worker/Syncify.Worker.csproj \
    -c Release -o /app/publish --no-restore

# ─────────────────────────────────────────────
# Stage 2 – runtime
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# Install curl, Node.js (for Playwright browser install), Python + pip (for spotdl)
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    gnupg \
    python3 \
    python3-pip \
    python3-venv \
    ffmpeg \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Install spotdl in an isolated venv so it doesn't clash with system Python
RUN python3 -m venv /opt/spotdl-venv \
    && /opt/spotdl-venv/bin/pip install --no-cache-dir spotdl
ENV PATH="/opt/spotdl-venv/bin:$PATH"

# Install Playwright Chromium browser into a fixed path inside the image.
# Pin the Playwright npm version to match the NuGet package (1.59.0).
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN npx --yes playwright@1.59.0 install --with-deps chromium

WORKDIR /app
COPY --from=build /app/publish .

# Data volume – SQLite DB lives here
VOLUME ["/data"]
# Music volume – spotdl downloads land here
VOLUME ["/music"]

ENV DB_PATH=/data/syncify.db
ENV TARGET_USER_ID=mnupea
# Cron expression (default: every day at midnight)
ENV CRON_EXPRESSION="0 0 0 * * ?"
ENV MUSIC_OUTPUT_PATH=/music

ENTRYPOINT ["dotnet", "Syncify.Worker.dll"]
