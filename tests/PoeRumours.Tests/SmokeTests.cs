namespace PoeRumours.Tests;

public class SmokeTests
{
    // M0: the solution builds and the test project can see the app assembly. Real tests arrive with M1.
    [Fact]
    public void AppVersion_IsReported()
    {
        Assert.False(string.IsNullOrWhiteSpace(PoeRumours.AppVersion.Current));
    }
}
