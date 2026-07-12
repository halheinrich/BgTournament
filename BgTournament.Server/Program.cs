using System.Runtime.CompilerServices;
using System.Text;
using BgTournament.Api;
using BgTournament.Server;
using BgTournament.Server.Persistence;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TournamentOptions>(builder.Configuration.GetSection("Tournament"));
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("Persistence"));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

// The admin surface's identity: named API keys, validated once at startup —
// a broken key configuration (blank name/key, shared key value) fails the
// boot loudly rather than serving with an ambiguous or silent identity story.
builder.Services.AddSingleton<AdminApiKeys>();

// The one timestamp source for clock logic and journal timestamps (never
// ambient DateTime/Stopwatch), so both are deterministic under test.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EngineRegistry>();
builder.Services.AddSingleton<IJournalStore, FileJournalStore>();
builder.Services.AddSingleton<MatchService>();
builder.Services.AddSingleton<TournamentService>();

// The engine roster (registration/originality): folded from its journal
// segments at startup, mutated only through the admin endpoints below. The
// container disposes it at shutdown, closing this session's segment.
builder.Services.AddSingleton<RosterService>();
builder.Services.AddSingleton<JournalRehydrator>();

// The server-session journal (engine lifecycle evidence): one singleton, also
// hosted so its segment opens before Kestrel accepts and closes gracefully on
// shutdown — a segment without a stopped marker is crash evidence.
builder.Services.AddSingleton<ServerJournal>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerJournal>());

var app = builder.Build();

// Fold the durable journals back into records before any endpoint serves:
// every match and tournament the server ever hosted is queryable from the
// first request, and an orphaned Running journal (the server died under it)
// folds to an Interrupted terminal record with all evidence intact.
await app.Services.GetRequiredService<JournalRehydrator>().RehydrateAsync();

// Boot-time policy coherence checks — warnings, not failures: both states
// are reachable and repairable while the server runs (register engines /
// configure admin keys and restart), and an enforcing server with nothing
// registered is safe (it rejects every engine), just probably not intended.
if (app.Services.GetRequiredService<IOptions<TournamentOptions>>().Value.EnginePolicy
    == EnginePolicy.Registered)
{
    if (!app.Services.GetRequiredService<RosterService>().HasActiveEngines)
    {
        app.Logger.LogWarning(
            "EnginePolicy is Registered but the roster has no active engines — every engine "
                + "connection will be rejected until one is registered (POST /roster).");
    }

    if (!app.Services.GetRequiredService<AdminApiKeys>().Enforcing)
    {
        app.Logger.LogWarning(
            "EnginePolicy is Registered but the admin surface is serving anonymously — anyone "
                + "reaching the port can register engines. Configure Admin:ApiKeys to close this.");
    }
}

app.UseWebSockets();

// The admin identity gate — one rule for the whole admin surface (every HTTP
// endpoint except the engine wire, whose gate is the hello handshake): keys
// configured ⇒ a valid X-Api-Key header is required and the resolved actor
// rides the request; none configured ⇒ anonymous, but a presented key is
// still validated so a client/server key mismatch fails loudly.
app.UseMiddleware<AdminAuthenticationMiddleware>();

// The engine wire endpoint (PROTOCOL.md).
app.Map("/engine", EngineSocketEndpoint.HandleAsync);

// The admin HTTP surface. Its shapes are the BgTournament.Api contracts —
// self-describing (string enums are pinned per member), so no serializer
// configuration is needed here beyond ASP.NET Core's Web defaults.
app.MapGet("/engines", (EngineRegistry registry) =>
    Results.Ok(registry.Snapshot().Select(ApiMapping.ToSummary)));

