namespace SystemD.Crystal;

/// <summary>
/// A key-value pair returned by box scans (spec 002 design § 2).
/// The key is box-relative (prefix stripped).
/// </summary>
/// <param name="Key">The key bytes within the box.</param>
/// <param name="Value">The uninterpreted value bytes.</param>
public readonly record struct KeyValue(byte[] Key, byte[] Value);
