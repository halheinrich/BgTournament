using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Protocol;
using ApiCubeOwner = BgTournament.Api.CubeOwner;
using ApiCubeResponse = BgTournament.Api.CubeResponseAction;
using ApiResultKind = BgTournament.Api.GameResultKind;
using SubstrateCubeOwner = BgDataTypes_Lib.CubeOwner;
using SubstrateResultKind = BgGame_Lib.GameResultKind;

namespace BgTournament.Server;

/// <summary>
/// Projects a match's substrate transcripts onto the replay contract
/// (<see cref="MatchGamesResponse"/>) and its constituent shapes. Two jobs the
/// substrate leaves to the host:
///
/// <para><b>Seat attribution.</b> A transcript entry's <see cref="GameSnapshot"/>
/// is in the on-roll player's frame; the entry itself now carries that frame as
/// <see cref="TranscriptEntry.OnRollSeat"/>, and the subtypes own the
/// attribution rule — a play's mover is its <c>OnRollSeat</c>, a cube entry's
/// actor is <see cref="CubeTranscriptEntry.ActingSeat"/>, a game's winner is
/// <see cref="GameEndedTranscriptEntry.Winner"/>. The projection is a plain
/// read of those stamped facts; it derives nothing from sequencing.</para>
///
/// <para><b>Frame normalization.</b> Every position is re-expressed in seat
/// One's frame before it crosses the API, so viewers never handle
/// perspective. The flip round-trips through the substrate's own
/// <see cref="GameState.OpponentView"/> — the system's single re-expression;
/// no mirror rule is duplicated here.</para>
///
/// <para>The per-entry (<see cref="ProjectEntry"/>) and per-game
/// (<see cref="ProjectGame"/>) projections are the single source of truth for
/// both the settled-replay endpoint and the live per-move feed
/// (<c>LiveMatch</c>).</para>
/// </summary>
internal static class ReplayProjection
{
    /// <summary>
    /// Project a terminal match's retained games onto the replay response,
    /// tagged with the match's <see cref="MatchRecord.Status"/> so a partial
    /// (non-Completed) list is self-describing. The games come from the
    /// substrate result when the match completed, else from the games the
    /// observer collected before the run was interrupted — the same
    /// <see cref="GameRecord"/> instances either way. The caller gates out
    /// running matches.
    /// </summary>
    public static MatchGamesResponse ToGamesResponse(this MatchRecord record)
    {
        IReadOnlyList<GameRecord> source = record.Result?.Games ?? record.Live.CompletedGames;

        var games = new GameReplay[source.Count];
        for (int i = 0; i < games.Length; i++)
        {
            games[i] = ProjectGame(source[i], gameNumber: i + 1);
        }

        return new MatchGamesResponse(
            record.MatchId, record.EngineOne, record.EngineTwo, record.MatchLength, record.Status, games);
    }

    /// <summary>Project one recorded game onto its replay shape.</summary>
    public static GameReplay ProjectGame(GameRecord game, int gameNumber)
    {
        var entries = game.Transcript.Entries;
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("A recorded game always has at least one entry.");
        }

        // Entering scores and Crawford are constant across the game; read them
        // from the first entry, interpreted in that entry's stamped frame.
        var first = entries[0];
        (int seatOneScore, int seatTwoScore) = ScoresBySeat(first.State.Match, first.OnRollSeat);
        bool isCrawford = first.State.Match.IsCrawford;

        var projected = new List<GameEntry>(entries.Count);
        GamePosition? finalState = null;
        foreach (var entry in entries)
        {
            if (entry is GameEndedTranscriptEntry end)
            {
                // Not an entry in the contract: the outcome lives at game level
                // and the terminal position becomes FinalState.
                finalState = ToPosition(end.State, end.OnRollSeat);
                continue;
            }

            projected.Add(ProjectEntry(entry));
        }

