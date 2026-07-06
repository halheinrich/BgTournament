using BgGame_Lib;
using BgTournament.Api;

namespace BgTournament.Server;

/// <summary>
/// One settled clocked decision — the per-decision evidence the clock
/// measured, reported through <see cref="MatchClock"/>'s settlement callback
/// and journaled as the arbitration record's clock event.
/// </summary>
/// <param name="Seat">The seat whose pool the decision ran on.</param>
/// <param name="Decision">Which decision query was timed.</param>
/// <param name="Think">The wall time measured around the query (latency deliberately on the player's clock).</param>
/// <param name="IncrementCredited">Whether the increment was credited — true iff the engine answered.</param>
/// <param name="RemainingBefore">The seat's pool entering the decision.</param>
/// <param name="RemainingAfter">The seat's pool after settlement (debit, zero floor, then any credit).</param>
internal sealed record ClockDecisionSettlement(
    MatchSeat Seat,
    DecisionKind Decision,
    TimeSpan Think,
    bool IncrementCredited,
    TimeSpan RemainingBefore,
    TimeSpan RemainingAfter);

/// <summary>
/// One match's Fischer clock: a per-seat time pool, debited by the decision
/// time the server measures around each wire query and credited the increment
/// after each answered decision. An emptied pool is the flag — the running
/// decision's <see cref="Decision.FlagToken"/> fires and the seat forfeits.
///
/// <para>Time is read exclusively through the injected
/// <see cref="TimeProvider"/> — never ambient — so clock behavior is
/// deterministic under test. The clock is also the arbitration log's
/// measurement source: every settlement is reported once, at settle time,
/// through the optional callback. Not thread-safe by design: the match loop
/// runs strictly one decision at a time, so all clock access happens on that
/// loop.</para>
/// </summary>
internal sealed class MatchClock
{
    private readonly TimeProvider _time;
    private readonly Action<ClockDecisionSettlement>? _onSettled;
    private TimeSpan _remainingOne;
    private TimeSpan _remainingTwo;

    /// <param name="control">The Fischer control to enforce.</param>
    /// <param name="time">The one timestamp source (never ambient time).</param>
    /// <param name="onSettled">
    /// Invoked once per settled decision with the measured evidence, on the
    /// match loop inside the decision scope's dispose. Observer discipline
    /// applies: the callback must be fast, in-memory, and non-throwing (the
    /// journal's consumer contains its own failures).
    /// </param>
    public MatchClock(TimeControl control, TimeProvider time, Action<ClockDecisionSettlement>? onSettled = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(time);
        Control = control;
        _time = time;
        _onSettled = onSettled;
        _remainingOne = control.Initial;
        _remainingTwo = control.Initial;
    }

    /// <summary>The control this clock enforces (wire announcement, forfeit detail).</summary>
    public TimeControl Control { get; }

    /// <summary>
    /// The seat's current pool. Never negative; an empty pool means the seat's
    /// next decision flags immediately.
    /// </summary>
    public TimeSpan Remaining(MatchSeat seat) =>
        seat == MatchSeat.One ? _remainingOne : _remainingTwo;

    /// <summary>
    /// Start timing one <paramref name="decision"/> for <paramref name="seat"/>.
    /// The returned scope's <see cref="Decision.FlagToken"/> fires when the
    /// seat's pool empties; disposing it settles the decision — the measured
    /// time is debited exactly once, the increment is credited iff
    /// <see cref="Decision.MarkAnswered"/> was called first (only answered
    /// decisions earn the increment), and the settlement is reported.
    /// </summary>
    public Decision StartDecision(MatchSeat seat, DecisionKind decision) => new(this, seat, decision);

    private void Settle(MatchSeat seat, DecisionKind decision, TimeSpan elapsed, bool answered)
    {
        var before = Remaining(seat);
        var remaining = before - elapsed;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        if (answered)
        {
            remaining += Control.Increment;
        }

        if (seat == MatchSeat.One)
        {
            _remainingOne = remaining;
        }
        else
        {
            _remainingTwo = remaining;
        }

        _onSettled?.Invoke(new ClockDecisionSettlement(seat, decision, elapsed, answered, before, remaining));
    }

    /// <summary>One timed decision; see <see cref="StartDecision"/>.</summary>
    internal sealed class Decision : IDisposable
    {
        private readonly MatchClock _clock;
        private readonly MatchSeat _seat;
        private readonly DecisionKind _decision;
        private readonly long _started;
        private readonly CancellationTokenSource _flag;
        private bool _answered;
        private bool _settled;

        internal Decision(MatchClock clock, MatchSeat seat, DecisionKind decision)
        {
            _clock = clock;
            _seat = seat;
            _decision = decision;
            _started = clock._time.GetTimestamp();
            _flag = new CancellationTokenSource(clock.Remaining(seat), clock._time);
        }

        /// <summary>Fires when the seat's pool is exhausted — the flag falls.</summary>
        public CancellationToken FlagToken => _flag.Token;

        /// <summary>Record that the engine answered: this decision earns the increment when settled.</summary>
        public void MarkAnswered() => _answered = true;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_settled)
            {
                _settled = true;
                _clock.Settle(_seat, _decision, _clock._time.GetElapsedTime(_started), _answered);
            }

            _flag.Dispose();
        }
    }
}
