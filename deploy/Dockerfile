# TimeStore server as a Native AOT binary on Linux.
#   docker build -f deploy/Dockerfile -t timestore .
#   docker run -p 9000:9000 -p 9001:9001 -v timestore-data:/data \
#     -e TimeStore__S3__RootAccessKey=... -e TimeStore__S3__RootSecretKey=... timestore
# The admin port (UI + API) binds to 0.0.0.0 inside the container (publish it only where you want it); the server logs a warning
# until a TLS proxy and TimeStore__Admin__SecureCookies=true are in front of it.

# The admin UI: built here, embedded into the Server binary by its build (TimeStore.Server.csproj, AdminUi.cs).
FROM node:22-bookworm-slim AS ui
WORKDIR /src/frontend
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
# pnpm in the version of package.json "packageManager".
RUN corepack enable && pnpm install --frozen-lockfile
COPY frontend/ ./
# The user guide is imported from ../docs/user-guide (src/lib/guide.ts); without it the Guide page would be empty.
COPY docs/user-guide/ /src/docs/user-guide/
RUN test -n "$(ls /src/docs/user-guide/*.md)" && pnpm build

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
# Native AOT links with clang; zlib for the runtime's compression support.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY backend/ ./backend/
COPY --from=ui /src/frontend/dist/ ./frontend/dist/
ARG TARGETARCH
# Releases pass the tag's version (release workflow); local builds stay 0.0.0-dev.
ARG VERSION=0.0.0-dev
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac \
    && dotnet publish backend/Src/TimeStore.Server -c Release -r "$rid" -p:Version="$VERSION" -o /out \
    # Ship the binary and its native SQLite only: no symbols, no development settings (they hold the dev key pair).
    && rm -f /out/*.pdb /out/*.dbg /out/appsettings.Development.json

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir /data && chown app:app /data
USER app
# The base image sets ASPNETCORE_HTTP_PORTS=8080; TimeStore binds its own ports (TimeStore:S3Port / AdminPort).
ENV ASPNETCORE_HTTP_PORTS= \
    TimeStore__DataPath=/data \
    TimeStore__AdminAddress=0.0.0.0 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 9000 9001
VOLUME /data
ENTRYPOINT ["/app/TimeStore.Server"]
