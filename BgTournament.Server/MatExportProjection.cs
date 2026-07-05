using BgGame_Lib;
using BgMatchFormat_Lib;
using BgTournament.Api;

namespace BgTournament.Server;

/// <summary>
/// Assembles a terminal match's retained data into a <see cref="MatchExport"/> —
/// the producer-agnostic input to <see cref="MatExporter"/> — and renders it as
/// <c>.MAT</c> text. The counterpart of <see cref="ReplayProjection"/> for the
/// export surface: the single place the server maps its own record (engine names
/// → Player 1/2 on the absolute seat convention, the retained games, the forfeit
/// taxonomy, the match id) onto the exporter's factory choice. The caller gates
/// out running matches; every terminal status maps to exactly one factory:
///
/// <list type="bullet">
///   <item>Completed, length &gt; 0 → <see cref="MatchExport.ForMatch"/>.</item>
///   <item>Completed money session (length 0) → <see cref="MatchExport.ForMoneySession"/>.</item>
///   <item>Forfeited → <see cref="MatchExport.ForForfeit"/>, the non-forfeiting
///     seat awarded the match.</item>
///   <item>Aborted / Faulted → <see cref="MatchExport.ForAbandoned"/>, winner-less,
///     with a one-line reason drawn from the status.</item>
/// </list>
///
/// The completed games and any trailing in-flight game come straight from the
/// substrate (<see cref="GameRecord"/> / <see cref="Transcript"/>) — the same
/// instances replay reads — so the exporter, not this projection, owns every
/// byte of <c>.MAT</c> framing.
/// </summary>
internal static class MatExportProjection
{
    /// <summary>Render a terminal match as Jellyfish <c>.MAT</c> text.</summary>
    public static string ToMatText(this MatchRecord record) =>
        MatExporter.Export(record.ToMatExport());

    /// <summary>
    /// Map a terminal match onto the exporter factory its status calls for.
    /// Throws for a running match — the endpoint refuses those before reaching
    /// here.
    /// </summary>
    public static MatchExport ToMatExport(this MatchRecord record)
    {
        // The only tag the server can populate from a fact it holds; MatHeaderTag
        // is a dumb carrier, so more can be added here as the server gains facts.
        var tags = new[] { new MatHeaderTag("Match ID", record.MatchId) };

        return record.Status switch
        {
            MatchStatus.Completed when record.MatchLength > 0 => MatchExport.ForMatch(
                record.MatchLength, record.EngineOne, record.EngineTwo, RetainedGames(record), tags),

            MatchStatus.Completed => MatchExport.ForMoneySession(
                record.EngineOne, record.EngineTwo, RetainedGames(record), tags),

            MatchStatus.Forfeited => MatchExport.ForForfeit(
                record.MatchLength, record.EngineOne, record.EngineTwo,
                RetainedGames(record), record.Live.PartialTranscript, ForfeitWinner(record), tags),

            MatchStatus.Aborted => MatchExport.ForAbandoned(
                record.MatchLength, record.EngineOne, record.EngineTwo,
                RetainedGames(record), record.Live.PartialTranscript,
                "Match aborted: server shutdown", tags),

            MatchStatus.Faulted => MatchExport.ForAbandoned(
                record.MatchLength, record.EngineOne, record.EngineTwo,
                RetainedGames(record), record.Live.PartialTranscript,
                "Match faulted: internal server error", tags),

            // A rehydrated orphan: the server died mid-match. Winner-less like
            // the other abandonments; the journal retained the finished games
            // and the trailing partial the export carries.
            MatchStatus.Interrupted => MatchExport.ForAbandoned(
                record.MatchLength, record.EngineOne, record.EngineTwo,
                RetainedGames(record), record.Live.PartialTranscript,
                "Match interrupted: the server was terminated while it ran", tags),

            _ => throw new InvalidOperationException(
                $"Match '{record.MatchId}' is {record.Status}; only terminal matches export "
                    + "(the endpoint gates out running matches)."),
        };
    }

    /// <summary>
    /// The finished games: the substrate result for a completed match, else the
    /// games the observer collected before the break — the same source
    /// <see cref="ReplayProjection.ToGamesResponse"/> reads.
    /// </summary>
    private static IReadOnlyList<GameRecord> RetainedGames(MatchRecord record) =>
        record.Result?.Games ?? record.Live.CompletedGames;

    /// <summary>
    /// The seat awarded a forfeited match: the one the forfeiter does not hold.
    /// Reads the same seat-attributed taxonomy the admin summary exposes
    /// (<see cref="MatchRecord.ForfeitedBy"/>), never re-deriving fault.
    /// </summary>
    private static MatchSeat ForfeitWinner(MatchRecord record) =>
        record.ForfeitedBy == record.EngineOne ? MatchSeat.Two : MatchSeat.One;
}
