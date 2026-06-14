namespace Poe2LootLens;

/// <summary>
/// Accumulates short-lived OCR evidence for one rumor candidate. The scanner owns one instance per
/// island and rumor id; the overlay can therefore show a pending card immediately while confirmation
/// proceeds on the single OCR worker.
/// </summary>
internal sealed class RumorConfirmationState
{
    private DateTime _lastCountedAt = DateTime.MinValue;

    public RumorConfirmationState(RumorMatch match, DateTime firstSeenAt, int requiredObservations)
    {
        BestMatch = match;
        FirstSeenAt = firstSeenAt;
        LastSeenAt = firstSeenAt;
        RequiredObservations = Math.Max(2, requiredObservations);
    }

    public RumorMatch BestMatch { get; private set; }
    public DateTime FirstSeenAt { get; }
    public DateTime LastSeenAt { get; private set; }
    public int RequiredObservations { get; }
    public int ObservationCount { get; private set; }
    public bool IsConfirmed => ObservationCount >= RequiredObservations;

    public void RefreshEntry(RumorCatalogEntry entry) =>
        BestMatch = BestMatch with { Entry = entry };

    /// <summary>
    /// Records one completed capture. A manual trigger and an automatic tick can be delivered close
    /// together after an OCR pass, so observations inside a small guard window are coalesced. The
    /// fingerprint is retained in the API for diagnostics and future frame-correlation policies; an
    /// unchanged image captured later is still valid independent evidence.
    /// </summary>
    public bool Observe(RumorMatch match, DateTime now, ulong fingerprint)
    {
        _ = fingerprint;
        LastSeenAt = now;
        if ((match.Exact && !BestMatch.Exact) ||
            (match.Exact == BestMatch.Exact && match.Score > BestMatch.Score))
        {
            BestMatch = match;
        }

        if (_lastCountedAt != DateTime.MinValue &&
            now - _lastCountedAt < TimeSpan.FromMilliseconds(250))
        {
            return false;
        }

        _lastCountedAt = now;
        ObservationCount++;
        return true;
    }
}
