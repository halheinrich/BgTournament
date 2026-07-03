using BgTournament.Core;

namespace BgTournament.Tests;

/// <summary>
/// Pure domain pins for BgTournament.Core: construction validation, schedule
/// completeness, seat balancing, seed divergence, the standings fold, and
/// each tier of the tie-break ladder against hand-constructed outcomes.
/// </summary>
public class TournamentCoreTests
{
    private static Tournament NewTournament(
        string[] participants, int matchLength = 1, int matchesPerPairing = 1, int seed = 42) =>
        new(participants, new TournamentFormat(matchLength, matchesPerPairing), seed);

    /// <summary>Record one pairing's winners in schedule order: pair (a, b) → winner per encounter.</summary>
    private static void RecordPairing(Tournament tournament, string a, string b, params string[] winners)
    {
        var indices = tournament.Schedule
            .Where(m => (m.SeatOne == a && m.SeatTwo == b) || (m.SeatOne == b && m.SeatTwo == a))
            .Select(m => m.Index)
            .ToList();
        Assert.Equal(winners.Length, indices.Count);
        for (int i = 0; i < winners.Length; i++)
        {
            tournament.RecordResult(indices[i], winners[i]);
        }
    }

    // ---- Construction validation ----------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -3)]
    public void Format_RejectsNonPositiveValues(int matchLength, int matchesPerPairing)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TournamentFormat(matchLength, matchesPerPairing));
    }

    [Fact]
    public void Tournament_RejectsFewerThanTwoParticipants()
    {
        Assert.Throws<ArgumentException>(() => NewTournament(["Solo"]));
        Assert.Throws<ArgumentException>(() => NewTournament([]));
    }

    [Fact]
    public void Tournament_RejectsDuplicateAndBlankNames()
    {
        Assert.Throws<ArgumentException>(() => NewTournament(["A", "B", "A"]));
        Assert.Throws<ArgumentException>(() => NewTournament(["A", " "]));
        Assert.Throws<ArgumentNullException>(() => new Tournament(null!, new TournamentFormat(1, 1), 0));
    }

    // ---- Schedule --------------------------------------------------------

    [Fact]
    public void Schedule_EveryPairMeetsExactlyMatchesPerPairingTimes()
    {
        var tournament = NewTournament(["A", "B", "C", "D"], matchesPerPairing: 3);

        Assert.Equal(6 * 3, tournament.Schedule.Count);
        Assert.Equal(Enumerable.Range(0, 18), tournament.Schedule.Select(m => m.Index));

        var encounters = tournament.Schedule
            .GroupBy(m => (Low: string.CompareOrdinal(m.SeatOne, m.SeatTwo) < 0 ? m.SeatOne : m.SeatTwo,
                           High: string.CompareOrdinal(m.SeatOne, m.SeatTwo) < 0 ? m.SeatTwo : m.SeatOne))
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(6, encounters.Count);
        Assert.All(encounters.Values, count => Assert.Equal(3, count));
    }

    [Fact]
    public void Schedule_EvenMatchesPerPairing_BalancesSeatsExactly()
    {
        var tournament = NewTournament(["A", "B", "C"], matchesPerPairing: 4);

        foreach (var pairing in tournament.Schedule.GroupBy(
            m => string.CompareOrdinal(m.SeatOne, m.SeatTwo) < 0
                ? (m.SeatOne, m.SeatTwo)
                : (m.SeatTwo, m.SeatOne)))
        {
            var (low, high) = pairing.Key;
            Assert.Equal(2, pairing.Count(m => m.SeatOne == low));
            Assert.Equal(2, pairing.Count(m => m.SeatOne == high));
        }
    }

    [Fact]
    public void Schedule_OddMatchesPerPairing_EarlierRegisteredTakesTheExtraSeatOne()
    {
        var tournament = NewTournament(["First", "Second"], matchesPerPairing: 3);

        Assert.Equal(2, tournament.Schedule.Count(m => m.SeatOne == "First"));
        Assert.Equal(1, tournament.Schedule.Count(m => m.SeatOne == "Second"));

        // And the alternation starts with the earlier-registered participant.
        Assert.Equal("First", tournament.Schedule[0].SeatOne);
        Assert.Equal("Second", tournament.Schedule[1].SeatOne);
        Assert.Equal("First", tournament.Schedule[2].SeatOne);
    }

    // ---- Seed derivation ---------------------------------------------------

    [Fact]
    public void Seeds_WithinAPairing_Diverge()
    {
        // Identical seeds across a pairing would replay the same game against
        // deterministic engines — the whole point of K matches per pairing.
        var tournament = NewTournament(["A", "B", "C"], matchesPerPairing: 8);

        foreach (var pairing in tournament.Schedule.GroupBy(
            m => string.CompareOrdinal(m.SeatOne, m.SeatTwo) < 0
                ? (m.SeatOne, m.SeatTwo)
                : (m.SeatTwo, m.SeatOne)))
        {
            var seeds = pairing.Select(m => m.Seed).ToList();
            Assert.Equal(seeds.Count, seeds.Distinct().Count());
        }

        // Stronger, same construction: every match in the schedule got its own seed.
        Assert.Equal(
            tournament.Schedule.Count,
            tournament.Schedule.Select(m => m.Seed).Distinct().Count());
    }

    [Fact]
    public void Seeds_AreDeterministicFromTheTournamentSeed()
    {
        var first = NewTournament(["A", "B", "C"], matchesPerPairing: 4, seed: 1234);
        var second = NewTournament(["A", "B", "C"], matchesPerPairing: 4, seed: 1234);
        var different = NewTournament(["A", "B", "C"], matchesPerPairing: 4, seed: 1235);

        Assert.Equal(first.Schedule, second.Schedule);
        Assert.NotEqual(
            first.Schedule.Select(m => m.Seed).ToList(),
            different.Schedule.Select(m => m.Seed).ToList());
    }

    // ---- Recording results -------------------------------------------------

    [Fact]
    public void RecordResult_ValidatesIndexWinnerAndDuplicates()
    {
        var tournament = NewTournament(["A", "B", "C"]);

        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.RecordResult(-1, "A"));
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.RecordResult(3, "A"));
        Assert.Throws<ArgumentNullException>(() => tournament.RecordResult(0, null!));

        // Match 0 is A vs B: a stranger and a non-playing participant both rejected.
        Assert.Throws<ArgumentException>(() => tournament.RecordResult(0, "Nobody"));
        Assert.Throws<ArgumentException>(() => tournament.RecordResult(0, "C"));

        tournament.RecordResult(0, "A");
        Assert.Equal("A", tournament.ResultOf(0));
        Assert.Throws<InvalidOperationException>(() => tournament.RecordResult(0, "B"));
    }

    [Fact]
    public void WinnerAndCompletion_NullUntilEveryResultIsIn()
    {
        var tournament = NewTournament(["A", "B", "C"]);

        Assert.False(tournament.IsComplete);
        Assert.Null(tournament.Winner);

        RecordPairing(tournament, "A", "B", "A");
        RecordPairing(tournament, "A", "C", "A");
        Assert.False(tournament.IsComplete);
        Assert.Null(tournament.Winner);

        RecordPairing(tournament, "B", "C", "B");
        Assert.True(tournament.IsComplete);
        Assert.Equal("A", tournament.Winner);
    }

    // ---- Standings: the fold and each tie-break tier -----------------------

    [Fact]
    public void Standings_FoldWinsLossesAndSonnebornBerger()
    {
        // A beats B and C; B beats C. SB: A won against B (1 win) and C (0) → 1.
        var tournament = NewTournament(["A", "B", "C"]);
        RecordPairing(tournament, "A", "B", "A");
        RecordPairing(tournament, "A", "C", "A");
        RecordPairing(tournament, "B", "C", "B");

        Assert.Equal(
            new[]
            {
                new StandingsRow(1, "A", 2, 0, 1),
                new StandingsRow(2, "B", 1, 1, 0),
                new StandingsRow(3, "C", 0, 2, 0),
            },
            tournament.ComputeStandings());
    }

    [Fact]
    public void Standings_PartialResults_CountOnlyRecordedMatches()
    {
        // One recorded match: SB(A) counts the beaten B's current win total,
        // which is 0. B and C tie at zero wins with nothing to separate them,
        // so seeding order places B (registered earlier) above C.
        var tournament = NewTournament(["A", "B", "C"]);
        RecordPairing(tournament, "A", "B", "A");

        Assert.Equal(
            new[]
            {
                new StandingsRow(1, "A", 1, 0, 0),
                new StandingsRow(2, "B", 0, 1, 0),
                new StandingsRow(3, "C", 0, 0, 0),
            },
            tournament.ComputeStandings());
    }

    [Fact]
    public void Standings_HeadToHead_BreaksWinsTies_AndOutranksSonnebornBerger()
    {
        // Full round-robin of four, K=1:
        //   A beats B, C;  D beats A;  B beats C, D;  C beats D.
        // A and B tie at 2 wins — A won their encounter, so A ranks above B
        // even though nothing else separates them.
        // C and D tie at 1 win — C won their encounter and ranks above D,
        // even though D's Sonneborn-Berger (beat A: 2) beats C's (beat D: 1):
        // head-to-head is the higher tier.
        var tournament = NewTournament(["A", "B", "C", "D"]);
        RecordPairing(tournament, "A", "B", "A");
        RecordPairing(tournament, "A", "C", "A");
        RecordPairing(tournament, "A", "D", "D");
        RecordPairing(tournament, "B", "C", "B");
        RecordPairing(tournament, "B", "D", "B");
        RecordPairing(tournament, "C", "D", "C");

        Assert.Equal(
            new[]
            {
                new StandingsRow(1, "A", 2, 1, 3), // beat B (2 wins) + C (1 win)
                new StandingsRow(2, "B", 2, 1, 2), // beat C (1) + D (1)
                new StandingsRow(3, "C", 1, 2, 1), // beat D (1)
                new StandingsRow(4, "D", 1, 2, 2), // beat A (2)
            },
            tournament.ComputeStandings());
        Assert.Equal("A", tournament.Winner);
    }

    [Fact]
    public void Standings_SonnebornBerger_BreaksTiesHeadToHeadCannot()
    {
        // K=2, four players. A and B tie at 3 wins with their pairing split
        // 1–1, so head-to-head cannot separate them. B's wins came against
        // stronger opposition:
        //   SB(A) = w(B) + 2·w(C)        = 3 + 2   = 5
        //   SB(B) = w(A) + w(C) + w(D)   = 3 + 1+5 = 9  → B above A.
        var tournament = NewTournament(["A", "B", "C", "D"], matchesPerPairing: 2);
        RecordPairing(tournament, "A", "B", "A", "B"); // split 1–1
        RecordPairing(tournament, "A", "C", "A", "A"); // A sweeps C
        RecordPairing(tournament, "A", "D", "D", "D"); // D sweeps A
        RecordPairing(tournament, "B", "C", "B", "C"); // split
        RecordPairing(tournament, "B", "D", "B", "D"); // split
        RecordPairing(tournament, "C", "D", "D", "D"); // D sweeps C

        Assert.Equal(
            new[]
            {
                new StandingsRow(1, "D", 5, 1, 11), // beat A twice (2·3) + B (3) + C twice (2·1)
                new StandingsRow(2, "B", 3, 3, 9),
                new StandingsRow(3, "A", 3, 3, 5),
                new StandingsRow(4, "C", 1, 5, 3),  // beat B once (3)
            },
            tournament.ComputeStandings());
        Assert.Equal("D", tournament.Winner);
    }

    [Fact]
    public void Standings_SeedingOrder_IsTheDeterministicLastResort()
    {
        // Two players split K=2: wins tie, head-to-head ties, Sonneborn-Berger
        // ties (each beat the other once). The earlier-registered participant
        // ranks first — deterministic by construction, never ambiguous.
        var tournament = NewTournament(["Late", "Early"], matchesPerPairing: 2);
        RecordPairing(tournament, "Late", "Early", "Late", "Early");

        Assert.Equal(
            new[]
            {
                new StandingsRow(1, "Late", 1, 1, 1),
                new StandingsRow(2, "Early", 1, 1, 1),
            },
            tournament.ComputeStandings());
        Assert.True(tournament.IsComplete);
        Assert.Equal("Late", tournament.Winner);
    }

    [Fact]
    public void Standings_ForfeitIsJustALoss()
    {
        // The domain never hears "forfeit" — the host reports the non-offender
        // as the winner and the fold treats it like any other match.
        var tournament = NewTournament(["A", "B"]);
        tournament.RecordResult(0, "B");

        Assert.Equal("B", tournament.Winner);
        Assert.Equal(
            new[]
            {
                new StandingsRow(1, "B", 1, 0, 0),
                new StandingsRow(2, "A", 0, 1, 0),
            },
            tournament.ComputeStandings());
    }
}
