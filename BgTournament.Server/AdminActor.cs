using System.Reflection;

namespace BgTournament.Server;

/// <summary>
/// The authenticated identity of one admin request — the API-key <em>name</em>
/// the presented key resolved to, never the key itself. Set as a request
/// feature by <see cref="AdminAuthenticationMiddleware"/> when a valid key was
/// presented; absent on an anonymous request (an unconfigured server serving
/// openly). Endpoint handlers bind it as an optional parameter
/// (<see cref="BindAsync"/>), so "who did this" flows to the durable record
/// without any handler touching headers.
/// </summary>
/// <param name="Name">The actor's configured key name.</param>
internal sealed record AdminActor(string Name)
{
    /// <summary>Minimal-API parameter binding: the feature the middleware set, or null when anonymous.</summary>
    public static ValueTask<AdminActor?> BindAsync(HttpContext context, ParameterInfo parameter) =>
        ValueTask.FromResult(context.Features.Get<AdminActor>());
}
