using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matmon.Core.Domain;
using Matmon.Core.Sample;
using Matmon.Core.Telemetry;
using Matmon.Host.Ui;
using Microsoft.AspNetCore.DataProtection;

namespace Matmon.Host.Services;

public sealed partial class InMemoryMonitoringWorkspaceStore : IMonitoringWorkspaceStore, IDisposable
{
    private static readonly JsonSerializerOptions FileSerializerOptions = CreateSerializerOptions();
    private static readonly TimeSpan ConfigurationSaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TelemetrySaveDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDirtySaveDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackupRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly ILogger<InMemoryMonitoringWorkspaceStore> _logger;
    private readonly IDataProtector _credentialProtector;
    private readonly MatmonAuthOptions _authOptions;
    private readonly MatmonRuntimeOptions _runtimeOptions;
    private readonly string _workspacePath;
    private readonly string _workspaceBackupPath;
    private readonly string _backupDirectoryPath;
    private readonly Timer _saveTimer;
    private readonly ITelemetryRepository _telemetry;
    private readonly INotificationSink? _notificationSink;
    private WorkspaceDocument _document;
    private DateTimeOffset? _firstDirtyUtc;
    private DateTimeOffset _lastBackupUtc = DateTimeOffset.MinValue;
    private bool _saveInProgress;
    private bool _disposed;
    private long _dirtyVersion;
    private long _savedVersion;

    public InMemoryMonitoringWorkspaceStore(
        IHostEnvironment environment,
        MatmonRuntimeOptions runtimeOptions,
        MatmonAuthOptions authOptions,
        IDataProtectionProvider dataProtectionProvider,
        ITelemetryRepository telemetry,
        ILogger<InMemoryMonitoringWorkspaceStore> logger,
        INotificationSink? notificationSink = null)
    {
        _logger = logger;
        _authOptions = authOptions;
        _runtimeOptions = runtimeOptions;
        _telemetry = telemetry;
        _notificationSink = notificationSink;
        _credentialProtector = dataProtectionProvider.CreateProtector("Matmon.Credentials");

        var configuredPath = string.IsNullOrWhiteSpace(runtimeOptions.WorkspacePath)
            ? "data/workspace.json"
            : runtimeOptions.WorkspacePath;

        _workspacePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
        _workspaceBackupPath = _workspacePath + ".bak";
        _backupDirectoryPath = ResolveBackupDirectoryPath(environment, runtimeOptions, _workspacePath);
        _saveTimer = new Timer(FlushPendingSaveFromTimer);

        _document = LoadDocument();
        HydrateCredentialBundles(_document);
        MigrateDocumentTelemetryIntoRepository();
        EnsureSensorDefinitionCatalog();
        EnsureDefaultTemplates();
        MigrateAppliedTemplatesToCopies();
        MigrateSslCertificateThresholds();
        MigrateRetiredProxmoxSensors();
        EnsureDefaultProbeMetadata(_runtimeOptions.AutoCreateProbeSystemSensors);
        if (_runtimeOptions.ProvisionLocalDockerProbe)
        {
            EnsureDefaultDockerSecondaryProbe();
        }

        if (_runtimeOptions.ProvisionDemoSensors)
        {
            EnsureDefaultWindowsHealthSensor();
            EnsureDefaultProxmoxSensor();
        }

        if (!string.IsNullOrWhiteSpace(_runtimeOptions.UnifiCloudApiKey))
        {
            EnsureUnifiCloudSensor();
        }

        EnsureDefaultNotificationConfiguration();
        EnsureDefaultUsers();
        EnsureDefaultMaps(_runtimeOptions.CreateStarterMap);
        EnsureDefaultAlertCollection();
        SaveNow();
    }

