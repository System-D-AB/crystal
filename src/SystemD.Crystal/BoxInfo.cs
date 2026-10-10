using System;

namespace SystemD.Crystal;

/// <summary>
/// Information about a live box in a database catalogue (spec 002 R2.1, R3.3; design § 2).
/// </summary>
/// <param name="Id">The immutable box id.</param>
/// <param name="Name">The current name alias.</param>
/// <param name="Profile">The workload profile choosing the column family.</param>
/// <param name="Created">When the box was created.</param>
public sealed record BoxInfo(uint Id, string Name, BoxProfile Profile, DateTimeOffset Created);
