using System;
using System.Collections.Generic;
using System.Linq;

namespace PathFinder.Network;

public static class PlayerNames
{
    public const int MaxLength = 20;

    /// <summary>Strips control characters, trims, and caps the length. Falls back if nothing is left.</summary>
    public static string Clean(string? name, string fallback)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength].Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>"Alice" -> "Alice 2" (or 3, ...) if the name is already taken, ignoring case.</summary>
    public static string MakeUnique(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!used.Contains($"{name} {i}")) return $"{name} {i}";
    }
}
