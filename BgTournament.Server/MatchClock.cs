using BgGame_Lib;
using BgTournament.Api;

namespace BgTournament.Server;

/// <summary>
/// One match's Fischer clock: a per-seat time pool, debited by the decision
/// time the server measures around each wire query and credited the increment
/// after each answered decision. An emptied pool is the flag — the running
/// decision's <see cref="Decision.FlagToken"/> fires and the seat forfeits.
///
/// <para>Time is read exclusively through the injected
/// <see cref="TimeProvider"/> — never ambient — so clock behavior is
/// deterministic under test (and the timestamp source is ready for reuse by a
/// future arbitration log). Not thread-safe by design: the match loop runs
/// strictly one decision at a time, so all clock access happens on that loop.</para>
/// </summary>
internal sealed class MatchClock
{
    private readonly TimeProvider _time;
    private TimeSpan _remainingOne;
    private TimeSpan _remainingTwo;

    public MatchClock(TimeControl control, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(time);
        Control = control;
        _time = time;
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
    /// Start timing one decision for <paramref name="seat"/>. The returned
    /// scope's <see cref="Decision.FlagToken"/> fires when the seat's pool
    /// empties; disposing it settles the decision — the measured time is
    /// debited exactly once, and the increment is credited iff
    /// <see cref="Decision.MarkAnswered"/> was called first (only answered
    /// decisions earn the increment).
    /// </summary>
    public Decision StartDecision(MatchSeat seat) => new(this, seat);

    private void Settle(MatchSeat seat, TimeSpan elapsed, bool answered)
    {
        var remaining = Remaining(seat) - elapsed;
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
    }

    /// <summary>One timed decision; see <see cref="StartDecision"/>.</summary>
    internal sealed class Decision : IDisposable
    {
        private readonly MatchClock _clock;
        private readonly MatchSeat _seat;
        private readonly long _started;
        private readonly CancellationTokenSource _flag;
        private bool _answered;
        private bool _settled;

        internal Decision(MatchClock clock, MatchSeat seat)
        {
            _clock = clock;
            _seat = seat;
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
                _clock.Settle(_seat, _clock._time.GetElapsedTime(_started), _answered);
            }

            _flag.Dispose();
        }
    }
}
