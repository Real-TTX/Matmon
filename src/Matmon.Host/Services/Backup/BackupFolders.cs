using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Matmon.Core.Domain;

namespace Matmon.Host.Services.Backup;

/// <summary>A folder somewhere else a backup job writes its files into - an SMB share or a WebDAV folder.</summary>
public interface IBackupFolder
{
    /// <summary>How it reads on a page - never with a password.</summary>
    string Display { get; }

    Task WriteAsync(string fileName, byte[] content, CancellationToken cancellationToken);

    Task<IReadOnlyList<SmbFile>> ListAsync(CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(string fileName, CancellationToken cancellationToken);

    Task DeleteAsync(string fileName, CancellationToken cancellationToken);
}

/// <summary>An SMB share through the platform's SMB client.</summary>
public sealed class SmbBackupFolder(SmbLocation location, ISmbBackupClient client) : IBackupFolder
{
    public string Display => location.Display;

    public Task WriteAsync(string fileName, byte[] content, CancellationToken cancellationToken) => client.WriteAsync(location, fileName, content, cancellationToken);

    public Task<IReadOnlyList<SmbFile>> ListAsync(CancellationToken cancellationToken) => client.ListAsync(location, cancellationToken);

    public Task<byte[]> ReadAsync(string fileName, CancellationToken cancellationToken) => client.ReadAsync(location, fileName, cancellationToken);

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken) => client.DeleteAsync(location, fileName, cancellationToken);
}

