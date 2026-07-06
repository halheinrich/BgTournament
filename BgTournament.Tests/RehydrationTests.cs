using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BgGame_Lib;
using BgTournament.Server.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;

namespace BgTournament.Tests;

/// <summary>
/// The arc's smoke: matches and tournaments played over the real wire, the
/// host torn down, a fresh host booted over the same data directory — and the
/// records served identically from disk. Plus the damage policies: a torn
/// journal tail and a mid-file corruption each fold to an honest Interrupted
/// record with all evidence intact (including the fair-mode dice key, which
/// must keep the partial roll stream verifiable).
///
/// Deterministic and isolated: FakeTimeProvider for every timestamp, a
/// per-test temp data directory in the system temp area, explicit dice seeds
/// except where fair mode is the point.
/// </summary>
public class RehydrationTests
{
    private static readonly DateTimeOffset TestEpoch = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static string NewDataDirectory() =>
        Directory.CreateTempSubdirectory("bgtournament-rehydrate-").FullName;

    private static string JournalPath(string dataDirectory, string kind, string id) =>
        Path.Combine(dataDirectory, kind, id + ".jsonl");

    /// <summary>
    /// Wait until the journal on disk carries its terminal event as a whole
    /// line — the durable end of the record, which the admin status can
    /// slightly precede (the journal drains on a background pump).
    /// </summary>
    private static async Task WaitForJournalTerminalAsync(string journalPath)
    {
        var deadline = DateTime.UtcNow + ServerHarness.ReceiveDeadline;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(journalPath))
            {
                string text = await ReadSharedAsync(journalPath);
                if (text.EndsWith('\n')
                    && text.TrimEnd('\n').Split('\n')[^1].Contains("\"type\":\"terminal\""))
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail($"No terminal event appeared in {journalPath} within {ServerHarness.ReceiveDeadline}.");
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Play one seeded match to completion and return its id — journal durably terminal.</summary>
    private static async Task<string> PlayMatchAsync(
        WebApplicationFactory<Program> factory, string dataDirectory, int seed)
    {
        var started = await ServerHarness.StartMatchAsync(factory, "Alpha", "Beta", matchLength: 1, seed);
        string matchId = started.GetProperty("matchId").GetString()!;
        await ServerHarness.WaitForMatchEndAsync(factory, matchId);
        await WaitForJournalTerminalAsync(JournalPath(dataDirectory, "matches", matchId));
        return matchId;
    }

    [Fact]
    public async Task CompletedMatch_ServesIdenticallyAfterRestart()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            string matchId;
            string summaryBefore, gamesBefore, matBefore;
            using (var factory = ServerHarness.NewFactory(
                timeProvider: new FakeTimeProvider(TestEpoch), dataDirectory: dataDirectory))
            {
                using var clients = new CancellationTokenSource();
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
                await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

                matchId = await PlayMatchAsync(factory, dataDirectory, seed: 7);

                using var http = factory.CreateClient();
                summaryBefore = await http.GetStringAsync($"/matches/{matchId}");
                gamesBefore = await http.GetStringAsync($"/matches/{matchId}/games");
                matBefore = await http.GetStringAsync($"/matches/{matchId}/export.mat");
                clients.Cancel();
            }

            // A fresh host over the same store: the record must serve the very
            // same bytes — summary, replay, and .MAT export.
            using var restarted = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http2 = restarted.CreateClient();
            Assert.Equal(summaryBefore, await http2.GetStringAsync($"/matches/{matchId}"));
            Assert.Equal(gamesBefore, await http2.GetStringAsync($"/matches/{matchId}/games"));
            Assert.Equal(matBefore, await http2.GetStringAsync($"/matches/{matchId}/export.mat"));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_PreservesListingOrder()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            var time = new FakeTimeProvider(TestEpoch);
            string listBefore;
            using (var factory = ServerHarness.NewFactory(
                timeProvider: time, dataDirectory: dataDirectory))
            {
                using var clients = new CancellationTokenSource();
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
                await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

                await PlayMatchAsync(factory, dataDirectory, seed: 7);

                // Distinct creation stamps make the restart order exact (equal
                // stamps would fall back to the deterministic ordinal-id tiebreak).
                time.Advance(TimeSpan.FromMinutes(1));
                await PlayMatchAsync(factory, dataDirectory, seed: 8);

                using var http = factory.CreateClient();
                listBefore = await http.GetStringAsync("/matches");
                clients.Cancel();
            }

            using var restarted = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http2 = restarted.CreateClient();
            Assert.Equal(listBefore, await http2.GetStringAsync("/matches"));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The crash story, fair mode: the journal is cut mid-match with a torn
    /// final line. The rehydrated record is Interrupted (no end time, no
    /// winner), still serves its surfaces — and the escrowed dice key
    /// re-derives every journaled roll, so the partial stream stays verifiable.
    /// </summary>
    [Fact]
    public async Task FairMatch_TornJournal_FoldsToInterrupted_WithVerifiableDice()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            string matchId;
            using (var factory = ServerHarness.NewFactory(
                timeProvider: new FakeTimeProvider(TestEpoch), dataDirectory: dataDirectory))
            {
                using var clients = new CancellationTokenSource();
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
                await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

                var started = await ServerHarness.StartFairMatchAsync(factory, "Alpha", "Beta", matchLength: 1);
                matchId = started.GetProperty("matchId").GetString()!;
                await ServerHarness.WaitForMatchEndAsync(factory, matchId);
                await WaitForJournalTerminalAsync(JournalPath(dataDirectory, "matches", matchId));
                clients.Cancel();
            }

            // Simulate the crash: keep the first 8 whole lines (header, started,
            // gameStarted, five entries) and a torn partial line, no terminal.
            string path = JournalPath(dataDirectory, "matches", matchId);
            string[] lines = (await File.ReadAllLinesAsync(path)).ToArray();
            Assert.True(lines.Length >= 10, $"Expected a substantial journal; got {lines.Length} lines.");
            var kept = lines.Take(8).ToArray();
            await File.WriteAllTextAsync(
                path, string.Join("\n", kept) + "\n" + """{"type":"play","at":"20""");

            using var restarted = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http = restarted.CreateClient();

            var summary = await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}");
            Assert.Equal("interrupted", summary.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("endedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("winner").ValueKind);

            // Both terminal read surfaces still serve (no completed games yet —
            // the cut is inside game one — but the endpoints answer, not 409/500).
            var games = await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}/games");
            Assert.Equal("interrupted", games.GetProperty("status").GetString());
            var export = await http.GetAsync($"/matches/{matchId}/export.mat");
            Assert.True(export.IsSuccessStatusCode, $"export.mat: {export.StatusCode}");

            // Evidence intact: the escrowed key re-derives the journaled rolls,
            // in order, as a prefix-bounded subsequence of the committed stream.
            var header = Assert.IsType<MatchCreatedEvent>(JournalCodec.DeserializeMatchEvent(kept[0]));
            Assert.NotNull(header.DiceKey);
            var journaledRolls = kept.Skip(1)
                .Select(JournalCodec.DeserializeMatchEvent)
                .OfType<MatchPlayEvent>()
                .Select(play => (play.Die1, play.Die2))
                .ToList();
            Assert.NotEmpty(journaledRolls);

            var derived = new VerifiableDiceSource(DiceKey.FromHex(header.DiceKey!));
            int matched = 0;
            for (int i = 0; i < 500 && matched < journaledRolls.Count; i++)
            {
                if (derived.Roll() == journaledRolls[matched])
                {
                    matched++;
                }
            }

            Assert.Equal(journaledRolls.Count, matched);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Mid-file corruption (not the tail): the fold serves the trusted prefix
    /// as an Interrupted record whose detail names the corruption — even
    /// though a terminal event exists beyond the corrupt line, it is not
    /// trusted.
    /// </summary>
    [Fact]
    public async Task CorruptJournalLine_FoldsTrustedPrefix_AsInterrupted()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            string matchId;
            using (var factory = ServerHarness.NewFactory(
                timeProvider: new FakeTimeProvider(TestEpoch), dataDirectory: dataDirectory))
            {
                using var clients = new CancellationTokenSource();
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
                await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");
                matchId = await PlayMatchAsync(factory, dataDirectory, seed: 7);
                clients.Cancel();
            }

            string path = JournalPath(dataDirectory, "matches", matchId);
            string[] lines = await File.ReadAllLinesAsync(path);
            Assert.True(lines.Length >= 6, $"Expected a substantial journal; got {lines.Length} lines.");
            lines[4] = "this is not a journal event";
            await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n");

            using var restarted = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http = restarted.CreateClient();

            var summary = await http.GetFromJsonAsync<JsonElement>($"/matches/{matchId}");
            Assert.Equal("interrupted", summary.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("endedAtUtc").ValueKind);
            Assert.Contains("corrupt at line 5", summary.GetProperty("detail").GetString());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private const string RawAt = "2026-07-05T12:00:00+00:00";

    private const string RawState =
        """{"board":[0,-2,0,0,0,0,5,0,3,0,0,0,-5,5,0,0,0,-3,0,-5,0,0,0,0,2,0],"cubeSize":1,"cubeOwner":"centered","matchLength":1,"onRollScore":0,"opponentScore":0,"isCrawford":false}""";

    /// <summary>
    /// Old files fold forever: a raw schema-v1 journal (the exact byte shape
    /// the v1 server wrote — no evidence events, <c>schemaVersion: 1</c>)
    /// still rehydrates into a served record under the v2 codec.
    /// </summary>
    [Fact]
    public async Task SchemaV1Journal_StillFolds()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            await ServerHarness.WriteJournalAsync(
                dataDirectory, "matches", "v1match",
                $$"""{"type":"created","schemaVersion":1,"at":"{{RawAt}}","matchId":"v1match","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7}""",
                $$"""{"type":"started","at":"{{RawAt}}"}""",
                $$"""{"type":"gameStarted","at":"{{RawAt}}","gameNumber":1,"seatOneScore":0,"seatTwoScore":0,"isCrawford":false}""",
                $$"""{"type":"play","at":"{{RawAt}}","onRollSeat":"one","state":{{RawState}},"die1":3,"die2":1,"moves":[{"from":8,"to":5},{"from":6,"to":5}]}""",
                $$$"""{"type":"gameEnded","at":"{{{RawAt}}}","onRollSeat":"one","state":{{{RawState}}},"result":{"kind":"single","onRollWon":true,"cubeSize":1}}""",
                $$"""{"type":"terminal","at":"{{RawAt}}","status":"completed","winner":"Alpha","seatOneScore":1,"seatTwoScore":0}""");

            using var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http = factory.CreateClient();

            var summary = await http.GetFromJsonAsync<JsonElement>("/matches/v1match");
            Assert.Equal("completed", summary.GetProperty("status").GetString());
            Assert.Equal("Alpha", summary.GetProperty("winner").GetString());

            var games = await http.GetFromJsonAsync<JsonElement>("/matches/v1match/games");
            Assert.Equal(1, games.GetProperty("games").GetArrayLength());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The v2 evidence events (clock settlements, late-reply discards) are
    /// audit material, not record state: a journal carrying them folds to the
    /// same record it would without them.
    /// </summary>
    [Fact]
    public async Task EvidenceEvents_AreIgnoredByTheFold()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            await ServerHarness.WriteJournalAsync(
                dataDirectory, "matches", "v2match",
                $$"""{"type":"created","schemaVersion":2,"at":"{{RawAt}}","matchId":"v2match","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7}""",
                $$"""{"type":"started","at":"{{RawAt}}"}""",
                $$"""{"type":"gameStarted","at":"{{RawAt}}","gameNumber":1,"seatOneScore":0,"seatTwoScore":0,"isCrawford":false}""",
                $$"""{"type":"clock","at":"{{RawAt}}","seat":"one","decision":"play","thinkSeconds":2.5,"incrementCredited":true,"remainingBeforeSeconds":60,"remainingAfterSeconds":62.5}""",
                $$"""{"type":"play","at":"{{RawAt}}","onRollSeat":"one","state":{{RawState}},"die1":3,"die2":1,"moves":[{"from":8,"to":5},{"from":6,"to":5}]}""",
                $$"""{"type":"lateReply","at":"{{RawAt}}","seat":"two","requestId":"q-4"}""",
                $$$"""{"type":"gameEnded","at":"{{{RawAt}}}","onRollSeat":"one","state":{{{RawState}}},"result":{"kind":"single","onRollWon":true,"cubeSize":1}}""",
                $$"""{"type":"terminal","at":"{{RawAt}}","status":"completed","winner":"Alpha","seatOneScore":1,"seatTwoScore":0}""");

            using var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http = factory.CreateClient();

            var summary = await http.GetFromJsonAsync<JsonElement>("/matches/v2match");
            Assert.Equal("completed", summary.GetProperty("status").GetString());
            Assert.Equal("Alpha", summary.GetProperty("winner").GetString());
            Assert.Equal(1, summary.GetProperty("seatOneScore").GetInt32());

            // The replay fold is untouched by the evidence lines: one game,
            // whose single entry is the play (evidence events are not entries).
            var games = await http.GetFromJsonAsync<JsonElement>("/matches/v2match/games");
            var game = Assert.Single(games.GetProperty("games").EnumerateArray().ToArray());
            Assert.Equal(1, game.GetProperty("entries").GetArrayLength());
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>An unknown (future) schema version skips the whole file, loudly — no partial trust.</summary>
    [Fact]
    public async Task UnknownSchemaVersion_SkipsTheFile()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            await ServerHarness.WriteJournalAsync(
                dataDirectory, "matches", "v9match",
                $$"""{"type":"created","schemaVersion":9,"at":"{{RawAt}}","matchId":"v9match","engineOne":"Alpha","engineTwo":"Beta","matchLength":1,"seed":7}""",
                $$"""{"type":"terminal","at":"{{RawAt}}","status":"completed","winner":"Alpha","seatOneScore":1,"seatTwoScore":0}""");

            using var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http = factory.CreateClient();
            var response = await http.GetAsync("/matches/v9match");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedTournament_ServesIdenticallyAfterRestart()
    {
        string dataDirectory = NewDataDirectory();
        try
        {
            string tournamentId;
            string tournamentBefore;
            using (var factory = ServerHarness.NewFactory(
                timeProvider: new FakeTimeProvider(TestEpoch), dataDirectory: dataDirectory))
            {
                using var clients = new CancellationTokenSource();
                await ServerHarness.RunWellBehavedClientAsync(factory, "Alpha", seed: 11, clients.Token);
                await ServerHarness.RunWellBehavedClientAsync(factory, "Beta", seed: 22, clients.Token);
                await ServerHarness.WaitForEnginesAsync(factory, "Alpha", "Beta");

                var started = await ServerHarness.StartTournamentAsync(
                    factory, ["Alpha", "Beta"], matchLength: 1, matchesPerPairing: 2, seed: 5);
                tournamentId = started.GetProperty("tournamentId").GetString()!;
                await ServerHarness.WaitForTournamentEndAsync(factory, tournamentId);
                await WaitForJournalTerminalAsync(
                    JournalPath(dataDirectory, "tournaments", tournamentId));

                using var http = factory.CreateClient();
                tournamentBefore = await http.GetStringAsync($"/tournaments/{tournamentId}");
                clients.Cancel();
            }

            // The tournament summary — standings, winner, ledger, timestamps —
            // serves identically. (The /matches listing is not string-compared
            // here: the tournament's matches share one fake-time stamp, so the
            // restart order legitimately falls to the ordinal-id tiebreak.)
            using var restarted = ServerHarness.NewFactory(dataDirectory: dataDirectory);
            using var http2 = restarted.CreateClient();
            Assert.Equal(tournamentBefore, await http2.GetStringAsync($"/tournaments/{tournamentId}"));

            // Every ledger match id resolves on the match surface.
            var summary = await http2.GetFromJsonAsync<JsonElement>($"/tournaments/{tournamentId}");
            foreach (var entry in summary.GetProperty("matches").EnumerateArray())
            {
                string matchId = entry.GetProperty("matchId").GetString()!;
                var match = await http2.GetAsync($"/matches/{matchId}");
                Assert.True(match.IsSuccessStatusCode, $"ledger match {matchId}: {match.StatusCode}");
            }
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
