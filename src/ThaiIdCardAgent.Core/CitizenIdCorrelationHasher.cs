using System.Security.Cryptography;
using System.Text;

namespace ThaiIdCardAgent.Core;

/// <summary>
/// HMAC-SHA256 correlation hash over a citizen ID, keyed with a configured secret.
/// </summary>
/// <remarks>
/// A citizen ID has only ~10^12 valid values, so an unkeyed hash of one can be reversed by
/// exhaustive search in seconds — it would be a reversible encoding of the identifier, not a
/// protection. Keying with a secret the attacker does not hold is what makes the output
/// non-reversible in practice.
/// <para>When no key is configured, <see cref="IsEnabled"/> is false and no hash is produced, so
/// the audit trail simply carries no correlation value rather than a weak one.</para>
/// </remarks>
public sealed class CitizenIdCorrelationHasher : ICitizenIdCorrelationHasher, IDisposable
{
    private readonly byte[]? _key;

    public CitizenIdCorrelationHasher(string? correlationKey)
    {
        if (!string.IsNullOrWhiteSpace(correlationKey))
        {
            _key = Encoding.UTF8.GetBytes(correlationKey);
        }
    }

    public bool IsEnabled => _key is not null;

    public string? ComputeHash(string citizenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(citizenId);
        if (_key is null)
        {
            return null;
        }

        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(citizenId));
        return Convert.ToHexStringLower(hash);
    }

    public void Dispose()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
