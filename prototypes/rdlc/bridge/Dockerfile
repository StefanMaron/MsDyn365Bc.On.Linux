FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-amd64
RUN apt-get update && apt-get install -y --no-install-recommends \
    mono-devel gcc pkg-config libpango1.0-dev libfontconfig1-dev \
    libfreetype-dev libharfbuzz-dev libgdiplus fonts-liberation \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
WORKDIR /work
RUN apt-get update && apt-get install -y --no-install-recommends \
    fonts-noto-core fonts-droid-fallback \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