app.MapPost("/matches", (StartMatchRequest request, AdminActor? actor, MatchService matches) =>
{
    var (record, error, detail) = matches.StartMatch(
        request.EngineOne, request.EngineTwo, request.MatchLength, request.Seed, request.MaxGames,
        request.TimeControl, actor?.Name);
    return error switch
    {
        StartMatchError.None => Results.Ok(record!.ToSummary()),
        StartMatchError.UnknownEngine => Results.NotFound(new ErrorResponse(detail!)),
        StartMatchError.EngineBusy or StartMatchError.SameEngine => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapGet("/matches", (MatchService matches) =>
    Results.Ok(matches.ListRecords().Select(ApiMapping.ToSummary)));

app.MapGet("/matches/{matchId}", (string matchId, MatchService matches) =>
    matches.TryGetRecord(matchId, out var record)
        ? Results.Ok(record.ToSummary())
        : Results.NotFound());

// Replay: a terminal match's per-game transcripts, every position re-expressed
// in seat One's frame so viewers keep a stable orientation. A completed match
// serves its full games; a forfeited/aborted/faulted one serves the games that
// finished before the break (partial — the response's status says so).
app.MapGet("/matches/{matchId}/games", (string matchId, MatchService matches) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    // A running match has no settled games yet — the live feed is its surface.
    return record.Status == MatchStatus.Running
        ? Results.Conflict(new ErrorResponse(
            $"Match '{matchId}' is still running; watch it at /matches/{matchId}/live, "
                + "or read its games once it ends."))
        : Results.Ok(record.ToGamesResponse());
});

// Export: a terminal match rendered as Jellyfish .MAT text, served as a
// download. Same terminal-only gate as replay — a running match is refused,
// pointed at the live feed. Every terminal match exports: completed and money
// sessions carry their per-game results; a forfeited/aborted/faulted one carries
// the games that finished, the trailing in-flight game, and a winner/reason
// comment (MatExportProjection picks the factory). The exporter's bytes are
// LF-only with no trailing whitespace — served verbatim (no BOM, no re-framing).
app.MapGet("/matches/{matchId}/export.mat", IResult (string matchId, MatchService matches) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    if (record.Status == MatchStatus.Running)
    {
        return Results.Conflict(new ErrorResponse(
            $"Match '{matchId}' is still running; watch it at /matches/{matchId}/live, "
                + "or export it once it ends."));
    }

    byte[] mat = Encoding.UTF8.GetBytes(record.ToMatText());
    return Results.File(mat, "text/plain; charset=utf-8", $"match_{matchId}.mat");
});

// Audit: a terminal match's arbitration timeline, projected from its durable
// journal at request time — timestamps, per-decision clock evidence, late-
// reply discards, and the terminal outcome with its structured forfeit cause
// (no boards or moves: replay is that surface; join by game number + entry
// index). Same terminal-only gate as replay/export. The journal-settled
// await closes the drain race: a match turns terminal before its journal
// finishes flushing, and an audit read must see the settled file.
app.MapGet("/matches/{matchId}/audit", async Task<IResult> (
    string matchId, MatchService matches, IJournalStore store, ILoggerFactory loggerFactory) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    if (record.Status == MatchStatus.Running)
    {
        return Results.Conflict(new ErrorResponse(
            $"Match '{matchId}' is still running; watch it at /matches/{matchId}/live, "
                + "or audit it once it ends."));
    }

    await record.JournalSettled;
    var journal = await JournalReader.ReadTrustedEventsAsync(
        store, JournalKind.Match, matchId, JournalCodec.DeserializeMatchEvent,
        loggerFactory.CreateLogger("BgTournament.Server.AuditEndpoint"));
    if (journal is not { } trusted)
    {
        // The record exists but its journal cannot be read (it faulted at
        // creation, or the file is gone) — there is no audit record to serve,
        // and saying so beats a fabricated empty timeline.
        return Results.NotFound(new ErrorResponse(
            $"Match '{matchId}' has no readable audit journal."));
    }

    return Results.Ok(AuditProjection.ToAuditResponse(record, trusted.Events, trusted.CorruptionDetail));
});

// Live spectating: a Server-Sent Events feed of one match as it plays. Each
// subscriber gets a join-in-progress snapshot, then per-move increments, then
// a terminal event, then close. Payloads are the same replay contracts the
// settled-replay endpoint serves, projected identically.
app.MapGet("/matches/{matchId}/live", IResult (string matchId, MatchService matches, CancellationToken ct) =>
{
    if (!matches.TryGetRecord(matchId, out var record))
    {
        return Results.NotFound();
    }

    // Subscribe before the stream begins: the snapshot is captured and the
    // subscriber registered atomically, so no event falls in the gap.
    return TypedResults.ServerSentEvents(StreamLive(record.Live.Subscribe(), ct));
});

