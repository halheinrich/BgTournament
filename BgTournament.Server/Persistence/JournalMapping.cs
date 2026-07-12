using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Api;
using SubstrateCubeOwner = BgDataTypes_Lib.CubeOwner;
using SubstrateResultKind = BgGame_Lib.GameResultKind;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The only home of substrate/server ↔ journal-event correspondences (the
/// persistence counterpart of <c>WireMapping</c> and <c>ApiMapping</c>).
/// Substrate types are not serialization shapes: everything written to a
/// journal passes through here on the way down, and everything a rehydration
/// folds passes back through here on the way up — full fidelity both ways,
/// including the hit encoding the wire deliberately strips.
///
/// <para>Strictly frame-preserving, like the wire mapping: entries are
/// journaled in their native (on-roll) frames and rebuilt in the same frames.
/// No flip ever happens here — <c>ReplayProjection</c> owns the viewer-facing
/// re-expression, downstream of the records this mapping restores.</para>
/// </summary>
internal static class JournalMapping
{
    // ---- write side: server/substrate → journal events ----

    /// <summary>The journal header for a freshly created hosted match.</summary>
    public static MatchCreatedEvent ToCreatedEvent(MatchRecord record, DateTimeOffset at) =>
        new(
            at,
            JournalCodec.SchemaVersion,
            record.MatchId,
            record.EngineOne,
            record.EngineTwo,
            record.MatchLength,
            record.MaxGames,
            record.Seed,
            record.DiceKey is null ? null : Protocol.VerifiableDice.AlgorithmId,
            record.DiceKey?.ToHex(),
            ToJournal(record.TimeControl),
            record.CreatedBy);

    /// <summary>A game's frame-free start context, verbatim.</summary>
    public static MatchGameStartedEvent ToEvent(GameStartContext context, DateTimeOffset at) =>
        new(at, context.GameNumber, context.SeatOneScore, context.SeatTwoScore, context.IsCrawford);

    /// <summary>
    /// A transcript entry at full substrate fidelity, in its native frame.
    /// Every concrete entry type journals; an unknown one is a mapping bug.
    /// </summary>
    public static MatchJournalEvent ToEvent(TranscriptEntry entry, DateTimeOffset at) => entry switch
    {
        PlayTranscriptEntry play => new MatchPlayEvent(
            at, ToJournal(play.OnRollSeat), ToJournal(play.State), play.Die1, play.Die2,
            ToJournalMoves(play.ChosenPlay)),

        CubeTranscriptEntry cube => new MatchCubeEvent(
            at, ToJournal(cube.OnRollSeat), ToJournal(cube.State), ToJournal(cube.Action)),

        GameEndedTranscriptEntry end => new MatchGameEndedEvent(
            at, ToJournal(end.OnRollSeat), ToJournal(end.State), ToJournal(end.Result)),

        _ => throw new InvalidOperationException(
            $"Cannot journal transcript entry: {entry.GetType().Name}."),
    };

    /// <summary>One settled clocked decision — the per-decision arbitration evidence.</summary>
    public static MatchClockEvent ToEvent(ClockDecisionSettlement settlement, DateTimeOffset at) =>
        new(
            at,
            ToJournal(settlement.Seat),
            ToJournal(settlement.Decision),
            settlement.Think.TotalSeconds,
            settlement.IncrementCredited,
            settlement.RemainingBefore.TotalSeconds,
            settlement.RemainingAfter.TotalSeconds);

    /// <summary>A discarded late reply to an abandoned query (the benign race, made visible).</summary>
    public static MatchLateReplyEvent ToLateReplyEvent(MatchSeat seat, string requestId, DateTimeOffset at) =>
        new(at, ToJournal(seat), requestId);

    /// <summary>An engine's successful registration, for the server journal.</summary>
    public static EngineConnectedEvent ToConnectedEvent(EngineSession session, DateTimeOffset at) =>
        new(at, session.Name, session.Version, session.Author);

    /// <summary>A registered engine's connection end, for the server journal.</summary>
    public static EngineDisconnectedEvent ToDisconnectedEvent(EngineSession session, DateTimeOffset at) =>
        new(at, session.Name);

    /// <summary>A handshake rejection, for the server journal — same reason string the wire carries.</summary>
    public static HandshakeRejectedEvent ToRejectedEvent(string reason, string? engineName, DateTimeOffset at) =>
        new(at, reason, engineName);

    /// <summary>An admin-surface refusal, for the server journal — same reason string the response carries.</summary>
    public static AdminRejectedEvent ToAdminRejectedEvent(
        string reason, string method, string path, DateTimeOffset at) =>
        new(at, reason, method, path);

    /// <summary>The terminal outcome folded into <paramref name="record"/> by the host.</summary>
    public static MatchTerminalEvent ToTerminalEvent(MatchRecord record, DateTimeOffset at) =>
        new(
            at,
            ToJournalOutcome(record.Status),
            record.Winner,
            record.SeatOneScore,
            record.SeatTwoScore,
            record.ForfeitedBy,
            record.ForfeitCause is { } cause ? ToJournal(cause) : null,
            record.Detail);

    /// <summary>An engine's registration, for the roster journal: identity, declaration, salted credential — never the key.</summary>
    public static EngineRegisteredEvent ToRegisteredEvent(
        string name, EngineAttestation attestation, EngineCredential credential, string? actor,
        DateTimeOffset at) =>
        new(at, name, ToJournal(attestation), ToJournal(credential), actor);

    /// <summary>A re-declared attestation, for the roster journal.</summary>
    public static AttestationDeclaredEvent ToAttestationEvent(
        string name, EngineAttestation attestation, string? actor, DateTimeOffset at) =>
        new(at, name, ToJournal(attestation), actor);

    /// <summary>A key rotation, for the roster journal: the new salted credential — never the key.</summary>
    public static CredentialRotatedEvent ToRotatedEvent(
        string name, EngineCredential credential, string? actor, DateTimeOffset at) =>
        new(at, name, ToJournal(credential), actor);

    /// <summary>An engine's deactivation, for the roster journal.</summary>
    public static EngineDeactivatedEvent ToDeactivatedEvent(string name, string? actor, DateTimeOffset at) =>
        new(at, name, actor);

    /// <summary>The journal header for a freshly created tournament.</summary>
    public static TournamentCreatedEvent ToCreatedEvent(TournamentRecord record, DateTimeOffset at) =>
        new(
            at,
            JournalCodec.SchemaVersion,
            record.TournamentId,
            record.Tournament.Participants,
            record.Tournament.Format.MatchLength,
            record.Tournament.Format.MatchesPerPairing,
            record.Tournament.Seed,
            record.FairDice,
            ToJournal(record.TimeControl),
            record.CreatedBy);

    /// <summary>The tournament's terminal outcome.</summary>
    public static TournamentTerminalEvent ToTerminalEvent(
        TournamentStatus status, string? detail, DateTimeOffset at) =>
        new(at, ToJournalOutcome(status), detail);

    // ---- read side: journal events → server/substrate ----

    /// <summary>Rebuild a game's start context from its journaled event.</summary>
    public static GameStartContext ToGameStartContext(MatchGameStartedEvent evt) =>
        new(evt.GameNumber, evt.SeatOneScore, evt.SeatTwoScore, evt.IsCrawford);

    /// <summary>
    /// Rebuild the exact substrate transcript entry a journaled decision
    /// records — same frame, same hit encoding. Only the three entry events
    /// map; any other event type here is a rehydration bug.
    /// </summary>
    public static TranscriptEntry ToTranscriptEntry(MatchJournalEvent journalEvent) => journalEvent switch
    {
        MatchPlayEvent play => new PlayTranscriptEntry(
            ToSnapshot(play.State), ToSeat(play.OnRollSeat), play.Die1, play.Die2,
            ToPlay(play.Moves)),

        MatchCubeEvent cube => new CubeTranscriptEntry(
            ToSnapshot(cube.State), ToSeat(cube.OnRollSeat), ToCubeAction(cube.Action)),

        MatchGameEndedEvent end => new GameEndedTranscriptEntry(
            ToSnapshot(end.State), ToSeat(end.OnRollSeat), ToGameResult(end.Result)),

        _ => throw new InvalidOperationException(
            $"Not a transcript-entry event: {journalEvent.GetType().Name}."),
    };

    /// <summary>The record status a journaled terminal outcome folds to.</summary>
    public static MatchStatus ToMatchStatus(JournalMatchOutcome outcome) => outcome switch
    {
        JournalMatchOutcome.Completed => MatchStatus.Completed,
        JournalMatchOutcome.Forfeited => MatchStatus.Forfeited,
        JournalMatchOutcome.Aborted => MatchStatus.Aborted,
        JournalMatchOutcome.Faulted => MatchStatus.Faulted,
        _ => throw new InvalidOperationException($"Unhandled JournalMatchOutcome value: {outcome}."),
    };

    /// <summary>The record status a journaled tournament outcome folds to.</summary>
    public static TournamentStatus ToTournamentStatus(JournalTournamentOutcome outcome) => outcome switch
    {
        JournalTournamentOutcome.Completed => TournamentStatus.Completed,
        JournalTournamentOutcome.Aborted => TournamentStatus.Aborted,
        JournalTournamentOutcome.Faulted => TournamentStatus.Faulted,
        _ => throw new InvalidOperationException($"Unhandled JournalTournamentOutcome value: {outcome}."),
    };

    /// <summary>The structured forfeit cause a journaled one folds to.</summary>
    public static ForfeitCause ToForfeitCause(JournalForfeitCause cause) => cause switch
    {
        JournalForfeitCause.ContractViolation => ForfeitCause.ContractViolation,
        JournalForfeitCause.Timeout => ForfeitCause.Timeout,
        JournalForfeitCause.FlagFall => ForfeitCause.FlagFall,
        JournalForfeitCause.Disconnect => ForfeitCause.Disconnect,
        JournalForfeitCause.NeverConnected => ForfeitCause.NeverConnected,
        _ => throw new InvalidOperationException($"Unhandled JournalForfeitCause value: {cause}."),
    };

    /// <summary>Rebuild the validated Api time control from its journaled shape (null-preserving).</summary>
    public static TimeControl? ToTimeControl(JournalTimeControl? timeControl) =>
        timeControl is null ? null : new TimeControl(timeControl.InitialSeconds, timeControl.IncrementSeconds);

    /// <summary>Rebuild the Api attestation a journaled declaration folds to.</summary>
    public static EngineAttestation ToAttestation(RosterAttestation attestation) =>
        new(attestation.Authors, attestation.Origin, attestation.DerivedFrom);

    /// <summary>Rebuild the server credential a journaled one folds to.</summary>
    public static EngineCredential ToCredential(RosterCredential credential) =>
        new(credential.Scheme, credential.Salt, credential.Hash);

    /// <summary>The seat a journaled seat folds to (public: the audit read attributes evidence events with it).</summary>
    public static MatchSeat ToSeat(JournalSeat seat) =>
        seat == JournalSeat.One ? MatchSeat.One : MatchSeat.Two;

    /// <summary>The decision kind a journaled one folds to.</summary>
    public static DecisionKind ToDecisionKind(JournalDecisionKind kind) => kind switch
    {
        JournalDecisionKind.Play => DecisionKind.Play,
        JournalDecisionKind.CubeOffer => DecisionKind.CubeOffer,
        JournalDecisionKind.CubeResponse => DecisionKind.CubeResponse,
        _ => throw new InvalidOperationException($"Unhandled JournalDecisionKind value: {kind}."),
    };

    // ---- field correspondences (private: every path above funnels through these) ----

    private static JournalTimeControl? ToJournal(TimeControl? timeControl) =>
        timeControl is null ? null : new JournalTimeControl(timeControl.InitialSeconds, timeControl.IncrementSeconds);

    private static RosterAttestation ToJournal(EngineAttestation attestation) =>
        new(attestation.Authors, attestation.Origin, attestation.DerivedFrom);

    private static RosterCredential ToJournal(EngineCredential credential) =>
        new(credential.Scheme, credential.Salt, credential.Hash);

    private static JournalDecisionKind ToJournal(DecisionKind kind) => kind switch
    {
        DecisionKind.Play => JournalDecisionKind.Play,
        DecisionKind.CubeOffer => JournalDecisionKind.CubeOffer,
        DecisionKind.CubeResponse => JournalDecisionKind.CubeResponse,
        _ => throw new InvalidOperationException($"Unhandled DecisionKind value: {kind}."),
    };

    private static JournalGameState ToJournal(GameSnapshot state) =>
        new(
            state.Board,
            state.CubeSize,
            ToJournal(state.CubeOwner),
            state.Match.MatchLength,
            state.Match.OnRollScore,
            state.Match.OpponentScore,
            state.Match.IsCrawford);

    private static GameSnapshot ToSnapshot(JournalGameState state) =>
        new(
            state.Board.ToArray(),
            state.CubeSize,
            ToCubeOwner(state.CubeOwner),
            new MatchSnapshot(state.MatchLength, state.OnRollScore, state.OpponentScore, state.IsCrawford));

    /// <summary>The play's moves in play order, sign encoding (hits, bear-offs) intact.</summary>
    private static IReadOnlyList<JournalMove> ToJournalMoves(Play play)
    {
        var moves = new JournalMove[play.Count];
        for (int i = 0; i < moves.Length; i++)
        {
            moves[i] = new JournalMove(play[i].FrPt, play[i].ToPt);
        }

        return moves;
    }

    private static Play ToPlay(IReadOnlyList<JournalMove> moves)
    {
        var play = new Play();
        foreach (var move in moves)
        {
            play.Add(new Move(move.From, move.To));
        }

        return play;
    }

    private static JournalSeat ToJournal(MatchSeat seat) =>
        seat == MatchSeat.One ? JournalSeat.One : JournalSeat.Two;

    private static JournalCubeOwner ToJournal(SubstrateCubeOwner owner) => owner switch
    {
        SubstrateCubeOwner.Centered => JournalCubeOwner.Centered,
        SubstrateCubeOwner.OnRoll => JournalCubeOwner.OnRoll,
        SubstrateCubeOwner.Opponent => JournalCubeOwner.Opponent,
        _ => throw new InvalidOperationException($"Unhandled CubeOwner value: {owner}."),
    };

    private static SubstrateCubeOwner ToCubeOwner(JournalCubeOwner owner) => owner switch
    {
        JournalCubeOwner.Centered => SubstrateCubeOwner.Centered,
        JournalCubeOwner.OnRoll => SubstrateCubeOwner.OnRoll,
        JournalCubeOwner.Opponent => SubstrateCubeOwner.Opponent,
        _ => throw new InvalidOperationException($"Unhandled JournalCubeOwner value: {owner}."),
    };

    private static JournalCubeAction ToJournal(CubeAction action) => action switch
    {
        CubeAction.NoDouble => JournalCubeAction.NoDouble,
        CubeAction.Double => JournalCubeAction.Double,
        CubeAction.Take => JournalCubeAction.Take,
        CubeAction.Pass => JournalCubeAction.Pass,
        _ => throw new InvalidOperationException($"Unhandled CubeAction value: {action}."),
    };

    private static CubeAction ToCubeAction(JournalCubeAction action) => action switch
    {
        JournalCubeAction.NoDouble => CubeAction.NoDouble,
        JournalCubeAction.Double => CubeAction.Double,
        JournalCubeAction.Take => CubeAction.Take,
        JournalCubeAction.Pass => CubeAction.Pass,
        _ => throw new InvalidOperationException($"Unhandled JournalCubeAction value: {action}."),
    };

    private static JournalGameResult ToJournal(GameResult result) =>
        new(ToJournal(result.Kind), result.OnRollWon, result.CubeSize);

    private static GameResult ToGameResult(JournalGameResult result) =>
        new(ToResultKind(result.Kind), result.OnRollWon, result.CubeSize);

    private static JournalResultKind ToJournal(SubstrateResultKind kind) => kind switch
    {
        SubstrateResultKind.WinSingle => JournalResultKind.Single,
        SubstrateResultKind.WinGammon => JournalResultKind.Gammon,
        SubstrateResultKind.WinBackgammon => JournalResultKind.Backgammon,
        _ => throw new InvalidOperationException($"Unhandled GameResultKind value: {kind}."),
    };

    private static SubstrateResultKind ToResultKind(JournalResultKind kind) => kind switch
    {
        JournalResultKind.Single => SubstrateResultKind.WinSingle,
        JournalResultKind.Gammon => SubstrateResultKind.WinGammon,
        JournalResultKind.Backgammon => SubstrateResultKind.WinBackgammon,
        _ => throw new InvalidOperationException($"Unhandled JournalResultKind value: {kind}."),
    };

    private static JournalMatchOutcome ToJournalOutcome(MatchStatus status) => status switch
    {
        MatchStatus.Completed => JournalMatchOutcome.Completed,
        MatchStatus.Forfeited => JournalMatchOutcome.Forfeited,
        MatchStatus.Aborted => JournalMatchOutcome.Aborted,
        MatchStatus.Faulted => JournalMatchOutcome.Faulted,
        _ => throw new InvalidOperationException(
            $"Only terminal outcomes are journaled; '{status}' never writes a terminal event."),
    };

    private static JournalTournamentOutcome ToJournalOutcome(TournamentStatus status) => status switch
    {
        TournamentStatus.Completed => JournalTournamentOutcome.Completed,
        TournamentStatus.Aborted => JournalTournamentOutcome.Aborted,
        TournamentStatus.Faulted => JournalTournamentOutcome.Faulted,
        _ => throw new InvalidOperationException(
            $"Only terminal outcomes are journaled; '{status}' never writes a terminal event."),
    };

    private static JournalForfeitCause ToJournal(ForfeitCause cause) => cause switch
    {
        ForfeitCause.ContractViolation => JournalForfeitCause.ContractViolation,
        ForfeitCause.Timeout => JournalForfeitCause.Timeout,
        ForfeitCause.FlagFall => JournalForfeitCause.FlagFall,
        ForfeitCause.Disconnect => JournalForfeitCause.Disconnect,
        ForfeitCause.NeverConnected => JournalForfeitCause.NeverConnected,
        _ => throw new InvalidOperationException($"Unhandled ForfeitCause value: {cause}."),
    };
}
