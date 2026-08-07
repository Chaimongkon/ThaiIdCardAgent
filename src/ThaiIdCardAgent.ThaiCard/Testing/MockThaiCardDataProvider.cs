using ThaiIdCardAgent.Core;

namespace ThaiIdCardAgent.ThaiCard.Testing;

/// <summary>
/// A scriptable <see cref="IThaiCardDataProvider"/> for automated tests.
/// </summary>
/// <remarks>
/// <para><b>TESTS ONLY. Never register this in the service host.</b> It contains no card protocol
/// and cannot read a physical card; it returns whatever the test tells it to. Registering it in
/// production would make <c>/api/v1/card/read</c> return fabricated identity data, which is exactly
/// the failure mode the not-configured provider exists to prevent.
/// <c>ProviderRegistrationTests</c> asserts the host registers
/// <see cref="NotConfiguredThaiCardDataProvider"/> and not this type.</para>
/// <para>Any citizen ID used with this provider must be a synthetic, checksum-valid value. Never
/// put a real citizen ID in a test, a fixture, or a commit.</para>
/// </remarks>
public sealed class MockThaiCardDataProvider : IThaiCardDataProvider
{
    private readonly Func<ThaiCardReadContext, CancellationToken, Task<ThaiIdCardIdentityResult>> _behavior;

    private MockThaiCardDataProvider(
        string providerName,
        bool isConfigured,
        Func<ThaiCardReadContext, CancellationToken, Task<ThaiIdCardIdentityResult>> behavior)
    {
        ProviderName = providerName;
        IsConfigured = isConfigured;
        _behavior = behavior;
    }

    public string ProviderName { get; }

    public bool IsConfigured { get; }

    /// <summary>Number of times a read was attempted. Used to assert double-read protection.</summary>
    public int ReadAttempts => _readAttempts;

    private int _readAttempts;

    public Task<ThaiIdCardIdentityResult> ReadCitizenIdAsync(ThaiCardReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.Increment(ref _readAttempts);
        return _behavior(context, cancellationToken);
    }

    /// <summary>Returns the supplied synthetic citizen ID.</summary>
    public static MockThaiCardDataProvider Returning(string citizenId, string providerName = "mock")
        => new(providerName, true, (context, _) => Task.FromResult(new ThaiIdCardIdentityResult(
            context.RequestId,
            context.ReaderName,
            citizenId,
            DateTimeOffset.UtcNow,
            providerName,
            context.IncludeCardAtrForDiagnostics ? "3B-88-80-01-00-00-00-00-00-00-00-00-09" : null)));

    /// <summary>Throws the supplied exception instead of reading.</summary>
    public static MockThaiCardDataProvider Throwing(Exception exception, string providerName = "mock")
        => new(providerName, true, (_, _) => Task.FromException<ThaiIdCardIdentityResult>(exception));

    /// <summary>Blocks until cancelled, so timeout and cancellation paths can be exercised.</summary>
    public static MockThaiCardDataProvider Hanging(string providerName = "mock")
        => new(providerName, true, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        });

    /// <summary>Runs arbitrary behaviour, for concurrency tests.</summary>
    public static MockThaiCardDataProvider Custom(
        Func<ThaiCardReadContext, CancellationToken, Task<ThaiIdCardIdentityResult>> behavior,
        string providerName = "mock")
        => new(providerName, true, behavior);

    /// <summary>Configured but unavailable, e.g. the device or SDK cannot be reached.</summary>
    public static MockThaiCardDataProvider Unavailable(string providerName = "mock")
        => new(providerName, true, (_, _) => Task.FromException<ThaiIdCardIdentityResult>(
            new ThaiCardProviderUnavailableException(providerName)));

    private sealed class UnreachableException : Exception;
}