// Round-robin tournaments: server-side orchestration over the same match
// host — invisible on the wire (engines just see per-match lifecycles).
app.MapPost("/tournaments", (StartTournamentRequest request, AdminActor? actor, TournamentService tournaments) =>
{
    var (record, error, detail) = tournaments.StartTournament(
        request.Participants, request.MatchLength, request.MatchesPerPairing, request.Seed,
        request.TimeControl, actor?.Name);
    return error switch
    {
        StartTournamentError.None => Results.Ok(tournaments.Summarize(record!)),
        StartTournamentError.UnknownEngine => Results.NotFound(new ErrorResponse(detail!)),
        StartTournamentError.EngineBusy => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapGet("/tournaments", (TournamentService tournaments) =>
    Results.Ok(tournaments.ListRecords().Select(tournaments.Summarize)));

app.MapGet("/tournaments/{tournamentId}", (string tournamentId, TournamentService tournaments) =>
    tournaments.TryGetSummary(tournamentId, out var summary)
        ? Results.Ok(summary)
        : Results.NotFound());

// The engine roster (registration under the attestation posture). All of it
// rides behind the admin identity gate like every other admin endpoint;
// registration is a deliberate administrative act, never self-serve, and the
// acting admin's identity stamps each roster journal event. The register and
// rotate responses carry the issued engine key exactly once — the server
// retains only its salted hash.
app.MapGet("/roster", (RosterService roster) =>
    Results.Ok(roster.List().Select(ApiMapping.ToEntry)));

app.MapGet("/roster/{name}", (string name, RosterService roster) =>
    roster.TryGet(name, out var engine)
        ? Results.Ok(engine.ToEntry())
        : Results.NotFound());

app.MapPost("/roster", (RegisterEngineRequest request, AdminActor? actor, RosterService roster) =>
{
    var (engine, engineKey, error, detail) = roster.Register(
        request.Name, request.Attestation, actor?.Name);
    return error switch
    {
        RosterError.None => Results.Ok(new EngineKeyGrant(engine!.ToEntry(), engineKey!)),
        RosterError.AlreadyRegistered => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapPost(
    "/roster/{name}/attestation",
    (string name, EngineAttestation attestation, AdminActor? actor, RosterService roster) =>
    {
        var (engine, error, detail) = roster.DeclareAttestation(name, attestation, actor?.Name);
        return error switch
        {
            RosterError.None => Results.Ok(engine!.ToEntry()),
            RosterError.NotRegistered => Results.NotFound(new ErrorResponse(detail!)),
            RosterError.Deactivated => Results.Conflict(new ErrorResponse(detail!)),
            _ => Results.BadRequest(new ErrorResponse(detail!)),
        };
    });

app.MapPost("/roster/{name}/rotate", (string name, AdminActor? actor, RosterService roster) =>
{
    var (engine, engineKey, error, detail) = roster.RotateKey(name, actor?.Name);
    return error switch
    {
        RosterError.None => Results.Ok(new EngineKeyGrant(engine!.ToEntry(), engineKey!)),
        RosterError.NotRegistered => Results.NotFound(new ErrorResponse(detail!)),
        RosterError.Deactivated => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.MapPost("/roster/{name}/deactivate", (string name, AdminActor? actor, RosterService roster) =>
{
    var (engine, error, detail) = roster.Deactivate(name, actor?.Name);
    return error switch
    {
        RosterError.None => Results.Ok(engine!.ToEntry()),
        RosterError.NotRegistered => Results.NotFound(new ErrorResponse(detail!)),
        RosterError.Deactivated => Results.Conflict(new ErrorResponse(detail!)),
        _ => Results.BadRequest(new ErrorResponse(detail!)),
    };
});

app.Run();

// Drain one subscriber's ordered event stream to the SSE response, detaching
// it when the stream ends — whether the match went terminal (the channel
// completes) or the client disconnected (the request token cancels).
static async IAsyncEnumerable<LiveMatchEvent> StreamLive(
    LiveSubscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    try
    {
        await foreach (var liveEvent in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            yield return liveEvent;
        }
    }
    finally
    {
        subscription.Unsubscribe();
    }
}
