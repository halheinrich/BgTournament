using BgDataTypes_Lib;
using BgGame_Lib;
using BgMoveGen;
using BgTournament.EngineClient;

namespace BgTournament.Tests;

/// <summary>
/// Pins the reference bot's contract: RandomPlayAgent always answers with a
/// legal play (or the empty play when none exists) and is deterministic under
/// a seed; PassiveCubeAgent never doubles and always takes.
/// </summary>
public class ReferenceBotTests
{
    private static int[] OpeningBoard() => new[]
    {
        0, -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2, 0,
    };

    /// <summary>On the bar behind a closed board: die 3 enters on 22, die 1 on 24 — both blocked.</summary>
    private static int[] ClosedOutBoard() => new[]
    {
        0, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -3, 0, 0, 0, 0, 0, 0, -2, -2, -2, -2, -2, -2, 1,
    };

    private static GameSnapshot StateOver(int[] board) => GameState.FromPosition(
        MatchState.NewMatch(7), BoardState.FromMop(board), cubeSize: 1, CubeOwner.Centered).Snapshot();

    public static TheoryData<int, int> AllDicePairs()
    {
        var data = new TheoryData<int, int>();
        for (int die1 = 1; die1 <= 6; die1++)
        {
            for (int die2 = die1; die2 <= 6; die2++)
            {
                data.Add(die1, die2);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllDicePairs))]
    public async Task RandomPlayAgent_AlwaysChoosesALegalPlay(int die1, int die2)
    {
        var agent = new RandomPlayAgent(seed: 42);
        var state = StateOver(OpeningBoard());

        var play = await agent.ChoosePlayAsync(state, die1, die2);

        var board = new BoardState(state.Board);
        var candidates = MoveGenerator.GeneratePlays(board, die1, die2);
        Assert.True(board.IndexOfSamePlay(play, candidates) >= 0, "the agent's play is a legal play");
    }

    [Fact]
    public async Task RandomPlayAgent_SameSeed_SameChoices()
    {
        var first = new RandomPlayAgent(seed: 7);
        var second = new RandomPlayAgent(seed: 7);

        for (int die1 = 1; die1 <= 6; die1++)
        {
            for (int die2 = 1; die2 <= 6; die2++)
            {
                var state = StateOver(OpeningBoard());
                var firstPlay = await first.ChoosePlayAsync(state, die1, die2);
                var secondPlay = await second.ChoosePlayAsync(state, die1, die2);
                Assert.True(firstPlay.IsSameEncoding(secondPlay), "the same seed chooses the same play");
            }
        }
    }

    [Fact]
    public async Task RandomPlayAgent_NoLegalPlay_ReturnsTheEmptyPlay()
    {
        var agent = new RandomPlayAgent(seed: 42);
        var state = StateOver(ClosedOutBoard());

        // On the bar; entry points 22 (die 3) and 24 (die 1) are both closed.
        // The substrate expresses "no legal move" as a single empty candidate.
        var candidates = MoveGenerator.GeneratePlays(new BoardState(state.Board), 3, 1);
        var candidate = Assert.Single(candidates);
        Assert.Equal(0, candidate.Count);

        var play = await agent.ChoosePlayAsync(state, 3, 1);

        Assert.Equal(0, play.Count);
    }

    [Fact]
    public async Task RandomPlayAgent_PreCanceledToken_Throws()
    {
        var agent = new RandomPlayAgent(seed: 42);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await agent.ChoosePlayAsync(StateOver(OpeningBoard()), 3, 1, canceled));
    }

    [Fact]
    public async Task PassiveCubeAgent_NeverDoubles()
    {
        var agent = new PassiveCubeAgent();

        Assert.Equal(CubeAction.NoDouble, await agent.ChooseOfferAsync(StateOver(OpeningBoard())));
    }

    [Fact]
    public async Task PassiveCubeAgent_AlwaysTakes()
    {
        var agent = new PassiveCubeAgent();

        Assert.Equal(CubeAction.Take, await agent.ChooseResponseAsync(StateOver(OpeningBoard())));
    }
}
