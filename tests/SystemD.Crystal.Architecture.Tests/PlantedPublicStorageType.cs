namespace SystemD.Crystal.Storage;

/// <summary>
/// A deliberately public type in the <c>SystemD.Crystal.Storage</c> namespace, planted in the
/// test assembly for the R3.4 mutation check of R3.2's "every Storage type is internal" rule.
/// It lives only here; the kernel's own Storage types are all internal.
/// </summary>
public sealed class PlantedPublicStorageType
{
    /// <summary>A marker; the type's mere public visibility is the planted violation.</summary>
    public bool IsPlanted => true;
}
