namespace UniMap.ServicePoints.Tests;

public class SetupTests
{
    [Fact]
    public void Test_project_runs_and_references_api_assembly()
    {
        var api = typeof(Program).Assembly;
        Assert.Equal("UniMap.ServicePoints.API", api.GetName().Name);
    }
}
