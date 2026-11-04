using UniMap.ServicePoints.API.Here;

namespace UniMap.ServicePoints.Tests.Here;

public class FlexiblePolylineTests
{
    [Fact]
    public void Decodes_the_reference_example_from_the_HERE_specification_and_skips_the_third_dimension()
    {
        var points = FlexiblePolyline.Decode("BFoz5xJ67i1B1B7PzIhaxL7Y");

        Assert.Equal(4, points.Count);
        Assert.Equal((50.10228, 8.69821), points[0]);
        Assert.Equal((50.10201, 8.69567), points[1]);
        Assert.Equal((50.09878, 8.68752), points[3]);
    }

    [Fact]
    public void Decodes_the_first_vertex_of_the_recorded_car_isoline()
    {
        var outer = System.Text.Json.JsonDocument.Parse(Fixtures.Read("isoline-car-time.json"))
            .RootElement.GetProperty("isolines")[0].GetProperty("polygons")[0].GetProperty("outer").GetString()!;

        var points = FlexiblePolyline.Decode(outer);

        Assert.Equal(173, points.Count);
        Assert.Equal((39.914703, 32.817707), points[0]);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("BFoz5xJ")]
    [InlineData("BFoz5xJ6")]
    [InlineData("BF!!")]
    [InlineData("AFoz5xJ")]
    public void Rejects_truncated_or_invalid_input(string encoded)
    {
        Assert.Throws<FormatException>(() => FlexiblePolyline.Decode(encoded));
    }
}
