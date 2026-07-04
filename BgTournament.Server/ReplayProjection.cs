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
/// Projects a completed match's substrate transcripts onto the replay
/// contract (<see cref="MatchGamesResponse"/>). Two jobs the substrate
/// deliberately leaves to the host:
///
/// <para><b>Seat attribution (the frame walk).</b> A transcript entry's
/// <see cref="GameSnapshot"/> is in the on-roll player's frame and carries no
/// seat identity, so the walk re-derives it from the runner's documented
/// sequencing: a game's first entry is always the opening play, whose higher
/// die names the winner (Die1 is seat One's); every play entry flips the
/// on-roll seat afterwards; cube entries do not flip — the runner snapshots
/// <em>both</em> the offer and the response against the live state, which
/// stays in the offerer's frame (the response's actor is the other seat).</para>
///
/// <para><b>Frame normalization.</b> Every position is re-expressed in seat
/// One's frame before it crosses the API, so viewers never handle
/// perspective. The flip round-trips through the substrate's own
/// <see cref="GameState.OpponentView"/> — the system's single re-expression;
/// no mirror rule is duplicated here.</para>
/// </summary>
internal static class ReplayProjection
{
    /// <summary>
    /// Project the record's retained <see cref="MatchRecord.Result"/> onto
    /// the replay response. The caller gates on
    /// <see cref="MatchStatus.Completed"/>; a completed record always
    /// retains its result.
    /// </summary>
    public static MatchGamesResponse ToGamesResponse(this MatchRecord record)
    {
        var result = record.Result
            ?? throw new InvalidOperationException(
                $"Match '{record.MatchId}' has no retained result to project.");

        var games = new GameReplay[result.Games.Count];
        for (int i = 0; i < games.Length; i++)
        {
            games[i] = ProjectGame(result.Games[i], gameNumber: i + 1);
        }

        return new MatchGamesResponse(
            record.MatchId, record.EngineOne, record.EngineTwo, record.MatchLength, games);
    }

    private static GameReplay ProjectGame(GameRecord game, int gameNumber)
    {
        var entries = game.Transcript.Entries;
        if (entries.Count == 0 || entries[0] is not PlayTranscriptEntry opening)
        {
            throw new InvalidOperationException(
                "A recorded game always opens with the opening-roll play entry.");
        }

        // The opening roll names the first mover: Die1 is seat One's die,
        // ties never reach the transcript, the higher die wins.
        MatchSeat onRollSeat = opening.Die1 > opening.Die2 ? MatchSeat.One : MatchSeat.Two;
        (int seatOneScore, int seatTwoScore) = ScoresBySeat(opening.State.Match, onRollSeat);
        bool isCrawford = opening.State.Match.IsCrawford;

        var projected = new List<GameEntry>(entries.Count);
        GamePosition? finalState = null;
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case PlayTranscriptEntry play:
                    projected.Add(new PlayEntry(
                        ToSeat(onRollSeat),
                        ToPosition(play.State, onRollSeat),
                        play.Die1,
                        play.Die2,
                        ToMoves(play.ChosenPlay)));
                    onRollSeat = onRollSeat.Other();   // ApplyPlay flipped the live frame
                    break;

                case CubeTranscriptEntry { Action: CubeAction.Double } offer:
                    projected.Add(new CubeOfferEntry(
                        ToSeat(onRollSeat), ToPosition(offer.State, onRollSeat)));
                    break;

                case CubeTranscriptEntry { Action: CubeAction.Take or CubeAction.Pass } response:
                    // Recorded against the live state — still the offerer's
                    // frame — while the decision belongs to the other seat.
                    projected.Add(new CubeResponseEntry(
                        ToSeat(onRollSeat.Other()),
                        ToPosition(response.State, onRollSeat),
                        response.Action == CubeAction.Take
                            ? ApiCubeResponse.Take
                            : ApiCubeResponse.Pass));
                    break;

                case GameEndedTranscriptEntry end:
                    // Not an entry in the contract: the outcome lives at game
                    // level and the terminal position becomes FinalState. The
                    // frame here is wherever the last flip left the live state.
                    finalState = ToPosition(end.State, onRollSeat);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unexpected transcript entry: {entry.GetType().Name}.");
            }
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
