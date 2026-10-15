namespace SystemD.Crystal;

/// <summary>The order a scan returns keys in (spec 012 R1).</summary>
public enum ScanDirection
{
    /// <summary>Ascending key order, from <c>from</c> up to (not including) <c>to</c>.</summary>
    Forward = 0,

    /// <summary>
    /// Descending key order over the same range: from the greatest key strictly below <c>to</c> down to <c>from</c>.
    /// </summary>
    Backward = 1,
}
