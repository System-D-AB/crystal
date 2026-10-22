namespace SystemD.Crystal;

/// <summary>
/// The version of the Crystal storage kernel: the package version, recorded in metrics and backup manifests.
/// </summary>
public static class CrystalVersion
{
    /// <summary>The informational version string of this build.</summary>
    /// <remarks>
    /// A property, not a constant (spec 015 R3.1): a constant is copied into the application at compile time, so an
    /// application built against one version would report it while running on another.
    /// </remarks>
    public static string Value { get; } = "1.0.0-rc.2";
}
