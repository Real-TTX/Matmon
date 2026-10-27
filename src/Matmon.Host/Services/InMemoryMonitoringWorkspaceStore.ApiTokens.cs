using Matmon.Core.Domain;

namespace Matmon.Host.Services;

// API tokens (System > API tokens): what a CI/CD job presents to the instance's own API. Only the hash is kept;
// the secret is handed out once, by CreateApiToken. Instance-bound - stripped from every backup package.
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    /// <summary>At most this many tokens; a list longer than that is a sign tokens are made per run instead of per job.</summary>
    public const int MaxApiTokens = 100;

    /// <summary>A used token's "last used" is written to disk at most this often (every call would rewrite workspace.json).</summary>
    private static readonly TimeSpan ApiTokenUseSaveInterval = TimeSpan.FromMinutes(10);

    public ApiTokenIssue CreateApiToken(string name, IEnumerable<string> scopes, DateTimeOffset? expiresUtc, string? createdBy)
    {
        var now = DateTimeOffset.UtcNow;
        var cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
        {
            throw new InvalidOperationException("Give the token a name - what uses it (\"GitLab deploy\").");
        }
        if (cleanName.Length > 80)
        {
            throw new InvalidOperationException("A name has 80 characters at most.");
        }
        var cleanScopes = scopes.Select(scope => scope.Trim().ToLowerInvariant()).Where(scope => scope.Length > 0).Distinct().ToList();
        if (cleanScopes.Count == 0)
        {
            throw new InvalidOperationException("Choose what the token may do.");
        }
        if (cleanScopes.FirstOrDefault(scope => !ApiTokenScopes.IsKnown(scope)) is { } unknown)
        {
            throw new InvalidOperationException($"\"{unknown}\" is not something a token can be allowed.");
        }
        if (expiresUtc is { } end && (end <= now || end > now + ApiTokenSecret.MaxValidity))
        {
            throw new InvalidOperationException("The expiry must lie in the future and at most two years ahead (or choose \"until revoked\").");
        }

        var secret = ApiTokenSecret.Generate();
        var token = new ApiToken
        {
            Name = cleanName,
            Prefix = ApiTokenSecret.PrefixOf(secret),
            Hash = ApiTokenSecret.Hash(secret),
            Scopes = cleanScopes,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedUtc = now,
            ExpiresUtc = expiresUtc
        };

        lock (_gate)
        {
            _document.ApiTokens ??= [];
            if (_document.ApiTokens.Count(existing => existing.RevokedUtc is null) >= MaxApiTokens)
            {
                throw new InvalidOperationException($"There are {MaxApiTokens} tokens already - revoke the ones no longer used.");
            }
            _document.ApiTokens.Add(token);
            QueueSave(SavePriority.Configuration);
        }

        return new ApiTokenIssue(token.Clone(), secret);
    }

    public IReadOnlyList<ApiToken> GetApiTokens()
    {
        lock (_gate)
        {
            return (_document.ApiTokens ?? []).OrderByDescending(token => token.CreatedUtc).Select(token => token.Clone()).ToList();
        }
    }

    public bool RevokeApiToken(Guid id)
    {
        lock (_gate)
        {
            var token = _document.ApiTokens?.FirstOrDefault(candidate => candidate.Id == id);
            if (token is null || token.RevokedUtc is not null)
            {
                return false;
            }
            token.RevokedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteApiToken(Guid id)
    {
        lock (_gate)
        {
            if (_document.ApiTokens is null || _document.ApiTokens.RemoveAll(token => token.Id == id) == 0)
            {
                return false;
            }
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public ApiTokenCheck CheckApiToken(string? secret, string scope, string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.Trim().StartsWith(ApiTokenSecret.Marker, StringComparison.Ordinal))
        {
            return new ApiTokenCheck(ApiTokenCheckResult.Missing, null);
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            // every stored hash is compared (constant time each), so how long it takes says nothing about which matched
            ApiToken? found = null;
            foreach (var candidate in _document.ApiTokens ?? [])
            {
                if (ApiTokenSecret.Matches(candidate.Hash, secret) && found is null)
                {
                    found = candidate;
                }
            }

            if (found is null || found.RevokedUtc is not null)
            {
                return new ApiTokenCheck(ApiTokenCheckResult.Invalid, null);
            }
            if (found.IsExpired(now))
            {
                return new ApiTokenCheck(ApiTokenCheckResult.Expired, found.Clone());
            }
            if (!found.Allows(scope))
            {
                return new ApiTokenCheck(ApiTokenCheckResult.ScopeMissing, found.Clone());
            }

            var saveDue = found.LastUsedUtc is not { } last || now - last >= ApiTokenUseSaveInterval;
            found.LastUsedUtc = now;
            found.LastUsedFrom = remoteAddress;
            if (saveDue)
            {
                QueueSave(SavePriority.Telemetry);
            }
            return new ApiTokenCheck(ApiTokenCheckResult.Ok, found.Clone());
        }
    }
}

/// <summary>A token just created: the stored record and the secret, which is shown once and never again.</summary>
public sealed record ApiTokenIssue(ApiToken Token, string Secret);

public enum ApiTokenCheckResult { Ok, Missing, Invalid, Expired, ScopeMissing }

public sealed record ApiTokenCheck(ApiTokenCheckResult Result, ApiToken? Token);
