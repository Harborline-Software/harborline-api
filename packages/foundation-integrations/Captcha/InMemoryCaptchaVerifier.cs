using System.Collections.Concurrent;
using System.Net;

namespace Harborline.Api.Foundation.Integrations.Captcha;

/// <summary>
/// W#28 Phase 3 stub <see cref="ICaptchaVerifier"/>. Drives test scenarios
/// without contacting any provider. Tokens are pre-seeded with a verdict
/// via <see cref="Seed(string, double)"/>; unseeded tokens fail by
/// default. ADR 0096 Step 1 retrofit adds <see cref="IMockVendorProvider"/>
/// marker membership + canonical static factories
/// (<see cref="AlwaysPass"/>, <see cref="AlwaysFail"/>,
/// <see cref="WithMagicToken(string)"/>) for the Tier-2 vendor-provider
/// substrate.
/// </summary>
/// <remarks>
/// Records every call in an in-memory journal for assertion in tests.
/// The journal exposes the verified token + client IP. **NOT for
/// production** — production deployments wire the
/// <c>providers-recaptcha</c> (or, per ADR 0096 Step 3,
/// <c>providers-turnstile</c>) adapter via
/// <see cref="DependencyInjection.VendorProviderServiceCollectionExtensions.UseVendorProviderIfConfigured{TContract, TReal}(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>.
/// </remarks>
public sealed class InMemoryCaptchaVerifier : ICaptchaVerifier, IMockVendorProvider
{
    private readonly ConcurrentDictionary<string, double> _seeds = new();
    private readonly ConcurrentBag<(string Token, IPAddress ClientIp)> _calls = new();
    private readonly double _minPassingScore;
    private readonly bool _alwaysReturn;
    private readonly double? _forcedScore;

    /// <summary>Snapshot of every verify call (token + client IP) for test assertions.</summary>
    public IReadOnlyCollection<(string Token, IPAddress ClientIp)> Calls => _calls;

    /// <summary>Initialises a verifier with the standard 0.3 minimum passing score.</summary>
    public InMemoryCaptchaVerifier() : this(minPassingScore: 0.3) { }

    /// <summary>Initialises a verifier with a custom minimum passing score.</summary>
    public InMemoryCaptchaVerifier(double minPassingScore)
    {
        if (minPassingScore < 0 || minPassingScore > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minPassingScore), "Score must be in [0.0, 1.0].");
        }
        _minPassingScore = minPassingScore;
        _alwaysReturn = false;
        _forcedScore = null;
    }

    private InMemoryCaptchaVerifier(bool alwaysPass, double minPassingScore)
    {
        _minPassingScore = minPassingScore;
        _alwaysReturn = true;
        _forcedScore = alwaysPass ? 1.0 : 0.0;
    }

    /// <summary>
    /// Returns a verifier whose
    /// <see cref="VerifyAsync(string, IPAddress, CancellationToken)"/>
    /// always returns <see cref="CaptchaVerifyResult.Passed"/> =
    /// <see langword="true"/> with score 1.0. Convenience factory for
    /// tests + dev-mode bypass.
    /// </summary>
    public static InMemoryCaptchaVerifier AlwaysPass() => new(alwaysPass: true, minPassingScore: 0.3);

    /// <summary>
    /// Returns a verifier whose
    /// <see cref="VerifyAsync(string, IPAddress, CancellationToken)"/>
    /// always returns <see cref="CaptchaVerifyResult.Passed"/> =
    /// <see langword="false"/> with score 0.0. Convenience factory for
    /// failure-path tests.
    /// </summary>
    public static InMemoryCaptchaVerifier AlwaysFail() => new(alwaysPass: false, minPassingScore: 0.3);

    /// <summary>
    /// Returns a verifier that passes only when the verify token matches
    /// <paramref name="magicToken"/> exactly. All other tokens fail.
    /// Convenience factory for dev-mode bypass scenarios where a developer
    /// wants to test the post-CAPTCHA flow without a real Turnstile token.
    /// </summary>
    public static InMemoryCaptchaVerifier WithMagicToken(string magicToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magicToken);
        var verifier = new InMemoryCaptchaVerifier();
        verifier.Seed(magicToken, 1.0);
        return verifier;
    }

    /// <summary>
    /// Pre-seeds a token with a score; subsequent <see cref="VerifyAsync"/>
    /// calls with this token return <see cref="CaptchaVerifyResult.Passed"/>
    /// based on whether the seeded score meets the minimum.
    /// </summary>
    public void Seed(string token, double score)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (score < 0 || score > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(score), "Score must be in [0.0, 1.0].");
        }
        _seeds[token] = score;
    }

    /// <inheritdoc />
    public Task<CaptchaVerifyResult> VerifyAsync(string token, IPAddress clientIp, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(clientIp);
        ct.ThrowIfCancellationRequested();

        _calls.Add((token, clientIp));

        if (_alwaysReturn)
        {
            var score = _forcedScore ?? 0.0;
            return Task.FromResult(new CaptchaVerifyResult(
                Passed: score >= _minPassingScore,
                Score: score,
                Provider: "in-memory"));
        }

        if (!_seeds.TryGetValue(token, out var seededScore))
        {
            return Task.FromResult(new CaptchaVerifyResult(Passed: false, Score: 0.0, Provider: "in-memory"));
        }

        return Task.FromResult(new CaptchaVerifyResult(
            Passed: seededScore >= _minPassingScore,
            Score: seededScore,
            Provider: "in-memory"));
    }
}
