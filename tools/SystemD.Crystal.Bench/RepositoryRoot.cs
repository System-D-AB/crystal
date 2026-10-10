using System;
using System.IO;

namespace SystemD.Crystal.Bench;

/// <summary>
/// Finds the repository root — the folder holding <c>SystemD.Crystal.slnx</c> — by walking up from the running
/// binary (spec 008 design § 2). If it is not found and no <c>--out</c> was given, the run refuses rather than
/// writing somewhere unexpected (StowDB's harness once wrote results into the wrong folder through a fallback).
/// </summary>
internal static class RepositoryRoot
{
    private const string SolutionFile = "SystemD.Crystal.slnx";

    /// <summary>The repository root, or <c>null</c> if it cannot be found.</summary>
    public static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
