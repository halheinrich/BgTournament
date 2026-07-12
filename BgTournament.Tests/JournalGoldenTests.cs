using BgTournament.Server.Persistence;

namespace BgTournament.Tests;

/// <summary>
/// Pins the exact JSONL bytes of every journal event and every journal enum
/// string — the same discipline <see cref="GoldenWireTests"/> applies to the
/// wire and <see cref="ApiGoldenTests"/> to the admin contracts, with one
/// difference in stakes: journal files are durable, so a drift here is not
/// just a consumer break, it silently orphans every record already on disk.
/// A deliberate format change bumps <c>JournalCodec.SchemaVersion</c> and
/// moves these pins together.
/// </summary>
public class JournalGoldenTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
    private const string AtWire = "2026-07-05T12:00:00+00:00";

    /// <summary>A representative snapshot (the opening position, mover's frame).</summary>
    private static readonly JournalGameState State = new(
        new[] { 0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0 },
        CubeSize: 1, JournalCubeOwner.Centered,
        MatchLength: 5, OnRollScore: 2, OpponentScore: 3, IsCrawford: false);

    private const string StateWire =
        """{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeSize":1,"cubeOwner":"centered","matchLength":5,"onRollScore":2,"opponentScore":3,"isCrawford":false}""";

    private static void AssertGolden(MatchJournalEvent journalEvent, string golden)
    {
        Assert.Equal(golden, JournalCodec.Serialize(journalEvent));
        Assert.Equal(golden, JournalCodec.Serialize(JournalCodec.DeserializeMatchEvent(golden)));
    }

    private static void AssertGolden(TournamentJournalEvent journalEvent, string golden)
    {
        Assert.Equal(golden, JournalCodec.Serialize(journalEvent));
        Assert.Equal(golden, JournalCodec.Serialize(JournalCodec.DeserializeTournamentEvent(golden)));
    }

    private static void AssertGolden(ServerJournalEvent journalEvent, string golden)
    {
        Assert.Equal(golden, JournalCodec.Serialize(journalEvent));
        Assert.Equal(golden, JournalCodec.Serialize(JournalCodec.DeserializeServerEvent(golden)));
    }

    /// <summary>
    /// The version constants are format facts, pinned like bytes: v2 added the
    /// match-journal arbitration evidence; v1 files must keep folding; the
    /// server journal versions independently — its v2 added the admin-refusal
    /// evidence. The <c>createdBy</c> actor stamp on the created headers is
    /// deliberately <em>not</em> a bump: it is an optional field whose absence
    /// means exactly what it meant before the field existed (no authenticated
    /// actor), so old files stay truthful under the same version.
    /// </summary>
    [Fact]
    public void SchemaVersions_Pinned()
    {
        Assert.Equal(2, JournalCodec.SchemaVersion);
        Assert.Equal(1, JournalCodec.MinSchemaVersion);
        Assert.Equal(2, JournalCodec.ServerSchemaVersion);
        Assert.Equal(1, JournalCodec.RosterSchemaVersion);
        Assert.True(JournalCodec.IsSupported(1));
        Assert.True(JournalCodec.IsSupported(2));
        Assert.False(JournalCodec.IsSupported(0));
        Assert.False(JournalCodec.IsSupported(3));
        Assert.True(JournalCodec.IsRosterSupported(1));
        Assert.False(JournalCodec.IsRosterSupported(0));
        Assert.False(JournalCodec.IsRosterSupported(2));
    }

    [Fact]
    public void MatchCreated_FairClocked_Golden() =>
        AssertGolden(
            new MatchCreatedEvent(
                At, JournalCodec.SchemaVersion, "match-1", "Alpha", "Beta", MatchLength: 7, MaxGames: 50,
                Seed: 42,
                DiceAlgorithm: "hmac-sha256-dice-v1",
                DiceKey: "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
                TimeControl: new JournalTimeControl(120, 8),
                CreatedBy: "director"),
            $$$"""{"type":"created","schemaVersion":2,"at":"{{{AtWire}}}","matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":7,"maxGames":50,"seed":42,"diceAlgorithm":"hmac-sha256-dice-v1","diceKey":"00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff","timeControl":{"initialSeconds":120,"incrementSeconds":8},"createdBy":"director"}""");

    /// <summary>
    /// Explicit-seed, flat-regime, anonymous header: every optional field is
    /// omitted, not null — which also proves an actor-less header is
    /// byte-identical to every file written before the actor stamp existed.
    /// </summary>
    [Fact]
    public void MatchCreated_SeededFlat_OmitsAbsentFields() =>
        AssertGolden(
            new MatchCreatedEvent(
                At, JournalCodec.SchemaVersion, "match-1", "Alpha", "Beta", MatchLength: 1, MaxGames: null,
                Seed: 7, DiceAlgorithm: null, DiceKey: null, TimeControl: null, CreatedBy: null),
            $$"""{"type":"created","schemaVersion":2,"at":"{{AtWire}}","matchId":"match-1","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7}""");

    [Fact]
    public void MatchStarted_Golden() =>
        AssertGolden(
            new MatchStartedEvent(At),
            $$"""{"type":"started","at":"{{AtWire}}"}""");

    [Fact]
    public void MatchGameStarted_Golden() =>
        AssertGolden(
            new MatchGameStartedEvent(At, GameNumber: 3, SeatOneScore: 2, SeatTwoScore: 1, IsCrawford: true),
            $$"""{"type":"gameStarted","at":"{{AtWire}}","gameNumber":3,"seatOneScore":2,"seatTwoScore":1,"isCrawford":true}""");

    /// <summary>
    /// The sign encoding is the point of this pin: a hit is a negative
    /// destination and a bear-off is 0 — full substrate fidelity, unlike the
    /// wire's deliberately hit-less moves.
    /// </summary>
    [Fact]
    public void MatchPlay_HitAndBearOff_SignEncodingPinned() =>
        AssertGolden(
            new MatchPlayEvent(
                At, JournalSeat.One, State, Die1: 3, Die2: 1,
                Moves: [new JournalMove(8, -5), new JournalMove(6, 0)]),
            $$"""{"type":"play","at":"{{AtWire}}","onRollSeat":"one","state":{{StateWire}},"die1":3,"die2":1,"moves":[{"from":8,"to":-5},{"from":6,"to":0}]}""");

    /// <summary>A dance is an empty play, not an absent one.</summary>
    [Fact]
    public void MatchPlay_Dance_EmptyMoves() =>
        AssertGolden(
            new MatchPlayEvent(At, JournalSeat.Two, State, Die1: 6, Die2: 6, Moves: []),
            $$"""{"type":"play","at":"{{AtWire}}","onRollSeat":"two","state":{{StateWire}},"die1":6,"die2":6,"moves":[]}""");

    [Theory]
    [InlineData(nameof(JournalCubeAction.NoDouble), "noDouble")]
    [InlineData(nameof(JournalCubeAction.Double), "double")]
    [InlineData(nameof(JournalCubeAction.Take), "take")]
    [InlineData(nameof(JournalCubeAction.Pass), "pass")]
    public void MatchCube_EveryAction(string action, string wire) =>
        AssertGolden(
            new MatchCubeEvent(At, JournalSeat.One, State, Enum.Parse<JournalCubeAction>(action)),
            $$"""{"type":"cube","at":"{{AtWire}}","onRollSeat":"one","state":{{StateWire}},"action":"{{wire}}"}""");

    [Theory]
    [InlineData(nameof(JournalResultKind.Single), "single")]
    [InlineData(nameof(JournalResultKind.Gammon), "gammon")]
    [InlineData(nameof(JournalResultKind.Backgammon), "backgammon")]
    public void MatchGameEnded_EveryResultKind(string kind, string wire) =>
        AssertGolden(
            new MatchGameEndedEvent(
                At, JournalSeat.Two, State,
                new JournalGameResult(Enum.Parse<JournalResultKind>(kind), OnRollWon: true, CubeSize: 2)),
            $$$"""{"type":"gameEnded","at":"{{{AtWire}}}","onRollSeat":"two","state":{{{StateWire}}},"result":{"kind":"{{{wire}}}","onRollWon":true,"cubeSize":2}}""");

    /// <summary>Non-centered cube owners, pinned once (the play/cube pins above use centered).</summary>
    [Theory]
    [InlineData(nameof(JournalCubeOwner.OnRoll), "onRoll")]
    [InlineData(nameof(JournalCubeOwner.Opponent), "opponent")]
    public void JournalCubeOwner_NonCentered(string owner, string wire) =>
        AssertGolden(
            new MatchCubeEvent(
                At, JournalSeat.One,
                State with { CubeSize = 2, CubeOwner = Enum.Parse<JournalCubeOwner>(owner) },
                JournalCubeAction.NoDouble),
            $$"""{"type":"cube","at":"{{AtWire}}","onRollSeat":"one","state":{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeSize":2,"cubeOwner":"{{wire}}","matchLength":5,"onRollScore":2,"opponentScore":3,"isCrawford":false},"action":"noDouble"}""");

    /// <summary>
    /// The per-decision clock evidence (schema v2). Every decision kind, and
    /// the credited/uncredited branch, byte-pinned.
    /// </summary>
    [Theory]
    [InlineData(nameof(JournalDecisionKind.Play), "play")]
    [InlineData(nameof(JournalDecisionKind.CubeOffer), "cubeOffer")]
    [InlineData(nameof(JournalDecisionKind.CubeResponse), "cubeResponse")]
    public void MatchClock_EveryDecisionKind(string kind, string wire) =>
        AssertGolden(
            new MatchClockEvent(
                At, JournalSeat.One, Enum.Parse<JournalDecisionKind>(kind),
                ThinkSeconds: 12.5, IncrementCredited: true,
                RemainingBeforeSeconds: 120, RemainingAfterSeconds: 115.5),
            $$"""{"type":"clock","at":"{{AtWire}}","seat":"one","decision":"{{wire}}","thinkSeconds":12.5,"incrementCredited":true,"remainingBeforeSeconds":120,"remainingAfterSeconds":115.5}""");

    /// <summary>A flagged decision: debit without credit, pool floored at zero.</summary>
    [Fact]
    public void MatchClock_FlagFall_UncreditedZeroFloor() =>
        AssertGolden(
            new MatchClockEvent(
                At, JournalSeat.Two, JournalDecisionKind.Play,
                ThinkSeconds: 200, IncrementCredited: false,
                RemainingBeforeSeconds: 120, RemainingAfterSeconds: 0),
            $$"""{"type":"clock","at":"{{AtWire}}","seat":"two","decision":"play","thinkSeconds":200,"incrementCredited":false,"remainingBeforeSeconds":120,"remainingAfterSeconds":0}""");

    /// <summary>The benign race, made visible (schema v2).</summary>
    [Fact]
    public void MatchLateReply_Golden() =>
        AssertGolden(
            new MatchLateReplyEvent(At, JournalSeat.Two, RequestId: "q-17"),
            $$"""{"type":"lateReply","at":"{{AtWire}}","seat":"two","requestId":"q-17"}""");

    [Fact]
    public void MatchTerminal_Completed_Golden() =>
        AssertGolden(
            new MatchTerminalEvent(
                At, JournalMatchOutcome.Completed, Winner: "Alpha", SeatOneScore: 7, SeatTwoScore: 3,
                ForfeitedBy: null, ForfeitCause: null, Detail: null),
            $$"""{"type":"terminal","at":"{{AtWire}}","status":"completed","winner":"Alpha","seatOneScore":7,"seatTwoScore":3}""");

    /// <summary>A capped money session ends completed with no winner — the field is omitted.</summary>
    [Fact]
    public void MatchTerminal_CompletedNoWinner_Golden() =>
        AssertGolden(
            new MatchTerminalEvent(
                At, JournalMatchOutcome.Completed, Winner: null, SeatOneScore: 4, SeatTwoScore: 6,
                ForfeitedBy: null, ForfeitCause: null, Detail: null),
            $$"""{"type":"terminal","at":"{{AtWire}}","status":"completed","seatOneScore":4,"seatTwoScore":6}""");

    /// <summary>
    /// Every structured forfeit cause. Note the default encoder escapes the
    /// apostrophes engine names are quoted with — pinned deliberately, the
    /// Api-golden precedent.
    /// </summary>
    [Theory]
    [InlineData(nameof(JournalForfeitCause.ContractViolation), "contractViolation")]
    [InlineData(nameof(JournalForfeitCause.Timeout), "timeout")]
    [InlineData(nameof(JournalForfeitCause.FlagFall), "flagFall")]
    [InlineData(nameof(JournalForfeitCause.Disconnect), "disconnect")]
    [InlineData(nameof(JournalForfeitCause.NeverConnected), "neverConnected")]
    public void MatchTerminal_Forfeited_EveryCause(string cause, string wire) =>
        AssertGolden(
            new MatchTerminalEvent(
                At, JournalMatchOutcome.Forfeited, Winner: "Beta", SeatOneScore: null,
                SeatTwoScore: null, ForfeitedBy: "Alpha",
                ForfeitCause: Enum.Parse<JournalForfeitCause>(cause),
                Detail: "Engine Alpha broke the contract."),
            $$"""{"type":"terminal","at":"{{AtWire}}","status":"forfeited","winner":"Beta","forfeitedBy":"Alpha","forfeitCause":"{{wire}}","detail":"Engine Alpha broke the contract."}""");

    [Theory]
    [InlineData(nameof(JournalMatchOutcome.Aborted), "aborted")]
    [InlineData(nameof(JournalMatchOutcome.Faulted), "faulted")]
    public void MatchTerminal_WinnerlessOutcomes(string outcome, string wire) =>
        AssertGolden(
            new MatchTerminalEvent(
                At, Enum.Parse<JournalMatchOutcome>(outcome), Winner: null, SeatOneScore: null,
                SeatTwoScore: null,
                ForfeitedBy: null, ForfeitCause: null, Detail: "The server stopped the match."),
            $$"""{"type":"terminal","at":"{{AtWire}}","status":"{{wire}}","detail":"The server stopped the match."}""");

    [Fact]
    public void TournamentCreated_Golden() =>
        AssertGolden(
            new TournamentCreatedEvent(
                At, JournalCodec.SchemaVersion, "tournament-1", ["Alpha", "Beta", "Gamma"], MatchLength: 3,
                MatchesPerPairing: 2, Seed: 99, FairDice: true,
                TimeControl: new JournalTimeControl(60, 5),
                CreatedBy: "director"),
            $$$"""{"type":"created","schemaVersion":2,"at":"{{{AtWire}}}","tournamentId":"tournament-1","participants":["Alpha","Beta","Gamma"],"matchLength":3,"matchesPerPairing":2,"seed":99,"fairDice":true,"timeControl":{"initialSeconds":60,"incrementSeconds":5},"createdBy":"director"}""");

    /// <summary>An anonymous, seeded, flat-regime header omits every optional field — byte-identical to the pre-actor format.</summary>
    [Fact]
    public void TournamentCreated_SeededFlat_OmitsTimeControl() =>
        AssertGolden(
            new TournamentCreatedEvent(
                At, JournalCodec.SchemaVersion, "tournament-1", ["Alpha", "Beta"], MatchLength: 1,
                MatchesPerPairing: 1, Seed: 7, FairDice: false, TimeControl: null, CreatedBy: null),
            $$"""{"type":"created","schemaVersion":2,"at":"{{AtWire}}","tournamentId":"tournament-1","participants":["Alpha","Beta"],"matchLength":1,"matchesPerPairing":1,"seed":7,"fairDice":false}""");

    [Fact]
    public void TournamentMatchStarted_Golden() =>
        AssertGolden(
            new TournamentMatchStartedEvent(At, MatchIndex: 4, MatchId: "match-5"),
            $$"""{"type":"matchStarted","at":"{{AtWire}}","matchIndex":4,"matchId":"match-5"}""");

    [Fact]
    public void TournamentResult_Golden() =>
        AssertGolden(
            new TournamentResultEvent(At, MatchIndex: 4, Winner: "Alpha"),
            $$"""{"type":"result","at":"{{AtWire}}","matchIndex":4,"winner":"Alpha"}""");

    [Theory]
    [InlineData(nameof(JournalTournamentOutcome.Completed), "completed")]
    [InlineData(nameof(JournalTournamentOutcome.Aborted), "aborted")]
    [InlineData(nameof(JournalTournamentOutcome.Faulted), "faulted")]
    public void TournamentTerminal_EveryOutcome(string outcome, string wire) =>
        AssertGolden(
            new TournamentTerminalEvent(At, Enum.Parse<JournalTournamentOutcome>(outcome), Detail: null),
            $$"""{"type":"terminal","at":"{{AtWire}}","status":"{{wire}}"}""");

    [Fact]
    public void TournamentTerminal_WithDetail_Golden() =>
        AssertGolden(
            new TournamentTerminalEvent(
                At, JournalTournamentOutcome.Aborted, Detail: "The server stopped the tournament."),
            $$"""{"type":"terminal","at":"{{AtWire}}","status":"aborted","detail":"The server stopped the tournament."}""");

    // ---- the roster journal (registration history; independent schema) ----

    private static void AssertGolden(RosterJournalEvent journalEvent, string golden)
    {
        Assert.Equal(golden, JournalCodec.Serialize(journalEvent));
        Assert.Equal(golden, JournalCodec.Serialize(JournalCodec.DeserializeRosterEvent(golden)));
    }

    private static readonly RosterCredential Credential = new(
        Scheme: "sha256-salted-v1",
        Salt: "00112233445566778899aabbccddeeff",
        Hash: "aa5566778899aabbccddeeff0011223344556677aabbccddeeff001122334455");

    private const string CredentialWire =
        """{"scheme":"sha256-salted-v1","salt":"00112233445566778899aabbccddeeff","hash":"aa5566778899aabbccddeeff0011223344556677aabbccddeeff001122334455"}""";

    [Fact]
    public void RosterStarted_Golden() =>
        AssertGolden(
            new RosterStartedEvent(At, JournalCodec.RosterSchemaVersion),
            $$"""{"type":"started","schemaVersion":1,"at":"{{AtWire}}"}""");

    /// <summary>
    /// The registration record: identity, declaration, salted credential —
    /// the pin also documents that no plaintext key ever appears here.
    /// </summary>
    [Fact]
    public void EngineRegistered_Golden() =>
        AssertGolden(
            new EngineRegisteredEvent(
                At, "MyBot",
                new RosterAttestation(
                    ["Jane Doe", "Ken Kata"], "Original neural-net engine.",
                    DerivedFrom: "gnubg 1.08 evaluation weights"),
                Credential, Actor: "director"),
            $$$"""{"type":"registered","at":"{{{AtWire}}}","name":"MyBot","attestation":{"authors":["Jane Doe","Ken Kata"],"origin":"Original neural-net engine.","derivedFrom":"gnubg 1.08 evaluation weights"},"credential":{{{CredentialWire}}},"actor":"director"}""");

    /// <summary>An original work registered anonymously: derivedFrom and actor are omitted, not null.</summary>
    [Fact]
    public void EngineRegistered_AnonymousOriginal_OmitsAbsentFields() =>
        AssertGolden(
            new EngineRegisteredEvent(
                At, "MyBot",
                new RosterAttestation(["Jane Doe"], "Original work.", DerivedFrom: null),
                Credential, Actor: null),
            $$$"""{"type":"registered","at":"{{{AtWire}}}","name":"MyBot","attestation":{"authors":["Jane Doe"],"origin":"Original work."},"credential":{{{CredentialWire}}}}""");

    [Fact]
    public void AttestationDeclared_Golden() =>
        AssertGolden(
            new AttestationDeclaredEvent(
                At, "MyBot",
                new RosterAttestation(["Jane Doe"], "Corrected: derived work.", "BgRLEngine parity model"),
                Actor: "director"),
            $$"""{"type":"attestation","at":"{{AtWire}}","name":"MyBot","attestation":{"authors":["Jane Doe"],"origin":"Corrected: derived work.","derivedFrom":"BgRLEngine parity model"},"actor":"director"}""");

    [Fact]
    public void CredentialRotated_Golden() =>
        AssertGolden(
            new CredentialRotatedEvent(At, "MyBot", Credential, Actor: "director"),
            $$$"""{"type":"credentialRotated","at":"{{{AtWire}}}","name":"MyBot","credential":{{{CredentialWire}}},"actor":"director"}""");

    [Fact]
    public void EngineDeactivated_Golden() =>
        AssertGolden(
            new EngineDeactivatedEvent(At, "MyBot", Actor: "director"),
            $$"""{"type":"deactivated","at":"{{AtWire}}","name":"MyBot","actor":"director"}""");

    // ---- the server journal (engine lifecycle evidence; independent schema) ----

    [Fact]
    public void ServerStarted_Golden() =>
        AssertGolden(
            new ServerStartedEvent(At, JournalCodec.ServerSchemaVersion),
            $$"""{"type":"started","schemaVersion":2,"at":"{{AtWire}}"}""");

    [Fact]
    public void EngineConnected_Golden() =>
        AssertGolden(
            new EngineConnectedEvent(At, EngineName: "Alpha", Version: "1.2", Author: "Ada"),
            $$"""{"type":"engineConnected","at":"{{AtWire}}","engineName":"Alpha","version":"1.2","author":"Ada"}""");

    /// <summary>The hello's optional identity fields are omitted, not null.</summary>
    [Fact]
    public void EngineConnected_MinimalHello_OmitsAbsentFields() =>
        AssertGolden(
            new EngineConnectedEvent(At, EngineName: "Alpha", Version: null, Author: null),
            $$"""{"type":"engineConnected","at":"{{AtWire}}","engineName":"Alpha"}""");

    [Fact]
    public void EngineDisconnected_Golden() =>
        AssertGolden(
            new EngineDisconnectedEvent(At, EngineName: "Alpha"),
            $$"""{"type":"engineDisconnected","at":"{{AtWire}}","engineName":"Alpha"}""");

    /// <summary>A rejection carrying the claimed name (duplicate-name case). Note the escaped apostrophes, the Api-golden precedent.</summary>
    [Fact]
    public void HandshakeRejected_WithName_Golden() =>
        AssertGolden(
            new HandshakeRejectedEvent(
                At, Reason: "An engine named 'Alpha' is already connected.", EngineName: "Alpha"),
            $$"""{"type":"handshakeRejected","at":"{{AtWire}}","reason":"An engine named \u0027Alpha\u0027 is already connected.","engineName":"Alpha"}""");

    /// <summary>A rejection before a usable hello: the name is omitted, not null.</summary>
    [Fact]
    public void HandshakeRejected_NoName_OmitsField() =>
        AssertGolden(
            new HandshakeRejectedEvent(
                At, Reason: "No hello within the handshake timeout (10 s).", EngineName: null),
            $$"""{"type":"handshakeRejected","at":"{{AtWire}}","reason":"No hello within the handshake timeout (10 s)."}""");

    /// <summary>
    /// An admin-surface refusal (server schema v2) — the reason is the exact
    /// response body string; the presented key value is never recorded.
    /// </summary>
    [Fact]
    public void AdminRejected_Golden() =>
        AssertGolden(
            new AdminRejectedEvent(
                At, Reason: "The presented admin API key is not recognized.",
                Method: "POST", Path: "/matches"),
            $$"""{"type":"adminRejected","at":"{{AtWire}}","reason":"The presented admin API key is not recognized.","method":"POST","path":"/matches"}""");

    [Fact]
    public void ServerStopped_Golden() =>
        AssertGolden(
            new ServerStoppedEvent(At),
            $$"""{"type":"stopped","at":"{{AtWire}}"}""");
}
