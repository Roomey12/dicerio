using System.Security.Cryptography;

namespace Dicerio.Server.Rooms;

public static class RoomCode
{
    // Crockford-ish unambiguous alphabet — no 0/O, 1/I, U.
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTVWXYZ";
    public const int Length = 4;

    public static string Generate()
    {
        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(0, Alphabet.Length)];
        }

        return new string(code);
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        Span<char> buf = stackalloc char[Length];
        var i = 0;
        foreach (var ch in raw)
        {
            if (ch is ' ' or '-' or '_')
            {
                continue;
            }

            if (i >= Length)
            {
                return null;
            }

            var upper = char.ToUpperInvariant(ch);
            if (Alphabet.IndexOf(upper) < 0)
            {
                return null;
            }

            buf[i++] = upper;
        }

        return i == Length ? new string(buf) : null;
    }
}
