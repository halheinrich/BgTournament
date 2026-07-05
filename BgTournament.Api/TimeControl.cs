using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// A Fischer time control: each player starts the match with
/// <see cref="InitialSeconds"/> on their clock and is credited
/// <see cref="IncrementSeconds"/> after every answered decision; a player
/// whose clock empties mid-decision forfeits the match (flag fall). Optional
/// on <see cref="StartMatchRequest"/> and <see cref="StartTournamentRequest"/>
/// — omitted, the server's flat per-decision timeout governs instead. Present,
/// it <em>replaces</em> that timeout: the remaining pool is the only limit on
/// any single decision.
///
/// <para>Validated on construction, so an instance always describes a playable
/// control: finite values, a positive initial pool, a non-negative increment,
/// both representable as a <see cref="TimeSpan"/>. On JSON deserialization the
/// dedicated converter folds a validation failure into
/// <see cref="JsonException"/> — the same funnel as any other malformed field —
/// so an HTTP host's standard bad-request handling applies. A per-decision cap
/// is a degenerate configuration rather than a separate mode:
/// <c>initial = increment = C</c> guarantees at least <c>C</c> seconds for
/// every decision (unused time banks, per Fischer).</para>
/// </summary>
[JsonConverter(typeof(TimeControlJsonConverter))]
public sealed record TimeControl
{
    /// <summary>
    /// Create a validated time control.
    /// </summary>
    /// <param name="initialSeconds">Each player's starting pool, in seconds; finite and &gt; 0.</param>
    /// <param name="incrementSeconds">Seconds credited after each answered decision; finite and ≥ 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is NaN or infinite, <paramref name="initialSeconds"/> is not
    /// positive, <paramref name="incrementSeconds"/> is negative, or a value is
    /// too large to represent as a <see cref="TimeSpan"/>.
    /// </exception>
    public TimeControl(double initialSeconds, double incrementSeconds)
    {
        InitialSeconds = ValidateSeconds(initialSeconds, allowZero: false);
        IncrementSeconds = ValidateSeconds(incrementSeconds, allowZero: true);
    }

    /// <summary>Each player's starting pool, in seconds. Finite and &gt; 0.</summary>
    public double InitialSeconds { get; }

    /// <summary>Seconds credited after each answered decision. Finite and ≥ 0.</summary>
    public double IncrementSeconds { get; }

    /// <summary>The starting pool as a <see cref="TimeSpan"/>.</summary>
    [JsonIgnore]
    public TimeSpan Initial => TimeSpan.FromSeconds(InitialSeconds);

    /// <summary>The per-decision increment as a <see cref="TimeSpan"/>.</summary>
    [JsonIgnore]
    public TimeSpan Increment => TimeSpan.FromSeconds(IncrementSeconds);

    /// <summary>The conventional shorthand, e.g. <c>120s + 8s/decision</c> (invariant culture).</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture, $"{InitialSeconds}s + {IncrementSeconds}s/decision");

    private static double ValidateSeconds(
        double value, bool allowZero, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, "A time-control value must be a finite number of seconds.");
        }

        if (value < 0 || (value == 0 && !allowZero))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value,
                allowZero
                    ? "The increment must be zero or more seconds."
                    : "The initial pool must be more than zero seconds.");
        }

        if (value >= TimeSpan.MaxValue.TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, "A time-control value must be representable as a TimeSpan.");
        }

        return value;
    }
}

/// <summary>
/// The JSON binding of <see cref="TimeControl"/>. The wire names are the
/// type's own contract (<c>initialSeconds</c> / <c>incrementSeconds</c>,
/// pinned here like the per-member string-enum pins on the other Api shapes),
/// and a value the constructor rejects surfaces as <see cref="JsonException"/>
/// — one malformed-payload funnel, so hosts and consumers handle an invalid
/// control exactly like any other malformed field.
/// </summary>
internal sealed class TimeControlJsonConverter : JsonConverter<TimeControl>
{
    private const string InitialSecondsName = "initialSeconds";
    private const string IncrementSecondsName = "incrementSeconds";

    /// <inheritdoc/>
    public override TimeControl Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A time control must be a JSON object.");
        }

        double? initialSeconds = null;
        double? incrementSeconds = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string? name = reader.GetString();
            reader.Read();
            if (string.Equals(name, InitialSecondsName, StringComparison.OrdinalIgnoreCase))
            {
                initialSeconds = reader.GetDouble();
            }
            else if (string.Equals(name, IncrementSecondsName, StringComparison.OrdinalIgnoreCase))
            {
                incrementSeconds = reader.GetDouble();
            }
            else
            {
                reader.Skip();
            }
        }

        if (initialSeconds is null || incrementSeconds is null)
        {
            throw new JsonException(
                $"A time control requires both '{InitialSecondsName}' and '{IncrementSecondsName}'.");
        }

        try
        {
            return new TimeControl(initialSeconds.Value, incrementSeconds.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeControl value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(InitialSecondsName, value.InitialSeconds);
        writer.WriteNumber(IncrementSecondsName, value.IncrementSeconds);
        writer.WriteEndObject();
    }
}
