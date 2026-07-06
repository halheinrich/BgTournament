using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Server;
using Microsoft.Extensions.Time.Testing;
using DecisionKind = BgTournament.Server.DecisionKind;

namespace BgTournament.Tests;

/// <summary>
/// The Fischer clock's arithmetic, deterministic on a fake TimeProvider (no
/// wall-clock anywhere): per-seat pools, measured-time debits, the
/// answered-decisions-only increment, the zero floor, and the flag token
/// firing exactly when a pool empties.
/// </summary>
public class MatchClockTests
{
    private static readonly TimeControl Control = new(initialSeconds: 120, incrementSeconds: 8);

    [Fact]
    public void NewClock_BothSeatsStartWithTheInitialPool()
    {
        var clock = new MatchClock(Control, new FakeTimeProvider());

        Assert.Equal(TimeSpan.FromSeconds(120), clock.Remaining(MatchSeat.One));
        Assert.Equal(TimeSpan.FromSeconds(120), clock.Remaining(MatchSeat.Two));
    }

    [Fact]
    public void AnsweredDecision_DebitsMeasuredTime_AndCreditsTheIncrement()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        using (var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(30));
            decision.MarkAnswered();
        }

        Assert.Equal(TimeSpan.FromSeconds(120 - 30 + 8), clock.Remaining(MatchSeat.One));
        Assert.Equal(TimeSpan.FromSeconds(120), clock.Remaining(MatchSeat.Two)); // opponent untouched
    }

    [Fact]
    public void UnansweredDecision_DebitsWithoutCredit()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        using (clock.StartDecision(MatchSeat.Two, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(TimeSpan.FromSeconds(90), clock.Remaining(MatchSeat.Two));
    }

    [Fact]
    public void InstantAnswers_BankTheIncrement_PoolGrowsBeyondInitial()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        for (int i = 0; i < 3; i++)
        {
            using var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play);
            decision.MarkAnswered();
        }

        Assert.Equal(TimeSpan.FromSeconds(120 + 3 * 8), clock.Remaining(MatchSeat.One));
    }

    [Fact]
    public void OverrunDecision_FloorsThePoolAtZero()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        using (clock.StartDecision(MatchSeat.One, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(500));
        }

        Assert.Equal(TimeSpan.Zero, clock.Remaining(MatchSeat.One));
    }

    [Fact]
    public void FlagToken_FiresExactlyWhenThePoolEmpties()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        using var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.False(decision.FlagToken.IsCancellationRequested);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(decision.FlagToken.IsCancellationRequested);
    }

    [Fact]
    public void FlaggedDecision_EarnsNoIncrement()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        using (var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(200));
            Assert.True(decision.FlagToken.IsCancellationRequested);
        }

        Assert.Equal(TimeSpan.Zero, clock.Remaining(MatchSeat.One));
    }

    /// <summary>
    /// An already-empty pool flags the next decision immediately — the belt
    /// for the boundary where a decision consumed exactly the remaining time.
    /// </summary>
    [Fact]
    public void EmptyPool_FlagsTheNextDecisionImmediately()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(new TimeControl(60, 0), time);

        using (clock.StartDecision(MatchSeat.One, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Equal(TimeSpan.Zero, clock.Remaining(MatchSeat.One));
        using var next = clock.StartDecision(MatchSeat.One, DecisionKind.Play);
        Assert.True(next.FlagToken.IsCancellationRequested);
    }

    /// <summary>Per-move-cap as a degenerate config: initial = increment = C.</summary>
    [Fact]
    public void PerMoveCapConfig_RestoresAtLeastTheCapEachDecision()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(new TimeControl(10, 10), time);

        using (var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(9));
            decision.MarkAnswered();
        }

        // 10 - 9 + 10: the next decision has at least the cap again.
        Assert.Equal(TimeSpan.FromSeconds(11), clock.Remaining(MatchSeat.One));
    }

    [Fact]
    public void Dispose_IsIdempotent_SettlesOnce()
    {
        var time = new FakeTimeProvider();
        var clock = new MatchClock(Control, time);

        var decision = clock.StartDecision(MatchSeat.One, DecisionKind.Play);
        time.Advance(TimeSpan.FromSeconds(10));
        decision.MarkAnswered();
        decision.Dispose();
        decision.Dispose();

        Assert.Equal(TimeSpan.FromSeconds(118), clock.Remaining(MatchSeat.One));
    }

    // ---- the settlement report: the arbitration journal's measurement feed ----

    [Fact]
    public void AnsweredDecision_ReportsTheFullSettlement()
    {
        var time = new FakeTimeProvider();
        var settlements = new List<ClockDecisionSettlement>();
        var clock = new MatchClock(Control, time, settlements.Add);

        using (var decision = clock.StartDecision(MatchSeat.One, DecisionKind.CubeOffer))
        {
            time.Advance(TimeSpan.FromSeconds(30));
            decision.MarkAnswered();
        }

        var settlement = Assert.Single(settlements);
        Assert.Equal(MatchSeat.One, settlement.Seat);
        Assert.Equal(DecisionKind.CubeOffer, settlement.Decision);
        Assert.Equal(TimeSpan.FromSeconds(30), settlement.Think);
        Assert.True(settlement.IncrementCredited);
        Assert.Equal(TimeSpan.FromSeconds(120), settlement.RemainingBefore);
        Assert.Equal(TimeSpan.FromSeconds(120 - 30 + 8), settlement.RemainingAfter);
    }

    /// <summary>
    /// A flagged decision still reports — debit without credit, pool floored
    /// at zero. This is the evidence trail for a flag-fall forfeit.
    /// </summary>
    [Fact]
    public void FlaggedDecision_ReportsDebitWithoutCredit()
    {
        var time = new FakeTimeProvider();
        var settlements = new List<ClockDecisionSettlement>();
        var clock = new MatchClock(Control, time, settlements.Add);

        using (var decision = clock.StartDecision(MatchSeat.Two, DecisionKind.Play))
        {
            time.Advance(TimeSpan.FromSeconds(200));
            Assert.True(decision.FlagToken.IsCancellationRequested);
        }

        var settlement = Assert.Single(settlements);
        Assert.Equal(MatchSeat.Two, settlement.Seat);
        Assert.Equal(DecisionKind.Play, settlement.Decision);
        Assert.Equal(TimeSpan.FromSeconds(200), settlement.Think);
        Assert.False(settlement.IncrementCredited);
        Assert.Equal(TimeSpan.FromSeconds(120), settlement.RemainingBefore);
        Assert.Equal(TimeSpan.Zero, settlement.RemainingAfter);
    }

    /// <summary>The idempotent dispose reports exactly once, like it settles exactly once.</summary>
    [Fact]
    public void DoubleDispose_ReportsOnce()
    {
        var time = new FakeTimeProvider();
        var settlements = new List<ClockDecisionSettlement>();
        var clock = new MatchClock(Control, time, settlements.Add);

        var decision = clock.StartDecision(MatchSeat.One, DecisionKind.CubeResponse);
        decision.MarkAnswered();
        decision.Dispose();
        decision.Dispose();

        Assert.Single(settlements);
    }
}
