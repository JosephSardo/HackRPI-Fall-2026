using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PathFinder.Network;

/// <summary>
/// Short, typeable game codes like "K3QZ-7TB" that are just the host's IPv4 address in disguise,
/// so joining needs no server. 32 address bits + a 3-bit typo check = 35 bits = 7 base-32 chars.
/// Uses Crockford base-32 (no I, L, O, U) and ignores case, dashes and spaces.
/// </summary>
public static class GameCode
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    const int Length = 7;

    /// <summary>The code for an IPv4 address, or null for anything else (IPv6 would need 26 chars).</summary>
    public static string? FromAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;

        var ip = ToUInt32(address);
        var bits = ((ulong)ip << 3) | Check(ip);
        var chars = new char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(bits & 31)];
            bits >>= 5;
        }
        var code = new string(chars);
        return $"{code[..4]}-{code[4..]}";
    }

    /// <summary>
    /// Decodes a game code. Returns false if it isn't code-shaped or fails the typo check.
    /// <paramref name="looksLikeCode"/> says whether the text was meant as a code (7 letters/digits, no dots or colons),
    /// so callers can say "invalid code" instead of treating it as a host name.
    /// </summary>
    public static bool TryParse(string text, out IPAddress address, out bool looksLikeCode)
    {
        address = IPAddress.None;
        var cleaned = new StringBuilder();
        foreach (var c in text.Trim().ToUpperInvariant())
        {
            if (c is '-' or ' ') continue;
            cleaned.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }); // Crockford's look-alikes
        }

        looksLikeCode = cleaned.Length == Length && !text.Contains('.') && !text.Contains(':');
        if (!looksLikeCode) return false;

        ulong bits = 0;
        foreach (var c in cleaned.ToString())
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) return false;
            bits = (bits << 5) | (uint)value;
        }

        var ip = (uint)(bits >> 3);
        if ((bits & 7) != Check(ip)) return false; // typo
        address = FromUInt32(ip);
        return true;
    }

    /// <summary>3 bits that change unpredictably with the address, so ~7 in 8 typos are caught.</summary>
    static ulong Check(uint ip) => (ip * 2654435761u) >> 29;

    static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    static IPAddress FromUInt32(uint ip) =>
        new([(byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip]);
}
