namespace Matmon.Host.Services;

/// <summary>A template store set this instance added, with the version it has and the one the store has now.</summary>
public sealed record TemplateStoreSet(string Slug, string? Installed, int TemplateCount, string? Live, string? Title)
{
    /// <summary>The store has a different version than the one added here (a newer one, or the operator rolled back).</summary>
    public bool UpdateAvailable => Live is not null && !string.Equals(Installed, Live, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Which store sets this instance added (templates carrying <c>StoreSlug</c>) and whether the store has another version of them -
/// what Templates and Templates > Store point out. Asking the cloud is cached by <see cref="CloudTemplateStoreClient"/>; when it
/// cannot be asked, the sets come back without a live version and nothing is announced.
/// </summary>
public static class TemplateStoreUpdates
{
    public static IReadOnlyDictionary<string, (string? Version, int Count)> Installed(IMonitoringWorkspaceStore store) =>
        store.GetAllTemplates()
            .Where(template => !string.IsNullOrWhiteSpace(template.StoreSlug))
            .GroupBy(template => template.StoreSlug!.Trim().ToLowerInvariant())
            // Templates of one set normally share a version; after a partial update the oldest one is what still needs updating.
            .ToDictionary(group => group.Key,
                group => (group.Select(template => template.StoreVersion).OrderBy(version => version, StringComparer.OrdinalIgnoreCase).First(), group.Count()));

    public static async Task<IReadOnlyList<TemplateStoreSet>> FindAsync(IMonitoringWorkspaceStore store, CloudTemplateStoreClient client,
        CancellationToken cancellationToken)
    {
        var installed = Installed(store);
        if (installed.Count == 0)
        {
            return [];
        }

        var live = await client.LiveVersionsAsync(installed.Keys.ToList(), cancellationToken) ?? [];
        var bySlug = live.ToDictionary(version => version.Slug, StringComparer.OrdinalIgnoreCase);
        return installed.Select(pair => bySlug.TryGetValue(pair.Key, out var current)
                ? new TemplateStoreSet(pair.Key, pair.Value.Version, pair.Value.Count, current.Version, current.Title)
                : new TemplateStoreSet(pair.Key, pair.Value.Version, pair.Value.Count, null, null))
            .OrderBy(set => set.Title ?? set.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
