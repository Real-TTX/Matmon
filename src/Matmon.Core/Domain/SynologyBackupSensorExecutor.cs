using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Matmon.Core.Domain;

/// <summary>
/// The Hyper Backup tasks of a Synology NAS through the DSM Web API: one channel per task with the result of its last run
/// (0 = done, 1 = partial / cancelled / suspended, 2 = failed) in that task's own state, the hours since its last successful
/// backup, and whether it missed its schedule (the next run lies more than <see cref="MissedGrace"/> in the past while nothing
/// runs). The worst task decides the sensor; the message names every task that is not fine.
/// <para>Sign-in: <c>SYNO.API.Auth</c> with a DSM user (a dedicated one in the administrators group - Hyper Backup's task list
/// needs it; an account with two-step verification cannot sign in from a program), the password sent in a POST body, never in
/// the address; the session is logged out again after every check. API paths and versions come from <c>SYNO.API.Info</c>.
/// The field names follow what DSM 7 answers for <c>SYNO.Backup.Task</c> <c>list</c>; tested against a stand-in, NOT against a
/// real NAS.</para>
/// </summary>
public sealed class SynologyBackupSensorExecutor : ISensorExecutor
{
    public const int DefaultPort = 5001;
    public const string FailedChannel = "failedTasks";
    public const string WarningChannel = "warningTasks";
    public const string MissedChannel = "missedTasks";
    public const string OldestSuccessChannel = "oldestSuccessHours";

    /// <summary>A scheduled run counts as missed once its time is this far behind and nothing runs.</summary>
    public static readonly TimeSpan MissedGrace = TimeSpan.FromHours(2);

    /// <summary>At most this many task channels.</summary>
    public const int MaxTaskChannels = 100;

    private readonly Func<bool, HttpMessageHandler> _handlerFactory;

    public SynologyBackupSensorExecutor()
        : this(DefaultHandler)
    {
    }

