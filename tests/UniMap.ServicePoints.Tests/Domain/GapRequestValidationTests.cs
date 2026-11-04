using NetTopologySuite.Geometries;
using UniMap.ServicePoints.API.Domain;

namespace UniMap.ServicePoints.Tests.Domain;

public class GapRequestValidationTests
{
    private static readonly ServicePointLimits Limits = new(
        MinDriveTimeMinutes: 5, MaxDriveTimeMinutes: 60, MinDistanceKm: 5, MaxDistanceKm: 100);

    private static readonly Polygon Area = new GeometryFactory(new PrecisionModel(), 4326)
        .CreatePolygon([new(0, 0), new(0, 1), new(1, 1), new(1, 0), new(0, 0)]);

    private static GapRequest Request(int? driveTime, int? distance) =>
        new(Area, VehicleType.PassengerCar, driveTime, distance);

    [Fact]
    public void Accepts_drive_time_only() =>
        Assert.Empty(GapRequestValidator.Validate(Request(20, null), Limits));

    [Fact]
    public void Accepts_distance_only() =>
        Assert.Empty(GapRequestValidator.Validate(Request(null, 30), Limits));

    [Fact]
    public void Accepts_both_thresholds_at_the_limits() =>
        Assert.Empty(GapRequestValidator.Validate(Request(60, 5), Limits));

    private static readonly ServicePointLimits ZeroMinimums = new(0, 60, 0, 100);

    [Fact]
    public void Rejects_when_both_thresholds_are_null()
    {
        var error = Assert.Single(GapRequestValidator.Validate(Request(null, null), Limits));
        Assert.Contains("At least one threshold", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Rejects_non_positive_drive_time_even_when_the_minimum_is_zero(int minutes)
    {
        var error = Assert.Single(GapRequestValidator.Validate(Request(minutes, null), ZeroMinimums));
        Assert.Equal("Drive time must be positive.", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_distance_even_when_the_minimum_is_zero(int km)
    {
        var error = Assert.Single(GapRequestValidator.Validate(Request(null, km), ZeroMinimums));
        Assert.Equal("Distance must be positive.", error);
    }

    [Theory]
    [InlineData(0, "must be positive")]
    [InlineData(-5, "must be positive")]
    [InlineData(4, "between 5 and 60")]
    [InlineData(61, "between 5 and 60")]
    public void Drive_time_outside_limits_gets_a_specific_message(int minutes, string expected)
    {
        var error = Assert.Single(GapRequestValidator.Validate(Request(minutes, null), Limits));
        Assert.StartsWith("Drive time", error);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData(0, "must be positive")]
    [InlineData(-1, "must be positive")]
    [InlineData(4, "between 5 and 100")]
    [InlineData(101, "between 5 and 100")]
    public void Distance_outside_limits_gets_a_specific_message(int km, string expected)
    {
        var error = Assert.Single(GapRequestValidator.Validate(Request(null, km), Limits));
        Assert.StartsWith("Distance", error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Returns_a_separate_message_per_problem()
    {
        var errors = GapRequestValidator.Validate(Request(0, 101), Limits);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Drive time") && e.Contains("must be positive"));
        Assert.Contains(errors, e => e.StartsWith("Distance") && e.Contains("between 5 and 100"));
    }

    [Fact]
    public void Minimum_gap_size_is_one_square_kilometre() =>
        Assert.Equal(1.0, ServicePointLimits.MinGapSquareKm);
}
