using System.Globalization;
using Microsoft.Extensions.Logging;

namespace BgTournament.ReferenceBot;

/// <summary>
/// The validated inputs for one reference-bot session, parsed from the command
/// line. Immutable; the only construction path is <see cref="Parse"/>, which
/// funnels every malformed input into a <see cref="UsageException"/>.
/// </summary>
internal sealed record ReferenceBotOptions
{
    private ReferenceBotOptions(
        Uri serverUri, string name, int seed, string? version, string? author,
        string? engineKey, LogLevel verbosity)
    {
        ServerUri = serverUri;
        Name = name;
        Seed = seed;
        Version = version;
        Author = author;
        EngineKey = engineKey;
        Verbosity = verbosity;
    }

    /// <summary>The engine endpoint to connect to (<c>ws://</c> or <c>wss://</c>).</summary>
    public Uri ServerUri { get; }

    /// <summary>The engine's registry name, presented on the handshake.</summary>
    public string Name { get; }

    /// <summary>The seed for the random play policy — reproducible by construction.</summary>
    public int Seed { get; }

    /// <summary>Optional engine version label (handshake metadata).</summary>
    public string? Version { get; }

    /// <summary>Optional author label (handshake metadata).</summary>
    public string? Author { get; }

    /// <summary>Optional registration key for a server enforcing the Registered policy (PROTOCOL.md §3.1).</summary>
    public string? EngineKey { get; }

    /// <summary>The minimum log level for the SDK's diagnostics.</summary>
    public LogLevel Verbosity { get; }

    /// <summary>
    /// Parse and validate the command-line arguments. Recognizes
    /// <c>--server</c>, <c>--name</c>, <c>--seed</c>, <c>--version</c>,
    /// <c>--author</c>, <c>--engine-key</c>, and <c>--verbosity</c>, in either
    /// <c>--key value</c> or <c>--key=value</c> form.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    /// <exception cref="UsageException">A required argument is missing, an unknown flag appears, or a value is malformed.</exception>
    public static ReferenceBotOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"Unexpected argument '{token}'; options take the form --key value.");
            }

            string key;
            string value;
            int equals = token.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                key = token[..equals];
                value = token[(equals + 1)..];
            }
            else
            {
                key = token;
                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"Option '{key}' expects a value.");
                }

                value = args[++i];
            }

            if (!IsKnownOption(key))
            {
                throw new UsageException($"Unknown option '{key}'.");
            }

            if (!values.TryAdd(key, value))
            {
                throw new UsageException($"Option '{key}' was given more than once.");
            }
        }

        Uri serverUri = ParseServerUri(Require(values, "--server"));
        string name = Require(values, "--name");
        int seed = ParseSeed(values);
        LogLevel verbosity = ParseVerbosity(values);

        return new ReferenceBotOptions(
            serverUri, name, seed,
            values.GetValueOrDefault("--version"),
            values.GetValueOrDefault("--author"),
            values.GetValueOrDefault("--engine-key"),
            verbosity);
    }

    private static bool IsKnownOption(string key) => key switch
    {
        "--server" or "--name" or "--seed" or "--version"
            or "--author" or "--engine-key" or "--verbosity" => true,
        _ => false,
    };

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"Required option '{key}' is missing.");
        }

        return value;
    }

    private static Uri ParseServerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            throw new UsageException(
                $"'--server' must be an absolute ws:// or wss:// URI; got '{value}'.");
        }

        return uri;
    }

    private static int ParseSeed(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("--seed", out string? raw))
        {
            return 0;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        {
            throw new UsageException($"'--seed' must be an integer; got '{raw}'.");
        }

        return seed;
    }

    private static LogLevel ParseVerbosity(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("--verbosity", out string? raw))
        {
            return LogLevel.Information;
        }

        if (!Enum.TryParse(raw, ignoreCase: true, out LogLevel level) || !Enum.IsDefined(level))
        {
            throw new UsageException(
                $"'--verbosity' must be one of trace|debug|information|warning|error|critical|none; got '{raw}'.");
        }

        return level;
    }
}
