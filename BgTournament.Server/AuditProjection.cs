using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Api;
using BgTournament.Server.Persistence;

namespace BgTournament.Server;

/// <summary>
/// Projects a terminal match's journal onto the audit contract
/// (<see cref="MatchAuditResponse"/>) — the arbitration sibling of
/// <see cref="ReplayProjection"/>, at a deliberately different altitude:
/// timestamps, attribution, per-decision clock evidence, discipline events,
/// and the terminal outcome, with no boards and no moves (replay's surface).
///
/// <para><b>Layering.</b> The walker composes the two mapping homes and adds
/// none of its own correspondences: journal events fold back to substrate/
/// server types through <see cref="JournalMapping"/> (so cube attribution is
/// read off the substrate's stamped <c>ActingSeat</c>, never re-derived from
/// sequencing) and cross to the Api vocabulary through
/// <see cref="ApiMapping"/>. Scalars the journal already carries in the
/// served unit (timestamps, dice, pool seconds) are passed through verbatim —
/// the rehydrator's precedent — so no round-trip can perturb the evidence.</para>
///
/// <para><b>The terminal event is record-derived.</b> The journal's terminal
/// line, when present, is skipped and the timeline is closed from the
/// <see cref="MatchRecord"/> instead — the host truth the journal line
/// mirrors (a rehydrated record <em>is</em> that line's fold). That way an
/// Interrupted match, whose journal has no terminal line, still ends its
/// timeline truthfully (null <c>at</c> — the true end died with the server),
/// and the fair-dice key reveal rides every terminal outcome. The derived
/// commitment on the created event comes from the same single derivation the
/// wire used (<see cref="MatchRecord.Commitment"/>), never from storage.</para>
/// </summary>
internal static class AuditProjection
{
    /// <summary>
    /// Project the journal's trusted event prefix, closed with the
    /// record-derived terminal event. The caller gates out running matches
    /// and supplies the prefix via <see cref="JournalReader"/>.
    /// </summary>
    public static MatchAuditResponse ToAuditResponse(
        MatchRecord record, IReadOnlyList<MatchJournalEvent> trustedEvents, string? corruptionDetail)
    {
        var events = new List<AuditEvent>(trustedEvents.Count + 1);
        int gameNumber = 0;
        int entryIndex = 0;

        foreach (var journalEvent in trustedEvents)
        {
            switch (journalEvent)
            {
                case MatchCreatedEvent created:
                    events.Add(new AuditCreatedEvent(
                        created.At,
                        created.MatchLength,
                        created.MaxGames,
                        created.Seed,
                        created.DiceAlgorithm,
                        record.Commitment?.ToHex(),
                        JournalMapping.ToTimeControl(created.TimeControl)));
                    break;

                case MatchStartedEvent started:
                    events.Add(new AuditStartedEvent(started.At));
                    break;

                case MatchGameStartedEvent gameStarted:
                    gameNumber = gameStarted.GameNumber;
                    entryIndex = 0;
                    events.Add(new AuditGameStartedEvent(
                        gameStarted.At,
                        gameStarted.GameNumber,
                        gameStarted.SeatOneScore,
                        gameStarted.SeatTwoScore,
                        gameStarted.IsCrawford));
                    break;

                case MatchPlayEvent play:
                    events.Add(new AuditPlayEvent(
                        play.At,
                        gameNumber,
                        entryIndex++,
                        ApiMapping.ToSeat(JournalMapping.ToSeat(play.OnRollSeat)),
                        play.Die1,
                        play.Die2));
                    break;

                case MatchCubeEvent cube:
                    events.Add(ToCubeEvent(cube, gameNumber, entryIndex++));
                    break;

                case MatchGameEndedEvent gameEnded:
                    var end = (GameEndedTranscriptEntry)JournalMapping.ToTranscriptEntry(gameEnded);
                    events.Add(new AuditGameEndedEvent(
                        gameEnded.At,
                        gameNumber,
                        ApiMapping.ToSeat(end.Winner),
                        ApiMapping.ToResultKind(end.Result.Kind),
                        end.Result.CubeSize));
                    break;

                case MatchClockEvent clock:
                    events.Add(new AuditClockEvent(
                        clock.At,
                        gameNumber,
                        ApiMapping.ToSeat(JournalMapping.ToSeat(clock.Seat)),
                        ApiMapping.ToApiDecisionKind(JournalMapping.ToDecisionKind(clock.Decision)),
                        clock.ThinkSeconds,
                        clock.IncrementCredited,
                        clock.RemainingBeforeSeconds,
                        clock.RemainingAfterSeconds));
                    break;

                case MatchLateReplyEvent lateReply:
                    events.Add(new AuditLateReplyEvent(
                        lateReply.At,
                        ApiMapping.ToSeat(JournalMapping.ToSeat(lateReply.Seat)),
                        lateReply.RequestId));
                    break;

                case MatchTerminalEvent:
                    // Skipped: the timeline is closed from the record below —
                    // identical content for a journaled terminal (the record
                    // is its fold), and the only truthful close for a journal
                    // that never got one.
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled match journal event: {journalEvent.GetType().Name}.");
            }
        }

        events.Add(new AuditTerminalEvent(
            record.EndedAtUtc,
            record.Status,
            record.Winner,
            record.SeatOneScore,
            record.SeatTwoScore,
            record.ForfeitedBy,
            record.ForfeitCause is { } cause ? ApiMapping.ToApiForfeitCause(cause) : null,
            record.Detail,
            record.DiceKey is null ? null : Protocol.VerifiableDice.AlgorithmId,
            record.DiceKey?.ToHex()));

        return new MatchAuditResponse(
            record.MatchId, record.EngineOne, record.EngineTwo, record.Status, corruptionDetail, events);
    }

    /// <summary>
    /// Attribute a journaled cube decision by folding it back to the
    /// substrate entry and reading the stamped <c>ActingSeat</c> — the
    /// subtype-owned attribution rule, reused, never re-derived. Mirrors
    /// replay's offer/response split (a NoDouble is never recorded as an
    /// entry, so mapping one here is the same projection bug it is there).
    /// </summary>
    private static AuditEvent ToCubeEvent(MatchCubeEvent cube, int gameNumber, int entryIndex)
    {
        var entry = (CubeTranscriptEntry)JournalMapping.ToTranscriptEntry(cube);
        var actor = ApiMapping.ToSeat(entry.ActingSeat);
        return entry.Action switch
        {
            CubeAction.Double => new AuditCubeOfferEvent(cube.At, gameNumber, entryIndex, actor),
            CubeAction.Take => new AuditCubeResponseEvent(
                cube.At, gameNumber, entryIndex, actor, CubeResponseAction.Take),
            CubeAction.Pass => new AuditCubeResponseEvent(
                cube.At, gameNumber, entryIndex, actor, CubeResponseAction.Pass),
            _ => throw new InvalidOperationException(
                $"Cannot project cube action as an audit entry: {entry.Action}."),
        };
    }
}
