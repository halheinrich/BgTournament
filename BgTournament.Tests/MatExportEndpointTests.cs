using System.Net;
using System.Net.Http.Json;
using System.Text;
using BgTournament.Api;
using BgTournament.Protocol;
using BgTournament.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BgTournament.Tests;

/// <summary>
/// The <c>.MAT</c> export endpoint (<c>GET /matches/{matchId}/export.mat</c>)
/// end to end over the real in-proc server: it serves a terminal match as
/// Jellyfish <c>.MAT</c> text, refuses a running one at the same 409 the replay
/// endpoint uses, and picks the right exporter factory for each terminal status.
/// The completed-match happy path is pinned byte-for-byte against a golden — it
/// guards the server's assembly of names, tags, and games, not the exporter's
/// internals (those have their own goldens in BgMatchFormat_Lib).
/// </summary>
public class MatExportEndpointTests
{
    [Fact]
    public async Task ExportEndpoint_UnknownMatch_Is404()
    {
        using var factory = ServerHarness.NewFactory();
        using var http = factory.CreateClient();

        var response = await http.GetAsync("/matches/no-such-match/export.mat");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExportEndpoint_RunningMatch_Is409_PointingAtLive()
    {
        using var factory = ServerHarness.NewFactory();
        await using var one = await TestEngine.ConnectAsync(factory, "Thinker");
        await using var two = await TestEngine.ConnectAsync(factory, "Waiter");

        // Neither engine ever answers, so the match stays running.
        var started = await ServerHarness.StartMatchAsync(factory, "Thinker", "Waiter", matchLength: 1, seed: 1);
        string matchId = started.GetProperty("matchId").GetString()!;
        Assert.Equal("running", started.GetProperty("status").GetString());

        using var http = factory.CreateClient();
        var response = await http.GetAsync($"/matches/{matchId}/export.mat");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("still running", error!.Error);
        Assert.Contains($"/matches/{matchId}/live", error.Error);
    }

    [Fact]
    public async Task ExportEndpoint_CompletedMatch_ByteEqualsGolden_ServedAsLfOnlyDownload()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, teardown.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, teardown.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 1, seed: 5);
        string matchId = started.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(
            factory, matchId, deadlineOverride: TimeSpan.FromSeconds(30));
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        using var http = factory.CreateClient();
        var response = await http.GetAsync($"/matches/{matchId}/export.mat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Served as a text download: text/plain, attachment, match-scoped filename.
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Contains($"match_{matchId}.mat", $"{disposition.FileName} {disposition.FileNameStar}");

        // Byte-exact against the golden, with the match id (random per run) woven
        // back in. Equality of these byte arrays also proves the served text is
        // LF-only: the golden carries no CR, so a CR in the response would fail.
        byte[] actual = await response.Content.ReadAsByteArrayAsync();
        byte[] expected = Encoding.UTF8.GetBytes(
            GoldenText("export_completed_match.mat").Replace("__MATCH_ID__", matchId));
        Assert.Equal(expected, actual);

        teardown.Cancel();
    }

