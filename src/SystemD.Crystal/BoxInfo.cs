using System;

namespace SystemD.Crystal;

/// <summary>
/// Information about a live box in a database catalogue (spec 002 R2.1, R3.3; design § 2).
/// </summary>
/// <param name="Id">The immutable box id.</param>
/// <param name="Name">The current name alias.</param>
/// <param name="Profile">The workload profile choosing the column family.</param>
/// <param name="Created">When the box was created.</param>
public sealed record BoxInfo(uint Id, string Name, BoxProfile Profile, DateTimeOffset Created)
{
    /// <summary>
    /// The box's handle, issued by the database that listed it (spec 013 R9): ready for reads, scans, batches and locks
    /// without a <see cref="BoxCatalog.Find"/> per box, as a product walking every box needs.
    /// </summary>
    public Box Handle { get; init; }
}