        return new GameReplay(
            gameNumber,
            ToSeat(game.Winner),
            ToResultKind(game.Result.Kind),
            game.Result.CubeSize,
            game.Result.Points,
            seatOneScore,
            seatTwoScore,
            isCrawford,
            projected,
            finalState ?? throw new InvalidOperationException(
                "A recorded game always terminates with a game-end entry."));
    }

    /// <summary>
    /// Project a single decision entry onto its replay shape, reading
    /// attribution straight off the entry's stamped frame. The terminating
    /// <see cref="GameEndedTranscriptEntry"/> is not a contract entry — it
    /// becomes a game's <see cref="GameReplay.FinalState"/>, never a
    /// <see cref="GameEntry"/> — so passing one is a projection bug.
    /// </summary>
    public static GameEntry ProjectEntry(TranscriptEntry entry) => entry switch
    {
        PlayTranscriptEntry play => new PlayEntry(
            ToSeat(play.OnRollSeat),
            ToPosition(play.State, play.OnRollSeat),
            play.Die1,
            play.Die2,
            ToMoves(play.ChosenPlay)),

        CubeTranscriptEntry { Action: CubeAction.Double } offer => new CubeOfferEntry(
            ToSeat(offer.ActingSeat), ToPosition(offer.State, offer.OnRollSeat)),

        // Recorded against the live state — still the offerer's frame — while
        // the decision belongs to ActingSeat (the other seat).
        CubeTranscriptEntry { Action: CubeAction.Take or CubeAction.Pass } response => new CubeResponseEntry(
            ToSeat(response.ActingSeat),
            ToPosition(response.State, response.OnRollSeat),
            response.Action == CubeAction.Take ? ApiCubeResponse.Take : ApiCubeResponse.Pass),

        _ => throw new InvalidOperationException(
            $"Cannot project transcript entry as a replay entry: {entry.GetType().Name}."),
    };

    /// <summary>
    /// Express a recorded snapshot as a seat-One-frame position.
    /// <paramref name="frameSeat"/> is the seat whose frame the snapshot is
    /// in; a seat-Two-frame snapshot is re-expressed through the substrate.
    /// </summary>
    private static GamePosition ToPosition(GameSnapshot snapshot, MatchSeat frameSeat)
    {
        GameSnapshot seatOneFrame = frameSeat == MatchSeat.One ? snapshot : Flipped(snapshot);
        return new GamePosition(
            seatOneFrame.Board, seatOneFrame.CubeSize, ToOwner(seatOneFrame.CubeOwner));
    }

    /// <summary>
    /// The frame flip, single-sourced: rebuild the live state the snapshot
    /// captured and round-trip it through <see cref="GameState.OpponentView"/> —
    /// the substrate's only re-expression — rather than mirroring boards,
    /// scores, or cube owners here.
    /// </summary>
    private static GameSnapshot Flipped(GameSnapshot snapshot) =>
        GameState.FromPosition(
                MatchState.FromScores(
                    snapshot.Match.MatchLength,
                    snapshot.Match.OnRollScore,
                    snapshot.Match.OpponentScore,
                    snapshot.Match.IsCrawford),
                BoardState.FromMop(snapshot.Board),
                snapshot.CubeSize,
                snapshot.CubeOwner)
            .OpponentView()
            .Snapshot();

    /// <summary>
    /// Reuse the wire's single-sourced play flattening (hit-encoding
    /// stripped, plain from/to) and re-shape it into the admin contract.
    /// </summary>
    private static IReadOnlyList<PlayMove> ToMoves(Play play)
    {
        var wireMoves = play.ToWireMoves();
        var moves = new PlayMove[wireMoves.Count];
        for (int i = 0; i < moves.Length; i++)
        {
            moves[i] = new PlayMove(wireMoves[i].From, wireMoves[i].To);
        }

        return moves;
    }

    private static (int SeatOne, int SeatTwo) ScoresBySeat(MatchSnapshot match, MatchSeat frameSeat) =>
        frameSeat == MatchSeat.One
            ? (match.OnRollScore, match.OpponentScore)
            : (match.OpponentScore, match.OnRollScore);

    private static Seat ToSeat(MatchSeat seat) =>
        seat == MatchSeat.One ? Seat.One : Seat.Two;

    /// <summary>Seat-key the cube owner. Valid only for a seat-One-frame snapshot.</summary>
    private static ApiCubeOwner ToOwner(SubstrateCubeOwner owner) => owner switch
    {
        SubstrateCubeOwner.OnRoll => ApiCubeOwner.SeatOne,
        SubstrateCubeOwner.Opponent => ApiCubeOwner.SeatTwo,
        SubstrateCubeOwner.Centered => ApiCubeOwner.Centered,
        _ => throw new InvalidOperationException($"Unhandled CubeOwner value: {owner}."),
    };

    private static ApiResultKind ToResultKind(SubstrateResultKind kind) => kind switch
    {
        SubstrateResultKind.WinSingle => ApiResultKind.Single,
        SubstrateResultKind.WinGammon => ApiResultKind.Gammon,
        SubstrateResultKind.WinBackgammon => ApiResultKind.Backgammon,
        _ => throw new InvalidOperationException($"Unhandled GameResultKind value: {kind}."),
    };
}
