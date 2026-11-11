# .NET 10 SDK base image + sqlite3 CLI for inspecting the database file
FROM mcr.microsoft.com/devcontainers/dotnet:10.0

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends sqlite3 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
