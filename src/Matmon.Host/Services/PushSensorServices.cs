using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// The last push of every push sensor. Updated BEFORE the push is recorded, and read by the sensor's periodic check, so a
/// check that runs while a push arrives can never record an older "last push" over a newer one. After a restart it starts
/// from what the latest observation carries (<see cref="PushSensor.FromObservation"/>), so a daily backup that pushed
/// before the restart is not called overdue.
/// </summary>
public sealed class PushRegistry(IMonitoringWorkspaceStore store)
{
    private readonly ConcurrentDictionary<Guid, PushSensor.Push> _last = new();

    public void Record(Guid sensorId, PushSensor.Push push) => _last[sensorId] = push;

    public PushSensor.Push? Last(Guid sensorId)
    {
        if (_last.TryGetValue(sensorId, out var push))
        {
            return push;
        }

        var carried = PushSensor.FromObservation(store.GetLatestObservation(sensorId));
        return carried is null ? null : _last.GetOrAdd(sensorId, carried);
    }
}

/// <summary>The push sensor's periodic check: is the last push recent enough, and what did it say? Primary only - the pushes
/// arrive at the primary (<see cref="SensorPlacement.RunsOnPrimaryOnly"/>).</summary>
public sealed class PushSensorExecutor(PushRegistry registry) : ISensorExecutor
{
    public static SensorDefinition Definition => PushSensor.Definition;

    public string SensorTypeKey => PushSensor.Key;

    public ValueTask<SensorExecutionResult> ExecuteAsync(SensorExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.SensorId is not { } sensorId)
        {
            return ValueTask.FromResult(SensorExecutionResult.Unknown("Save the sensor, then call its address - the sensor page shows it."));
        }

        return ValueTask.FromResult(PushSensor.Evaluate(registry.Last(sensorId), context.Settings, DateTimeOffset.UtcNow));
    }
}

/// <summary>What a push request carries: status, value and message from the query string, a form or a JSON body.</summary>
public static class PushRequestReader
{
    public sealed record Fields(string? Status, string? Value, string? Message);

    public static async Task<Fields> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        string? status = request.Query["status"], value = request.Query["value"], message = request.Query["message"];
        if (HttpMethods.IsPost(request.Method) && request.ContentLength is > 0 and <= 16 * 1024)
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken);
                status ??= form["status"];
                value ??= form["value"];
                message ??= form["message"];
            }
            else if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                    string? Read(string name) => document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var property)
                        ? property.ValueKind switch
                        {
                            JsonValueKind.String => property.GetString(),
                            JsonValueKind.Number => property.GetRawText(),
                            _ => null
                        }
                        : null;
                    status ??= Read("status");
                    value ??= Read("value");
                    message ??= Read("message");
                }
                catch (JsonException)
                {
                    // not JSON after all - the query string still counts
                }
            }
        }

        return new Fields(status, value, message);
    }

    public static bool TryParseValue(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            value = number;
            return true;
        }

        return false;
    }
}
