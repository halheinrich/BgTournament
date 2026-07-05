using BgDataTypes_Lib;
using BgGame_Lib;
using BgTournament.Server;
using BgTournament.Server.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using SubstrateCubeOwner = BgDataTypes_Lib.CubeOwner;

namespace BgTournament.Tests;

/// <summary>
/// The journal mapping is a fidelity contract: a transcript entry written
/// down and folded back must be the <em>same substrate fact</em> — same
/// frame, same hit encoding, same derived attribution. Every round trip here
/// passes through the codec's actual bytes, not just the DTOs.
/// </summary>
public class JournalMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static GameSnapshot Snapshot(
        SubstrateCubeOwner owner = SubstrateCubeOwner.Centered, int cubeSize = 1) =>
        new(
            new[] { 0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0 },
            cubeSize, owner, new MatchSnapshot(MatchLength: 5, OnRollScore: 2, OpponentScore: 3, IsCrawford: false));

    /// <summary>Write the entry down, read the bytes back, rebuild the entry.</summary>
    private static TranscriptEntry RoundTrip(TranscriptEntry entry)
    {
        string line = JournalCodec.Serialize(JournalMapping.ToEvent(entry, At));
        return JournalMapping.ToTranscriptEntry(JournalCodec.DeserializeMatchEvent(line));
    }

    private static void AssertSnapshotEqual(GameSnapshot expected, GameSnapshot actual)
    {
        Assert.Equal(expected.Board, actual.Board);
        Assert.Equal(expected.CubeSize, actual.CubeSize);
        Assert.Equal(expected.CubeOwner, actual.CubeOwner);
        Assert.Equal(expected.Match, actual.Match);
    }

    /// <summary>
    /// The wire strips hit signs on purpose; the journal must not — a play
    /// with a hit (negative destination) and a bear-off (0) survives intact,
    /// in move order.
    /// </summary>
    [Fact]
    public void PlayEntry_RoundTrips_HitEncodingIntact()
    {
        var play = new Play();
        play.Add(new Move(8, -5));
        play.Add(new Move(6, 0));
        var entry = new PlayTranscriptEntry(Snapshot(), MatchSeat.One, Die1: 3, Die2: 1, play);

        var back = Assert.IsType<PlayTranscriptEntry>(RoundTrip(entry));

        Assert.Equal(MatchSeat.One, back.OnRollSeat);
        Assert.Equal(3, back.Die1);
        Assert.Equal(1, back.Die2);
        Assert.Equal(2, back.ChosenPlay.Count);
        Assert.Equal(new Move(8, -5), back.ChosenPlay[0]);
        Assert.Equal(new Move(6, 0), back.ChosenPlay[1]);
        AssertSnapshotEqual(entry.State, back.State);
    }

    [Fact]
    public void PlayEntry_Dance_RoundTripsEmpty()
    {
        var entry = new PlayTranscriptEntry(
            Snapshot(), MatchSeat.Two, Die1: 6, Die2: 6, ChosenPlay: default);

        var back = Assert.IsType<PlayTranscriptEntry>(RoundTrip(entry));

        Assert.Equal(MatchSeat.Two, back.OnRollSeat);
        Assert.Equal(0, back.ChosenPlay.Count);
    }

    /// <summary>
    /// Both cube sides round-trip, and the derived attribution rule
    /// (<see cref="CubeTranscriptEntry.ActingSeat"/>) yields the same seat —
    /// the rule lives on the type, never in the format.
    /// </summary>
    [Theory]
    [InlineData(CubeAction.NoDouble)]
    [InlineData(CubeAction.Double)]
    [InlineData(CubeAction.Take)]
    [InlineData(CubeAction.Pass)]
    public void CubeEntry_RoundTrips_EveryAction(CubeAction action)
    {
        var entry = new CubeTranscriptEntry(Snapshot(), MatchSeat.Two, action);

        var back = Assert.IsType<CubeTranscriptEntry>(RoundTrip(entry));

        Assert.Equal(MatchSeat.Two, back.OnRollSeat);
        Assert.Equal(action, back.Action);
        Assert.Equal(entry.ActingSeat, back.ActingSeat);
        AssertSnapshotEqual(entry.State, back.State);
    }

    /// <summary>
    /// The terminating entry round-trips with its perspective-relative result,
    /// and the derived winner resolves to the same seat.
    /// </summary>
    [Theory]
    [InlineData(GameResultKind.WinSingle, true)]
    [InlineData(GameResultKind.WinGammon, false)]
    [InlineData(GameResultKind.WinBackgammon, true)]
    public void GameEndedEntry_RoundTrips_WinnerDerivationIntact(GameResultKind kind, bool onRollWon)
    {
        var entry = new GameEndedTranscriptEntry(
            Snapshot(SubstrateCubeOwner.OnRoll, cubeSize: 2), MatchSeat.Two,
            new GameResult(kind, onRollWon, CubeSize: 2));

        var back = Assert.IsType<GameEndedTranscriptEntry>(RoundTrip(entry));

        Assert.Equal(entry.Result, back.Result);
        Assert.Equal(entry.Winner, back.Winner);
        AssertSnapshotEqual(entry.State, back.State);
    }

    /// <summary>
    /// Frame preservation: a seat-Two-frame snapshot comes back in seat Two's
    /// frame, board untouched — no flip ever happens in the mapping.
    /// </summary>
    [Fact]
    public void SeatTwoFrame_IsPreserved_NoFlip()
    {
        var entry = new PlayTranscriptEntry(Snapshot(), MatchSeat.Two, Die1: 4, Die2: 2, ChosenPlay: default);

        var back = Assert.IsType<PlayTranscriptEntry>(RoundTrip(entry));

        Assert.Equal(MatchSeat.Two, back.OnRollSeat);
        Assert.Equal(entry.State.Board, back.State.Board);
    }

    [Fact]
    public void GameStartContext_RoundTrips()
    {
        var context = new GameStartContext(GameNumber: 4, SeatOneScore: 3, SeatTwoScore: 2, IsCrawford: true);

        string line = JournalCodec.Serialize(JournalMapping.ToEvent(context, At));
        var back = JournalMapping.ToGameStartContext(
            Assert.IsType<MatchGameStartedEvent>(JournalCodec.DeserializeMatchEvent(line)));

        Assert.Equal(context, back);
    }

    /// <summary>
    /// The header maps the record's identity and configuration — including the
    /// escrowed fair-dice key (hex) and the derived algorithm id — and the
    /// time control folds back to the validated Api value.
    /// </summary>
    [Fact]
    public void CreatedEvent_MapsRecordConfiguration()
    {
        var key = DiceKey.Generate();
        var record = new MatchRecord
        {
            MatchId = "match-1",
            EngineOne = "Alpha",
            EngineTwo = "Beta",
            MatchLength = 7,
            MaxGames = 50,
            Seed = 42,
            DiceKey = key,
            TimeControl = new BgTournament.Api.TimeControl(120, 8),
            Sequence = 1,
            StartedAtUtc = At,
            Live = new LiveMatch("match-1", NullLogger.Instance),
        };

        string line = JournalCodec.Serialize(JournalMapping.ToCreatedEvent(record, At));
        var header = Assert.IsType<MatchCreatedEvent>(JournalCodec.DeserializeMatchEvent(line));

        Assert.Equal(JournalCodec.SchemaVersion, header.SchemaVersion);
        Assert.Equal("match-1", header.MatchId);
        Assert.Equal("Alpha", header.EngineOne);
        Assert.Equal("Beta", header.EngineTwo);
        Assert.Equal(7, header.MatchLength);
        Assert.Equal(50, header.MaxGames);
        Assert.Equal(42, header.Seed);
        Assert.Equal(BgTournament.Protocol.VerifiableDice.AlgorithmId, header.DiceAlgorithm);
        Assert.Equal(key, DiceKey.FromHex(header.DiceKey!));

        var timeControl = JournalMapping.ToTimeControl(header.TimeControl);
        Assert.NotNull(timeControl);
        Assert.Equal(120, timeControl!.InitialSeconds);
        Assert.Equal(8, timeControl.IncrementSeconds);
    }

    /// <summary>A structured forfeit terminal maps the whole taxonomy down and back.</summary>
    [Fact]
    public void TerminalEvent_MapsForfeitTaxonomy()
    {
        var record = new MatchRecord
        {
            MatchId = "match-1",
            EngineOne = "Alpha",
            EngineTwo = "Beta",
            MatchLength = 3,
            Seed = 1,
            Sequence = 1,
            StartedAtUtc = At,
            Live = new LiveMatch("match-1", NullLogger.Instance),
        };
        record.RecordForfeit(seat: 1, ForfeitCause.FlagFall, "Engine Alpha ran out of time.");

        string line = JournalCodec.Serialize(JournalMapping.ToTerminalEvent(record, At));
        var terminal = Assert.IsType<MatchTerminalEvent>(JournalCodec.DeserializeMatchEvent(line));

        Assert.Equal(JournalMatchOutcome.Forfeited, terminal.Status);
        Assert.Equal("Beta", terminal.Winner);
        Assert.Equal("Alpha", terminal.ForfeitedBy);
        Assert.Equal(JournalForfeitCause.FlagFall, terminal.ForfeitCause);
        Assert.Equal("Engine Alpha ran out of time.", terminal.Detail);
        Assert.Equal(ForfeitCause.FlagFall, JournalMapping.ToForfeitCause(terminal.ForfeitCause!.Value));
    }
}
