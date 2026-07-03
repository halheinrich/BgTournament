namespace BgTournament.Core;

/// <summary>
/// A round-robin tournament aggregate: given the participants, the format,
/// and a seed, it owns the pairing schedule; given match winners, it folds
/// the standings, the tie-breaks, and the tournament winner. Deliberately
/// execution-blind — it never runs a match, so a host executes
/// <see cref="Schedule"/> with whatever machinery it owns and reports each
/// outcome through <see cref="RecordResult"/>.
///
/// <para><b>Scheduling.</b> Every unordered pair of participants meets
/// <see cref="TournamentFormat.MatchesPerPairing"/> times, pairing-major, and
/// the seat assignment alternates within a pairing (the earlier-registered
/// participant takes seat one in the pairing's first match) so an even count
/// balances the seats exactly half each way. Each match's dice seed is
/// derived from the tournament seed and the match index by a SplitMix64 mix,
/// so a pairing's matches diverge against deterministic engines while the
/// whole tournament stays reproducible from one seed.</para>
///
/// <para><b>Standings.</b> Wins first; ties broken by head-to-head wins
/// within the tied group, then by Sonneborn-Berger on wins (the sum, per won
/// match, of the beaten opponent's total wins), then by seeding (registration)
/// order as the deterministic last resort. Every recorded match counts one
/// win and one loss — a forfeit is reported as an ordinary win for the
/// non-offender.</para>
///
/// <para>Not thread-safe: a host that mutates and reads concurrently must
/// serialize access externally.</para>
/// </summary>
public sealed class Tournament
{
    private readonly string[] _participants;
    private readonly Dictionary<string, int> _participantIndex;
    private readonly ScheduledMatch[] _schedule;
    private readonly string?[] _winners;
    private int _resultsRecorded;

    /// <summary>Create a tournament and build its full schedule eagerly.</summary>
    /// <param name="participants">The participant names, in seeding (registration) order.</param>
    /// <param name="format">The round-robin format.</param>
    /// <param name="seed">The tournament seed every match seed is derived from.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="participants"/> or <paramref name="format"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Fewer than two participants, an empty or whitespace name, or a duplicate name.
    /// </exception>
    public Tournament(IReadOnlyList<string> participants, TournamentFormat format, int seed)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(format);

        _participants = participants.ToArray();
        if (_participants.Length < 2)
        {
            throw new ArgumentException("A tournament needs at least two participants.", nameof(participants));
        }

        _participantIndex = new Dictionary<string, int>(_participants.Length, StringComparer.Ordinal);
        for (int i = 0; i < _participants.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(_participants[i]))
            {
                throw new ArgumentException("Participant names must be non-empty.", nameof(participants));
            }

