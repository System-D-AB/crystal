namespace SystemD.Crystal;

/// <summary>
/// The version of the Crystal storage kernel. The only public surface of the skeleton
/// (spec 001); boxes, batches, snapshots and the rest arrive in spec 002.
/// </summary>
public static class CrystalVersion
{
    /// <summary>The informational version string of this build.</summary>
    public const string Value = "0.1.0-preview.1";
}