    public SynologyBackupSensorExecutor(Func<bool, HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;

    public static SensorDefinition Definition { get; } = new()
    {
        Key = "synology-backup",
        DisplayName = "Synology Hyper Backup",
        Description = "The Hyper Backup tasks of a Synology NAS via the DSM Web API: one channel per task - the result of its last run, the hours since its last successful backup and a missed schedule.",
        ChannelMode = SensorChannelMode.Dynamic,
        Parameters =
        [
            new SensorParameterDefinition
            {
                Key = "generic.username",
                Label = "DSM user",
                Kind = SensorParameterKind.Text,
                Description = "A DSM account in the administrators group (Hyper Backup lists its tasks to administrators only). Best a dedicated one without two-step verification - a program cannot answer the second step.",
                Required = true,
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "generic.password",
                Label = "Password",
                Kind = SensorParameterKind.Secret,
                Required = true,
                CredentialKind = MonitoringCredentialKind.Generic
            },
            new SensorParameterDefinition
            {
                Key = "synology.port",
                Label = "Port",
                Group = "Connection",
                Kind = SensorParameterKind.Integer,
                Description = "DSM's port: 5001 for HTTPS (default), 5000 for HTTP. A target with its own scheme (https://nas:5001) wins.",
                DefaultValue = "5001",
                Min = 1,
                Max = 65535,
                Step = "1"
            },
            new SensorParameterDefinition
            {
                Key = "synology.verifySsl",
                Label = "Verify SSL",
                Group = "Connection",
                Kind = SensorParameterKind.Boolean,
                Description = "Validate the NAS's certificate (a fresh DSM has a self-signed one).",
                DefaultValue = "false"
            }
        ]
    };

    public string SensorTypeKey => Definition.Key;

    public async ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        var target = context.Target?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "The NAS's address is missing (the host's address, or a target).");
        }

        MonitoringSettings.TryReadParameter(context.Settings, "generic.username", out var user);
        MonitoringSettings.TryReadParameter(context.Settings, "generic.password", out var password);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, "A DSM user and its password are needed.");
        }

        if (!target.Contains("://", StringComparison.Ordinal))
        {
            var port = MonitoringSettings.TryReadParameterInt(context.Settings, "synology.port", out var configured) && configured is >= 1 and <= 65535 ? configured : DefaultPort;
            var scheme = port == 5000 ? "http" : "https";
            target = $"{scheme}://{target}:{port}";
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return SensorExecutionResult.Critical(TimeSpan.Zero, $"'{context.Target}' is not an address.");
        }

        var verifySsl = MonitoringSettings.TryReadParameterBool(context.Settings, "synology.verifySsl", out var verify) && verify;
        var timeout = context.Settings.Timeout ?? TimeSpan.FromSeconds(20);
        var watch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var client = new HttpClient(_handlerFactory(verifySsl), disposeHandler: true)
        {
            BaseAddress = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/webapi/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        string? sid = null;
        string? authPath = null;
        try
        {
            var info = await CallAsync(client, "query.cgi", new()
            {
                ["api"] = "SYNO.API.Info", ["version"] = "1", ["method"] = "query", ["query"] = "SYNO.API.Auth,SYNO.Backup.Task"
            }, timeoutCts.Token);
            var auth = Api(info, "SYNO.API.Auth") ?? ("entry.cgi", 6);
            var backup = Api(info, "SYNO.Backup.Task")
                ?? throw new SynologyException("This NAS has no Hyper Backup (the API SYNO.Backup.Task is missing) - install Hyper Backup, or use the Synology Health sensor.");
            authPath = auth.Path;

            var login = await CallAsync(client, auth.Path, new()
            {
                ["api"] = "SYNO.API.Auth", ["version"] = Math.Min(auth.MaxVersion, 6).ToString(CultureInfo.InvariantCulture), ["method"] = "login",
                ["account"] = user.Trim(), ["passwd"] = password, ["session"] = "MatmonBackup", ["format"] = "sid"
            }, timeoutCts.Token, describeError: LoginError);
            sid = login.TryGetProperty("sid", out var sidValue) ? sidValue.GetString() : null;
            if (string.IsNullOrEmpty(sid))
            {
                throw new SynologyException("DSM signed in but returned no session.");
            }

            var list = await CallAsync(client, backup.Path, new()
            {
                ["api"] = "SYNO.Backup.Task", ["version"] = "1", ["method"] = "list",
                ["additional"] = "[\"last_bkp_time\",\"next_bkp_time\",\"last_bkp_result\",\"last_bkp_success_time\",\"is_modified\",\"last_bkp_progress\"]",
                ["_sid"] = sid
            }, timeoutCts.Token);
            watch.Stop();
            var tasks = ParseTasks(list);
            return SensorThresholdEvaluator.ApplyChannelThresholds(context.Settings, BuildResult(tasks, DateTimeOffset.UtcNow, LocalOffset(), watch.Elapsed));
        }
        catch (SynologyException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The NAS at {uri.Host} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException exception)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"The NAS at {uri.Host} cannot be reached: {exception.Message}");
        }
        catch (JsonException)
        {
            return SensorExecutionResult.Critical(watch.Elapsed, $"{uri.Host}:{uri.Port} does not answer like Synology DSM.");
        }
        finally
        {
            if (sid is not null && authPath is not null)
            {
                try
                {
                    using var logout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await CallAsync(client, authPath, new() { ["api"] = "SYNO.API.Auth", ["version"] = "1", ["method"] = "logout", ["session"] = "MatmonBackup", ["_sid"] = sid }, logout.Token);
                }
                catch
                {
                    // The session expires on its own.
                }
            }
        }
    }

    // ---- the result (pure) -----------------------------------------------------------------------------------------------

    /// <summary>One Hyper Backup task as DSM lists it. Times are the NAS's local wall-clock times ("2026/11/10 02:00").</summary>
    public sealed record HyperBackupTask(string Id, string Name, string? LastResult, string? Status, DateTime? LastRun, DateTime? LastSuccess, DateTime? NextRun);

    /// <summary>The task list of a <c>SYNO.Backup.Task list</c> answer (<c>data.task_list</c>, older DSM: <c>data.tasks</c>).</summary>
    public static IReadOnlyList<HyperBackupTask> ParseTasks(JsonElement data)
    {
        var array = data.TryGetProperty("task_list", out var list) && list.ValueKind == JsonValueKind.Array ? list
            : data.TryGetProperty("tasks", out var older) && older.ValueKind == JsonValueKind.Array ? older
            : default;
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(task => new HyperBackupTask(
                Text(task, "task_id") ?? Text(task, "id") ?? Guid.NewGuid().ToString("N"),
                Text(task, "name") ?? Text(task, "task_name") ?? "Backup task",
                Text(task, "last_bkp_result"),
                Text(task, "status"),
                Time(task, "last_bkp_time"),
                Time(task, "last_bkp_success_time"),
                Time(task, "next_bkp_time")))
            .ToList();
    }

    /// <summary>
    /// One channel per task (`task.&lt;slug&gt;.result`, 0 / 1 / 2 in the task's own state, its words as the message) plus
    /// `task.&lt;slug&gt;.successAgeHours`; the totals failed / with warnings / missed and the oldest last success. The worst
    /// task decides: a failed one is critical, a partial or cancelled one or a missed schedule a warning, a running one fine.
    /// </summary>
    public static SensorExecutionResult BuildResult(IReadOnlyList<HyperBackupTask> tasks, DateTimeOffset now, TimeSpan nasOffset, TimeSpan duration)
    {
        if (tasks.Count == 0)
        {
            return SensorExecutionResult.Warning(duration, "Hyper Backup has no backup task - or this user may not see them (the task list is shown to administrators only).");
        }

        var channels = new List<SensorChannelValue>();
        var problems = new List<string>();
        int failed = 0, warned = 0, missed = 0;
        double? oldestSuccess = null;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks.Take(MaxTaskChannels))
        {
            var (code, words) = Judge(task, now, nasOffset);
            if (code == 2) failed++;
            if (code == 1 && words.StartsWith("missed", StringComparison.Ordinal)) missed++;
            else if (code == 1) warned++;
            if (code > 0) problems.Add($"{task.Name}: {words}");

            var lastSuccess = task.LastSuccess ?? (Outcome(task.LastResult) == 0 ? task.LastRun : null);
            double? successAge = lastSuccess is { } success ? Math.Round(Math.Max(0, (now - ToUtc(success, nasOffset)).TotalHours), 1) : null;
            if (successAge is { } age)
            {
                oldestSuccess = oldestSuccess is null ? age : Math.Max(oldestSuccess.Value, age);
            }

            var slug = RedfishSensorExecutor.Slug(task.Name);
            if (!used.Add(slug))
            {
                slug = $"{slug}-{RedfishSensorExecutor.Slug(task.Id)}";
                used.Add(slug);
            }

            channels.Add(new SensorChannelValue
            {
                Key = $"task.{slug}.result",
                Label = $"{task.Name} - last run",
                Value = code,
                MeasurementKind = SensorMeasurementKind.Count,
                State = code switch { 2 => SensorState.Critical, 1 => SensorState.Warning, _ => SensorState.Healthy },
                Message = words
            });
            channels.Add(new SensorChannelValue
            {
                Key = $"task.{slug}.successAgeHours",
                Label = $"{task.Name} - last successful backup",
                Value = successAge,
                Unit = "h",
                MeasurementKind = SensorMeasurementKind.Count,
                Message = lastSuccess is { } shown ? shown.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (NAS time)" : "never"
            });
        }

        channels.InsertRange(0,
        [
            new SensorChannelValue { Key = FailedChannel, Label = "Failed tasks", Value = failed, MeasurementKind = SensorMeasurementKind.Count, IsDefault = true },
            new SensorChannelValue { Key = WarningChannel, Label = "Tasks with warnings", Value = warned, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false },
            new SensorChannelValue { Key = MissedChannel, Label = "Tasks that missed their schedule", Value = missed, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false },
            new SensorChannelValue { Key = "tasks", Label = "Backup tasks", Value = tasks.Count, MeasurementKind = SensorMeasurementKind.Count, LogByDefault = false }
        ]);
        if (oldestSuccess is { } oldest)
        {
            channels.Insert(4, new SensorChannelValue { Key = OldestSuccessChannel, Label = "Oldest last successful backup", Value = oldest, Unit = "h", MeasurementKind = SensorMeasurementKind.Count });
        }

        var message = problems.Count == 0
            ? $"{tasks.Count} backup task{(tasks.Count == 1 ? string.Empty : "s")}, all fine"
            : $"{problems.Count} of {tasks.Count} backup task{(tasks.Count == 1 ? string.Empty : "s")} not fine - {string.Join("; ", problems.Take(5))}{(problems.Count > 5 ? "; …" : string.Empty)}";
        return failed > 0 ? SensorExecutionResult.Critical(duration, message, failed, FailedChannel, channels)
            : warned + missed > 0 ? SensorExecutionResult.Warning(duration, message, failed, FailedChannel, channels)
            : SensorExecutionResult.Healthy(duration, message, failed, FailedChannel, channels);
    }

    /// <summary>A task's state in one number and a few words: 0 fine, 1 warning, 2 failed.</summary>
    public static (int Code, string Words) Judge(HyperBackupTask task, DateTimeOffset now, TimeSpan nasOffset)
    {
        var running = task.Status is { } status && (status.Contains("backup", StringComparison.OrdinalIgnoreCase) || status.Contains("running", StringComparison.OrdinalIgnoreCase))
            || string.Equals(task.LastResult, "backingup", StringComparison.OrdinalIgnoreCase);
        if (running)
        {
            return (0, "backing up right now");
        }

        var outcome = Outcome(task.LastResult);
        if (outcome == 2)
        {
            return (2, $"last run failed{When(task.LastRun)}");
        }

        if (task.NextRun is { } next && now - ToUtc(next, nasOffset) > MissedGrace)
        {
            return (1, $"missed its scheduled run of {next.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        }

        return outcome switch
        {
            1 => (1, $"last run {Describe(task.LastResult)}{When(task.LastRun)}"),
            0 => (0, $"last run done{When(task.LastRun)}"),
            _ => (0, task.LastRun is null ? "not run yet" : $"last run {Describe(task.LastResult)}{When(task.LastRun)}")
        };
    }

    /// <summary>Hyper Backup's result word: done / partial / failed / cancel / suspended / none …</summary>
    public static int? Outcome(string? result) => result?.Trim().ToLowerInvariant() switch
    {
        "done" or "success" or "succeeded" or "ok" => 0,
        "partial" or "partial_success" or "cancel" or "cancelled" or "canceled" or "suspend" or "suspended" or "warning" => 1,
        "failed" or "fail" or "error" or "broken" => 2,
        _ => null
    };

    private static string Describe(string? result) => string.IsNullOrWhiteSpace(result) ? "ended" : result.Trim().ToLowerInvariant() switch
    {
        "partial" or "partial_success" => "partially done",
        "cancel" or "cancelled" or "canceled" => "cancelled",
        "suspend" or "suspended" => "suspended",
        var other => other
    };

    private static string When(DateTime? time) => time is { } at ? $" ({at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})" : string.Empty;

    private static DateTimeOffset ToUtc(DateTime nasLocal, TimeSpan nasOffset) => new DateTimeOffset(DateTime.SpecifyKind(nasLocal, DateTimeKind.Unspecified), nasOffset).ToUniversalTime();

    /// <summary>The NAS's times carry no zone; the probe's own zone is taken for them (a NAS and its probe nearly always share one).</summary>
    private static TimeSpan LocalOffset() => TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);

    // ---- the Web API ------------------------------------------------------------------------------------------------------

    private static (string Path, int MaxVersion)? Api(JsonElement info, string name) =>
        info.TryGetProperty(name, out var api) && api.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } text
            ? (text, api.TryGetProperty("maxVersion", out var max) && max.TryGetInt32(out var version) ? version : 1)
            : null;

    private static async Task<JsonElement> CallAsync(HttpClient client, string path, Dictionary<string, string> form, CancellationToken cancellationToken, Func<int, string>? describeError = null)
    {
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SynologyException($"DSM answered {(int)response.StatusCode} for {form["api"]}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var number) ? number : 0;
            throw new SynologyException(describeError?.Invoke(code) ?? GeneralError(form["api"], code));
        }

        return root.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    /// <summary>SYNO.API.Auth's error codes in words.</summary>
    public static string LoginError(int code) => code switch
    {
        400 => "DSM refused the sign-in: wrong user name or password.",
        401 => "DSM refused the sign-in: the account is disabled.",
        402 => "DSM refused the sign-in: the account may not use this service.",
        403 or 404 or 406 => "DSM asks this account for two-step verification - a program cannot answer it. Use an account without it.",
        407 => "DSM has blocked this address after too many failed sign-ins (Control Panel → Security → Protection).",
        408 or 409 or 410 => "DSM refused the sign-in: the password has expired and must be changed.",
        _ => $"DSM refused the sign-in (error {code})."
    };

    private static string GeneralError(string api, int code) => code switch
    {
        105 => $"DSM refused {api}: this account may not see the backup tasks - it must be in the administrators group.",
        106 or 107 or 119 => $"DSM ended the session ({api}, error {code}).",
        _ => $"DSM answered error {code} for {api}."
    };

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        } : null;

    /// <summary>"2026/11/10 02:00" (DSM's local format), an ISO time, or Unix seconds; empty / "-" / 0 = none.</summary>
    private static DateTime? Time(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            return epoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().DateTime : null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(text) || text == "-" || text == "0")
        {
            return null;
        }

        string[] formats = ["yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/M/d H:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed
            : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose) ? loose
            : null;
    }

    private static HttpMessageHandler DefaultHandler(bool verifySsl)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (!verifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private sealed class SynologyException(string message) : Exception(message);
}
