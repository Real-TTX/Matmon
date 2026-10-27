using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Accounts beyond the plain user CRUD: setting up a fresh instance with a Matmon Cloud account instead of a local one,
/// the recovery admin a file in the data folder creates, and switching an account's local password off once it signs in
/// through the cloud.
/// </summary>
public sealed partial class InMemoryMonitoringWorkspaceStore
{
    public MatmonUser CompleteInitialSetupWithCloud(string email)
    {
        var normalized = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Contains('@'))
        {
            throw new InvalidOperationException("Matmon Cloud did not say which account connected this instance.");
        }

        lock (_gate)
        {
            EnsureDefaultUsers();
            if (_document.SetupCompletedUtc is not null)
            {
                throw new InvalidOperationException("Setup has already been completed.");
            }

            var now = DateTimeOffset.UtcNow;
            var admin = new MatmonUser
            {
                Username = normalized,
                Email = normalized,
                PasswordHash = string.Empty, // signs in through the cloud; a local password can be set on the account page
                Role = MatmonUserRole.Admin,
                IsEnabled = true,
                CloudLinked = true,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastLoginUtc = now
            };
            _document.Users.Add(admin);
            _document.SetupCompletedUtc = now;
            SeedDefaultNotificationRuleLocked();
            QueueSave(SavePriority.Configuration);
            return CloneUser(admin);
        }
    }

    public MatmonUser ApplyRecoveryAdmin(string? email, string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("The password must be at least 8 characters.");
        }

        lock (_gate)
        {
            EnsureDefaultUsers();
            var wanted = (email ?? string.Empty).Trim();
            if (wanted.Length == 0)
            {
                // No address given: the first administrator there is (the one most likely locked out), else a fixed name.
                wanted = _document.Users
                    .Where(user => user.Role == MatmonUserRole.Admin)
                    .Select(user => string.IsNullOrWhiteSpace(user.Email) ? user.Username : user.Email)
                    .FirstOrDefault() ?? RecoveryAdmin.DefaultEmail;
            }

            var now = DateTimeOffset.UtcNow;
            var user = _document.Users.FirstOrDefault(candidate =>
                (!string.IsNullOrWhiteSpace(candidate.Email) && string.Equals(candidate.Email, wanted, StringComparison.OrdinalIgnoreCase))
                || string.Equals(candidate.Username, wanted, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                user = new MatmonUser
                {
                    Username = wanted,
                    Email = wanted.Contains('@') ? wanted : string.Empty,
                    CreatedUtc = now
                };
                _document.Users.Add(user);
            }

            // Whoever can write the data folder owns the machine: the account comes back fully usable - enabled, an
            // administrator, with this password and without the second factor (the lost phone may be why it is needed).
            user.PasswordHash = MatmonPasswordHasher.Hash(password);
            user.Role = MatmonUserRole.Admin;
            user.IsEnabled = true;
            user.TwoFactorEnabled = false;
            user.TotpSecretProtected = null;
            user.TotpEnrolledUtc = null;
            user.UpdatedUtc = now;

            if (_document.SetupCompletedUtc is null)
            {
                _document.SetupCompletedUtc = now;
                SeedDefaultNotificationRuleLocked();
            }

            QueueSave(SavePriority.Configuration);
            return CloneUser(user);
        }
    }

    public RemovePasswordResult RemoveOwnPassword(Guid userId, string currentPassword)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return RemovePasswordResult.NotFound;
            }
            if (string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                return RemovePasswordResult.NoPassword;
            }
            // Only an account that already signs in through the cloud, while the cloud link is there - else nobody could
            // sign in with it any more.
            if (!user.CloudLinked || !IsCloudLinkConfiguredLocked())
            {
                return RemovePasswordResult.NoCloudSignIn;
            }
            if (!MatmonPasswordHasher.Verify(currentPassword ?? string.Empty, user.PasswordHash))
            {
                return RemovePasswordResult.WrongCurrent;
            }

            user.PasswordHash = string.Empty;
            user.TwoFactorEnabled = false;
            user.TotpSecretProtected = null;
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return RemovePasswordResult.Success;
        }
    }

    public bool AnyLocalSignIn()
    {
        lock (_gate)
        {
            return _document.Users.Any(user => user.IsEnabled && !string.IsNullOrWhiteSpace(user.PasswordHash));
        }
    }

    public bool AnyLocalAdminSignIn()
    {
        lock (_gate)
        {
            return _document.Users.Any(user => user.IsEnabled && user.Role == MatmonUserRole.Admin && !string.IsNullOrWhiteSpace(user.PasswordHash));
        }
    }

    private bool IsCloudLinkConfiguredLocked()
    {
        var cloud = _document.CloudSettings;
        return cloud is { Enabled: true } && !string.IsNullOrWhiteSpace(cloud.Url) && !string.IsNullOrWhiteSpace(cloud.InstanceId);
    }
}

public enum RemovePasswordResult
{
    Success,
    NotFound,
    NoPassword,
    NoCloudSignIn,
    WrongCurrent
}
