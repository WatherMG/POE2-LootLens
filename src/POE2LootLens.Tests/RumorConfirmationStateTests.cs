using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class RumorConfirmationStateTests
{
    [Fact]
    public void Observe_RequiresSeparateCompletedCaptures()
    {
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RumorMatch match = Match("endless", exact: true, score: 1d);
        var state = new RumorConfirmationState(match, start, requiredObservations: 2);

        Assert.True(state.Observe(match, start, fingerprint: 1));
        Assert.False(state.IsConfirmed);
        Assert.False(state.Observe(match, start.AddMilliseconds(100), fingerprint: 2));
        Assert.Equal(1, state.ObservationCount);
        Assert.True(state.Observe(match, start.AddMilliseconds(500), fingerprint: 2));
        Assert.True(state.IsConfirmed);
    }

    [Fact]
    public void Observe_KeepsBestAvailableMatch()
    {
        DateTime start = DateTime.UtcNow;
        var state = new RumorConfirmationState(
            Match("candidate", exact: false, score: 0.82d),
            start,
            requiredObservations: 2);

        state.Observe(Match("candidate", exact: true, score: 1d), start.AddMilliseconds(300), 2);

        Assert.True(state.BestMatch.Exact);
        Assert.Equal(1d, state.BestMatch.Score);
    }

    private static RumorMatch Match(string id, bool exact, double score) => new(
        new RumorCatalogEntry
        {
            Id = id,
            Kind = "expedition",
            Phrases = [id],
        },
        id,
        score,
        exact);
}
