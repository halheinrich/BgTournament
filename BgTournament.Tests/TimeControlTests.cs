using System.Text.Json;
using BgTournament.Api;

namespace BgTournament.Tests;

/// <summary>
/// The validated time-control value type: construction rejects every
/// non-playable configuration, so downstream clock logic never sees garbage;
/// equality is by value; the TimeSpan views and the shorthand rendering are
/// the type's own (single-sourced) unit conversions.
/// </summary>
public class TimeControlTests
{
    [Fact]
    public void Construct_ValidValues_ExposesSecondsAndTimeSpans()
    {
        var control = new TimeControl(initialSeconds: 120, incrementSeconds: 8);

        Assert.Equal(120, control.InitialSeconds);
        Assert.Equal(8, control.IncrementSeconds);
        Assert.Equal(TimeSpan.FromSeconds(120), control.Initial);
        Assert.Equal(TimeSpan.FromSeconds(8), control.Increment);
    }

    [Fact]
    public void Construct_ZeroIncrement_IsAValidDegenerateConfig()
    {
        var control = new TimeControl(initialSeconds: 0.25, incrementSeconds: 0);

        Assert.Equal(TimeSpan.Zero, control.Increment);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Construct_InvalidInitial_Throws(double initialSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            "initialSeconds", () => new TimeControl(initialSeconds, 8));

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Construct_InvalidIncrement_Throws(double incrementSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            "incrementSeconds", () => new TimeControl(120, incrementSeconds));

    /// <summary>
    /// Values must stay representable as a TimeSpan — the clock arithmetic's
    /// working unit — so overflow is rejected at the boundary, not deep in a
    /// match.
    /// </summary>
    [Fact]
    public void Construct_BeyondTimeSpanRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            "initialSeconds", () => new TimeControl(double.MaxValue / 2, 8));
        Assert.Throws<ArgumentOutOfRangeException>(
            "incrementSeconds", () => new TimeControl(120, double.MaxValue / 2));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new TimeControl(120, 8), new TimeControl(120, 8));
        Assert.NotEqual(new TimeControl(120, 8), new TimeControl(120, 0));
    }

    /// <summary>
    /// The JSON funnel: a value the constructor rejects deserializes as
    /// <see cref="JsonException"/> — like any other malformed field — so an
    /// HTTP host's standard bad-request handling applies (no 500s from
    /// binding).
    /// </summary>
    [Theory]
    [InlineData("""{"initialSeconds":0,"incrementSeconds":5}""")]
    [InlineData("""{"initialSeconds":60,"incrementSeconds":-1}""")]
    [InlineData("""{"initialSeconds":60}""")]
    [InlineData("""{"incrementSeconds":5}""")]
    [InlineData("""[60,5]""")]
    public void Deserialize_InvalidControl_IsAJsonException(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TimeControl>(json));

    [Fact]
    public void Deserialize_UnknownFields_AreIgnored()
    {
        var control = JsonSerializer.Deserialize<TimeControl>(
            """{"initialSeconds":60,"futureField":{"nested":true},"incrementSeconds":5}""");

        Assert.Equal(new TimeControl(60, 5), control);
    }

    [Fact]
    public void ToString_IsTheConventionalShorthand() =>
        Assert.Equal("120s + 8s/decision", new TimeControl(120, 8).ToString());

    [Fact]
    public void ToString_FractionalSeconds_InvariantCulture() =>
        Assert.Equal("90.5s + 0.25s/decision", new TimeControl(90.5, 0.25).ToString());
}
