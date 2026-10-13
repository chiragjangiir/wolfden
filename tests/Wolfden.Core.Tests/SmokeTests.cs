using System.Reflection;

namespace Wolfden.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        var assembly = Assembly.Load("Wolfden.Core");

        Assert.Equal("Wolfden.Core", assembly.GetName().Name);
    }
}
