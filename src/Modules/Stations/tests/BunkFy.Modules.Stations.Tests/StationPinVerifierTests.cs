namespace BunkFy.Modules.Stations.Tests;

using System.Diagnostics;
using System.Text.Json;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Domain;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

public sealed class StationPinVerifierTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("000001", true)]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData(" 12345", false)]
    [InlineData("１２３４５６", false)]
    [InlineData("12a456", false)]
    public void Pin_is_six_ascii_digits(string pin, bool expected) => Assert.Equal(expected, StationPinVerifier.IsPin(pin));

    [Fact]
    public async Task Native_KDF_uses_distinct_salts_leading_zero_and_versioned_pepper()
    {
        var peppers = new TestPeppers();
        using var verifier = new StationPinVerifier(peppers, Options.Create(StationDomainTests.Options()));
        var timer = Stopwatch.StartNew();
        StationPinMaterial first = Assert.IsType<StationPinMaterial>(await verifier.CreateAsync("000001"));
        StationPinMaterial second = Assert.IsType<StationPinMaterial>(await verifier.CreateAsync("000001"));
        Assert.True(first.Salt != second.Salt && first.Verifier != second.Verifier);
        var credential = new StationStaffCredential(StationDomainTests.Tenant, Guid.NewGuid(), first, StationDomainTests.Now);
        Assert.Equal(StationPinVerification.Valid, await verifier.VerifyAsync("000001", credential));
        Assert.Equal(StationPinVerification.Invalid, await verifier.VerifyAsync("000002", credential));
        peppers.Wrong = true;
        Assert.Equal(StationPinVerification.Invalid, await verifier.VerifyAsync("000001", credential));
        peppers.Missing = true;
        Assert.Equal(StationPinVerification.Unavailable, await verifier.VerifyAsync("000001", credential));
        Assert.Null(await verifier.CreateAsync("000001"));
        string json = JsonSerializer.Serialize(credential);
        Assert.DoesNotContain("Verifier", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Salt", json, StringComparison.Ordinal);
        Assert.Equal("{}", JsonSerializer.Serialize(first));
        output.WriteLine("Native PBKDF2/HMAC: 5 derivations elapsed {0} ms; fixture CPU timing only.", timer.ElapsedMilliseconds);
    }
    [Fact]
    public async Task Verifier_has_no_unbounded_wait_queue_and_honors_cancellation()
    {
        var options = StationDomainTests.Options();
        options.MaximumConcurrentKdf = 1;
        using var verifier = new StationPinVerifier(new TestPeppers(), Options.Create(options));
        Task<StationPinMaterial?> first = verifier.CreateAsync("000001");
        Assert.Null(await verifier.CreateAsync("000002"));
        StationPinMaterial material = Assert.IsType<StationPinMaterial>(await first);
        var credential = new StationStaffCredential(StationDomainTests.Tenant, Guid.NewGuid(), material, StationDomainTests.Now);
        Task<StationPinVerification> verify = verifier.VerifyAsync("000001", credential);
        Assert.Equal(StationPinVerification.Busy, await verifier.VerifyAsync("000001", credential));
        Assert.Equal(StationPinVerification.Valid, await verify);
        Assert.Equal(StationPinVerification.Valid, await verifier.VerifyAsync("000001", credential));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.CreateAsync("000001", canceled.Token));
    }
    internal sealed class TestPeppers : IStationPepperProvider
    {
        public bool Wrong { get; set; }
        public bool Missing { get; set; }
        public bool TryGet(string version, out ReadOnlyMemory<byte> pepper)
        {
            pepper = new byte[32].Select(_ => this.Wrong ? (byte)2 : (byte)1).ToArray();
            return !this.Missing && version == "test-v1";
        }
    }
}
