using BgTournament.Api;
using BgTournament.Server;
using Microsoft.Extensions.Logging.Abstractions;
using ApiForfeitCause = BgTournament.Api.ForfeitCause;
using ServerForfeitCause = BgTournament.Server.ForfeitCause;

namespace BgTournament.Tests;

/// <summary>
/// The server → admin-API projection is a correspondence contract, the admin
/// counterpart of <see cref="JournalMappingTests"/>: a record projected onto
/// its <see cref="MatchSummary"/> must carry the same structured facts. The
/// byte shape is pinned by <see cref="ApiGoldenTests"/>; this pins the
/// mapping — the field a golden refresh would silently paper over.
/// </summary>
public class ApiMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh Running record with the identity fields a summary needs.</summary>
    private static MatchRecord NewRecord() =>
        new()
        {
            MatchId = "match-1",
            EngineOne = "Alpha",
            EngineTwo = "Beta",
            MatchLength = 3,
            Seed = 1,
            Sequence = 1,
            StartedAtUtc = At,
            Live = new LiveMatch("match-1", NullLogger.Instance),
        };

    /// <summary>
    /// Every forfeit path projects its structured cause onto the summary. The
    /// server taxonomy is internal, so the theory is driven off the public Api
    /// enum and the same-named server cause feeds the record — the reused
    /// <c>ApiMapping.ToApiForfeitCause</c> is what maps one to the other.
    /// </summary>
    [Theory]
    [InlineData(ApiForfeitCause.ContractViolation)]
    [InlineData(ApiForfeitCause.Timeout)]
    [InlineData(ApiForfeitCause.FlagFall)]
    [InlineData(ApiForfeitCause.Disconnect)]
    [InlineData(ApiForfeitCause.NeverConnected)]
    public void ToSummary_ForfeitedMatch_CarriesTheStructuredCause(ApiForfeitCause expected)
    {
        var cause = Enum.Parse<ServerForfeitCause>(expected.ToString());
        var record = NewRecord();
        record.RecordForfeit(seat: 1, cause, "Alpha forfeited.");

        var summary = record.ToSummary();

        Assert.Equal(MatchStatus.Forfeited, summary.Status);
        Assert.Equal("Alpha", summary.ForfeitedBy);
        Assert.Equal(expected, summary.ForfeitCause);
    }

    /// <summary>
    /// A non-forfeited terminal record carries no cause — the field mirrors
    /// <see cref="MatchSummary.ForfeitedBy"/>'s null-unless-forfeited contract.
    /// </summary>
    [Fact]
    public void ToSummary_CompletedMatch_HasNoForfeitCause()
    {
        var record = NewRecord();
        record.Status = MatchStatus.Completed;
        record.Winner = "Alpha";
        record.SeatOneScore = 3;
        record.SeatTwoScore = 1;

        var summary = record.ToSummary();

        Assert.Null(summary.ForfeitCause);
    }
}
