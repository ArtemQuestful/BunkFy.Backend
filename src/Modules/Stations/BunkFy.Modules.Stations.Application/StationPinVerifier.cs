namespace BunkFy.Modules.Stations.Application;

using System.Security.Cryptography;
using BunkFy.Modules.Stations.Domain;
using Microsoft.Extensions.Options;

/// <summary>Bounded native KDF; secrets are never diagnostic values. There is no queued KDF fallback.</summary>
public sealed class StationPinVerifier : IStationPinVerifier, IDisposable
{
    private readonly IStationPepperProvider peppers;
    private readonly StationOptions options;
    private readonly SemaphoreSlim slots;
    public StationPinVerifier(IStationPepperProvider peppers, IOptions<StationOptions> options)
    {
        this.peppers = peppers;
        this.options = options.Value;
        if (!this.options.IsValid())
        {
            throw new ArgumentException("Invalid station verifier options.", nameof(options));
        }

        this.slots = new SemaphoreSlim(this.options.MaximumConcurrentKdf);
    }
    public static bool IsPin(string pin) => pin is { Length: 6 } && pin.All(c => c is >= '0' and <= '9');
    public async Task<StationPinMaterial?> CreateAsync(string pin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPin(pin))
        {
            return null;
        }

        if (!this.peppers.TryGet(this.options.PepperVersion, out ReadOnlyMemory<byte> pepper) || pepper.Length < 32)
        {
            return null;
        }

        if (!this.slots.Wait(0, cancellationToken))
        {
            return null;
        }

        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] verifier = await DeriveAsync(pin, salt, pepper, cancellationToken).ConfigureAwait(false);
            try
            { return new(Convert.ToBase64String(salt), Convert.ToBase64String(verifier), this.options.PepperVersion); }
            finally { CryptographicOperations.ZeroMemory(verifier); }
        }
        finally { this.slots.Release(); }
    }
    public async Task<StationPinVerification> VerifyAsync(string pin, StationStaffCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPin(pin) || credential.Revoked)
        {
            return StationPinVerification.Invalid;
        }

        if (credential.AlgorithmVersion != StationPinMaterial.AlgorithmVersion || credential.Iterations != StationPinMaterial.Iterations ||
            !this.peppers.TryGet(credential.PepperVersion, out ReadOnlyMemory<byte> pepper) || pepper.Length < 32)
        {
            return StationPinVerification.Unavailable;
        }

        byte[] salt, expected;
        try
        { salt = Convert.FromBase64String(credential.Salt); expected = Convert.FromBase64String(credential.Verifier); }
        catch (FormatException) { return StationPinVerification.Unavailable; }
        if (salt.Length < 16 || salt.Length > 64 || expected.Length != 32)
        {
            return StationPinVerification.Unavailable;
        }

        if (!this.slots.Wait(0, cancellationToken))
        {
            return StationPinVerification.Busy;
        }

        try
        {
            byte[] actual = await DeriveAsync(pin, salt, pepper, cancellationToken).ConfigureAwait(false);
            try
            { return CryptographicOperations.FixedTimeEquals(actual, expected) ? StationPinVerification.Valid : StationPinVerification.Invalid; }
            finally { CryptographicOperations.ZeroMemory(actual); CryptographicOperations.ZeroMemory(expected); }
        }
        finally { this.slots.Release(); }
    }
    private static Task<byte[]> DeriveAsync(string pin, byte[] salt, ReadOnlyMemory<byte> pepper, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(pin, salt, StationPinMaterial.Iterations, HashAlgorithmName.SHA256, 32);
            try
            { return HMACSHA256.HashData(pepper.Span, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }, ct);
    public void Dispose() => this.slots.Dispose();
}