/// <summary>A WebDAV folder and the account that may write there.</summary>
public sealed record WebDavLocation(Uri Folder, string? Username, string? Password)
{
    /// <summary>The address without a user name or password in it.</summary>
    public string Display => Folder.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>
    /// Reads the folder's address (http/https, as the server shows it - Nextcloud: <c>https://host/remote.php/dav/files/user/folder</c>).
    /// Null with the reason when it is not one. Plain http only to the local network: the password travels with every request.
    /// A user name or password written into the address is refused - they belong in their fields, where the password is sealed.
    /// </summary>
    public static WebDavLocation? Parse(string? address, string? username, string? password, out string? problem)
    {
        problem = null;
        var text = (address ?? string.Empty).Trim();
        if (text.StartsWith("webdav://", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text[9..];
        }
        else if (text.StartsWith("webdavs://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("davs://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            problem = "Enter the folder's address, e.g. https://cloud.example/remote.php/dav/files/me/matmon.";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            problem = "Put the user name and password in their fields, not into the address.";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problem = "The address may not carry a query (?...) or a #fragment.";
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !CloudUrlPolicy.IsPlainHttpAllowed(uri))
        {
            problem = "Use https for a server outside your own network - the password is sent with every request.";
            return null;
        }

        // Always a folder: every file name is resolved below it.
        var folder = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        return new WebDavLocation(
            folder,
            string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            string.IsNullOrEmpty(password) ? null : password);
    }

    public static WebDavLocation? For(WorkspaceBackupJob job, out string? problem) =>
        Parse(job.SmbPath, job.SmbUsername, job.SmbPassword, out problem);
}

/// <summary>
/// WebDAV with plain HTTP verbs: PUT (the folder chain is created with MKCOL when the server says it is missing), PROPFIND
/// with depth 1 to list, GET, DELETE. Basic authentication, sent up front. Errors are said with the server's status.
/// </summary>
public sealed class WebDavBackupFolder(WebDavLocation location, HttpClient http) : IBackupFolder
{
    public const string HttpClientName = "matmon-webdav-backup";

    private static readonly XNamespace Dav = "DAV:";

    public string Display => location.Display;

    public async Task WriteAsync(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var target = FileUri(fileName);
        using var response = await SendAsync(HttpMethod.Put, target, new ByteArrayContent(content), cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            // The folder (or one above it) is missing.
            await CreateFolderChainAsync(cancellationToken);
            using var again = await SendAsync(HttpMethod.Put, target, new ByteArrayContent(content), cancellationToken);
            await EnsureSuccessAsync(again, "write the file");
            return;
        }

        await EnsureSuccessAsync(response, "write the file");
    }

    public async Task<IReadOnlyList<SmbFile>> ListAsync(CancellationToken cancellationToken)
    {
        const string body = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:getcontentlength/><d:getlastmodified/><d:resourcetype/></d:prop></d:propfind>""";
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), location.Folder)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return []; // nothing written yet
        }

        await EnsureSuccessAsync(response, "list the folder");
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseListing(xml, location.Folder);
    }

    /// <summary>The files (not folders) of a PROPFIND answer, the folder itself left out.</summary>
    public static IReadOnlyList<SmbFile> ParseListing(XDocument xml, Uri folder)
    {
        var files = new List<SmbFile>();
        foreach (var entry in xml.Descendants(Dav + "response"))
        {
            var href = entry.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href) || entry.Descendants(Dav + "collection").Any())
            {
                continue;
            }

            var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
            var name = Uri.UnescapeDataString(path.TrimEnd('/').Split('/')[^1]);
            if (name.Length == 0)
            {
                continue;
            }

            long.TryParse(entry.Descendants(Dav + "getcontentlength").FirstOrDefault()?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            DateTimeOffset? modified = DateTimeOffset.TryParse(
                entry.Descendants(Dav + "getlastmodified").FirstOrDefault()?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUniversalTime()
                : null;
            files.Add(new SmbFile(name, size, modified));
        }

        return files;
    }

    public async Task<byte[]> ReadAsync(string fileName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, FileUri(fileName), null, cancellationToken);
        await EnsureSuccessAsync(response, "read the file");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeleteAsync(string fileName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, FileUri(fileName), null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, "delete the file");
        }
    }

    private Uri FileUri(string fileName)
    {
        if (fileName.IndexOfAny(['/', '\\']) >= 0 || fileName is "." or "..")
        {
            throw new InvalidOperationException("Not a file name.");
        }

        return new Uri(location.Folder, Uri.EscapeDataString(fileName));
    }

    /// <summary>MKCOL for every folder of the path that does not exist yet, from the top down. A folder that is there
    /// answers 405 (Method Not Allowed), which is fine.</summary>
    private async Task CreateFolderChainAsync(CancellationToken cancellationToken)
    {
        var segments = location.Folder.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = new StringBuilder("/");
        foreach (var segment in segments)
        {
            path.Append(segment).Append('/');
            var folder = new UriBuilder(location.Folder) { Path = path.ToString() }.Uri;
            using var response = await SendAsync(new HttpMethod("MKCOL"), folder, null, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.Forbidden and not HttpStatusCode.Conflict)
            {
                await EnsureSuccessAsync(response, "create the folder");
            }
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, HttpContent? content, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(method, uri) { Content = content }, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            if (location.Username is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{location.Username}:{location.Password}")));
            }

            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " - the user name or password is wrong (Nextcloud with two-factor sign-in needs an app password)",
            HttpStatusCode.Forbidden => " - this account may not write there",
            HttpStatusCode.NotFound => " - the address does not exist on the server",
            HttpStatusCode.InsufficientStorage => " - the storage is full",
            HttpStatusCode.RequestEntityTooLarge => " - the file is too large for the server",
            _ => string.Empty
        };
        var detail = string.Empty;
        try
        {
            var body = (await response.Content.ReadAsStringAsync()).Trim();
            var message = XDocument.Parse(body).Descendants().FirstOrDefault(element => element.Name.LocalName == "message")?.Value;
            detail = string.IsNullOrWhiteSpace(message) ? string.Empty : $": {message.Trim()}";
        }
        catch
        {
            // Not an XML error body - the status says enough.
        }

        throw new IOException($"Could not {what}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}{hint}{detail}");
    }
}
