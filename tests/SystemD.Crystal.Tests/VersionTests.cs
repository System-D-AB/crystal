// The version Crystal reports (metrics, backup manifests) is the package's: CrystalVersion.Value is a value kept beside
// <Version> in the project file, so this test keeps the two from drifting apart.

using System.Reflection;
using SystemD.Crystal;
using Xunit;

namespace SystemD.Crystal.Tests;

public sealed class VersionTests
{
    [Fact]
    public void CrystalVersion_IsThePackageVersion()
    {
        var informational = typeof(CrystalVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // The SDK appends "+{commit}" when source control information is available.
        Assert.Equal(CrystalVersion.Value, informational.Split('+')[0]);
    }
}
