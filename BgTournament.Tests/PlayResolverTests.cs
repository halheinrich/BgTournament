using BgDataTypes_Lib;
using BgTournament.Protocol;
using BgTournament.Server;

namespace BgTournament.Tests;

/// <summary>
/// Pins the canonicalization step that makes hit-less wire plays safe: the
/// resolver must return the generator's hit-encoded candidate — never the
/// probe — because an unresolved play would validate (hit-insensitive keys)
/// yet apply without sending the blot to the bar.
/// </summary>
public class PlayResolverTests
{
    /// <summary>Your 24-point anchor can hit an opponent blot on 18 with a 6.</summary>
    private static BoardState HittableBoard() => BoardState.FromMop(new[]
    {
        0, 0, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -2, -1, -5, 0, 0, 0, 0, 2, 0,
    });

    /// <summary>On the bar behind a closed board (dice 3-1 cannot enter).</summary>
    private static BoardState ClosedOutBoard() => BoardState.FromMop(new[]
    {
        0, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -3, 0, 0, 0, 0, 0, 0, -2, -2, -2, -2, -2, -2, 1,
    });

    [Fact]
    public void Resolve_SignFreeHitPlay_ReturnsTheHitEncodedCandidate()
    {
        var wireMoves = new[]
        {
            new WireMove { From = 24, To = 18 },
            new WireMove { From = 13, To = 10 },
        };

        var resolved = PlayResolver.Resolve(HittableBoard(), 6, 3, wireMoves.ToUnresolvedPlay());

        Assert.NotNull(resolved);
        var moves = Enumerable.Range(0, resolved.Value.Count).Select(i => resolved.Value[i]).ToList();
        Assert.Contains(new Move(24, -18), moves); // the hit, restored
        Assert.Contains(new Move(13, 10), moves);
    }

    [Fact]
    public void Resolve_IllegalPlay_ReturnsNull()
    {
        // No checker of yours on 3: never legal, whatever the dice.
        var wireMoves = new[] { new WireMove { From = 3, To = 2 } };

        Assert.Null(PlayResolver.Resolve(HittableBoard(), 6, 3, wireMoves.ToUnresolvedPlay()));
    }

    [Fact]
    public void Resolve_EmptyWirePlay_MatchesTheEmptyDanceCandidate()
    {
        // The dance over the wire: moves [] resolves against the substrate's
        // single empty candidate with no special case.
        var resolved = PlayResolver.Resolve(
            ClosedOutBoard(), 3, 1, Array.Empty<WireMove>().ToUnresolvedPlay());

        Assert.NotNull(resolved);
        Assert.Equal(0, resolved.Value.Count);
    }
}