            if (!_participantIndex.TryAdd(_participants[i], i))
            {
                throw new ArgumentException(
                    $"Participant names must be distinct: '{_participants[i]}' appears more than once.",
                    nameof(participants));
            }
        }

        Format = format;
        Seed = seed;
        _schedule = BuildSchedule(_participants, format, seed);
        _winners = new string?[_schedule.Length];
        Participants = Array.AsReadOnly(_participants);
        Schedule = Array.AsReadOnly(_schedule);
    }

    /// <summary>The participants in seeding (registration) order.</summary>
    public IReadOnlyList<string> Participants { get; }

    /// <summary>The round-robin format.</summary>
    public TournamentFormat Format { get; }

    /// <summary>The tournament seed every match seed is derived from.</summary>
    public int Seed { get; }

    /// <summary>
    /// The full schedule, in play order: C(N,2) pairings, each played
    /// <see cref="TournamentFormat.MatchesPerPairing"/> times.
    /// </summary>
    public IReadOnlyList<ScheduledMatch> Schedule { get; }

    /// <summary>True once every scheduled match has a recorded result.</summary>
    public bool IsComplete => _resultsRecorded == _schedule.Length;

    /// <summary>
    /// The tournament winner — the standings leader after the full tie-break
    /// ladder — or null while any match is unplayed.
    /// </summary>
    public string? Winner => IsComplete ? ComputeStandings()[0].Participant : null;

    /// <summary>The recorded winner of a scheduled match, or null if not yet recorded.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="matchIndex"/> is not a schedule index.
    /// </exception>
    public string? ResultOf(int matchIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matchIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(matchIndex, _schedule.Length);
        return _winners[matchIndex];
    }

    /// <summary>
    /// Record a scheduled match's winner. A forfeited match is recorded like
    /// any other, with the non-offender as the winner.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="matchIndex"/> is not a schedule index.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="winner"/> is not a participant in that match.
    /// </exception>
    /// <exception cref="InvalidOperationException">The match already has a result.</exception>
    public void RecordResult(int matchIndex, string winner)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentOutOfRangeException.ThrowIfNegative(matchIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(matchIndex, _schedule.Length);

        var match = _schedule[matchIndex];
        if (!string.Equals(winner, match.SeatOne, StringComparison.Ordinal)
            && !string.Equals(winner, match.SeatTwo, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{winner}' is not a participant in match {matchIndex} ({match.SeatOne} vs {match.SeatTwo}).",
                nameof(winner));
        }

        if (_winners[matchIndex] is not null)
        {
            throw new InvalidOperationException($"Match {matchIndex} already has a recorded result.");
        }

        _winners[matchIndex] = winner;
        _resultsRecorded++;
    }

    /// <summary>
    /// The standings over the results recorded so far, best first. The order
    /// is total: wins, then head-to-head wins within the wins-tied group,
    /// then Sonneborn-Berger, then seeding order — so ranks are unique and
    /// the same inputs always produce the same table.
    /// </summary>
    public IReadOnlyList<StandingsRow> ComputeStandings()
    {
        int count = _participants.Length;
        var wins = new int[count];
        var losses = new int[count];
        var winsAgainst = new int[count, count];

        for (int m = 0; m < _schedule.Length; m++)
        {
            if (_winners[m] is not { } winner)
            {
                continue;
            }

            var match = _schedule[m];
            bool seatOneWon = string.Equals(winner, match.SeatOne, StringComparison.Ordinal);
            int winnerIndex = _participantIndex[winner];
            int loserIndex = _participantIndex[seatOneWon ? match.SeatTwo : match.SeatOne];
            wins[winnerIndex]++;
            losses[loserIndex]++;
            winsAgainst[winnerIndex, loserIndex]++;
        }

        // Sonneborn-Berger on wins: each won match contributes the beaten
        // opponent's total win count (once per win, so a swept opponent
        // contributes once per match of the sweep).
        var sonnebornBerger = new int[count];
        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < count; j++)
            {
                sonnebornBerger[i] += winsAgainst[i, j] * wins[j];
            }
        }

        var ordered = new List<int>(count);
        foreach (var group in Enumerable.Range(0, count)
            .GroupBy(i => wins[i])
            .OrderByDescending(g => g.Key))
        {
            var members = group.ToArray();
            if (members.Length == 1)
            {
                ordered.Add(members[0]);
                continue;
            }

            // Head-to-head is group-relative: wins against the other members
            // of this wins-tied group only.
            int HeadToHeadWins(int i)
            {
                int total = 0;
                foreach (int j in members)
                {
                    total += winsAgainst[i, j];
                }

                return total;
            }

            ordered.AddRange(members
                .OrderByDescending(HeadToHeadWins)
                .ThenByDescending(i => sonnebornBerger[i])
                .ThenBy(i => i));
        }

        var rows = new StandingsRow[count];
        for (int position = 0; position < count; position++)
        {
            int participant = ordered[position];
            rows[position] = new StandingsRow(
                position + 1, _participants[participant],
                wins[participant], losses[participant], sonnebornBerger[participant]);
        }

        return Array.AsReadOnly(rows);
    }

    private static ScheduledMatch[] BuildSchedule(string[] participants, TournamentFormat format, int seed)
    {
        int count = participants.Length;
        var schedule = new ScheduledMatch[count * (count - 1) / 2 * format.MatchesPerPairing];
        int index = 0;
        for (int i = 0; i < count - 1; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                for (int k = 0; k < format.MatchesPerPairing; k++)
                {
                    bool earlierSeedTakesSeatOne = k % 2 == 0;
                    schedule[index] = new ScheduledMatch(
                        index,
                        earlierSeedTakesSeatOne ? participants[i] : participants[j],
                        earlierSeedTakesSeatOne ? participants[j] : participants[i],
                        DeriveMatchSeed(seed, index));
                    index++;
                }
            }
        }

        return schedule;
    }

    /// <summary>
    /// Derive one match's dice seed: the SplitMix64 finalizer over the 64-bit
    /// pack of (tournament seed, match index), folded to 32 bits. The mix is
    /// bijective on the packed input, so distinct matches collide only
    /// through the 64→32 fold (probability ~2⁻³²) — near-certainly distinct
    /// seeds per match, and always the same seeds for the same tournament.
    /// </summary>
    private static int DeriveMatchSeed(int tournamentSeed, int matchIndex)
    {
        ulong packed = ((ulong)(uint)tournamentSeed << 32) | (uint)matchIndex;
        unchecked
        {
            packed += 0x9E3779B97F4A7C15ul;
            packed = (packed ^ (packed >> 30)) * 0xBF58476D1CE4E5B9ul;
            packed = (packed ^ (packed >> 27)) * 0x94D049BB133111EBul;
            packed ^= packed >> 31;
            return (int)(packed ^ (packed >> 32));
        }
    }
}
