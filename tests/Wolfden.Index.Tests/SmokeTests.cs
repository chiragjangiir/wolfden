using System.Reflection;

namespace Wolfden.Index.Tests;

public class SmokeTests
{
    [Fact]
    public void IndexAssemblyLoads()
    {
        var assembly = Assembly.Load("Wolfden.Index");

        Assert.Equal("Wolfden.Index", assembly.GetName().Name);
    }
}
