using BgTournament.Api;
using BgTournament.Server.Persistence;

namespace BgTournament.Server;

/// <summary>
/// The admin surface's identity gate — one rule for the whole surface: every
/// HTTP endpoint except the engine wire (<c>/engine</c>, whose own gate is the
/// hello handshake) passes through here. When keys are configured, a request
/// must present a valid one in the <c>X-Api-Key</c> header; the resolved
/// <see cref="AdminActor"/> rides the request as a feature so handlers can
/// stamp the durable record. When none are configured the surface serves
/// anonymously — but a presented key is <em>always</em> validated, so a
/// client configured with a key against a server that lost its own fails
/// loudly (401) instead of silently working unattributed.
///
/// <para>Every refusal is named twice with the same reason — the
/// <see cref="ErrorResponse"/> body the caller sees and the server journal's
/// <c>adminRejected</c> evidence beside the wire's handshake rejections (the
/// one-funnel principle). The presented key value appears in neither: a
/// mistyped real secret must not land in a durable file.</para>
/// </summary>
internal sealed class AdminAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AdminApiKeys _keys;
    private readonly ServerJournal _serverJournal;

    public AdminAuthenticationMiddleware(
        RequestDelegate next, AdminApiKeys keys, ServerJournal serverJournal)
    {
        _next = next;
        _keys = keys;
        _serverJournal = serverJournal;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The engine wire is not the admin surface: its identity story is the
        // hello handshake (and session 3's engineKey), never an HTTP header.
        if (context.Request.Path.StartsWithSegments("/engine"))
        {
            await _next(context);
            return;
        }

        var presented = context.Request.Headers[AdminApiKey.HeaderName];
        if (presented.Count == 0)
        {
            if (_keys.Enforcing)
            {
                await RefuseAsync(
                    context,
                    $"This server requires an admin API key; send it in the {AdminApiKey.HeaderName} header.");
                return;
            }

            // Open mode: no keys configured, anonymous request — today's behavior.
            await _next(context);
            return;
        }

        if (presented.Count > 1)
        {
            await RefuseAsync(
                context, $"The request carries more than one {AdminApiKey.HeaderName} header; send exactly one.");
            return;
        }

        if (!_keys.Enforcing)
        {
            // A key was presented to a server that knows none — a configuration
            // mismatch, refused loudly rather than silently served anonymously.
            await RefuseAsync(
                context,
                $"This server has no admin API keys configured; remove the {AdminApiKey.HeaderName} header "
                    + "or configure Admin:ApiKeys.");
            return;
        }

        if (!_keys.TryIdentify(presented.ToString(), out string actor))
        {
            await RefuseAsync(context, "The presented admin API key is not recognized.");
            return;
        }

        context.Features.Set(new AdminActor(actor));
        await _next(context);
    }

    private async Task RefuseAsync(HttpContext context, string reason)
    {
        _serverJournal.RecordAdminRejected(
            reason, context.Request.Method, context.Request.Path.ToString());
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(reason));
    }
}
