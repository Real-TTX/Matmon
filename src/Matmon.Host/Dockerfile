# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Matmon.slnx ./
COPY src/Matmon.Core/Matmon.Core.csproj src/Matmon.Core/
COPY src/Matmon.Host/Matmon.Host.csproj src/Matmon.Host/
# The Host references Matmon.Probe, so the restore layer needs its project file too - without this line
# "dotnet restore" fails and with it every image build (and therefore every deploy).
COPY src/Matmon.Probe/Matmon.Probe.csproj src/Matmon.Probe/
RUN dotnet restore src/Matmon.Host/Matmon.Host.csproj

COPY . .
WORKDIR /src/src/Matmon.Host
RUN dotnet publish Matmon.Host.csproj -c Release -o /app/publish /p:UseAppHost=false

# The agent binaries the instance hands out on its Agents page (and, later, to the auto-update) - built
# here so an image always carries exactly the agent that matches it, versioned with the same build number.
# Packaging (self-contained single file) is set in Matmon.Agent.csproj; this only picks the platforms.
# With the signing key - a BuildKit secret, never a layer (CI: secrets.MATMON_AGENT_SIGNING_KEY) - every package is
# signed and the agents are built to accept only signed updates from then on; without it (a local build) neither.
FROM build AS agent
ARG MATMON_VERSION=""
WORKDIR /src/src/Matmon.Agent
# The Windows package is the net10.0-windows build: the same agent plus its tray icon (WinForms).
RUN --mount=type=secret,id=agent_signing_key \
    require=false; \
    if [ -s /run/secrets/agent_signing_key ] && [ -n "$MATMON_VERSION" ]; then require=true; fi; \
    for rid in win-x64 linux-x64 linux-arm64; do \
        case "$rid" in win-*) tfm=net10.0-windows ;; *) tfm=net10.0 ;; esac; \
        dotnet publish Matmon.Agent.csproj -c Release -f "$tfm" -r "$rid" -o "/app/agent/$rid" -p:MatmonVersion="$MATMON_VERSION" -p:MatmonAgentRequireSignature="$require" || exit 1; \
    done; \
    if [ "$require" = true ]; then \
        dotnet run --file /src/scripts/sign-agent-packages.cs -- /app/agent "$MATMON_VERSION" /run/secrets/agent_signing_key || exit 1; \
    else \
        echo "agent packages are NOT signed (no signing key or no version)"; \
    fi

# The Windows instance (a Windows service that updates itself from the release feed - WindowsUpdateFeed): the same
# Host published self-contained for win-x64 with the agent packages beside it, zipped with its manifest and - with the
# signing key - signed. Not part of the image: CI builds this stage on its own (--target windows-feed) and attaches the
# two files to the rolling release of its channel. Placed BEFORE "final" on purpose - the last stage is the default
# target of a plain "docker build".
FROM build AS windows-package
ARG MATMON_VERSION=""
WORKDIR /src/src/Matmon.Host
COPY --from=agent /app/agent /out/package/agent
RUN --mount=type=secret,id=agent_signing_key \
    require=false; \
    if [ -s /run/secrets/agent_signing_key ] && [ -n "$MATMON_VERSION" ]; then require=true; fi; \
    dotnet publish Matmon.Host.csproj -c Release -r win-x64 --self-contained true -o /out/package -p:MatmonVersion="$MATMON_VERSION" -p:MatmonAgentRequireSignature="$require" || exit 1; \
    if [ "$require" = true ]; then key=/run/secrets/agent_signing_key; else key=""; fi; \
    dotnet run --file /src/scripts/package-windows.cs -- /out/package "${MATMON_VERSION:-local}" /out/feed $key

FROM scratch AS windows-feed
COPY --from=windows-package /out/feed/ /

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
# PSWSMan (the WinRM client the PowerShell sensors use from Linux) is pinned to the last 2.x on purpose. 3.0.0,
# published 2026-10-13, no longer has Install-WSMan - an unpinned install failed this layer, and with it every image
# build (CI, "docker compose up --build"), the morning after. Moving to 3.x is a change to test against a Windows
# target, not a version bump to make here.
RUN apt-get update \
        && apt-get install -y --no-install-recommends wget ca-certificates gpg apt-transport-https \
        && . /etc/os-release \
        && wget -q https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends openssh-client sshpass powershell gss-ntlmssp \
    && pwsh -NoLogo -NoProfile -Command "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; Install-Module PSWSMan -RequiredVersion 2.3.1 -Scope AllUsers -Force -AllowClobber; Install-WSMan" \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8099
# Baked-in build version shown in the UI (see MatmonVersion). CI passes this as
# a build-arg; an empty value makes the app fall back to "local-<builddate>".
ARG MATMON_VERSION=""
ENV MATMON_VERSION=${MATMON_VERSION}
EXPOSE 8099
COPY --from=build /app/publish .
COPY --from=agent /app/agent ./agent
USER root
ENTRYPOINT ["dotnet", "Matmon.Host.dll"]
