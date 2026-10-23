using System.Threading.Channels;

namespace Matmon.Host.Services;

/// <summary>
/// "Poll this sensor now" from inside the store - a dependency to check before a dependent may alert, the dependents to
/// look again once their dependency is back up. The store cannot call <see cref="SensorWakeService"/> itself (that one
/// needs the store), so it drops the id here and <see cref="SensorWakeWorker"/> hands it over. Bounded and lossy on
/// purpose: a wake is a hurry-up, the normal schedule still polls the sensor if one is dropped.
/// </summary>
public sealed class SensorWakeQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(2000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    public void Request(Guid sensorId) => _channel.Writer.TryWrite(sensorId);

    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>Drains <see cref="SensorWakeQueue"/> into <see cref="SensorWakeService"/> (which keeps each sensor to one wake
/// per 10 s and sends a remote probe's sensors to that probe). Primary only.</summary>
public sealed class SensorWakeWorker(SensorWakeQueue queue, SensorWakeService wake, ILogger<SensorWakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var batch = new HashSet<Guid>();
                while (queue.Reader.TryRead(out var id))
                {
                    batch.Add(id);
                }

                try
                {
                    wake.WakeSensors(batch);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Polling {Count} sensor(s) on request failed", batch.Count);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
