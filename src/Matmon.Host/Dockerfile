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
FROM build AS agent
ARG MATMON_VERSION=""
WORKDIR /src/src/Matmon.Agent
RUN for rid in win-x64 linux-x64 linux-arm64; do \
        dotnet publish Matmon.Agent.csproj -c Release -r "$rid" -o "/app/agent/$rid" -p:MatmonVersion="$MATMON_VERSION" || exit 1; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
        && apt-get install -y --no-install-recommends wget ca-certificates gpg apt-transport-https \
        && . /etc/os-release \
        && wget -q https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends openssh-client sshpass powershell gss-ntlmssp \
    && pwsh -NoLogo -NoProfile -Command "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; Install-Module PSWSMan -Scope AllUsers -Force -AllowClobber; Install-WSMan" \
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
