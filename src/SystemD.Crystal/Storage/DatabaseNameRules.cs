// Ported from StowDB: src/Stow.Abstractions/Engine/DatabaseNameRules.cs
// Adapted for Crystal R6: '_' reserved, 1–256 bytes, no separators, ordinal comparison.

using System;
using System.Text;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Validation rules for database names (spec 005 R6).
/// </summary>
internal static class DatabaseNameRules
{
    public const int MaxByteLength = 256;

    /// <summary>
    /// Validates <paramref name="name"/> according to R6.1 and R6.2.
    /// Throws <see cref="ReservedNameException"/> if the name begins with '_',
    /// or <see cref="ArgumentException"/> if it violates length or character rules.
    /// </summary>
    public static void Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.StartsWith('_'))
        {
            throw new ReservedNameException(
                name,
                $"Database name '{name}' starts with '_' which is reserved for Crystal system databases (R6.2).");
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(name);
        if (byteCount is < 1 or > MaxByteLength)
        {
            throw new ArgumentException(
                $"Database name '{name}' length ({byteCount} bytes) must be between 1 and {MaxByteLength} UTF-8 bytes (R6.1).",
                nameof(name));
        }

        foreach (var c in name)
        {
            if (c is '/' or '\\')
            {
                throw new ArgumentException(
                    $"Database name '{name}' cannot contain '/' or '\\' (R6.1).",
                    nameof(name));
            }

            if (char.IsControl(c))
            {
                throw new ArgumentException(
                    $"Database name '{name}' cannot contain control characters (R6.1).",
                    nameof(name));
            }
        }
    }
}
