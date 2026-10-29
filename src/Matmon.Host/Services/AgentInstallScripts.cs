using System.Collections.Concurrent;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// The one-liner installs: <c>GET /api/agent/install.ps1</c> and <c>/api/agent/install.sh</c>, generated per request with
/// the instance address and the enrolment code baked in - the code arrives as the request's
/// <c>X-Matmon-Enrollment-Code</c> header, never in a URL. Each script downloads the plain package of its platform from
/// THIS instance (so the code check stays in one place), checks it against the SHA-256 the instance named when the script
/// was made, and runs the agent's own installer (<c>setup</c> on Windows, <c>install</c> elsewhere), which enrols first.
///
/// Script injection: both values are re-checked here - the code must be a code (<see cref="AgentEnrollmentCode.Canonical"/>:
/// letters, digits, dashes), the address an absolute http(s) URL of a restricted character set - and both are put into
/// single quotes, which neither may contain.
/// </summary>
public static class AgentInstallScripts
{
    /// <summary>The unix platforms a shell script can install, in the order <c>uname</c> is matched.</summary>
    private static readonly string[] UnixPlatforms = ["linux-x64", "linux-arm64", "osx-arm64", "osx-x64"];

    /// <summary>The URL in a form safe to quote, or null. No quote, dollar, backtick, backslash, space or control character
    /// can pass - the allowed set is what an http(s) address of an instance needs.</summary>
    public static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var text = url.Trim().TrimEnd('/');
        return text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or ':' or '/' or '[' or ']' or '%') ? text : null;
    }

    /// <summary>The PowerShell script (Windows x64 - Windows on ARM runs it emulated).</summary>
    public static string PowerShell(string url, string code, string? sha256)
    {
        var (safeUrl, safeCode) = Check(url, code);
        var hash = SafeHash(sha256);
        return $$"""
            # Matmon agent installer for {{safeUrl}}
            # Generated for ONE enrolment code: it works once and expires. Run in PowerShell as Administrator.
            & {
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                $MatmonUrl = '{{safeUrl}}'
                $MatmonCode = '{{safeCode}}'
                $Expected = '{{hash}}'
                $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
                if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    throw 'Run this in PowerShell as Administrator.'
                }
                if ($Expected -eq '') {
                    throw "The instance has no Windows agent package to hand out right now - see Probes > Agents on $MatmonUrl."
                }
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                $file = Join-Path $env:TEMP ('matmon-agent-' + [guid]::NewGuid().ToString('N') + '.exe')
                try {
                    Write-Host "Downloading the Matmon agent from $MatmonUrl ..."
                    Invoke-WebRequest -UseBasicParsing -Uri "$MatmonUrl/api/agent/packages/win-x64" -Headers @{ 'X-Matmon-Enrollment-Code' = $MatmonCode } -OutFile $file
                    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
                    if ($actual -ne $Expected) {
                        throw "The downloaded agent does not match its checksum ($actual, expected $Expected) - not installed."
                    }
                    & $file setup --url $MatmonUrl --code $MatmonCode
                    if ($LASTEXITCODE -ne 0) {
                        throw "The agent setup failed (exit code $LASTEXITCODE)."
                    }
                }
                finally {
                    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
                }
            }

            """.ReplaceLineEndings("\r\n");
    }

    /// <summary>The POSIX sh script (Linux x64/arm64 with systemd, macOS Apple Silicon/Intel with launchd).</summary>
    public static string Shell(string url, string code, IReadOnlyDictionary<string, string?> sha256ByPlatform)
    {
        ArgumentNullException.ThrowIfNull(sha256ByPlatform);
        var (safeUrl, safeCode) = Check(url, code);
        var hashes = string.Join("\n", UnixPlatforms.Select(platform =>
            $"    {platform}) expected='{SafeHash(sha256ByPlatform.TryGetValue(platform, out var hash) ? hash : null)}' ;;"));
        return $$"""
            #!/bin/sh
            # Matmon agent installer for {{safeUrl}}
            # Generated for ONE enrolment code: it works once and expires. Run as root: curl ... | sudo sh
            set -eu
            MATMON_URL='{{safeUrl}}'
            MATMON_CODE='{{safeCode}}'

            if [ "$(id -u)" -ne 0 ]; then
                echo "Run as root (... | sudo sh)." >&2
                exit 1
            fi

            os=$(uname -s)
            arch=$(uname -m)
            case "$os/$arch" in
                Linux/x86_64|Linux/amd64) rid=linux-x64 ;;
                Linux/aarch64|Linux/arm64) rid=linux-arm64 ;;
                Darwin/arm64) rid=osx-arm64 ;;
                Darwin/x86_64) rid=osx-x64 ;;
                *) echo "There is no Matmon agent for $os on $arch." >&2; exit 1 ;;
            esac

            case "$rid" in
            {{hashes}}
            esac
            if [ -z "$expected" ]; then
                echo "The instance has no agent package for $rid right now - see Probes > Agents on $MATMON_URL." >&2
                exit 1
            fi

            # Downloaded next to where the agent installs itself: /tmp is often mounted noexec.
            mkdir -p /usr/local/bin
            file="/usr/local/bin/.matmon-agent-download.$$"
            trap 'rm -f "$file"' EXIT INT TERM
            echo "Downloading the Matmon agent ($rid) from $MATMON_URL ..."
            if command -v curl >/dev/null 2>&1; then
                curl -fsSL -H "X-Matmon-Enrollment-Code: $MATMON_CODE" "$MATMON_URL/api/agent/packages/$rid" -o "$file"
            elif command -v wget >/dev/null 2>&1; then
                wget -q --header="X-Matmon-Enrollment-Code: $MATMON_CODE" -O "$file" "$MATMON_URL/api/agent/packages/$rid"
            else
                echo "Neither curl nor wget is installed." >&2
                exit 1
            fi

            if command -v sha256sum >/dev/null 2>&1; then
                actual=$(sha256sum "$file" | cut -d ' ' -f 1)
            else
                actual=$(shasum -a 256 "$file" | cut -d ' ' -f 1)
            fi
            if [ "$actual" != "$expected" ]; then
                echo "The downloaded agent does not match its checksum ($actual, expected $expected) - not installed." >&2
                exit 1
            fi

            chmod 0755 "$file"
            "$file" install --url "$MATMON_URL" --code "$MATMON_CODE"

            """.ReplaceLineEndings("\n");
    }

    private static (string Url, string Code) Check(string url, string code)
    {
        var safeUrl = SafeUrl(url) ?? throw new ArgumentException("not a usable instance address", nameof(url));
        var safeCode = AgentEnrollmentCode.Canonical(code) ?? throw new ArgumentException("not an enrolment code", nameof(code));
        return (safeUrl, safeCode);
    }

    private static string SafeHash(string? sha256) =>
        sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigit) ? sha256.ToLowerInvariant() : string.Empty;
}

/// <summary>
/// The plain enrolment codes of the codes created in THIS process, by enrolment id - in memory only, until the code
/// expires. They let a signed-in admin keep downloading the ready-to-install agent (code embedded) for a code after the
/// page that showed it once has been left, while the workspace still stores nothing but the code's hash. Lost on a
/// restart by design: the page then offers no download for that code and asks for a new one.
/// </summary>
public sealed class AgentInstallerCodes
{
    private readonly ConcurrentDictionary<Guid, (string Code, DateTimeOffset ExpiresUtc)> _codes = new();

    public void Remember(Guid enrollmentId, string code, DateTimeOffset expiresUtc)
    {
        Prune();
        _codes[enrollmentId] = (code, expiresUtc);
    }

    public string? Find(Guid enrollmentId)
    {
        Prune();
        return _codes.TryGetValue(enrollmentId, out var entry) ? entry.Code : null;
    }

    public void Forget(Guid enrollmentId) => _codes.TryRemove(enrollmentId, out _);

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in _codes)
        {
            if (entry.ExpiresUtc <= now)
            {
                _codes.TryRemove(id, out _);
            }
        }
    }
}
