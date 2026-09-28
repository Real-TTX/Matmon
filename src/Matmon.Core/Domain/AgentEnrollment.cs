using System.Security.Cryptography;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>
/// A pending agent enrolment: a short-lived, single-use code an admin hands to an agent install, which the
/// agent trades for a probe id + probe token of its own (<c>POST /api/agents/enroll</c>).
///
/// Why a code rather than handing out the probe token directly: the token is the agent's credential for
/// its whole life, and the install command it would sit in ends up in shell histories, tickets and chat.
/// A code that works once, for a day, and is stored only as a hash is worth nothing once it has been used -
/// and the probe is only created when an agent actually shows up, so an install that never happened does
/// not leave a dead probe behind.
/// </summary>
public sealed class AgentEnrollment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 of the normalised code. The code itself is shown once and never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Name for the probe the agent becomes; null = the agent's host name.</summary>
    public string? Name { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ExpiresUtc { get; set; }

    public string? CreatedBy { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresUtc;
}

/// <summary>Generation, normalisation and hashing of enrolment codes - pure, so it is unit-tested.</summary>
public static class AgentEnrollmentCode
{
    // Crockford-style base32 without the characters people misread from a screen: no 0/O, 1/I/L, U.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    private const int GroupCount = 5;
    private const int GroupLength = 4;

    /// <summary>
    /// A fresh code, e.g. <c>K7QM-4TXC-9RHA-B2NW-P6FE</c>: 20 symbols of a 30-letter alphabet, ~98 bits.
    /// Plenty for something that is also single-use, short-lived and rate-limited at the endpoint.
    /// </summary>
    public static string Generate()
    {
        var builder = new StringBuilder(GroupCount * (GroupLength + 1));
        for (var group = 0; group < GroupCount; group++)
        {
            if (group > 0)
            {
                builder.Append('-');
            }

            for (var index = 0; index < GroupLength; index++)
            {
                builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
            }
        }

        return builder.ToString();
    }

    /// <summary>What the user typed, reduced to what counts: case, dashes and whitespace do not.</summary>
    public static string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string? code)
    {
        var normalized = Normalize(code);
        return normalized.Length == 0
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
