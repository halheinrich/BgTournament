using BgDataTypes_Lib;
using BgTournament.Protocol;
using BgTournament.Server;

namespace BgTournament.Tests;

/// <summary>
/// Pins the wire-resolution projection that makes hit-less wire plays safe:
/// the resolver matches by the wire's own encoding — the unordered multiset
/// of hit-stripped single-die hops — and returns the generator's hit-encoded
/// candidate, never the probe. Hit-insensitive because the wire encodes no
/// hits; route-sensitive because the hops the engine named are the only
/// disambiguator between candidates that differ solely in their hits.
/// </summary>
public class PlayResolverTests
{
    /// <summary>Your 24-point anchor can hit an opponent blot on 18 with a 6.</summary>
    private static BoardState HittableBoard() => BoardState.FromMop(new[]
    {
        0, 0, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -2, -1, -5, 0, 0, 0, 0, 2, 0,
    });

    /// <summary>
    /// Your lone checker on 13 reaches 8 with a 3-2 by two routes: via 10
    /// (hitting the opponent blot there) or via 11 (clean). Two legal
    /// candidates identical up to hits — the resolver's hardest case.
    /// </summary>
    private static BoardState TwoRouteBoard() => BoardState.FromMop(new[]
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, -2, -2, 0, 0, 0, -2, 0,
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
    public void Resolve_RouteThroughTheBlot_ReturnsTheHittingCandidate()
    {
        // The engine names the via-10 route, so the via-10 candidate — the
        // one that hits — is the only correct resolution. A projection that
        // canonically chain-merged the hit-stripped routes would collapse
        // both candidates to 13/8 and could resolve the clean one instead.
        var wireMoves = new[]
        {
            new WireMove { From = 13, To = 10 },
            new WireMove { From = 10, To = 8 },
        };

        var resolved = PlayResolver.Resolve(TwoRouteBoard(), 3, 2, wireMoves.ToUnresolvedPlay());

        Assert.NotNull(resolved);
        var moves = Enumerable.Range(0, resolved.Value.Count).Select(i => resolved.Value[i]).ToList();
        Assert.Contains(new Move(13, -10), moves); // the hit at 10, restored
        Assert.Contains(new Move(10, 8), moves);
    }

    [Fact]
    public void Resolve_RouteAroundTheBlot_ReturnsTheCleanCandidate()
    {
        var wireMoves = new[]
        {
            new WireMove { From = 13, To = 11 },
            new WireMove { From = 11, To = 8 },
        };

        var resolved = PlayResolver.Resolve(TwoRouteBoard(), 3, 2, wireMoves.ToUnresolvedPlay());

        Assert.NotNull(resolved);
        var moves = Enumerable.Range(0, resolved.Value.Count).Select(i => resolved.Value[i]).ToList();
        Assert.Contains(new Move(13, 11), moves);
        Assert.Contains(new Move(11, 8), moves);
        Assert.All(moves, m => Assert.True(m.ToPt > 0, "the via-11 route hits nothing"));
    }

    [Fact]
    public void Resolve_WireMoveOrder_DoesNotMatter()
    {
        // PROTOCOL.md §6: "Order does not matter." The same route named
        // back-to-front resolves identically.
        var wireMoves = new[]
        {
            new WireMove { From = 10, To = 8 },
            new WireMove { From = 13, To = 10 },
        };

        var resolved = PlayResolver.Resolve(TwoRouteBoard(), 3, 2, wireMoves.ToUnresolvedPlay());

        Assert.NotNull(resolved);
        var moves = Enumerable.Range(0, resolved.Value.Count).Select(i => resolved.Value[i]).ToList();
        Assert.Contains(new Move(13, -10), moves);
    }

    [Fact]
    public void Resolve_MergedHopEncoding_DoesNotResolve()
    {
        // PROTOCOL.md §6: moving one checker twice is expressed as two moves.
        // A merged 13→8 names no route — and the route is the only hit
        // disambiguator — so resolution is deliberately hop-level strict.
        // (Substrate Play equality would have accepted this as the clean
        // 13/8; the runner remains the legality authority for the fallback.)
        var wireMoves = new[] { new WireMove { From = 13, To = 8 } };

        Assert.Null(PlayResolver.Resolve(TwoRouteBoard(), 3, 2, wireMoves.ToUnresolvedPlay()));
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
