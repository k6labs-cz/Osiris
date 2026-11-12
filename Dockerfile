# syntax=docker/dockerfile:1
# The Osiris server (Osiris.Web): many workspaces and users, signed in with Anubis.
#   docker build --build-arg GITHUB_PACKAGES_USER="$GITHUB_LOGIN" \
#     --secret id=github_packages_token,env=GITHUB_PACKAGES_TOKEN -t osiris .
#   docker run -p 8080:8080 -v osiris-data:/data -e Auth__Authority=https://id.example.com \
#     -e Auth__ClientSecret=… -e Osiris__Model__ApiKey=… osiris
#
# Everything Osiris writes (workspaces, the key sealing their secrets, sign-in cookie keys, downloaded
# packages) goes to /data: mount a persistent volume there. The rest of the filesystem can be read-only
# (with a writable /tmp).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG VERSION=0.0.0-dev
# The restore layer changes only with the projects' dependencies.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props README.md LICENSE ./
COPY src/Osiris.Abstractions/Osiris.Abstractions.csproj src/Osiris.Abstractions/
COPY src/Osiris.Core/Osiris.Core.csproj src/Osiris.Core/
COPY src/Osiris.Ui/Osiris.Ui.csproj src/Osiris.Ui/
COPY src/Osiris.Web/Osiris.Web.csproj src/Osiris.Web/
# k6labs.Hathor comes from GitHub Packages, which wants a token (read:packages) even for public packages.
ARG GITHUB_PACKAGES_USER
RUN --mount=type=secret,id=github_packages_token,required=true \
    NuGetPackageSourceCredentials_k6labs="Username=${GITHUB_PACKAGES_USER};Password=$(cat /run/secrets/github_packages_token)" \
    dotnet restore src/Osiris.Web/Osiris.Web.csproj
COPY src/ src/
RUN dotnet publish src/Osiris.Web/Osiris.Web.csproj --configuration Release --no-restore -p:Version=${VERSION} --output /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app ./

# - HTTP on 8080 (TLS ends at the ingress / reverse proxy).
# - Forwarded headers on: the scheme comes from X-Forwarded-Proto, so sign-in redirects use https.
#   Only expose the container through the proxy (it trusts any upstream).
# - HOME=/data: the key sealing secrets (unless Osiris__SecretKey is set) and NuGet's own files land on the volume.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Osiris__DataDirectory=/data \
    HOME=/data

# "app" (uid 1654) is the non-root user of the .NET images.
RUN mkdir -p /data && chown app:app /data
VOLUME /data
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "osiris-server.dll"]