    [Fact]
    public async Task ExportEndpoint_MoneySession_ZeroPointMatch_NoMatchFinalLine()
    {
        using var factory = ServerHarness.NewFactory();
        using var teardown = new CancellationTokenSource();
        await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, teardown.Token);
        await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, teardown.Token);
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        var started = await ServerHarness.StartMatchAsync(
            factory, "Alpha", "Beta", matchLength: 0, seed: 7, maxGames: 2);
        string matchId = started.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(
            factory, matchId, deadlineOverride: TimeSpan.FromSeconds(30));
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        using var http = factory.CreateClient();
        string mat = await http.GetStringAsync($"/matches/{matchId}/export.mat");

        // A money session: the 0-point header, both games' results, the Match ID
        // tag — and never a match-final "and the match" line.
        Assert.Contains("0 point match", mat);
        Assert.Contains($"; [Match ID \"{matchId}\"]", mat);
        Assert.Contains(" Game 1", mat);
        Assert.Contains(" Game 2", mat);
        Assert.DoesNotContain("and the match", mat);

        teardown.Cancel();
    }

    [Fact]
    public async Task ExportEndpoint_Forfeit_CompletedGamesPlusForfeitComment_NoResultOnPartial()
    {
        using var factory = ServerHarness.NewFactory();
        var (matchId, record) = await DriveToForfeitWithPartialAsync(factory);

        using var http = factory.CreateClient();
        string mat = await http.GetStringAsync($"/matches/{matchId}/export.mat");

        // The winner-by-forfeit comment names the non-forfeiting engine, and it
        // is the same name the admin summary attributes the win to.
        Assert.Contains($"; {record.Winner} wins by forfeit", mat);

        // Completed game 1 exports with its result; the in-flight game 2 exports
        // its moves with no result line — so "Wins" appears once per completed
        // game and never for the partial. No match-final line on a forfeit.
        Assert.Contains(" Game 1", mat);
        Assert.Contains(" Game 2", mat);
        Assert.Equal(record.Live.CompletedGames.Count, CountOccurrences(mat, "Wins"));
        Assert.DoesNotContain("and the match", mat);
    }

    [Fact]
    public async Task ExportEndpoint_Aborted_AbandonedWithServerShutdownReason()
    {
        using var factory = ServerHarness.NewFactory();
        var (matchId, record) = await DriveToForfeitWithPartialAsync(factory);

        // Aborted (server shutdown) is not deterministically inducible through the
        // runner in-test; the retained games and partial are identical to the
        // forfeit fixture, so we assert the status → factory mapping directly by
        // re-tagging the terminal record (the setter the runner itself uses).
        record.Status = MatchStatus.Aborted;

        using var http = factory.CreateClient();
        string mat = await http.GetStringAsync($"/matches/{matchId}/export.mat");

        Assert.Contains("; Match aborted: server shutdown", mat);
        Assert.DoesNotContain("wins by forfeit", mat);
        Assert.DoesNotContain("and the match", mat);
        Assert.Contains(" Game 1", mat);
        Assert.Contains(" Game 2", mat);
        Assert.Equal(record.Live.CompletedGames.Count, CountOccurrences(mat, "Wins"));
    }

    [Fact]
    public async Task ExportEndpoint_Faulted_AbandonedWithInternalErrorReason()
    {
        using var factory = ServerHarness.NewFactory();
        var (matchId, record) = await DriveToForfeitWithPartialAsync(factory);

        // Faulted (an unexpected server error) is likewise not inducible in-test;
        // re-tag the terminal record to exercise the abandoned mapping and reason.
        // If a Faulted path ever becomes deterministically reachable through the
        // real runner (e.g. a non-contract agent exception), drive it here instead
        // of re-tagging — a nice-to-have, not a gap.
        record.Status = MatchStatus.Faulted;

        using var http = factory.CreateClient();
        string mat = await http.GetStringAsync($"/matches/{matchId}/export.mat");

        Assert.Contains("; Match faulted: internal server error", mat);
        Assert.DoesNotContain("wins by forfeit", mat);
        Assert.DoesNotContain("and the match", mat);
        Assert.Equal(record.Live.CompletedGames.Count, CountOccurrences(mat, "Wins"));
    }

    /// <summary>
    /// Drive Alpha vs Beta to a state with exactly one completed game and a
    /// non-empty in-flight game, then forfeit Beta mid-game-two. Timing rides the
    /// in-proc record: we wait until game one is retained <em>and</em> game two
    /// has recorded at least one entry (a non-null <see cref="LiveMatch.PartialTranscript"/>)
    /// before disconnecting Beta — so the partial the export carries is real.
    /// </summary>
    private static async Task<(string MatchId, MatchRecord Record)> DriveToForfeitWithPartialAsync(
        WebApplicationFactory<Program> factory)
    {
        await using var alpha = await TestEngine.ConnectAsync(factory, "Alpha");
        await using var beta = await TestEngine.ConnectAsync(factory, "Beta");
        await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

        // Length 5 so game one (cube declined, ≤ 3 points) never ends the match.
        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 5, seed: 5);
        string matchId = started.GetProperty("matchId").GetString()!;

        var service = factory.Services.GetRequiredService<MatchService>();
        Assert.True(service.TryGetRecord(matchId, out var record));

        using var betaAbort = new CancellationTokenSource();
        var driveAlpha = AutoPlayAsync(alpha, CancellationToken.None);
        var driveBeta = AutoPlayAsync(beta, betaAbort.Token);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (record.Live.CompletedGames.Count < 1 || record.Live.PartialTranscript is null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, deadline.Token);
        }

        betaAbort.Cancel();
        var summary = await ServerHarness.WaitForMatchEndAsync(
            factory, matchId, deadlineOverride: TimeSpan.FromSeconds(30));
        Assert.Equal("forfeited", summary.GetProperty("status").GetString());
        await Task.WhenAll(driveAlpha, driveBeta);

        return (matchId, record);
    }

    /// <summary>
    /// Auto-play a raw-wire engine to match end: first legal play, decline cubes,
    /// take doubles. If <paramref name="abort"/> fires, disconnect on the next
    /// query — the forfeit lever, timed by the caller.
    /// </summary>
    private static async Task AutoPlayAsync(TestEngine engine, CancellationToken abort)
    {
        try
        {
            while (await engine.ReceiveAsync() is { } message)
            {
                if (abort.IsCancellationRequested)
                {
                    engine.Abort();
                    return;
                }

                switch (message)
                {
                    case PlayQueryMessage play:
                        await engine.ReplyWithFirstLegalPlayAsync(play);
                        break;
                    case CubeOfferQueryMessage offer:
                        await engine.SendAsync(new CubeOfferReplyMessage
                        {
                            RequestId = offer.RequestId,
                            Action = CubeOfferAction.NoDouble,
                        });
                        break;
                    case CubeResponseQueryMessage responseQuery:
                        await engine.SendAsync(new CubeResponseReplyMessage
                        {
                            RequestId = responseQuery.RequestId,
                            Action = BgTournament.Protocol.CubeResponseAction.Take,
                        });
                        break;
                    case MatchEndedMessage:
                        return;
                }
            }
        }
        catch (Exception) when (abort.IsCancellationRequested)
        {
            engine.Abort();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Read a committed golden, defensively normalizing to LF: the file is stored
    /// LF (see <c>.gitattributes</c>), but normalizing keeps the test honest on a
    /// checkout that rewrote line endings, without masking a CR the server emits.
    /// </summary>
    private static string GoldenText(string name)
    {
        string path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Goldens", name));
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }
}
