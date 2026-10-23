using Matmon.Core.Domain;
using Matmon.Host.Services.WebPush;

namespace Matmon.Host.Services;

/// <summary>
/// Browser notifications: the devices the users switched them on in, the built-in "Browser notifications" sender (one per
/// instance, kept by an invariant), and the rule that sends to them - seeded at setup and for an existing workspace by
/// migration 10. A rule with that sender sends to the devices of the users its receiver names: a built-in user group (All
/// users / admins / operators) or a receiver with users' e-mail addresses. Devices are instance-bound (the VAPID key pair
/// in push-keys.json) and never part of a backup.
/// </summary>
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    public const int MaxPushDevicesPerUser = 20;
    public const string BrowserPushSenderName = "Browser notifications";
    public const string BrowserPushRuleName = "Browser notifications";

    public IReadOnlyList<PushDevice> GetPushDevices(Guid userId)
    {
        lock (_gate)
        {
            return (_document.PushDevices ?? []).Where(device => device.UserId == userId)
                .OrderByDescending(device => device.CreatedUtc)
                .Select(device => device.Clone())
                .ToList();
        }
    }

    public int CountPushDevices()
    {
        lock (_gate)
        {
            return _document.PushDevices?.Count ?? 0;
        }
    }

    public PushDevice RegisterPushDevice(Guid userId, string endpoint, string p256dh, string auth, string? name)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || endpoint!.Length > 2000)
        {
            throw new InvalidOperationException("The browser gave no usable push address.");
        }

        if (WebPushEncryption.FromBase64Url(p256dh) is not { Length: 65 } || WebPushEncryption.FromBase64Url(auth) is not { Length: 16 })
        {
            throw new InvalidOperationException("The browser gave no usable keys for its subscription.");
        }

        lock (_gate)
        {
            if (!_document.Users.Any(user => user.Id == userId && user.IsEnabled))
            {
                throw new InvalidOperationException("Unknown user.");
            }

            _document.PushDevices ??= [];
            var clean = endpoint.Trim();
            var device = _document.PushDevices.FirstOrDefault(candidate => string.Equals(candidate.Endpoint, clean, StringComparison.Ordinal));
            if (device is null)
            {
                device = new PushDevice { Endpoint = clean, CreatedUtc = DateTimeOffset.UtcNow };
                _document.PushDevices.Add(device);
            }

            // The same browser signed in as someone else now takes the notifications of that someone.
            device.UserId = userId;
            device.P256dh = p256dh.Trim();
            device.Auth = auth.Trim();
            device.Name = string.IsNullOrWhiteSpace(name) ? "Browser" : name.Trim()[..Math.Min(name.Trim().Length, 80)];

            var mine = _document.PushDevices.Where(candidate => candidate.UserId == userId).OrderByDescending(candidate => candidate.CreatedUtc).ToList();
            foreach (var extra in mine.Skip(MaxPushDevicesPerUser))
            {
                _document.PushDevices.Remove(extra);
            }

            QueueSave(SavePriority.Configuration);
            return device.Clone();
        }
    }

    public bool RemovePushDevice(Guid deviceId, Guid? userId)
    {
        lock (_gate)
        {
            if (_document.PushDevices?.RemoveAll(device => device.Id == deviceId && (userId is null || device.UserId == userId)) is not > 0)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public PushDevice? FindPushDevice(Guid deviceId)
    {
        lock (_gate)
        {
            return _document.PushDevices?.FirstOrDefault(device => device.Id == deviceId)?.Clone();
        }
    }

    public void MarkPushDeviceUsed(Guid deviceId, DateTimeOffset when)
    {
        lock (_gate)
        {
            if (_document.PushDevices?.FirstOrDefault(device => device.Id == deviceId) is { } device)
            {
                device.LastUsedUtc = when;
                QueueSave(SavePriority.Telemetry); // a timestamp: no hurry
            }
        }
    }

    /// <summary>The devices a rule's receiver reaches with the browser notification sender: a built-in user group's members'
    /// devices, or (for a receiver with e-mail addresses) the devices of the users with those addresses. Enabled users only.</summary>
    public IReadOnlyList<PushDevice> ResolvePushDevices(Guid? builtInReceiverId, string? emailTargets)
    {
        lock (_gate)
        {
            var devices = _document.PushDevices ?? [];
            IEnumerable<MatmonUser> users = _document.Users.Where(user => user.IsEnabled);
            if (builtInReceiverId is { } id && NotificationReceiverDefaults.Find(id) is { } group)
            {
                users = users.Where(user => group.RoleMatch(user.Role));
            }
            else
            {
                var addresses = (emailTargets ?? string.Empty)
                    .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                users = users.Where(user => !string.IsNullOrWhiteSpace(user.Email) && addresses.Contains(user.Email.Trim()));
            }

            var ids = users.Select(user => user.Id).ToHashSet();
            return devices.Where(device => ids.Contains(device.UserId)).Select(device => device.Clone()).ToList();
        }
    }

    /// <summary>Invariant: the built-in browser notification sender exists (created when missing - an admin who switched it off
    /// keeps it off). Must hold <c>_gate</c>.</summary>
    private NotificationSender EnsureBrowserPushSenderLocked()
    {
        _document.NotificationSenders ??= [];
        var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Kind == NotificationEndpointKind.Push);
        if (sender is null)
        {
            sender = new NotificationSender { Name = BrowserPushSenderName, Kind = NotificationEndpointKind.Push, Enabled = true };
            _document.NotificationSenders.Add(sender);
        }

        return sender;
    }

    private void EnsureBrowserPushSender()
    {
        lock (_gate)
        {
            var before = _document.NotificationSenders?.Count ?? 0;
            EnsureBrowserPushSenderLocked();
            if (_document.NotificationSenders!.Count != before)
            {
                QueueSave(SavePriority.Configuration);
            }
        }
    }

    /// <summary>The rule that sends browser notifications - every sensor, warnings and errors, to the devices of all users. It
    /// does nothing until a user switches notifications on in a browser. Added once (setup, migration 10); an admin may change
    /// or delete it. Must hold <c>_gate</c>.</summary>
    private void EnsureBrowserPushRuleLocked()
    {
        var sender = EnsureBrowserPushSenderLocked();
        _document.NotificationRules ??= [];
        if (_document.NotificationRules.Any(rule => rule.SenderId == sender.Id))
        {
            return;
        }

        _document.NotificationRules.Add(new NotificationRule
        {
            Name = BrowserPushRuleName,
            Enabled = true,
            ChannelKind = NotificationChannelKind.Email,
            SenderId = sender.Id,
            ReceiverId = NotificationReceiverDefaults.AllUsersReceiverId,
            TargetElementId = null,
            IncludeDescendants = true,
            TriggerStates = { SensorState.Warning, SensorState.Critical },
            CooldownMinutes = 15,
            Threshold = 5
        });
    }

    /// <summary>Migration 10: an existing workspace gets the built-in browser notification sender and its rule.</summary>
    private void MigrateAddBrowserPushRule() => EnsureBrowserPushRuleLocked();
}
