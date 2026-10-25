namespace Matmon.Probe;

/// <summary>
/// A probe stamps each result with its OWN clock, and the primary records it at that time (that is what makes an
/// offline backfill land at the real check times). A probe whose clock is wrong used to put its results in the
/// future - where they stayed the "latest" result for as long as the error lasted - or so far in the past that every
/// sensor looked overdue. The probe therefore says what its clock read when it sent a batch
/// (<c>SentUtc</c>), the primary works out the difference to its own and moves every timestamp of the batch by it.
/// A difference within <see cref="Tolerance"/> is network time and ordinary drift and is left alone. Whatever the
/// probe sends, nothing is recorded in the primary's future.
/// </summary>
public static class ProbeClock
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    /// <summary>How far the primary's clock is ahead of the probe's (zero within <see cref="Tolerance"/>, or when an
    /// older probe sends no <paramref name="sentUtc"/>).</summary>
    public static TimeSpan Offset(DateTimeOffset? sentUtc, DateTimeOffset receivedUtc)
    {
        if (sentUtc is not { } sent)
        {
            return TimeSpan.Zero;
        }

        var offset = receivedUtc - sent;
        return offset.Duration() <= Tolerance ? TimeSpan.Zero : offset;
    }

    /// <summary>The time to record a result at: the probe's stamp moved by the offset, never later than now.</summary>
    public static DateTimeOffset Correct(DateTimeOffset timestampUtc, TimeSpan offset, DateTimeOffset receivedUtc)
    {
        var corrected = timestampUtc + offset;
        return corrected > receivedUtc ? receivedUtc : corrected;
    }
}
