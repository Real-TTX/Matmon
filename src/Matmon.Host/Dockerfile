FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Matmon.slnx ./
COPY src/Matmon.Core/Matmon.Core.csproj src/Matmon.Core/
COPY src/Matmon.Host/Matmon.Host.csproj src/Matmon.Host/
RUN dotnet restore src/Matmon.Host/Matmon.Host.csproj

COPY . .
WORKDIR /src/src/Matmon.Host
RUN dotnet publish Matmon.Host.csproj -c Release -o /app/publish /p:UseAppHost=false

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
USER root
ENTRYPOINT ["dotnet", "Matmon.Host.dll"]