    private static string ResolveBackupDirectoryPath(IHostEnvironment environment, MatmonRuntimeOptions runtimeOptions, string workspacePath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(runtimeOptions.BackupPath)
            ? Path.Combine(Path.GetDirectoryName(workspacePath) ?? environment.ContentRootPath, "backups")
            : runtimeOptions.BackupPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredPath));
    }

    public MonitoringWorkspaceSnapshot Workspace
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public IReadOnlyList<MonitoringElement> GetAllElements()
    {
        lock (_gate)
        {
            // Clone the tree once and enumerate the copy, so callers get detached, internally
            // consistent elements they can read without racing writers that mutate the live tree.
            return EnumerateElements(_document.RootProbe.Clone()).ToArray();
        }
    }

    public IReadOnlyList<MonitoringTemplate> GetAllTemplates()
    {
        lock (_gate)
        {
            return _document.Templates.Select(template => template.Clone()).ToArray();
        }
    }

    public IReadOnlyList<MatmonUser> GetUsers()
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            return _document.Users
                .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
                .Select(CloneUser)
                .ToArray();
        }
    }

    /// <summary>Comma-joined e-mails of the enabled users matching a built-in receiver's role filter (All users
    /// / All admins / All operators) - the target that built-in expands to at send time.</summary>
    public string ResolveBuiltInRecipients(Guid receiverId)
    {
        var builtIn = NotificationReceiverDefaults.Find(receiverId);
        if (builtIn is null)
        {
            return string.Empty;
        }

        lock (_gate)
        {
            return string.Join(", ", _document.Users
                .Where(user => user.IsEnabled && !string.IsNullOrWhiteSpace(user.Email) && user.Email.Contains('@') && builtIn.RoleMatch(user.Role))
                .Select(user => user.Email.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    public MatmonUser? FindUser(Guid userId)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            return user is null ? null : CloneUser(user);
        }
    }

    public MatmonUser? ValidateUser(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureDefaultUsers();
            var identifier = username.Trim();
            var user = _document.Users.FirstOrDefault(candidate =>
                candidate.IsEnabled &&
                (string.Equals(candidate.Username, identifier, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(candidate.Email) &&
                  string.Equals(candidate.Email, identifier, StringComparison.OrdinalIgnoreCase))));
            if (user is null || !MatmonPasswordHasher.Verify(password, user.PasswordHash))
            {
                return null;
            }

            user.LastLoginUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return CloneUser(user);
        }
    }

    public MatmonUser CreateUser(string username, string password, MatmonUserRole role)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var normalizedUsername = NormalizeUsername(username);
            if (_document.Users.Any(user => string.Equals(user.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"User '{normalizedUsername}' already exists.");
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Password is required.");
            }

            var now = DateTimeOffset.UtcNow;
            var user = new MatmonUser
            {
                Username = normalizedUsername,
                PasswordHash = MatmonPasswordHasher.Hash(password),
                Role = role,
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            _document.Users.Add(user);
            QueueSave(SavePriority.Configuration);
            return CloneUser(user);
        }
    }

    /// <summary>
    /// Find-or-create a user for a "Sign in with Matmon Cloud" identity. An existing local account with that
    /// e-mail is signed into (its role/password stay under local control) and flagged CloudLinked so the UI
    /// shows it's cloud-capable; a new one is created as a CloudLinked, password-less (SSO-only) account.
    /// Either way the last-login stamp is refreshed.
    /// </summary>
    public MatmonUser UpsertCloudUser(string email, MatmonUserRole role)
    {
        var normalized = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Contains('@'))
        {
            throw new InvalidOperationException("A valid e-mail is required.");
        }

        lock (_gate)
        {
            EnsureDefaultUsers();
            var existing = _document.Users.FirstOrDefault(user =>
                (!string.IsNullOrWhiteSpace(user.Email) && string.Equals(user.Email, normalized, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(user.Username, normalized, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (!existing.IsEnabled)
                {
                    throw new InvalidOperationException("This account is disabled.");
                }

                // Same identity (matched by e-mail): mark it cloud-capable + stamp the login. It keeps its
                // local password if it has one, so it can show as both "Local" and "Cloud".
                existing.CloudLinked = true;
                existing.LastLoginUtc = DateTimeOffset.UtcNow;
                QueueSave(SavePriority.Configuration);
                return CloneUser(existing);
            }

            var now = DateTimeOffset.UtcNow;
            var user = new MatmonUser
            {
                Username = normalized,
                Email = normalized,
                PasswordHash = string.Empty, // SSO-only until an admin sets a local password
                Role = role,
                IsEnabled = true,
                CloudLinked = true,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastLoginUtc = now
            };
            _document.Users.Add(user);
            QueueSave(SavePriority.Configuration);
            return CloneUser(user);
        }
    }

    public bool HasLocalPassword(Guid userId)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            return user is not null && !string.IsNullOrWhiteSpace(user.PasswordHash);
        }
    }

    public ChangePasswordResult ChangeOwnPassword(Guid userId, string? currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            return ChangePasswordResult.TooShort;
        }

        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return ChangePasswordResult.NotFound;
            }

            // If a local password already exists, require the current one; SSO-only accounts (no hash)
            // may set a first password without one (they're already authenticated via SSO).
            if (!string.IsNullOrWhiteSpace(user.PasswordHash) &&
                !MatmonPasswordHasher.Verify(currentPassword ?? string.Empty, user.PasswordHash))
            {
                return ChangePasswordResult.WrongCurrent;
            }

            user.PasswordHash = MatmonPasswordHasher.Hash(newPassword);
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return ChangePasswordResult.Success;
        }
    }

    // --- Two-factor (TOTP). The secret is stored DataProtection-encrypted; verification stays inside the store. ---

    public TotpEnrollmentInfo? BeginTotpEnrollment(Guid userId, string issuer)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null) { return null; }
            var secret = MatmonTotp.GenerateSecret();
            user.TotpSecretProtected = _credentialProtector.Protect(secret);
            user.TwoFactorEnabled = false; // not active until confirmed
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return new TotpEnrollmentInfo(secret, MatmonTotp.BuildOtpauthUri(issuer, TotpAccountName(user), secret));
        }
    }

    public TotpEnrollmentInfo? GetPendingTotpEnrollment(Guid userId, string issuer)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            var secret = UnprotectOrNull(user?.TotpSecretProtected);
            if (user is null || user.TwoFactorEnabled || secret is null) { return null; }
            return new TotpEnrollmentInfo(secret, MatmonTotp.BuildOtpauthUri(issuer, TotpAccountName(user), secret));
        }
    }

    public bool ConfirmTotp(Guid userId, string code)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            var secret = UnprotectOrNull(user?.TotpSecretProtected);
            if (user is null || secret is null || !MatmonTotp.Verify(secret, code)) { return false; }
            user.TwoFactorEnabled = true;
            user.TotpEnrolledUtc = DateTimeOffset.UtcNow;
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool VerifyTotp(Guid userId, string code)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null || !user.TwoFactorEnabled) { return false; }
            return MatmonTotp.Verify(UnprotectOrNull(user.TotpSecretProtected), code);
        }
    }

    /// <summary>Turn 2FA off + clear the secret. The CALLER authorizes first (a valid TOTP or e-mailed code).</summary>
    public bool DisableTotp(Guid userId)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null || !user.TwoFactorEnabled) { return false; }
            user.TwoFactorEnabled = false;
            user.TotpSecretProtected = null;
            user.TotpEnrolledUtc = null;
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    /// <summary>The e-mail to send a login/disable code to, or null (unknown user or no e-mail on file).</summary>
    public string? GetUserEmail(Guid userId)
    {
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            return string.IsNullOrWhiteSpace(user?.Email) ? null : user.Email;
        }
    }

    private static string TotpAccountName(MatmonUser user) =>
        string.IsNullOrWhiteSpace(user.Email) ? user.Username : user.Email;

    private string? UnprotectOrNull(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) { return null; }
        try { return _credentialProtector.Unprotect(cipher); }
        catch { return null; }
    }

    public bool UpdateUser(Guid userId, string username, MatmonUserRole role, bool isEnabled, string? password)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return false;
            }

            var normalizedUsername = NormalizeUsername(username);
            if (_document.Users.Any(candidate =>
                    candidate.Id != userId &&
                    string.Equals(candidate.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"User '{normalizedUsername}' already exists.");
            }

            user.Username = normalizedUsername;
            user.Role = role;
            user.IsEnabled = isEnabled;
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(password))
            {
                user.PasswordHash = MatmonPasswordHasher.Hash(password);
            }

            if (!_document.Users.Any(candidate => candidate.IsEnabled && candidate.Role == MatmonUserRole.Admin))
            {
                user.Role = MatmonUserRole.Admin;
                user.IsEnabled = true;
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteUser(Guid userId)
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            var user = _document.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return false;
            }

            if (user.Role == MatmonUserRole.Admin &&
                _document.Users.Count(candidate => candidate.IsEnabled && candidate.Role == MatmonUserRole.Admin) <= 1)
            {
                throw new InvalidOperationException("At least one enabled admin user is required.");
            }

            _document.Users.Remove(user);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public IReadOnlyList<MonitoringMap> GetMaps()
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            return _document.Maps
                .OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneMap)
                .ToArray();
        }
    }

    public MonitoringMap? FindMap(Guid id)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == id);
            return map is null ? null : CloneMap(map);
        }
    }

    public MonitoringMap? FindMapByPublicToken(string publicToken)
    {
        if (string.IsNullOrWhiteSpace(publicToken))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureDefaultMaps();
            var normalizedToken = publicToken.Trim();
            var map = _document.Maps.FirstOrDefault(candidate =>
                string.Equals(candidate.PublicToken, normalizedToken, StringComparison.OrdinalIgnoreCase));
            return map is null ? null : CloneMap(map);
        }
    }

    public MonitoringMap CreateMapWithSlides(
        string name,
        string? description,
        int columns,
        int rows,
        MonitoringMapDisplayPreset displayPreset,
        int aspectRatioWidth,
        int aspectRatioHeight,
        MonitoringMapWallboardFit wallboardFit,
        int autoRotateSeconds,
        MonitoringMapPaginationMode paginationMode,
        IReadOnlyList<MonitoringMapSlide> slides)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var now = DateTimeOffset.UtcNow;
            var normalizedColumns = Math.Clamp(columns, 4, 24);
            var normalizedRows = Math.Clamp(rows, 3, 16);
            var normalizedSlides = NormalizeMapSlides(slides, normalizedColumns, normalizedRows);
            var map = new MonitoringMap
            {
                Name = NormalizeMapName(name),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Columns = normalizedColumns,
                Rows = normalizedRows,
                DisplayPreset = displayPreset,
                AspectRatioWidth = Math.Clamp(aspectRatioWidth, 0, 64),
                AspectRatioHeight = Math.Clamp(aspectRatioHeight, 0, 64),
                WallboardFit = wallboardFit,
                AutoRotateSeconds = NormalizeAutoRotateSeconds(autoRotateSeconds),
                PaginationMode = paginationMode,
                PublicToken = CreateToken(),
                CreatedUtc = now,
                UpdatedUtc = now,
                Slides = normalizedSlides,
                Tiles = normalizedSlides[0].Tiles.Select(CloneMapTile).ToList()
            };

            _document.Maps.Add(map);
            QueueSave(SavePriority.Configuration);
            return CloneMap(map);
        }
    }

    public bool UpdateMapWithSlides(
        Guid mapId,
        string name,
        string? description,
        int columns,
        int rows,
        MonitoringMapDisplayPreset displayPreset,
        int aspectRatioWidth,
        int aspectRatioHeight,
        MonitoringMapWallboardFit wallboardFit,
        int autoRotateSeconds,
        MonitoringMapPaginationMode paginationMode,
        IReadOnlyList<MonitoringMapSlide> slides)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == mapId);
            if (map is null)
            {
                return false;
            }

            var normalizedColumns = Math.Clamp(columns, 4, 24);
            var normalizedRows = Math.Clamp(rows, 3, 16);
            var normalizedSlides = NormalizeMapSlides(slides, normalizedColumns, normalizedRows);
            map.Name = NormalizeMapName(name);
            map.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            map.Columns = normalizedColumns;
            map.Rows = normalizedRows;
            map.DisplayPreset = displayPreset;
            map.AspectRatioWidth = Math.Clamp(aspectRatioWidth, 0, 64);
            map.AspectRatioHeight = Math.Clamp(aspectRatioHeight, 0, 64);
            map.WallboardFit = wallboardFit;
            map.AutoRotateSeconds = NormalizeAutoRotateSeconds(autoRotateSeconds);
            map.PaginationMode = paginationMode;
            map.Slides = normalizedSlides;
            map.Tiles = normalizedSlides[0].Tiles.Select(CloneMapTile).ToList();
            map.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    private static int NormalizeAutoRotateSeconds(int seconds) => Math.Clamp(seconds, 3, 600);

    private static List<MonitoringMapSlide> NormalizeMapSlides(
        IReadOnlyList<MonitoringMapSlide> slides,
        int columns,
        int rows)
    {
        var result = (slides ?? [])
            .Select((slide, index) => new MonitoringMapSlide
            {
                Id = slide.Id == Guid.Empty ? Guid.NewGuid() : slide.Id,
                Name = string.IsNullOrWhiteSpace(slide.Name) ? $"Slide {index + 1}" : slide.Name.Trim(),
                Tiles = NormalizeMapTiles(slide.Tiles ?? [], columns, rows).ToList()
            })
            .ToList();

        if (result.Count == 0)
        {
            result.Add(new MonitoringMapSlide { Name = "Slide 1" });
        }

        return result;
    }

    public string RotateMapPublicToken(Guid mapId)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == mapId);
            if (map is null)
            {
                return string.Empty;
            }

            map.PublicToken = CreateToken();
            map.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return map.PublicToken;
        }
    }

    public bool DeleteMap(Guid mapId)
    {
        lock (_gate)
        {
            EnsureDefaultMaps();
            var map = _document.Maps.FirstOrDefault(candidate => candidate.Id == mapId);
            if (map is null)
            {
                return false;
            }

            _document.Maps.Remove(map);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public ProbeElement? FindProbeByProbeId(string probeId)
    {
        if (string.IsNullOrWhiteSpace(probeId))
        {
            return null;
        }

        lock (_gate)
        {
            return EnumerateElements(_document.RootProbe)
                .OfType<ProbeElement>()
                .FirstOrDefault(probe => string.Equals(probe.ProbeId, probeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<string> GetPrimaryProbeSubnets()
    {
        lock (_gate)
        {
            return (_document.RootProbe.Subnets ??= []).ToArray();
        }
    }

    public void AddPrimaryProbeSubnet(string cidr)
    {
        var normalized = (cidr ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_gate)
        {
            _document.RootProbe.Subnets ??= [];
            if (!_document.RootProbe.Subnets.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                _document.RootProbe.Subnets.Add(normalized);
                QueueSave(SavePriority.Configuration);
            }
        }
    }

    public void RemovePrimaryProbeSubnet(string cidr)
    {
        var normalized = (cidr ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_gate)
        {
            _document.RootProbe.Subnets ??= [];
            if (_document.RootProbe.Subnets.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                QueueSave(SavePriority.Configuration);
            }
        }
    }

    public MonitoringElement? FindElement(Guid id)
    {
        lock (_gate)
        {
            return EnumerateElements(_document.RootProbe)
                .FirstOrDefault(element => element.Id == id);
        }
    }

    public MonitoringTemplate? FindTemplate(Guid id)
    {
        lock (_gate)
        {
            return _document.Templates.FirstOrDefault(template => template.Id == id);
        }
    }

    /// <summary>
    /// Runs <paramref name="mutate"/> against the live element under <c>_gate</c>, then queues a save.
    /// This is the race-free way to edit an element: unlike <see cref="FindElement"/> + mutate + Save
    /// (which mutates outside the lock), the mutation here is serialized against readers and the
    /// polling service. Returns false if the id is unknown.
    /// </summary>
    public bool UpdateElement(Guid id, Action<MonitoringElement> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var element = FindElementInternal(id);
            if (element is null)
            {
                return false;
            }

            mutate(element);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    /// <summary>Template counterpart of <see cref="UpdateElement"/>: mutation runs under <c>_gate</c>.</summary>
    public bool UpdateTemplate(Guid id, Action<MonitoringTemplate> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var template = _document.Templates.FirstOrDefault(candidate => candidate.Id == id);
            if (template is null)
            {
                return false;
            }

            mutate(template);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    /// <summary>
    /// Resolves everything needed to execute a sensor - lineage, effective settings and target -
    /// atomically under <c>_gate</c>, returning a detached snapshot. Lets the polling hot path avoid
    /// walking the live element tree (which would race concurrent edits). Null if the id is not a sensor.
    /// </summary>
    public SensorExecutionPlan? GetSensorExecutionPlan(Guid sensorId)
    {
        lock (_gate)
        {
            if (FindElementInternal(sensorId) is not SensorElement sensor)
            {
                return null;
            }

            var lineage = BuildLineage(sensor);
            var templates = _document.Templates.ToDictionary(template => template.Id);
            // Resolve() builds a fresh MonitoringSettings (deep ApplyFrom), so it is already detached
            // from the live element settings.
            var effective = _telemetryInheritanceResolver.Resolve(lineage, templates);
            var target = SensorTargetResolver.Resolve(sensor, lineage);
            return new SensorExecutionPlan(sensor.Id, sensor.SensorTypeKey, target, sensor.IsPaused, effective);
        }
    }

    /// <summary>The sensor-definition catalog. Entries are an immutable catalog (rebuilt on load, not
    /// mutated at runtime), so this is a lightweight accessor that avoids cloning the whole workspace.</summary>
    public IReadOnlyList<SensorDefinition> GetSensorDefinitions()
    {
        lock (_gate)
        {
            return _document.SensorDefinitions.ToArray();
        }
    }

    public NotificationSender? FindNotificationSender(Guid id)
    {
        lock (_gate)
        {
            return _document.NotificationSenders.FirstOrDefault(sender => sender.Id == id);
        }
    }

    public NotificationReceiver? FindNotificationReceiver(Guid id)
    {
        lock (_gate)
        {
            return _document.NotificationReceivers.FirstOrDefault(receiver => receiver.Id == id);
        }
    }

    public NotificationRule? FindNotificationRule(Guid id)
    {
        lock (_gate)
        {
            return _document.NotificationRules.FirstOrDefault(rule => rule.Id == id);
        }
    }

    public (int Open, int Acknowledged, int Error, int Warning) GetActiveAlertCounts()
    {
        lock (_gate)
        {
            var open = 0;
            var acknowledged = 0;
            var error = 0;
            var warning = 0;
            foreach (var alert in _document.Alerts)
            {
                if (!alert.IsActive)
                {
                    continue;
                }

                if (alert.IsAcknowledged)
                {
                    // An acknowledged alert is "handled" - it counts as Ack, never as an
                    // Error/Warning in the status, even if its underlying state is critical.
                    acknowledged++;
                    continue;
                }

                open++;

                // Severity split over the UNACKNOWLEDGED alerts (mirrors the Alerts page StateKey
                // mapping: Warning -> warning, Paused -> its own bucket, everything else -> error).
                if (alert.State == SensorState.Warning)
                {
                    warning++;
                }
                else if (alert.State != SensorState.Paused)
                {
                    error++;
                }
            }

            return (open, acknowledged, error, warning);
        }
    }

    public bool AcknowledgeAlert(Guid alertId, string? acknowledgedBy = null)
    {
        lock (_gate)
        {
            var alert = _document.Alerts.FirstOrDefault(candidate => candidate.Id == alertId);
            if (alert is null || !alert.IsActive)
            {
                return false;
            }

            var changed = false;
            if (!alert.IsAcknowledged)
            {
                alert.AcknowledgedUtc = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (alert.AcknowledgedState != alert.State)
            {
                alert.AcknowledgedState = alert.State;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(acknowledgedBy) &&
                !string.Equals(alert.AcknowledgedBy, acknowledgedBy.Trim(), StringComparison.Ordinal))
            {
                alert.AcknowledgedBy = acknowledgedBy.Trim();
                changed = true;
            }

            if (changed)
            {
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Kind = MonitoringEventKind.AlertAcknowledged,
                    ElementId = alert.ElementId,
                    ElementKind = alert.ElementKind,
                    ElementName = alert.ElementName,
                    ElementPath = alert.ElementPath,
                    State = alert.State,
                    Message = $"Alert acknowledged{(string.IsNullOrWhiteSpace(alert.AcknowledgedBy) ? string.Empty : $" by {alert.AcknowledgedBy}")}"
                });
                QueueSave(SavePriority.Configuration);
            }

            // If the condition already recovered, acknowledging it now finishes the
            // job and closes the alert (Alerta-style "work it off").
            if (alert.RecoveredUtc is not null && alert.ResolvedUtc is null)
            {
                alert.ResolvedUtc = DateTimeOffset.UtcNow;
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = alert.ResolvedUtc.Value,
                    Kind = MonitoringEventKind.AlertResolved,
                    ElementId = alert.ElementId,
                    ElementKind = alert.ElementKind,
                    ElementName = alert.ElementName,
                    ElementPath = alert.ElementPath,
                    State = alert.State,
                    Message = alert.Message
                });
                QueueSave(SavePriority.Configuration);
            }

            return true;
        }
    }

    /// <summary>Mute = acknowledge + suppress: resolve the element's currently-active alert(s) to history now,
    /// and record a mute so future problem observations don't raise/re-open an alert (nor fire notifications)
    /// until it lifts. <paramref name="duration"/> null = permanent (until a manual un-mute).</summary>
    public void MuteElementAlerts(Guid elementId, TimeSpan? duration, string? mutedBy)
    {
        lock (_gate)
        {
            _document.AlertMutes ??= [];
            var now = DateTimeOffset.UtcNow;
            var until = duration.HasValue ? now + duration.Value : (DateTimeOffset?)null;
            var by = string.IsNullOrWhiteSpace(mutedBy) ? null : mutedBy.Trim();

            var existing = _document.AlertMutes.FirstOrDefault(mute => mute.ElementId == elementId);
            if (existing is null)
            {
                existing = new AlertMute { ElementId = elementId };
                _document.AlertMutes.Add(existing);
            }

            existing.MutedAtUtc = now;
            existing.MutedUntilUtc = until;
            existing.MutedBy = by;

            var scope = until is null ? "permanently" : $"until {until.Value.ToDisplay():g}";
            var elementName = FindElementInternal(elementId)?.Name ?? string.Empty;

            // Clear the current episode to history so it leaves the active list (the mute keeps it from coming back).
            ResolveAlertsForElement(elementId, now, $"Muted {scope}{(by is null ? string.Empty : $" by {by}")}");

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = now,
                Kind = MonitoringEventKind.AlertMuted,
                ElementId = elementId,
                ElementName = elementName,
                Message = $"Alerts muted {scope}{(by is null ? string.Empty : $" by {by}")}"
            });
            QueueSave(SavePriority.Configuration);
        }
    }

    public bool UnmuteElement(Guid elementId, string? by)
    {
        lock (_gate)
        {
            _document.AlertMutes ??= [];
            if (_document.AlertMutes.RemoveAll(mute => mute.ElementId == elementId) == 0)
            {
                return false;
            }

            var trimmedBy = string.IsNullOrWhiteSpace(by) ? null : by.Trim();
            AddEvent(new MonitoringEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = MonitoringEventKind.AlertUnmuted,
                ElementId = elementId,
                ElementName = FindElementInternal(elementId)?.Name ?? string.Empty,
                Message = $"Alerts un-muted{(trimmedBy is null ? string.Empty : $" by {trimmedBy}")}"
            });
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    /// <summary>The currently-muted elements (expired mutes are pruned), each with its resolved name/path for the UI.</summary>
    public IReadOnlyList<AlertMuteInfo> GetActiveAlertMutes()
    {
        lock (_gate)
        {
            _document.AlertMutes ??= [];
            var now = DateTimeOffset.UtcNow;
            if (_document.AlertMutes.RemoveAll(mute => !mute.IsActiveAt(now)) > 0)
            {
                QueueSave(SavePriority.Configuration);
            }

            return _document.AlertMutes
                .Select(mute =>
                {
                    var element = FindElementInternal(mute.ElementId);
                    return new AlertMuteInfo(
                        mute.ElementId,
                        element?.Name ?? "(deleted element)",
                        element is null ? string.Empty : GetElementPath(element),
                        mute.MutedUntilUtc,
                        mute.IsPermanent,
                        mute.MutedBy);
                })
                .OrderBy(mute => mute.ElementName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Under <c>_gate</c>: is this element's alerting currently muted? Prunes an expired mute in passing.</summary>
    private bool IsElementMutedLocked(Guid elementId, DateTimeOffset now)
    {
        if (_document.AlertMutes is not { Count: > 0 })
        {
            return false;
        }

        var mute = _document.AlertMutes.FirstOrDefault(candidate => candidate.ElementId == elementId);
        if (mute is null)
        {
            return false;
        }

        if (mute.IsActiveAt(now))
        {
            return true;
        }

        _document.AlertMutes.Remove(mute);
        return false;
    }

    private static bool ShouldCleanupHistory(StorageCleanupScope scope)
    {
        return scope is StorageCleanupScope.Telemetry
            or StorageCleanupScope.SensorHistory
            or StorageCleanupScope.Everything;
    }

    private static bool ShouldCleanupEvents(StorageCleanupScope scope)
    {
        return scope is StorageCleanupScope.Events
            or StorageCleanupScope.Everything;
    }

    private static bool ShouldCleanupStatistics(StorageCleanupScope scope)
    {
        return scope is StorageCleanupScope.Telemetry
            or StorageCleanupScope.Statistics
            or StorageCleanupScope.Everything;
    }


    public ProbeElement CreateProbe(Guid? parentId, string name, string? description)
    {
        lock (_gate)
        {
            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe);
            var probe = new ProbeElement(string.IsNullOrWhiteSpace(name) ? "Probe" : name.Trim())
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                ProbeId = GenerateUniqueProbeId(name),
                EnrollmentToken = CreateToken()
            };

            AddChild(parent, probe);
            EnsureProbeHeartbeatSensor(probe);
            QueueSave(SavePriority.Configuration);
            return probe;
        }
    }

    public FolderElement CreateFolder(Guid parentId, string name, string? description)
    {
        lock (_gate)
        {
            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe, MonitoringElementKind.Folder);
            var folder = new FolderElement(string.IsNullOrWhiteSpace(name) ? "Folder" : name.Trim())
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            AddChild(parent, folder);
            QueueSave(SavePriority.Configuration);
            return folder;
        }
    }

    public HostElement CreateHost(Guid parentId, string name, string address, string? description)
    {
        lock (_gate)
        {
            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe, MonitoringElementKind.Folder);
            var host = new HostElement(string.IsNullOrWhiteSpace(name) ? "Host" : name.Trim())
            {
                Address = address?.Trim() ?? string.Empty,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            AddChild(parent, host);
            QueueSave(SavePriority.Configuration);
            return host;
        }
    }

    public SensorElement CreateSensor(
        Guid parentId,
        string name,
        string sensorTypeKey,
        string target,
        string? description,
        MonitoringSettings? settings = null)
    {
        lock (_gate)
        {
            if (string.Equals(sensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("Heartbeat sensors must be attached to a non-root probe.");
            }

            var parent = ResolveParentContainer(parentId, MonitoringElementKind.Probe, MonitoringElementKind.Folder, MonitoringElementKind.Host);
            if (!_document.SensorDefinitions.Any(definition => string.Equals(definition.Key, sensorTypeKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Unknown sensor type '{sensorTypeKey}'.");
            }

            var sensor = new SensorElement(
                string.IsNullOrWhiteSpace(name) ? "Sensor" : name.Trim(),
                sensorTypeKey.Trim(),
                target?.Trim() ?? string.Empty)
            {
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            if (settings is not null)
            {
                sensor.Settings.ApplyFrom(settings);
            }

            // Seed sensible default conditions for this sensor type onto its own settings (only
            // where the user hasn't set one), so a new sensor alarms sensibly out of the box.
            SensorThresholdDefaults.Apply(sensorTypeKey, sensor.Settings);

            // SSL expiry warning/critical lives on the remainingDays channel thresholds; its own
            // seeder also migrates the legacy ssl.warningDays/criticalDays params.
            if (string.Equals(sensorTypeKey, SslCertificateSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            {
                SslCertificateSensorExecutor.EnsureDefaultThresholds(sensor.Settings);
            }

            AddChild(parent, sensor);
            QueueSave(SavePriority.Configuration);
            return sensor;
        }
    }

    public MonitoringTemplate CreateTemplate(string name, MonitoringTemplateScope targetKind, Guid? parentTemplateId)
    {
        lock (_gate)
        {
            if (parentTemplateId is Guid parentId && _document.Templates.All(template => template.Id != parentId))
            {
                throw new InvalidOperationException($"Template parent '{parentId}' does not exist.");
            }

            var template = new MonitoringTemplate
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Template" : name.Trim(),
                TargetKind = targetKind,
                ParentTemplateId = parentTemplateId
            };

            _document.Templates.Add(template);
            QueueSave(SavePriority.Configuration);
            return template;
        }
    }

    public NotificationSender CreateNotificationSender(string name)
    {
        lock (_gate)
        {
            var sender = new NotificationSender
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Notification sender" : name.Trim()
            };

            _document.NotificationSenders.Add(sender);
            QueueSave(SavePriority.Configuration);
            return sender;
        }
    }

    public NotificationReceiver CreateNotificationReceiver(string name)
    {
        lock (_gate)
        {
            var receiver = new NotificationReceiver
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Notification receiver" : name.Trim()
            };

            _document.NotificationReceivers.Add(receiver);
            QueueSave(SavePriority.Configuration);
            return receiver;
        }
    }

    public NotificationRule CreateNotificationRule(string name)
    {
        lock (_gate)
        {
            var rule = new NotificationRule
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Notification rule" : name.Trim()
            };

            rule.TriggerStates.Add(SensorState.Warning);
            rule.TriggerStates.Add(SensorState.Critical);

            _document.NotificationRules.Add(rule);
            QueueSave(SavePriority.Configuration);
            return rule;
        }
    }

    public bool HasEmailNotifications()
    {
        lock (_gate)
        {
            return _document.NotificationSenders.Any(sender => sender.Kind == NotificationEndpointKind.Email);
        }
    }

    public SummaryReportSettings GetSummaryReportSettings()
    {
        lock (_gate)
        {
            return _document.SummaryReport.Clone();
        }
    }

    public void UpdateSummaryReportSettings(SummaryReportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            // Preserve the runtime LastSentUtc bookkeeping; the caller only supplies the user-set fields.
            var lastSent = _document.SummaryReport.LastSentUtc;
            var updated = settings.Clone();
            updated.LastSentUtc = lastSent;
            _document.SummaryReport = updated;
            QueueSave(SavePriority.Configuration);
        }
    }

    public void MarkSummaryReportSent(DateTimeOffset sentUtc)
    {
        lock (_gate)
        {
            _document.SummaryReport.LastSentUtc = sentUtc;
            QueueSave(SavePriority.Configuration);
        }
    }

    public CloudConnectionState GetCloudConnection()
    {
        lock (_gate)
        {
            return _document.Cloud.Clone();
        }
    }

    public void UpdateCloudConnection(CloudConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _document.Cloud = state.Clone();
            QueueSave(SavePriority.Configuration);
        }
    }

    public CloudConnectionSettings GetCloudConnectionSettings()
    {
        lock (_gate)
        {
            return _document.CloudSettings.Clone();
        }
    }

    /// <summary>Unprotects the stored instance token (for the cloud connection service). Null if none/unreadable.</summary>
    public string? GetCloudConnectionToken()
    {
        lock (_gate)
        {
            var protectedToken = _document.CloudSettings.ProtectedToken;
            if (string.IsNullOrEmpty(protectedToken))
            {
                return null;
            }

            try
            {
                return _credentialProtector.Unprotect(protectedToken);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Connect/save the cloud link from the UI. A null/blank token keeps the stored one.</summary>
    public void SetCloudConnectionSettings(string? url, string? instanceId, string? token, bool enabled)
    {
        lock (_gate)
        {
            var settings = _document.CloudSettings;
            settings.Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            settings.InstanceId = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                settings.ProtectedToken = _credentialProtector.Protect(token.Trim());
            }
            settings.Enabled = enabled;
            settings.Configured = true;
            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>Disconnect from the UI: disable + drop the token, and mark configured so env no longer re-links.</summary>
    public void DisconnectCloud()
    {
        lock (_gate)
        {
            _document.CloudSettings.Enabled = false;
            _document.CloudSettings.ProtectedToken = null;
            _document.CloudSettings.Configured = true;
            _document.Cloud = new CloudConnectionState { LastStatus = "disconnected" };
            // Drop any cached managing-partner branding: once unlinked there is no cloud left to send
            // HasPartner=false, so without this the stale logo/name/colour would render in the UI + reports forever.
            _document.ServicePartner = null;
            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>Master switch for cloud alert relay. Enabling provisions/enables the built-in "Matmon Cloud"
    /// notification sender so rules can select it (recipients come from the rule's receiver); disabling
    /// turns that sender off but keeps it, so rules keep their reference for when relay is re-enabled.</summary>
    public void SetCloudRelaySettings(bool relayAlerts)
    {
        lock (_gate)
        {
            _document.CloudSettings.RelayAlerts = relayAlerts;

            var cloudSender = _document.NotificationSenders.FirstOrDefault(sender => sender.Kind == NotificationEndpointKind.Cloud);
            if (relayAlerts)
            {
                if (cloudSender is null)
                {
                    _document.NotificationSenders.Add(new NotificationSender
                    {
                        Name = "Matmon Cloud",
                        Kind = NotificationEndpointKind.Cloud,
                        Enabled = true
                    });
                }
                else
                {
                    cloudSender.Enabled = true;
                }
            }
            else if (cloudSender is not null)
            {
                cloudSender.Enabled = false;
            }

            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>Enable/disable Full Access (the outbound UI tunnel).</summary>
    public void SetCloudFullAccess(bool enabled)
    {
        lock (_gate)
        {
            _document.CloudSettings.FullAccessEnabled = enabled;
            QueueSave(SavePriority.Configuration);
        }
    }

    public string? GetLicenseToken()
    {
        lock (_gate)
        {
            return _document.LicenseToken;
        }
    }

    /// <summary>Caches the cloud-issued license token (for offline validation). Only saves on change.</summary>
    public void SetLicenseToken(string? token)
    {
        lock (_gate)
        {
            if (string.Equals(_document.LicenseToken, token, StringComparison.Ordinal))
            {
                return;
            }

            _document.LicenseToken = token;
            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>The managing service partner (name/contact + consent), cached from the cloud on heartbeat.</summary>
    public ServicePartnerInfo? GetServicePartnerInfo()
    {
        lock (_gate)
        {
            return _document.ServicePartner?.Clone();
        }
    }

    /// <summary>Just the partner brand accent colour (no logo clone) - the per-page layout reads this on every
    /// render to re-theme the app, so it must stay cheap (avoids cloning the potentially ~256KB logo bytes).</summary>
    public string? GetServicePartnerBrandColor()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false } partner ? partner.BrandColor : null;
        }
    }

    /// <summary>Just the managing partner's display name (no logo clone) - for the per-render sidebar "Managed by" line.
    /// Null when branding is suppressed (the relationship stays; only the visual brand is hidden).</summary>
    public string? GetServicePartnerName()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false } partner ? partner.Name : null;
        }
    }

    /// <summary>White-label product name (no logo clone) - when set, the partner's brand replaces "Matmon" in the
    /// sidebar/login/title. Null unless a partner set one and branding isn't suppressed.</summary>
    public string? GetServicePartnerProductName()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false } partner
                && !string.IsNullOrWhiteSpace(partner.ProductName)
                    ? partner.ProductName
                    : null;
        }
    }

    /// <summary>Whether a partner logo is available to render (no clone) - so the layout only emits the logo
    /// &lt;img&gt; when one actually exists, avoiding a broken-image icon when a product name is set without a logo.</summary>
    public bool GetServicePartnerHasLogo()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false, LogoPng.Length: > 0 };
        }
    }

    /// <summary>Whether the partner logo is a complete OEM lockup (show it alone; don't stack the product name beneath).</summary>
    public bool GetServicePartnerLogoIsOem()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false, LogoIsOem: true };
        }
    }

    /// <summary>White-label slogan (no logo clone), shown beneath the product name; null when suppressed/none.</summary>
    public string? GetServicePartnerSlogan()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false } partner
                && !string.IsNullOrWhiteSpace(partner.Slogan)
                    ? partner.Slogan
                    : null;
        }
    }

    /// <summary>The partner secondary accent colour (no logo clone), or null.</summary>
    public string? GetServicePartnerSecondaryColor()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false } partner ? partner.BrandColorSecondary : null;
        }
    }

    /// <summary>The partner sidebar layout (0 = logo top / name below, 1 = logo left / name right); 0 when none.</summary>
    public int GetServicePartnerSidebarStyle()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false } partner ? partner.SidebarStyle : 0;
        }
    }

    /// <summary>Whether a partner small logo (favicon) is available to render (no clone).</summary>
    public bool GetServicePartnerHasSmallLogo()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false, SmallLogoPng.Length: > 0 };
        }
    }

    /// <summary>The partner small-logo bytes + MIME (cloned once) for the favicon endpoint; null when suppressed / none.</summary>
    public (byte[] Bytes, string ContentType)? GetServicePartnerSmallLogo()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false, SmallLogoPng: { Length: > 0 } small } partner
                ? ((byte[])small.Clone(), string.IsNullOrWhiteSpace(partner.SmallLogoContentType) ? "image/png" : partner.SmallLogoContentType)
                : null;
        }
    }

    /// <summary>The partner logo bytes + MIME (cloned once) for the branding-logo endpoint; null when suppressed
    /// or none. Served via a cached endpoint rather than inlined so the prominently-shown logo isn't re-sent per page.</summary>
    public (byte[] Bytes, string ContentType)? GetServicePartnerLogo()
    {
        lock (_gate)
        {
            return _document.ServicePartner is { HasPartner: true, BrandingSuppressed: false, LogoPng: { Length: > 0 } logo } partner
                ? ((byte[])logo.Clone(), string.IsNullOrWhiteSpace(partner.LogoContentType) ? "image/png" : partner.LogoContentType)
                : null;
        }
    }

    /// <summary>Caches the cloud-reported service partner + consent. Only saves on change.</summary>
    public void SetServicePartnerInfo(ServicePartnerInfo? info)
    {
        lock (_gate)
        {
            if (info is null)
            {
                if (_document.ServicePartner is null)
                {
                    return;
                }
                _document.ServicePartner = null;
            }
            else
            {
                if (info.ValueEquals(_document.ServicePartner))
                {
                    return;
                }
                _document.ServicePartner = info.Clone();
            }

            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>The admin-configured system display timezone (IANA id); null = server local.</summary>
    public string? GetDisplayTimeZoneId()
    {
        lock (_gate)
        {
            return _document.DisplayTimeZoneId;
        }
    }

    /// <summary>Set the system default display timezone (IANA id, or null to clear). Only saves on change.</summary>
    public void SetDisplayTimeZoneId(string? timeZoneId)
    {
        var normalized = string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId.Trim();
        lock (_gate)
        {
            if (string.Equals(_document.DisplayTimeZoneId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _document.DisplayTimeZoneId = normalized;
            QueueSave(SavePriority.Configuration);
        }
    }

    /// <summary>Set a user's per-user display-timezone override (IANA id, or null to clear).</summary>
    public bool SetUserTimeZone(Guid userId, string? timeZoneId)
    {
        var normalized = string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId.Trim();
        lock (_gate)
        {
            var user = _document.Users.FirstOrDefault(u => u.Id == userId);
            if (user is null)
            {
                return false;
            }

            user.TimeZoneId = normalized;
            user.UpdatedUtc = DateTimeOffset.UtcNow;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public void ConfigureEmailNotifications(string smtpHost, int? smtpPort, string? username, string? password, bool useSsl, string fromEmail, string toEmail)
    {
        lock (_gate)
        {
            var sender = new NotificationSender
            {
                Name = "Email",
                Kind = NotificationEndpointKind.Email,
                Email = new EmailNotificationSettings
                {
                    SenderName = "Matmon",
                    SenderEmail = (fromEmail ?? string.Empty).Trim(),
                    SmtpHost = (smtpHost ?? string.Empty).Trim(),
                    SmtpPort = smtpPort ?? 587,
                    UseSsl = useSsl,
                    Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
                    Password = string.IsNullOrWhiteSpace(password) ? null : password
                }
            };
            _document.NotificationSenders.Add(sender);

            var receiver = new NotificationReceiver
            {
                Name = $"Email ({(toEmail ?? string.Empty).Trim()})",
                Kind = NotificationEndpointKind.Email,
                Target = (toEmail ?? string.Empty).Trim()
            };
            _document.NotificationReceivers.Add(receiver);

            var rule = new NotificationRule
            {
                Name = "Default alerts",
                ChannelKind = NotificationChannelKind.Email,
                SenderId = sender.Id,
                ReceiverId = receiver.Id,
                Recipient = receiver.Target,
                TriggerStates = { SensorState.Warning, SensorState.Critical }
            };
            _document.NotificationRules.Add(rule);

            QueueSave(SavePriority.Configuration);
        }
    }

    public bool DeleteElement(Guid id)
    {
        List<Guid> removedSensorIds;
        lock (_gate)
        {
            if (_document.RootProbe.Id == id)
            {
                return false;
            }

            var target = FindElement(id);
            if (target is null)
            {
                return false;
            }

            if (target is SensorElement sensor &&
                string.Equals(sensor.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Capture the sensor ids in the subtree so their telemetry can be purged after removal.
            removedSensorIds = EnumerateElements(target).OfType<SensorElement>().Select(element => element.Id).ToList();

            var removed = RemoveChild(_document.RootProbe, id);
            if (!removed)
            {
                return false;
            }

            QueueSave(SavePriority.Configuration);
        }

        // Purge outside the gate: SQLite I/O must not block the workspace lock, and the GUID ids can
        // never be reused, so there is no race with a concurrent re-create.
        foreach (var sensorId in removedSensorIds)
        {
            _telemetry.PurgeSensor(sensorId);
        }

        return true;
    }

    public IReadOnlyList<SensorElement> ResolveTargetSensors(string? targetToken)
    {
        lock (_gate)
        {
            if (MonitoringTargetResolver.TagName(targetToken) is { } tag)
            {
                var matches = new List<SensorElement>();
                foreach (var sensor in EnumerateElements(_document.RootProbe).OfType<SensorElement>())
                {
                    var effective = MonitoringTagResolver.ResolveEffective(BuildLineage(sensor));
                    if (MonitoringTagResolver.HasTag(effective, tag))
                    {
                        matches.Add(sensor);
                    }
                }

                return matches;
            }

            if (MonitoringTargetResolver.ElementId(targetToken) is { } id &&
                FindElementInternal(id) is { } element)
            {
                return EnumerateElements(element).OfType<SensorElement>().ToArray();
            }

            return [];
        }
    }

    public bool DeleteTemplate(Guid id)
    {
        lock (_gate)
        {
            var template = _document.Templates.FirstOrDefault(candidate => candidate.Id == id);
            if (template is null)
            {
                return false;
            }

            _document.Templates.Remove(template);

            foreach (var element in EnumerateElements(_document.RootProbe))
            {
                element.AppliedTemplateIds.RemoveAll(templateId => templateId == id);
            }

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteNotificationSender(Guid id)
    {
        lock (_gate)
        {
            if (_document.NotificationRules.Any(rule => rule.SenderId == id))
            {
                return false;
            }

            var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Id == id);
            if (sender is null)
            {
                return false;
            }

            _document.NotificationSenders.Remove(sender);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteNotificationReceiver(Guid id)
    {
        lock (_gate)
        {
            if (_document.NotificationRules.Any(rule => rule.ReceiverId == id))
            {
                return false;
            }

            var receiver = _document.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == id);
            if (receiver is null)
            {
                return false;
            }

            _document.NotificationReceivers.Remove(receiver);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool DeleteNotificationRule(Guid id)
    {
        lock (_gate)
        {
            var rule = _document.NotificationRules.FirstOrDefault(candidate => candidate.Id == id);
            if (rule is null)
            {
                return false;
            }

            _document.NotificationRules.Remove(rule);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool MoveElement(Guid elementId, Guid newParentId)
    {
        lock (_gate)
        {
            if (_document.RootProbe.Id == elementId)
            {
                return false;
            }

            var element = FindElement(elementId);
            if (element is null)
            {
                return false;
            }

            if (element is MonitoringContainerElement container &&
                EnumerateElements(container).Any(candidate => candidate.Id == newParentId))
            {
                return false;
            }

            var oldParent = FindParentContainer(_document.RootProbe, elementId);
            var newParent = ResolveParentContainer(newParentId, GetAllowedParentKinds(element));

            if (oldParent is null)
            {
                return false;
            }

            if (oldParent.Id == newParent.Id)
            {
                return true;
            }

            if (!oldParent.Children.Remove(element))
            {
                return false;
            }

            AddChild(newParent, element);
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public bool MoveElementBefore(Guid elementId, Guid siblingId)
    {
        return MoveElementRelative(elementId, siblingId, before: true);
    }

    public bool MoveElementAfter(Guid elementId, Guid siblingId)
    {
        return MoveElementRelative(elementId, siblingId, before: false);
    }

    public bool SetSensorPaused(Guid sensorId, bool paused)
    {
        lock (_gate)
        {
            var sensor = EnumerateElements(_document.RootProbe)
                .OfType<SensorElement>()
                .FirstOrDefault(candidate => candidate.Id == sensorId);

            if (sensor is null)
            {
                return false;
            }

            if (sensor.IsPaused == paused)
            {
                return true;
            }

            sensor.IsPaused = paused;

            if (paused)
            {
                ResolveAlertsForElement(sensor.Id, DateTimeOffset.UtcNow, "sensor paused");
            }

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = paused ? MonitoringEventKind.Paused : MonitoringEventKind.Resumed,
                ElementId = sensor.Id,
                ElementKind = sensor.Kind,
                ElementName = sensor.Name,
                ElementPath = GetElementPath(sensor),
                State = paused ? SensorState.Paused : SensorState.Healthy,
                Message = paused ? "Sensor paused" : "Sensor resumed"
            });

            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public int SetElementPaused(Guid elementId, bool paused)
    {
        lock (_gate)
        {
            var element = EnumerateElements(_document.RootProbe)
                .FirstOrDefault(candidate => candidate.Id == elementId);
            if (element is null)
            {
                return 0;
            }

            var now = DateTimeOffset.UtcNow;
            var changed = 0;

            // Pausing/resuming a container cascades to every sensor in its subtree; for a sensor
            // its subtree is just itself, so this also handles the single-sensor case.
            foreach (var sensor in EnumerateElements(element).OfType<SensorElement>())
            {
                if (sensor.IsPaused == paused)
                {
                    continue;
                }

                sensor.IsPaused = paused;

                if (paused)
                {
                    ResolveAlertsForElement(sensor.Id, now, "sensor paused");
                }

                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = now,
                    Kind = paused ? MonitoringEventKind.Paused : MonitoringEventKind.Resumed,
                    ElementId = sensor.Id,
                    ElementKind = sensor.Kind,
                    ElementName = sensor.Name,
                    ElementPath = GetElementPath(sensor),
                    State = paused ? SensorState.Paused : SensorState.Healthy,
                    Message = paused ? "Sensor paused" : "Sensor resumed"
                });

                changed++;
            }

            if (changed > 0)
            {
                QueueSave(SavePriority.Configuration);
            }

            return changed;
        }
    }

    private bool MoveElementRelative(Guid elementId, Guid siblingId, bool before)
    {
        lock (_gate)
        {
            if (_document.RootProbe.Id == elementId || elementId == siblingId)
            {
                return false;
            }

            var element = FindElement(elementId);
            var sibling = FindElement(siblingId);
            if (element is null || sibling is null)
            {
                return false;
            }

            if (element is MonitoringContainerElement container &&
                EnumerateElements(container).Any(candidate => candidate.Id == siblingId))
            {
                return false;
            }

            var oldParent = FindParentContainer(_document.RootProbe, elementId);
            var newParent = FindParentContainer(_document.RootProbe, siblingId);
            if (oldParent is null || newParent is null)
            {
                return false;
            }

            if (!GetAllowedParentKinds(element).Contains(newParent.Kind))
            {
                return false;
            }

            var sourceIndex = oldParent.Children.FindIndex(candidate => candidate.Id == elementId);
            var targetIndex = newParent.Children.FindIndex(candidate => candidate.Id == siblingId);
            if (sourceIndex < 0 || targetIndex < 0)
            {
                return false;
            }

            if (!oldParent.Children.Remove(element))
            {
                return false;
            }

            if (ReferenceEquals(oldParent, newParent) && targetIndex > sourceIndex)
            {
                targetIndex--;
            }

            var insertIndex = before ? targetIndex : targetIndex + 1;
            insertIndex = Math.Clamp(insertIndex, 0, newParent.Children.Count);
            newParent.Children.Insert(insertIndex, element);
            element.ParentId = newParent.Id;
            QueueSave(SavePriority.Configuration);
            return true;
        }
    }

    public void SyncAlerts(IEnumerable<MonitoringAlertCandidate> activeAlerts, DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureDefaultAlertCollection();

            var activeAlertList = activeAlerts.ToList();
            var activeByElementId = activeAlertList.ToDictionary(candidate => candidate.ElementId);
            var changed = false;

            foreach (var candidate in activeAlertList)
            {
                // Muted element: suppress - don't raise (nor keep) an active alert for it while the mute holds.
                if (IsElementMutedLocked(candidate.ElementId, now))
                {
                    continue;
                }

                var existing = _document.Alerts.FirstOrDefault(alert => alert.IsActive && alert.ElementId == candidate.ElementId);
                if (existing is null)
                {
                    _document.Alerts.Add(new MonitoringAlert
                    {
                        ElementId = candidate.ElementId,
                        ElementKind = candidate.ElementKind,
                        ElementName = candidate.ElementName,
                        ElementPath = candidate.ElementPath,
                        State = candidate.State,
                        Message = candidate.Message,
                        FirstSeenUtc = now,
                        LastSeenUtc = now
                    });
                    AddEvent(new MonitoringEvent
                    {
                        TimestampUtc = now,
                        Kind = MonitoringEventKind.AlertRaised,
                        ElementId = candidate.ElementId,
                        ElementKind = candidate.ElementKind,
                        ElementName = candidate.ElementName,
                        ElementPath = candidate.ElementPath,
                        State = candidate.State,
                        Message = candidate.Message
                    });
                    changed = true;
                    continue;
                }

                if (existing.ElementKind != candidate.ElementKind)
                {
                    existing.ElementKind = candidate.ElementKind;
                    changed = true;
                }

                if (!string.Equals(existing.ElementName, candidate.ElementName, StringComparison.Ordinal))
                {
                    existing.ElementName = candidate.ElementName;
                    changed = true;
                }

                if (!string.Equals(existing.ElementPath, candidate.ElementPath, StringComparison.Ordinal))
                {
                    existing.ElementPath = candidate.ElementPath;
                    changed = true;
                }

                if (existing.State != candidate.State)
                {
                    existing.State = candidate.State;
                    changed = true;
                }

                if (!string.Equals(existing.Message, candidate.Message, StringComparison.Ordinal))
                {
                    existing.Message = candidate.Message;
                    changed = true;
                }

                if (existing.RecoveredUtc is not null)
                {
                    existing.RecoveredUtc = null; // alarming again before it was worked off
                    changed = true;
                }

                existing.LastSeenUtc = now;
            }

            foreach (var alert in _document.Alerts.Where(alert => alert.IsActive))
            {
                if (activeByElementId.ContainsKey(alert.ElementId))
                {
                    continue;
                }

                // Alerta-style: only an acknowledged alert is auto-resolved on recovery.
                // An unacknowledged one is flagged recovered but stays open until worked off.
                if (alert.IsAcknowledged)
                {
                    alert.RecoveredUtc ??= now;
                    alert.ResolvedUtc = now;
                    AddEvent(new MonitoringEvent
                    {
                        TimestampUtc = now,
                        Kind = MonitoringEventKind.AlertResolved,
                        ElementId = alert.ElementId,
                        ElementKind = alert.ElementKind,
                        ElementName = alert.ElementName,
                        ElementPath = alert.ElementPath,
                        State = alert.State,
                        Message = alert.Message
                    });
                    changed = true;
                }
                else if (alert.RecoveredUtc is null)
                {
                    alert.RecoveredUtc = now;
                    changed = true;
                }
            }

            if (changed)
            {
                QueueSave(SavePriority.Telemetry);
            }
        }
    }

    public string RotateProbeToken(Guid probeId)
    {
        lock (_gate)
        {
            var probe = EnumerateElements(_document.RootProbe)
                .OfType<ProbeElement>()
                .FirstOrDefault(candidate => candidate.Id == probeId);

            if (probe is null)
            {
                throw new InvalidOperationException($"Probe '{probeId}' does not exist.");
            }

            probe.EnrollmentToken = CreateToken();
            QueueSave(SavePriority.Configuration);
            return probe.EnrollmentToken;
        }
    }

    public bool TryValidateProbe(string probeId, string? probeToken)
    {
        if (string.IsNullOrWhiteSpace(probeId))
        {
            return false;
        }

        lock (_gate)
        {
            var probe = EnumerateElements(_document.RootProbe)
                .OfType<ProbeElement>()
                .FirstOrDefault(candidate => string.Equals(candidate.ProbeId, probeId, StringComparison.OrdinalIgnoreCase));

            if (probe is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(probe.EnrollmentToken))
            {
                // A probe with no enrollment token is only ever the local root/master probe, which
                // executes in-process and never calls the remote /api/probes/* endpoints. Refusing it
                // here closes an anonymous auth bypass (previously any token - or none - was accepted).
                return false;
            }

            return !string.IsNullOrEmpty(probeToken) && FixedTimeEquals(probe.EnrollmentToken, probeToken);
        }
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        // Constant-time comparison so a probe token cannot be recovered by timing the response.
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public void Save()
    {
        QueueSave(SavePriority.Configuration);
    }

    public void Dispose()
    {
        lock (_saveGate)
        {
            _disposed = true;
            _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            while (_saveInProgress)
            {
                Monitor.Wait(_saveGate);
            }
        }

        try
        {
            FlushPendingSave();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush workspace during shutdown");
        }

        _saveTimer.Dispose();
    }

    private MonitoringWorkspaceSnapshot CreateSnapshot()
    {
        // Snapshot the mutable collections (cheap - .ToArray() of small lists) so consumers that
        // enumerate them (e.g. .Workspace.Alerts) can't hit a "collection modified" while the polling
        // path adds/removes alerts. RootProbe stays live: cloning the whole tree on every getter call
        // would be O(n²) on pages that read .SensorDefinitions/.Templates per element. Deeply tree-
        // walking consumers (the dashboard) use GetWorkspaceClone() instead.
        return new MonitoringWorkspaceSnapshot(
            _document.RootProbe,
            _document.Templates.ToArray(),
            _document.SensorDefinitions.ToArray(),
            _document.NotificationConfiguration,
            _document.NotificationSenders.ToArray(),
            _document.NotificationReceivers.ToArray(),
            _document.NotificationRules.ToArray(),
            _document.Alerts.ToArray());
    }

    /// <summary>
    /// A fully detached workspace snapshot with a <b>deep-cloned</b> element tree and templates, for
    /// consumers that walk the whole tree (the dashboard) and must not race concurrent edits.
    /// </summary>
    public MonitoringWorkspaceSnapshot GetWorkspaceClone()
    {
        lock (_gate)
        {
            return new MonitoringWorkspaceSnapshot(
                (ProbeElement)_document.RootProbe.Clone(),
                _document.Templates.Select(template => template.Clone()).ToArray(),
                _document.SensorDefinitions.ToArray(),
                _document.NotificationConfiguration,
                _document.NotificationSenders.ToArray(),
                _document.NotificationReceivers.ToArray(),
                _document.NotificationRules.ToArray(),
                _document.Alerts.ToArray());
        }
    }


    private void EnsureDefaultProbeMetadata(bool createSystemSensors)
    {
        lock (_gate)
        {
            EnsureProbeMetadataRecursive(_document.RootProbe, isRoot: true);
            if (createSystemSensors)
            {
                EnsureProbeHeartbeatSensorsRecursive(_document.RootProbe, isRoot: true);
                EnsureProbeHealthSensorsRecursive(_document.RootProbe);
            }
        }
    }

    private void EnsureDefaultDockerSecondaryProbe()
    {
        const string probeId = "probe-01";
        const string probeName = "Remote Probe 01";
        const string probeToken = "probe-01-token";

        var probe = EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.ProbeId, probeId, StringComparison.OrdinalIgnoreCase));

        if (probe is null)
        {
            probe = new ProbeElement(probeName)
            {
                ProbeId = probeId,
                EnrollmentToken = probeToken,
                Description = "Local Docker secondary probe"
            };
            AddChild(_document.RootProbe, probe);
        }
        else if (!string.Equals(probe.EnrollmentToken, probeToken, StringComparison.Ordinal))
        {
            // The local Docker secondary pairs with this well-known token (compose env
            // Matmon__ProbeToken: probe-01-token). Repair any stale/rotated token so an
            // existing bind-mounted workspace still authenticates the compose probe.
            probe.EnrollmentToken = probeToken;
        }

        if (string.IsNullOrWhiteSpace(probe.Name))
        {
            probe.Name = probeName;
        }

        probe.ParentId = _document.RootProbe.Id;
        EnsureProbeMetadataRecursive(probe);
        EnsureProbeHeartbeatSensor(probe);
        EnsureProbeHealthSensor(probe);
        EnsureDefaultDockerSecondaryTestSensor(probe);
    }

    private static void EnsureDefaultDockerSecondaryTestSensor(ProbeElement probe)
    {
        const string sensorName = "Secondary -> Primary Port 8099";

        var sensor = probe.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.SensorTypeKey, TcpPortSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, TcpPortSensorExecutor.Definition.Key, "primary")
            {
                Description = "Local Docker secondary execution test"
            };
            AddChild(probe, sensor);
        }

        sensor.ParentId = probe.Id;
        sensor.SensorTypeKey = TcpPortSensorExecutor.Definition.Key;
        sensor.Target = "primary";
        sensor.Settings.Parameters["tcp.port"] = "8099";
        sensor.Settings.Parameters["tcp.expectedOpen"] = "true";
        sensor.Settings.Timeout ??= TimeSpan.FromSeconds(3);
    }

    // The protector defaults to the instance-bound DataProtection key; a passphrase-derived protector is passed
    // when (un)sealing a PORTABLE cloud backup so its secrets round-trip across instances (see CreateBackupBytes).
    private void HydrateCredentialBundles(WorkspaceDocument document, IDataProtector? protector = null)
    {
        var secretProtector = protector ?? _credentialProtector;
        foreach (var settings in EnumerateSettings(document))
        {
            foreach (var credential in settings.Credentials)
            {
                if (credential.Values.Count > 0 || string.IsNullOrWhiteSpace(credential.ProtectedValues))
                {
                    continue;
                }

                try
                {
                    var payload = secretProtector.Unprotect(credential.ProtectedValues);
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, FileSerializerOptions);
                    credential.Values = values is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                    credential.HydrationFailed = false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt credential bundle {CredentialId}", credential.Id);
                    credential.Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    // Preserve the stored ciphertext: a transient decrypt failure (e.g. missing
                    // DataProtection keys) must not be re-encrypted as an empty payload on the next save.
                    credential.HydrationFailed = true;
                }
            }
        }

        HydrateNotificationSecrets(document, secretProtector);
    }

    // ---- Notification secrets (SMTP passwords, webhook secrets) are DataProtection-encrypted at rest ----
    // Same protect-before-save / hydrate-after-load lifecycle as credential bundles, so they never hit
    // workspace.json in plaintext. A "slot" is one secret + its ciphertext field on a settings object.
    private sealed record SecretSlot(Func<string?> GetPlain, Action<string?> SetPlain, Func<string?> GetCipher, Action<string?> SetCipher, Func<bool> GetFailed, Action<bool> SetFailed);

    private static IEnumerable<SecretSlot> EnumerateNotificationSecrets(WorkspaceDocument document)
    {
        static SecretSlot Email(EmailNotificationSettings e) => new(
            () => e.Password, v => e.Password = v, () => e.ProtectedPassword, v => e.ProtectedPassword = v,
            () => e.PasswordHydrationFailed, v => e.PasswordHydrationFailed = v);
        static SecretSlot Webhook(WebhookNotificationSettings w) => new(
            () => w.Secret, v => w.Secret = v, () => w.ProtectedSecret, v => w.ProtectedSecret = v,
            () => w.SecretHydrationFailed, v => w.SecretHydrationFailed = v);

        yield return Email(document.NotificationConfiguration.Email);
        yield return Webhook(document.NotificationConfiguration.Webhook);
        foreach (var sender in document.NotificationSenders)
        {
            yield return Email(sender.Email);
            yield return Webhook(sender.Webhook);
        }
        foreach (var receiver in document.NotificationReceivers)
        {
            yield return new SecretSlot(
                () => receiver.Secret, v => receiver.Secret = v, () => receiver.ProtectedSecret, v => receiver.ProtectedSecret = v,
                () => receiver.SecretHydrationFailed, v => receiver.SecretHydrationFailed = v);
        }
    }

    private void HydrateNotificationSecrets(WorkspaceDocument document, IDataProtector? protector = null)
    {
        var secretProtector = protector ?? _credentialProtector;
        foreach (var slot in EnumerateNotificationSecrets(document))
        {
            var cipher = slot.GetCipher();
            if (string.IsNullOrWhiteSpace(cipher))
            {
                // No ciphertext: either no secret, or a legacy plaintext still sitting in the plain field
                // (from before encryption existed) - leave it, it gets protected on the next save.
                slot.SetFailed(false);
                continue;
            }

            try
            {
                slot.SetPlain(secretProtector.Unprotect(cipher));
                slot.SetFailed(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt a notification secret");
                slot.SetPlain(null);
                slot.SetFailed(true); // keep the ciphertext; don't overwrite it with an encryption of empty
            }
        }
    }

    private void ProtectNotificationSecrets(WorkspaceDocument document, IDataProtector? protector = null)
    {
        var secretProtector = protector ?? _credentialProtector;
        foreach (var slot in EnumerateNotificationSecrets(document))
        {
            if (slot.GetFailed())
            {
                continue; // decryption failed on load - leave the stored ciphertext intact
            }

            var plain = slot.GetPlain();
            if (string.IsNullOrEmpty(plain))
            {
                slot.SetCipher(null); // secret cleared
                continue;
            }

            try
            {
                slot.SetCipher(secretProtector.Protect(plain));
                slot.SetPlain(null); // never serialize the plaintext; hydrate restores it after the write
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to protect a notification secret");
            }
        }
    }

    private void ProtectCredentialBundles(WorkspaceDocument document, IDataProtector? protector = null)
    {
        var secretProtector = protector ?? _credentialProtector;
        foreach (var settings in EnumerateSettings(document))
        {
            foreach (var credential in settings.Credentials)
            {
                if (credential.HydrationFailed)
                {
                    // Decryption failed at load; keep the original ciphertext untouched rather than
                    // clobbering it with an encryption of the empty in-memory Values.
                    continue;
                }

                try
                {
                    var payload = JsonSerializer.Serialize(credential.Values, FileSerializerOptions);
                    credential.ProtectedValues = secretProtector.Protect(payload);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to protect credential bundle {CredentialId}", credential.Id);
                }
            }
        }

        ProtectNotificationSecrets(document, secretProtector);
    }

    private static IEnumerable<MonitoringSettings> EnumerateSettings(WorkspaceDocument document)
    {
        yield return document.RootProbe.Settings;

        foreach (var element in EnumerateElements(document.RootProbe))
        {
            yield return element.Settings;
        }

        foreach (var template in document.Templates)
        {
            yield return template.Settings;
        }
    }

    private void EnsureDefaultTemplates()
    {
        lock (_gate)
        {
            // Only the generic small-office/home-lab presets are seeded. The Windows Health,
            // Synology NAS and Proxmox PVE templates were workarounds from before those got
            // dedicated full sensors (windows-health / synology-health / proxmox-health) - they're
            // redundant now, so they're no longer seeded (existing copies stay deletable).
            EnsureSmallOfficeHomeLabTemplates();
        }
    }

    private void EnsureDefaultNotificationConfiguration()
    {
        lock (_gate)
        {
            _document.NotificationConfiguration ??= new NotificationWorkspaceConfiguration();
            _document.NotificationConfiguration.Email ??= new EmailNotificationSettings();
            _document.NotificationConfiguration.Webhook ??= new WebhookNotificationSettings();
            _document.NotificationSenders ??= [];
            _document.NotificationReceivers ??= [];
            _document.NotificationRules ??= [];

            // A clean install starts with no senders/receivers/rules - the user sets up
            // notifications themselves (no example.local demo data). Existing rules are still
            // fixed up below, and ResolveReceiverIdForRule creates a receiver on demand.
            foreach (var rule in _document.NotificationRules)
            {
                if (rule.TriggerStates.Count == 0)
                {
                    rule.TriggerStates.Add(SensorState.Warning);
                    rule.TriggerStates.Add(SensorState.Critical);
                }

                if (rule.SenderId is null || _document.NotificationSenders.All(sender => sender.Id != rule.SenderId))
                {
                    rule.SenderId = ResolveSenderIdForRule(rule.ChannelKind);
                }

                // A built-in (virtual) receiver like "All users" is not in NotificationReceivers, so guard it
                // explicitly - otherwise the fixup would clobber every rule that targets it on each load.
                if (rule.ReceiverId is null ||
                    (!NotificationReceiverDefaults.IsBuiltIn(rule.ReceiverId) &&
                        _document.NotificationReceivers.All(receiver => receiver.Id != rule.ReceiverId)))
                {
                    rule.ReceiverId = ResolveReceiverIdForRule(rule.ChannelKind, rule.Recipient);
                }

                SynchronizeLegacyRuleFields(rule);
            }
        }
    }

    // Seeds one "works out of the box" rule on a fresh install so the instance alerts as soon as e-mail is
    // configured: all sensors, Warning + Critical, to every user's address (built-in "All users" receiver),
    // with NO fixed sender - it falls back to the workspace default SMTP, so it stays inert until an admin
    // sets up SMTP (or points it at the Cloud sender) and then delivers automatically without editing the rule.
    private void SeedDefaultNotificationRuleLocked()
    {
        _document.NotificationRules ??= [];
        if (_document.NotificationRules.Count > 0)
        {
            return;
        }

        _document.NotificationRules.Add(new NotificationRule
        {
            Name = "Default alerts",
            Enabled = true,
            ChannelKind = NotificationChannelKind.Email,
            SenderId = null,
            ReceiverId = NotificationReceiverDefaults.AllUsersReceiverId,
            TargetElementId = null,
            IncludeDescendants = true,
            TriggerStates = { SensorState.Warning, SensorState.Critical },
            CooldownMinutes = 15,
            Threshold = 5
        });
    }

    private void EnsureDefaultUsers()
    {
        _document.Users ??= [];

        if (_document.Users.Count > 0)
        {
            if (!_document.Users.Any(user => user.IsEnabled && user.Role == MatmonUserRole.Admin))
            {
                _document.Users[0].Role = MatmonUserRole.Admin;
                _document.Users[0].IsEnabled = true;
                _document.Users[0].UpdatedUtc = DateTimeOffset.UtcNow;
            }

            // An install that already has an account is past first-run setup - mark it so the setup
            // wizard never hijacks a configured instance (migration for pre-setup workspaces).
            _document.SetupCompletedUtc ??= DateTimeOffset.UtcNow;
            return;
        }

        // No accounts yet. If an admin was provisioned via Matmon__Auth__* (headless/automated
        // deploy), seed it and treat setup as done. Otherwise leave the workspace account-less so
        // the first-run setup wizard forces account creation on first launch.
        if (string.IsNullOrWhiteSpace(_authOptions.Username) || string.IsNullOrWhiteSpace(_authOptions.Password))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var seededUsername = _authOptions.Username.Trim();
        _document.Users.Add(new MatmonUser
        {
            Username = seededUsername,
            Email = seededUsername.Contains('@') ? seededUsername : string.Empty,
            PasswordHash = MatmonPasswordHasher.Hash(_authOptions.Password),
            Role = MatmonUserRole.Admin,
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        _document.SetupCompletedUtc = now;
        SeedDefaultNotificationRuleLocked();
    }

    public bool IsSetupRequired()
    {
        lock (_gate)
        {
            EnsureDefaultUsers();
            return _document.SetupCompletedUtc is null;
        }
    }

    public MatmonUser CompleteInitialSetup(string email, string password)
    {
        var normalizedEmail = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !normalizedEmail.Contains('@'))
        {
            throw new InvalidOperationException("A valid e-mail address is required.");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("The password must be at least 8 characters.");
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
                Username = normalizedEmail,
                Email = normalizedEmail,
                PasswordHash = MatmonPasswordHasher.Hash(password),
                Role = MatmonUserRole.Admin,
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            _document.Users.Add(admin);
            _document.SetupCompletedUtc = now;
            SeedDefaultNotificationRuleLocked();
            QueueSave(SavePriority.Configuration);
            return CloneUser(admin);
        }
    }

    private void EnsureDefaultMaps(bool createStarterMap = false)
    {
        _document.Maps ??= [];

        if (_document.Maps.Count > 0 || !createStarterMap)
        {
            EnsureMapPublicTokens();
            return;
        }

        var root = _document.RootProbe;
        _document.Maps.Add(new MonitoringMap
        {
            Name = "Operations Wall",
            Description = "A starter map for wall displays and office screens.",
            DisplayPreset = MonitoringMapDisplayPreset.FullHd1080,
            PublicToken = CreateToken(),
            Columns = 12,
            Rows = 6,
            Tiles =
            [
                new MonitoringMapTile
                {
                    Kind = MonitoringMapTileKind.Status,
                    Title = "Overall status",
                    ElementId = root.Id,
                    X = 1,
                    Y = 1,
                    Width = 4,
                    Height = 2
                },
                new MonitoringMapTile
                {
                    Kind = MonitoringMapTileKind.Text,
                    Title = "Matmon Map",
                    Text = "Assign sensors, folders or probes to tiles in edit mode.",
                    X = 5,
                    Y = 1,
                    Width = 4,
                    Height = 2
                }
            ]
        });
    }

    private void EnsureMapPublicTokens()
    {
        foreach (var map in _document.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.PublicToken))
            {
                map.PublicToken = CreateToken();
                map.UpdatedUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private Guid? ResolveSenderIdForRule(NotificationChannelKind channelKind)
    {
        var kind = channelKind == NotificationChannelKind.Webhook
            ? NotificationEndpointKind.Webhook
            : NotificationEndpointKind.Email;

        var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Kind == kind)
            ?? _document.NotificationSenders.FirstOrDefault();

        return sender?.Id;
    }

    private Guid? ResolveReceiverIdForRule(NotificationChannelKind channelKind, string recipient)
    {
        var kind = channelKind == NotificationChannelKind.Webhook
            ? NotificationEndpointKind.Webhook
            : NotificationEndpointKind.Email;

        var normalizedRecipient = recipient?.Trim() ?? string.Empty;

        var receiver = _document.NotificationReceivers.FirstOrDefault(candidate =>
            candidate.Kind == kind &&
            string.Equals(candidate.Target, normalizedRecipient, StringComparison.OrdinalIgnoreCase));

        if (receiver is not null)
        {
            return receiver.Id;
        }

        if (string.IsNullOrWhiteSpace(normalizedRecipient))
        {
            return _document.NotificationReceivers.FirstOrDefault(candidate => candidate.Kind == kind)?.Id
                ?? _document.NotificationReceivers.FirstOrDefault()?.Id;
        }

        receiver = new NotificationReceiver
        {
            Name = kind == NotificationEndpointKind.Webhook
                ? $"Webhook receiver ({normalizedRecipient})"
                : $"Email receiver ({normalizedRecipient})",
            Kind = kind,
            Target = normalizedRecipient
        };

        _document.NotificationReceivers.Add(receiver);
        return receiver.Id;
    }

    private void SynchronizeLegacyRuleFields(NotificationRule rule)
    {
        if (rule.SenderId is Guid senderId)
        {
            var sender = _document.NotificationSenders.FirstOrDefault(candidate => candidate.Id == senderId);
            if (sender is not null)
            {
                rule.ChannelKind = sender.Kind == NotificationEndpointKind.Webhook
                    ? NotificationChannelKind.Webhook
                    : NotificationChannelKind.Email;
            }
        }

        if (rule.ReceiverId is Guid receiverId)
        {
            var receiver = _document.NotificationReceivers.FirstOrDefault(candidate => candidate.Id == receiverId);
            if (receiver is not null)
            {
                rule.Recipient = receiver.Target;
            }
        }
    }

    private void EnsureDefaultAlertCollection()
    {
        lock (_gate)
        {
            _document.Alerts ??= [];
            _document.AlertMutes ??= [];
        }
    }


    private void EnsureSensorDefinitionCatalog()
    {
        lock (_gate)
        {
            var builtIns = SensorDefinitionCatalog.BuiltIns;

            var merged = new List<SensorDefinition>(_document.SensorDefinitions.Count + builtIns.Count);
            var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var builtIn in builtIns)
            {
                merged.Add(CloneSensorDefinition(builtIn));
                addedKeys.Add(builtIn.Key);
            }

            foreach (var definition in _document.SensorDefinitions)
            {
                if (addedKeys.Contains(definition.Key))
                {
                    continue;
                }

                merged.Add(CloneSensorDefinition(definition));
            }

            _document.SensorDefinitions = merged;
        }
    }

    private void EnsureSmallOfficeHomeLabTemplates()
    {
        var baseline = EnsureTemplate("small-office-baseline", "Small Office Baseline", MonitoringTemplateScope.Any);
        baseline.Settings.Enabled ??= true;
        SetDefaultPollingInterval(baseline.Settings, TimeSpan.FromMinutes(1));
        baseline.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        baseline.Settings.RetryCount ??= 1;
        baseline.Settings.EventRetentionDays ??= 30;
        baseline.Settings.ObservationRetentionDays ??= 14;
        baseline.Settings.StatisticsRetentionDays ??= 365;
        baseline.Settings.StatisticsBucketMinutes ??= 60;
        SetDefaultParameter(baseline.Settings, "profile", "small-office-home-lab");

        var networkDevice = EnsureTemplate(
            "network-device-defaults",
            "Network Device Defaults",
            MonitoringTemplateScope.Host,
            parentTemplateId: baseline.Id);
        networkDevice.Settings.Enabled ??= true;
        SetDefaultPollingInterval(networkDevice.Settings, TimeSpan.FromMinutes(1));
        networkDevice.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        SetDefaultParameter(networkDevice.Settings, "snmp.community", "public");
        SetDefaultParameter(networkDevice.Settings, "snmp.version", "v2c");
        SetDefaultParameter(networkDevice.Settings, "snmp.port", "161");

        var windowsHost = EnsureTemplate(
            "windows-host-defaults",
            "Windows Host Defaults",
            MonitoringTemplateScope.Host,
            parentTemplateId: baseline.Id);
        windowsHost.Settings.Enabled ??= true;
        SetDefaultPollingInterval(windowsHost.Settings, TimeSpan.FromMinutes(1));
        windowsHost.Settings.Timeout ??= TimeSpan.FromSeconds(10);
        SetDefaultParameter(windowsHost.Settings, "winrm.port", "5985");
        SetDefaultParameter(windowsHost.Settings, "winrm.useSsl", "false");
        SetDefaultParameter(windowsHost.Settings, "winrm.configurationName", "Microsoft.PowerShell");

    }

    private MonitoringTemplate EnsureTemplate(
        string templateKey,
        string templateName,
        MonitoringTemplateScope targetKind,
        string? sensorTypeKey = null,
        Guid? parentTemplateId = null)
    {
        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(candidate.Key) &&
             string.Equals(candidate.Name, templateName, StringComparison.OrdinalIgnoreCase) &&
             candidate.TargetKind == targetKind));

        if (template is null)
        {
            template = new MonitoringTemplate
            {
                Key = templateKey,
                Name = templateName,
                TargetKind = targetKind,
                SensorTypeKey = sensorTypeKey,
                ParentTemplateId = parentTemplateId
            };
            _document.Templates.Add(template);
            return template;
        }

        if (string.IsNullOrWhiteSpace(template.Key))
        {
            template.Key = templateKey;
        }

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            template.Name = templateName;
        }

        if (template.TargetKind == MonitoringTemplateScope.Any)
        {
            template.TargetKind = targetKind;
        }

        if (string.IsNullOrWhiteSpace(template.SensorTypeKey) && !string.IsNullOrWhiteSpace(sensorTypeKey))
        {
            template.SensorTypeKey = sensorTypeKey;
        }

        if (template.ParentTemplateId is null && parentTemplateId.HasValue)
        {
            template.ParentTemplateId = parentTemplateId;
        }

        return template;
    }

    private static void SetDefaultParameter(MonitoringSettings settings, string key, string value)
    {
        if (!settings.Parameters.TryGetValue(key, out var currentValue) || string.IsNullOrWhiteSpace(currentValue))
        {
            settings.Parameters[key] = value;
        }
    }

    private static void SetDefaultPollingInterval(MonitoringSettings settings, TimeSpan interval)
    {
        if (settings.PollingInterval is null && settings.PollingSchedule is null)
        {
            settings.PollingInterval = interval;
        }
    }

    private static void SetDefaultDailySchedule(MonitoringSettings settings, TimeSpan timeOfDay)
    {
        if (settings.PollingInterval is null && settings.PollingSchedule is null)
        {
            settings.PollingSchedule = new MonitoringSchedule
            {
                Mode = MonitoringScheduleMode.Daily,
                TimeOfDay = timeOfDay
            };
        }
    }

    private static void SetDefaultChannelThreshold(
        MonitoringSettings settings,
        string channelKey,
        string severity,
        ThresholdRule rule)
    {
        var key = MonitoringSettings.BuildChannelThresholdKey(channelKey, severity);
        if (!settings.Thresholds.ContainsKey(key))
        {
            MonitoringSettings.SetChannelThreshold(settings, channelKey, severity, rule);
        }
    }

    private static void SetDefaultOrMigrateChannelThreshold(
        MonitoringSettings settings,
        string channelKey,
        string severity,
        ThresholdRule rule,
        params string[] migrateFrom)
    {
        _ = migrateFrom;
        var key = MonitoringSettings.BuildChannelThresholdKey(channelKey, severity);
        if (!settings.Thresholds.ContainsKey(key))
        {
            MonitoringSettings.SetChannelThreshold(settings, channelKey, severity, rule);
        }
    }

    private void EnsureDefaultWindowsHealthSensor()
    {
        const string sensorName = "Windows Health";
        const string sensorTarget = "windows-host";
        const string templateKey = "windows-health";

        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase));

        var sensor = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SensorTypeKey, PowerShellRemoteSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Target, sensorTarget, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, PowerShellRemoteSensorExecutor.Definition.Key, sensorTarget)
            {
                Description = "Windows workstation health monitor"
            };
            AddChild(_document.RootProbe, sensor);
        }

        sensor.ParentId = _document.RootProbe.Id;
        sensor.SensorTypeKey = PowerShellRemoteSensorExecutor.Definition.Key;
        sensor.Target = sensorTarget;

        if (template is not null && !sensor.AppliedTemplateIds.Contains(template.Id))
        {
            sensor.AppliedTemplateIds.Add(template.Id);
        }

        sensor.Settings.Highlight = true;
    }

    private void EnsureUnifiCloudSensor()
    {
        var apiKey = _runtimeOptions.UnifiCloudApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        const string sensorName = "UniFi Cloud";

        var sensor = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SensorTypeKey, UnifiHealthSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, UnifiHealthSensorExecutor.Definition.Key, string.Empty)
            {
                Description = "UniFi Site Manager cloud health (auto-provisioned from Matmon__UnifiCloudApiKey)"
            };
            AddChild(_document.RootProbe, sensor);
        }

        sensor.ParentId = _document.RootProbe.Id;
        sensor.Settings.Parameters["unifi.mode"] = "cloud";
        sensor.Settings.Parameters["unifi.apiKey"] = apiKey;
    }

    /// <summary>The legacy scope-based <c>proxmox</c> sensor type was retired (split into <c>proxmox-health</c>
    /// + <c>proxmox-node-health</c>), so a workspace that still holds one fails with "No executor is registered
    /// for sensor type 'proxmox'". Rewrite any leftover <c>proxmox</c> sensor/template to the right replacement -
    /// <c>pve.scope=cluster</c> → cluster health, otherwise the per-node type. Idempotent (no-op once migrated).</summary>
    private void MigrateRetiredProxmoxSensors()
    {
        const string legacyKey = "proxmox";

        static string Replacement(MonitoringSettings settings) =>
            settings.Parameters.TryGetValue("pve.scope", out var scope) && string.Equals(scope, "cluster", StringComparison.OrdinalIgnoreCase)
                ? ProxmoxHealthSensorExecutor.Definition.Key
                : ProxmoxNodeHealthSensorExecutor.Definition.Key;

        var changed = false;

        foreach (var sensor in EnumerateElements(_document.RootProbe).OfType<SensorElement>()
            .Where(candidate => string.Equals(candidate.SensorTypeKey, legacyKey, StringComparison.OrdinalIgnoreCase)))
        {
            sensor.SensorTypeKey = Replacement(sensor.Settings);
            changed = true;
        }

        foreach (var template in _document.Templates
            .Where(candidate => string.Equals(candidate.SensorTypeKey, legacyKey, StringComparison.OrdinalIgnoreCase)))
        {
            template.SensorTypeKey = Replacement(template.Settings);
            changed = true;
        }

        if (changed)
        {
            QueueSave(SavePriority.Configuration);
        }
    }

    private void EnsureDefaultProxmoxSensor()
    {
        const string sensorName = "PVE";
        const string sensorTarget = "proxmox-host";
        const string templateKey = "proxmox-pve-node";

        var template = _document.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, templateKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, "Proxmox PVE Node", StringComparison.OrdinalIgnoreCase));

        var sensor = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.SensorTypeKey, ProxmoxNodeHealthSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, sensorName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Target, sensorTarget, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement(sensorName, ProxmoxNodeHealthSensorExecutor.Definition.Key, sensorTarget)
            {
                Description = "Proxmox node health monitor"
            };
            AddChild(_document.RootProbe, sensor);
        }

        sensor.ParentId = _document.RootProbe.Id;
        sensor.SensorTypeKey = ProxmoxNodeHealthSensorExecutor.Definition.Key;
        sensor.Target = sensorTarget;

        if (template is not null && !sensor.AppliedTemplateIds.Contains(template.Id))
        {
            sensor.AppliedTemplateIds.Add(template.Id);
        }

        if (sensor.Settings.Parameters.TryGetValue("payloadSize", out var legacyScope) &&
            !sensor.Settings.Parameters.ContainsKey("pve.scope"))
        {
            sensor.Settings.Parameters["pve.scope"] = string.Equals(legacyScope, "node", StringComparison.OrdinalIgnoreCase)
                ? "node"
                : "node";
            sensor.Settings.Parameters.Remove("payloadSize");
        }
        else
        {
            sensor.Settings.Parameters["pve.scope"] = "node";
            sensor.Settings.Parameters.Remove("payloadSize");
        }

        if (!sensor.Settings.Parameters.ContainsKey("pve.user"))
        {
            sensor.Settings.Parameters["pve.user"] = "root@pam";
        }

        if (!sensor.Settings.Parameters.ContainsKey("pve.tokenId"))
        {
            sensor.Settings.Parameters["pve.tokenId"] = "monitoring";
        }

        sensor.Settings.Highlight = true;
    }

    private void EnsureProbeMetadataRecursive(ProbeElement probe, bool isRoot = false)
    {
        if (string.IsNullOrWhiteSpace(probe.ProbeId))
        {
            probe.ProbeId = isRoot
                ? "master"
                : GenerateUniqueProbeId(probe.Name);
        }

        if (string.IsNullOrWhiteSpace(probe.EnrollmentToken) && !isRoot)
        {
            probe.EnrollmentToken = CreateToken();
        }

        foreach (var childProbe in probe.Children.OfType<ProbeElement>())
        {
            EnsureProbeMetadataRecursive(childProbe);
        }
    }

    private void EnsureProbeHeartbeatSensorsRecursive(ProbeElement probe, bool isRoot = false)
    {
        if (!isRoot)
        {
            EnsureProbeHeartbeatSensor(probe);
        }

        foreach (var childProbe in probe.Children.OfType<ProbeElement>())
        {
            EnsureProbeHeartbeatSensorsRecursive(childProbe);
        }
    }

    private void EnsureProbeHealthSensorsRecursive(ProbeElement probe)
    {
        EnsureProbeHealthSensor(probe);

        foreach (var childProbe in probe.Children.OfType<ProbeElement>())
        {
            EnsureProbeHealthSensorsRecursive(childProbe);
        }
    }

    private static void EnsureProbeHeartbeatSensor(ProbeElement probe)
    {
        var sensor = probe.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.SensorTypeKey, ProbeHeartbeatSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement("Heartbeat", ProbeHeartbeatSensorExecutor.Definition.Key, probe.ProbeId)
            {
                Description = "Probe heartbeat monitor"
            };
            AddChild(probe, sensor);
        }
        else
        {
            sensor.ParentId = probe.Id;
            sensor.Target = probe.ProbeId;
            if (string.IsNullOrWhiteSpace(sensor.Name))
            {
                sensor.Name = "Heartbeat";
            }
        }

        ApplyParameterDefaults(sensor.Settings, ProbeHeartbeatSensorExecutor.Definition);
        EnsureHeartbeatThresholdDefaults(sensor.Settings);
    }

    private static void EnsureProbeHealthSensor(ProbeElement probe)
    {
        var sensor = probe.Children
            .OfType<SensorElement>()
            .FirstOrDefault(candidate => string.Equals(candidate.SensorTypeKey, ProbeHealthSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase));

        if (sensor is null)
        {
            sensor = new SensorElement("Probe Health", ProbeHealthSensorExecutor.Definition.Key, probe.ProbeId)
            {
                Description = "Probe connection and storage health"
            };
            AddChild(probe, sensor);
        }
        else
        {
            sensor.ParentId = probe.Id;
            sensor.Target = probe.ProbeId;
            if (string.IsNullOrWhiteSpace(sensor.Name))
            {
                sensor.Name = "Probe Health";
            }
        }

        sensor.Settings.Enabled ??= true;
        sensor.Settings.PollingInterval ??= TimeSpan.FromSeconds(30);
        sensor.Settings.Timeout ??= TimeSpan.FromSeconds(5);
        sensor.Settings.DefaultChannelKey ??= "storageFreePercent";
        ApplyParameterDefaults(sensor.Settings, ProbeHealthSensorExecutor.Definition);
        EnsureProbeHealthThresholdDefaults(sensor.Settings);
    }

    private static void EnsureHeartbeatThresholdDefaults(MonitoringSettings settings)
    {
        var warningKey = MonitoringSettings.BuildChannelThresholdKey("ageSeconds", "warning");
        var criticalKey = MonitoringSettings.BuildChannelThresholdKey("ageSeconds", "critical");

        if (!settings.Thresholds.ContainsKey(warningKey))
        {
            settings.Thresholds[warningKey] = MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 30));
        }

        if (!settings.Thresholds.ContainsKey(criticalKey))
        {
            settings.Thresholds[criticalKey] = MonitoringSettings.FormatThresholdRule(new ThresholdRule(ThresholdDirection.Above, 60));
        }
    }

    private static void EnsureProbeHealthThresholdDefaults(MonitoringSettings settings)
    {
        SetDefaultChannelThreshold(settings, "storageFreePercent", "warning", new ThresholdRule(ThresholdDirection.Below, 15));
        SetDefaultChannelThreshold(settings, "storageFreePercent", "critical", new ThresholdRule(ThresholdDirection.Below, 8));
        SetDefaultChannelThreshold(settings, "connected", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
        SetDefaultChannelThreshold(settings, "dataPathAvailable", "critical", new ThresholdRule(ThresholdDirection.Below, 0.5));
    }

    private static void ApplyParameterDefaults(MonitoringSettings settings, SensorDefinition definition)
    {
        foreach (var parameter in definition.Parameters)
        {
            if (parameter.DefaultValue is null)
            {
                continue;
            }

            if (!settings.Parameters.ContainsKey(parameter.Key))
            {
                settings.Parameters[parameter.Key] = parameter.DefaultValue;
            }
        }
    }

    private MonitoringContainerElement ResolveParentContainer(Guid? parentId, params MonitoringElementKind[] allowedKinds)
    {
        var parent = parentId is Guid id
            ? FindElement(id)
            : _document.RootProbe;

        if (parent is null)
        {
            throw new InvalidOperationException("Parent element was not found.");
        }

        if (parent is not MonitoringContainerElement container)
        {
            throw new InvalidOperationException("Selected parent cannot contain children.");
        }

        if (allowedKinds.Length > 0 && !allowedKinds.Contains(parent.Kind))
        {
            throw new InvalidOperationException($"Parent kind '{parent.Kind}' is not allowed here.");
        }

        return container;
    }

    private static MonitoringElementKind[] GetAllowedParentKinds(MonitoringElement element)
    {
        return element switch
        {
            ProbeElement => [MonitoringElementKind.Probe],
            FolderElement => [MonitoringElementKind.Probe, MonitoringElementKind.Folder],
            HostElement => [MonitoringElementKind.Probe, MonitoringElementKind.Folder],
            SensorElement => [MonitoringElementKind.Probe, MonitoringElementKind.Folder, MonitoringElementKind.Host],
            _ => []
        };
    }

    private static MonitoringContainerElement? FindParentContainer(MonitoringContainerElement parent, Guid childId)
    {
        if (parent.Children.Any(candidate => candidate.Id == childId))
        {
            return parent;
        }

        foreach (var childContainer in parent.Children.OfType<MonitoringContainerElement>())
        {
            var found = FindParentContainer(childContainer, childId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void AddChild(MonitoringContainerElement parent, MonitoringElement child)
    {
        child.ParentId = parent.Id;
        parent.Children.Add(child);
    }

    private static bool RemoveChild(MonitoringContainerElement parent, Guid id)
    {
        var child = parent.Children.FirstOrDefault(candidate => candidate.Id == id);
        if (child is not null)
        {
            parent.Children.Remove(child);
            return true;
        }

        foreach (var childContainer in parent.Children.OfType<MonitoringContainerElement>())
        {
            if (RemoveChild(childContainer, id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One-time migration from the old live template-inheritance model to copy + origin: bake each
    /// element's applied templates' values into its own settings (the element's own values win),
    /// record the last applied template as the origin, and clear the legacy link list.
    /// </summary>
    private void MigrateAppliedTemplatesToCopies()
    {
        var elements = EnumerateElements(_document.RootProbe)
            .Where(element => element.AppliedTemplateIds.Count > 0)
            .ToList();

        if (elements.Count == 0)
        {
            return;
        }

        var templateMap = _document.Templates.ToDictionary(template => template.Id);
        var resolver = new MonitoringInheritanceResolver();

        foreach (var element in elements)
        {
            var baked = new MonitoringSettings();
            var origin = element.TemplateOriginId;

            foreach (var templateId in element.AppliedTemplateIds)
            {
                if (templateMap.TryGetValue(templateId, out var template))
                {
                    baked.ApplyFrom(resolver.ResolveTemplate(template, templateMap));
                    origin = templateId;
                }
            }

            baked.ApplyFrom(element.Settings);
            element.Settings = baked;
            element.TemplateOriginId = origin;
            element.AppliedTemplateIds.Clear();
        }

        QueueSave(SavePriority.Configuration);
    }

    /// <summary>
    /// One-time migration of SSL certificate sensors from the legacy ssl.warningDays /
    /// ssl.criticalDays parameters to real remainingDays channel thresholds (carrying any custom
    /// values over), so expiry warning/critical is configured like every other sensor's thresholds.
    /// </summary>
    private void MigrateSslCertificateThresholds()
    {
        var sslSensors = EnumerateElements(_document.RootProbe)
            .OfType<SensorElement>()
            .Where(sensor => string.Equals(sensor.SensorTypeKey, SslCertificateSensorExecutor.Definition.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sslSensors.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var sensor in sslSensors)
        {
            var hadParams = sensor.Settings.Parameters.ContainsKey("ssl.warningDays")
                || sensor.Settings.Parameters.ContainsKey("ssl.criticalDays");
            var hadWarning = MonitoringSettings.TryReadChannelThreshold(
                sensor.Settings, SslCertificateSensorExecutor.RemainingDaysChannelKey, "warning", out _);

            SslCertificateSensorExecutor.EnsureDefaultThresholds(sensor.Settings);

            if (hadParams || !hadWarning)
            {
                changed = true;
            }
        }

        if (changed)
        {
            QueueSave(SavePriority.Configuration);
        }
    }

    // Thin delegator kept because ~29 call sites across the store partials use the short name.
    private static IEnumerable<MonitoringElement> EnumerateElements(MonitoringElement element) =>
        MonitoringTopology.EnumerateSelfAndDescendants(element);

    private string GenerateUniqueProbeId(string name)
    {
        var baseId = Slugify(name);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "probe";
        }

        var existing = EnumerateElements(_document.RootProbe)
            .OfType<ProbeElement>()
            .Select(probe => probe.ProbeId)
            .Where(probeId => !string.IsNullOrWhiteSpace(probeId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = baseId;
        var suffix = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
                continue;
            }

            if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string CreateToken()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }


    private string GetElementName(Guid elementId)
    {
        return FindElementInternal(elementId)?.Name ?? elementId.ToString("N");
    }

    private string GetElementPath(Guid elementId)
    {
        var element = FindElementInternal(elementId);
        return element is null ? elementId.ToString("N") : GetElementPath(element);
    }

    private string GetElementPath(MonitoringElement element)
    {
        var lineage = BuildLineage(element);
        return string.Join(" / ", lineage.Select(node => node.Name));
    }

    private IReadOnlyList<MonitoringElement> BuildLineage(MonitoringElement element)
    {
        var lineage = new List<MonitoringElement>();
        var current = element;

        while (true)
        {
            lineage.Add(current);

            if (current.ParentId is not Guid parentId)
            {
                break;
            }

            var parent = FindElementInternal(parentId);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }

        lineage.Reverse();
        return lineage;
    }

    private MonitoringElement? FindElementInternal(Guid id)
    {
        return EnumerateElements(_document.RootProbe).FirstOrDefault(element => element.Id == id);
    }

    private static string BuildStateChangeMessage(SensorState? previousState, SensorState currentState, string? message)
    {
        var transition = previousState is null
            ? $"state {MonitoringStatePresentation.Label(currentState)}"
            : $"{MonitoringStatePresentation.Label(previousState.Value)} -> {MonitoringStatePresentation.Label(currentState)}";

        if (string.IsNullOrWhiteSpace(message))
        {
            return transition;
        }

        return $"{transition}: {message}";
    }

    private static string AppendExecutionProbe(string message, string? probeName, string? probeId)
    {
        if (string.IsNullOrWhiteSpace(probeName) && string.IsNullOrWhiteSpace(probeId))
        {
            return message;
        }

        var probe = string.IsNullOrWhiteSpace(probeName)
            ? probeId!.Trim()
            : string.IsNullOrWhiteSpace(probeId)
                ? probeName.Trim()
                : $"{probeName.Trim()} ({probeId.Trim()})";
        return $"{message} via {probe}";
    }

    private void ResolveAlertsForElement(Guid elementId, DateTimeOffset resolvedAt, string message)
    {
        foreach (var alert in _document.Alerts.Where(alert => alert.IsActive && alert.ElementId == elementId))
        {
            alert.ResolvedUtc = resolvedAt;
            if (!string.IsNullOrWhiteSpace(message))
            {
                alert.Message = message;
            }

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = resolvedAt,
                Kind = MonitoringEventKind.AlertResolved,
                ElementId = alert.ElementId,
                ElementKind = alert.ElementKind,
                ElementName = alert.ElementName,
                ElementPath = alert.ElementPath,
                State = alert.State,
                Message = alert.Message
            });
        }
    }

    /// <summary>
    /// Alerta-style recovery: when a sensor returns to a healthy state, an alert that
    /// the operator already acknowledged is resolved (worked off + condition cleared),
    /// but an unacknowledged alert is only flagged recovered and stays active so it
    /// remains visible until someone acknowledges it.
    /// </summary>
    private void MarkAlertsRecoveredForElement(Guid elementId, DateTimeOffset recoveredAt)
    {
        foreach (var alert in _document.Alerts.Where(alert => alert.IsActive && alert.ElementId == elementId))
        {
            if (alert.IsAcknowledged)
            {
                alert.RecoveredUtc ??= recoveredAt;
                alert.ResolvedUtc = recoveredAt;
                AddEvent(new MonitoringEvent
                {
                    TimestampUtc = recoveredAt,
                    Kind = MonitoringEventKind.AlertResolved,
                    ElementId = alert.ElementId,
                    ElementKind = alert.ElementKind,
                    ElementName = alert.ElementName,
                    ElementPath = alert.ElementPath,
                    State = alert.State,
                    Message = alert.Message
                });
                _notificationSink?.Enqueue(new AlertNotificationEvent(
                    alert.Id, alert.ElementId, SensorState.Healthy, "Condition cleared", recoveredAt, NotificationTransition.Recovered));
            }
            else if (alert.RecoveredUtc is null)
            {
                // Condition cleared but nobody has acknowledged it yet - keep it open.
                alert.RecoveredUtc = recoveredAt;
                _notificationSink?.Enqueue(new AlertNotificationEvent(
                    alert.Id, alert.ElementId, SensorState.Healthy, "Condition cleared", recoveredAt, NotificationTransition.Recovered));
            }
        }
    }

    private void SyncSensorAlertFromObservation(
        Guid sensorId,
        SensorExecutionResult result,
        DateTimeOffset timestampUtc)
    {
        if (result.State is not (SensorState.Warning or SensorState.Critical))
        {
            MarkAlertsRecoveredForElement(sensorId, timestampUtc);
            return;
        }

        // Muted element: the operator worked it off and asked not to be re-alarmed - don't raise/re-open
        // (and so fire no notification) until the mute lifts. Mute already cleared any active episode.
        if (IsElementMutedLocked(sensorId, timestampUtc))
        {
            return;
        }

        var sensor = FindElementInternal(sensorId) as SensorElement;
        if (sensor is null)
        {
            return;
        }

        var path = GetElementPath(sensor);
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? MonitoringStatePresentation.Label(result.State)
            : result.Message;
        var existing = _document.Alerts.FirstOrDefault(alert => alert.IsActive && alert.ElementId == sensorId);
        if (existing is null)
        {
            var raised = new MonitoringAlert
            {
                ElementId = sensor.Id,
                ElementKind = sensor.Kind,
                ElementName = sensor.Name,
                ElementPath = path,
                State = result.State,
                Message = message,
                FirstSeenUtc = timestampUtc,
                LastSeenUtc = timestampUtc
            };
            _document.Alerts.Add(raised);

            AddEvent(new MonitoringEvent
            {
                TimestampUtc = timestampUtc,
                Kind = MonitoringEventKind.AlertRaised,
                ElementId = sensor.Id,
                ElementKind = sensor.Kind,
                ElementName = sensor.Name,
                ElementPath = path,
                State = result.State,
                Message = message
            });

            // Hand the raised alert to the notification dispatcher (non-blocking enqueue). Matching,
            // rendering and SMTP delivery happen off this hot path in NotificationDispatchService.
            _notificationSink?.Enqueue(new AlertNotificationEvent(
                raised.Id, raised.ElementId, raised.State, message, timestampUtc, NotificationTransition.Raised));
            return;
        }

        existing.ElementKind = sensor.Kind;
        existing.ElementName = sensor.Name;
        existing.ElementPath = path;
        existing.State = result.State;
        existing.Message = message;
        existing.LastSeenUtc = timestampUtc;

        // Re-alarm transition: the alert had cleared (RecoveredUtc set) and is now firing again. Re-open the
        // episode and notify - otherwise the re-fire is silent AND, because the notification episode stays
        // closed, the *next* recovery is dropped too. The dispatcher applies the per-rule cooldown, so a
        // flapping sensor still can't spam. (No enqueue while it stays continuously active - only on the flip.)
        if (existing.RecoveredUtc is not null)
        {
            existing.RecoveredUtc = null;
            _notificationSink?.Enqueue(new AlertNotificationEvent(
                existing.Id, existing.ElementId, result.State, message, timestampUtc, NotificationTransition.Raised));
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static SensorDefinition CloneSensorDefinition(SensorDefinition source)
    {
        return new SensorDefinition
        {
            Key = source.Key,
            DisplayName = source.DisplayName,
            Description = source.Description,
            UsageLevel = source.UsageLevel ?? SensorUsageCatalog.Resolve(source.Key),
            ChannelMode = source.ChannelMode,
            Parameters = source.Parameters.Select(parameter => new SensorParameterDefinition
            {
                Key = parameter.Key,
                Label = parameter.Label,
                Group = parameter.Group,
                Kind = parameter.Kind,
                Description = parameter.Description,
                Required = parameter.Required,
                DefaultValue = parameter.DefaultValue,
                Placeholder = parameter.Placeholder,
                Min = parameter.Min,
                Max = parameter.Max,
                Step = parameter.Step,
                CredentialKind = parameter.CredentialKind,
                VisibleWhenParameterKey = parameter.VisibleWhenParameterKey,
                VisibleWhenValues = parameter.VisibleWhenValues.ToArray(),
                Options = parameter.Options.Select(option => new SensorParameterOption
                {
                    Value = option.Value,
                    Label = option.Label
                }).ToArray()
            }).ToArray()
        };
    }

    private static MatmonUser CloneUser(MatmonUser source)
    {
        return new MatmonUser
        {
            Id = source.Id,
            Username = source.Username,
            Email = source.Email,
            PasswordHash = string.Empty, // never expose the hash in a read clone
            Role = source.Role,
            IsEnabled = source.IsEnabled,
            CloudLinked = source.CloudLinked,
            CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc,
            LastLoginUtc = source.LastLoginUtc,
            TimeZoneId = source.TimeZoneId,
            TwoFactorEnabled = source.TwoFactorEnabled,
            TotpEnrolledUtc = source.TotpEnrolledUtc
            // TotpSecretProtected intentionally NOT copied (like PasswordHash) - verification stays inside the store.
        };
    }

    // Map/tile cloning is defined once on the domain type (MonitoringMap.Clone) so a newly added field is
    // copied in a single place. These thin wrappers keep the existing call sites.
    private static MonitoringMapTile CloneMapTile(MonitoringMapTile tile) => tile.Clone();

    private static MonitoringMap CloneMap(MonitoringMap source) => source.Clone();

    private static string NormalizeUsername(string username)
    {
        var normalized = username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Username is required.");
        }

        if (normalized.Length > 80)
        {
            throw new InvalidOperationException("Username is too long.");
        }

        return normalized;
    }

    private static string NormalizeMapName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? "Map" : normalized;
    }

    private static IReadOnlyList<MonitoringMapTile> NormalizeMapTiles(
        IReadOnlyList<MonitoringMapTile> tiles,
        int columns,
        int rows)
    {
        return tiles
            .Where(tile => !string.IsNullOrWhiteSpace(tile.Title) || !string.IsNullOrWhiteSpace(tile.Text) || tile.ElementId.HasValue || !string.IsNullOrWhiteSpace(tile.TargetTag))
            .Select(tile =>
            {
                var sizeLimits = GetMapTileSizeLimits(tile.Kind, columns, rows);
                var width = Math.Clamp(tile.Width <= 0 ? sizeLimits.DefaultWidth : tile.Width, sizeLimits.MinWidth, sizeLimits.MaxWidth);
                var height = Math.Clamp(tile.Height <= 0 ? sizeLimits.DefaultHeight : tile.Height, sizeLimits.MinHeight, sizeLimits.MaxHeight);
                var x = Math.Clamp(tile.X <= 0 ? 1 : tile.X, 1, Math.Max(1, columns - width + 1));
                var y = Math.Clamp(tile.Y <= 0 ? 1 : tile.Y, 1, Math.Max(1, rows - height + 1));

                return new MonitoringMapTile
                {
                    Id = tile.Id == Guid.Empty ? Guid.NewGuid() : tile.Id,
                    Kind = tile.Kind,
                    Title = string.IsNullOrWhiteSpace(tile.Title) ? "Tile" : tile.Title.Trim(),
                    ElementId = tile.ElementId == Guid.Empty ? null : tile.ElementId,
                    TargetTag = string.IsNullOrWhiteSpace(tile.TargetTag) ? null : tile.TargetTag.Trim(),
                    Text = string.IsNullOrWhiteSpace(tile.Text) ? null : tile.Text.Trim(),
                    IconKey = string.IsNullOrWhiteSpace(tile.IconKey) ? null : tile.IconKey.Trim(),
                    ShowCard = tile.ShowCard,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    BackgroundColor = NormalizeColor(tile.BackgroundColor),
                    AccentColor = NormalizeColor(tile.AccentColor),
                    TextColor = NormalizeColor(tile.TextColor),
                    GraphType = tile.GraphType,
                    VisualType = tile.VisualType,
                    ShowTitle = tile.ShowTitle,
                    ShowStateBadge = tile.ShowStateBadge,
                    ShowElementName = tile.ShowElementName
                };
            })
            .ToArray();
    }

    private static MapTileSizeLimits GetMapTileSizeLimits(MonitoringMapTileKind kind, int columns, int rows)
    {
        var limits = kind switch
        {
            MonitoringMapTileKind.Text => new MapTileSizeLimits(2, 1, 12, 6, 4, 2),
            MonitoringMapTileKind.Status => new MapTileSizeLimits(3, 2, 12, 8, 4, 2),
            MonitoringMapTileKind.Value => new MapTileSizeLimits(2, 2, 8, 6, 3, 2),
            MonitoringMapTileKind.Graph => new MapTileSizeLimits(4, 3, 12, 10, 5, 3),
            _ => new MapTileSizeLimits(2, 1, 8, 6, 3, 2)
        };

        var maxWidth = Math.Clamp(limits.MaxWidth, limits.MinWidth, columns);
        var maxHeight = Math.Clamp(limits.MaxHeight, limits.MinHeight, rows);
        return limits with
        {
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            DefaultWidth = Math.Clamp(limits.DefaultWidth, limits.MinWidth, maxWidth),
            DefaultHeight = Math.Clamp(limits.DefaultHeight, limits.MinHeight, maxHeight)
        };
    }

    private static string? NormalizeColor(string? color)
    {
        var normalized = color?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length == 7 &&
            normalized[0] == '#' &&
            normalized.Skip(1).All(Uri.IsHexDigit))
        {
            return normalized.ToLowerInvariant();
        }

        return null;
    }

    private sealed record MapTileSizeLimits(
        int MinWidth,
        int MinHeight,
        int MaxWidth,
        int MaxHeight,
        int DefaultWidth,
        int DefaultHeight);

    private sealed class WorkspaceDocument
    {
        public ProbeElement RootProbe { get; set; } = default!;

        public List<MonitoringTemplate> Templates { get; set; } = [];

        public List<SensorDefinition> SensorDefinitions { get; set; } = [];

        public List<MatmonUser> Users { get; set; } = [];

        /// <summary>
        /// When the first-run setup (initial admin account) was completed. Null means the workspace
        /// has no provisioned account yet and the setup wizard should run. Existing/seeded installs
        /// are migrated to "completed" on load so the wizard never hijacks a configured instance.
        /// </summary>
        public DateTimeOffset? SetupCompletedUtc { get; set; }

        public List<MonitoringMap> Maps { get; set; } = [];

        public NotificationWorkspaceConfiguration NotificationConfiguration { get; set; } = new();

        public SummaryReportSettings SummaryReport { get; set; } = new();

        public CloudConnectionState Cloud { get; set; } = new();

        public CloudConnectionSettings CloudSettings { get; set; } = new();

        /// <summary>Last license token fetched from the cloud (verified offline against the baked public key).</summary>
        public string? LicenseToken { get; set; }

        /// <summary>Managing service partner (name/contact + consent), cached from the cloud on heartbeat.</summary>
        public ServicePartnerInfo? ServicePartner { get; set; }

        /// <summary>System-wide display timezone (IANA id); null = server local.</summary>
        public string? DisplayTimeZoneId { get; set; }

        public List<NotificationSender> NotificationSenders { get; set; } = [];

        public List<NotificationReceiver> NotificationReceivers { get; set; } = [];

        public List<NotificationRule> NotificationRules { get; set; } = [];

        public List<MonitoringAlert> Alerts { get; set; } = [];

        public List<AlertMute> AlertMutes { get; set; } = [];

        public List<WorkspaceBackupJob> BackupJobs { get; set; } = [];

        public List<SensorObservation> SensorHistory { get; set; } = [];

        public List<MonitoringEvent> Events { get; set; } = [];

        public List<SensorStatisticsBucket> SensorStatistics { get; set; } = [];
    }


    private enum SavePriority
    {
        Configuration,
        Telemetry
    }
}
