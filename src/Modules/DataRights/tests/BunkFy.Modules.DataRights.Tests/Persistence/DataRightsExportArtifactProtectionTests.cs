namespace BunkFy.Modules.DataRights.Tests.Persistence;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Persistence;
using Gma.Framework.FileManagement;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsExportArtifactProtectionTests
{
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Chunked_envelope_round_trips_and_reports_lengths()
    {
        byte[] content = RandomNumberGenerator.GetBytes(40_000);
        AesGcmDataRightsExportEnvelopeProtector protector = Protector();
        await using MemoryStream plaintext = new(content, writable: false);
        await using MemoryStream encrypted = new();

        DataRightsExportProtectionResult protectedResult =
            await protector.ProtectAsync(
                plaintext,
                content.Length,
                encrypted,
                Context(),
                CancellationToken.None);
        encrypted.Position = 0;
        await using MemoryStream restored = new();
        DataRightsExportProtectionResult restoredResult =
            await protector.UnprotectAsync(
                encrypted,
                restored,
                Context(),
                CancellationToken.None);

        Assert.Equal(content, restored.ToArray());
        Assert.Equal(content.Length, protectedResult.PlaintextLength);
        Assert.Equal(encrypted.Length, protectedResult.EncryptedLength);
        Assert.Equal(protectedResult, restoredResult);
        CryptographicOperations.ZeroMemory(content);
    }

    [Fact]
    public async Task Chunked_envelope_rejects_tampering_and_wrong_binding()
    {
        byte[] content = RandomNumberGenerator.GetBytes(20_000);
        AesGcmDataRightsExportEnvelopeProtector protector = Protector();
        await using MemoryStream plaintext = new(content, writable: false);
        await using MemoryStream encrypted = new();
        _ = await protector.ProtectAsync(
            plaintext,
            content.Length,
            encrypted,
            Context(),
            CancellationToken.None);
        byte[] tampered = encrypted.ToArray();
        tampered[AesGcmDataRightsExportEnvelopeProtector.HeaderLength + 10] ^= 1;

        await using MemoryStream tamperedStream = new(tampered, writable: false);
        await using MemoryStream restored = new();
        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => protector.UnprotectAsync(
                    tamperedStream,
                    restored,
                    Context(),
                    CancellationToken.None));
        Assert.Equal("artifact-authentication-failed", exception.Code);

        encrypted.Position = 0;
        await using MemoryStream wrongBindingOutput = new();
        DataRightsExportGenerationException wrongBinding =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => protector.UnprotectAsync(
                    encrypted,
                    wrongBindingOutput,
                    Context() with { CaseId = Guid.NewGuid() },
                    CancellationToken.None));
        Assert.Equal("artifact-authentication-failed", wrongBinding.Code);
        CryptographicOperations.ZeroMemory(content);
        CryptographicOperations.ZeroMemory(tampered);
    }

    [Fact]
    public void Production_options_reject_development_key()
    {
        DataRightsExportArtifactOptions options = OptionsValue();

        Assert.True(new DataRightsExportArtifactOptionsValidator(
            isProduction: false).Validate(null, options).Succeeded);
        Assert.True(new DataRightsExportArtifactOptionsValidator(
            isProduction: true).Validate(null, options).Failed);
    }

    [Fact]
    public void Storage_options_require_opaque_content_type_and_export_capacity()
    {
        DataRightsExportArtifactOptions exportOptions = OptionsValue();
        DataRightsExportArtifactStorageOptionsValidator validator = new(
            Options.Create(exportOptions));
        FileManagementOptions storageOptions = new()
        {
            Enabled = true,
            Provider = FileStorageProvider.Minio,
            MaximumObjectBytes =
                exportOptions.MaximumPlaintextBytes +
                DataRightsExportArtifactOptions.MaximumEncryptionOverheadBytes,
            AllowedContentTypes =
                ["application/json", "application/octet-stream"]
        };

        Assert.True(validator.Validate(null, storageOptions).Succeeded);

        storageOptions.AllowedContentTypes = ["application/json"];
        Assert.True(validator.Validate(null, storageOptions).Failed);

        storageOptions.AllowedContentTypes = ["application/octet-stream"];
        storageOptions.MaximumObjectBytes--;
        Assert.True(validator.Validate(null, storageOptions).Failed);
    }

    [Fact]
    public async Task Generator_stores_and_revalidates_an_opaque_protected_artifact()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            """{"format":"bunkfy.data-rights.export","subject":"confidential"}""");
        InMemoryFileStorage storage = new();
        ProtectedDataRightsExportArtifactGenerator generator = Generator(
            storage,
            content);

        DataRightsProtectedExportArtifact artifact =
            await generator.GenerateAsync(
                Request(),
                CancellationToken.None);

        Assert.Matches(
            "^data-rights/exports/scope-[a-f0-9]{16}/" +
            "artifact-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa[.]bfdrx$",
            artifact.StorageKey);
        Assert.DoesNotContain("tenant-a", artifact.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Request().CaseId.ToString("N"),
            artifact.StorageKey,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(content, storage.Content);
        Assert.Equal(artifact.EncryptedByteLength, storage.Content.LongLength);
        Assert.Equal("data-rights", storage.Properties!.Metadata["module"]);
        Assert.Equal("bfdrx-1", storage.Properties.Metadata["format"]);
        Assert.Equal("1", storage.Properties.Metadata["key-version"]);
        CryptographicOperations.ZeroMemory(content);
    }

    [Fact]
    public async Task Generator_rejects_storage_tampering_before_availability()
    {
        byte[] content = Encoding.UTF8.GetBytes("""{"subject":"confidential"}""");
        InMemoryFileStorage storage = new() { TamperReads = true };
        ProtectedDataRightsExportArtifactGenerator generator = Generator(
            storage,
            content);

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => generator.GenerateAsync(
                    Request(),
                    CancellationToken.None));

        Assert.Equal("artifact-authentication-failed", exception.Code);
        Assert.Null(storage.Properties);
        Assert.Empty(storage.Content);
        CryptographicOperations.ZeroMemory(content);
    }

    [Fact]
    public async Task Storage_return_after_cancellation_deletes_the_unverified_object()
    {
        byte[] content = Encoding.UTF8.GetBytes("""{"subject":"confidential"}""");
        using CancellationTokenSource source = new();
        InMemoryFileStorage storage = new()
        {
            CancelAfterPut = source
        };
        ProtectedDataRightsExportArtifactGenerator generator = Generator(
            storage,
            content);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(Request(), source.Token));

        Assert.Equal(1, storage.DeleteCount);
        Assert.False(storage.LastDeleteToken.CanBeCanceled);
        Assert.Null(storage.Properties);
        Assert.Empty(storage.Content);
        CryptographicOperations.ZeroMemory(content);
    }

    private static ProtectedDataRightsExportArtifactGenerator Generator(
        InMemoryFileStorage storage,
        byte[] content)
    {
        IOptions<DataRightsExportArtifactOptions> options =
            Options.Create(OptionsValue());
        AesGcmDataRightsExportEnvelopeProtector protector = new(options);
        return new(
            new StaticAssembler(content),
            new ProtectedDataRightsExportObjectWriter(
                protector,
                storage,
                options,
                new TestClock(
                    new DateTimeOffset(
                        2026,
                        7,
                        27,
                        13,
                        0,
                        0,
                        TimeSpan.Zero))),
            options);
    }

    private static DataRightsExportGenerationRequest Request() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "tenant-a",
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        DataRightsCaseType.StaffRights,
        PropertyId: null,
        DecisionRevision: 7,
        [
            new DataRightsSubjectCoordinate(
                "staff",
                "staff-profile",
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                RecordVersion: 4)
        ],
        new DateTimeOffset(2026, 7, 27, 12, 30, 0, TimeSpan.Zero),
        ExpiresAt);

    private static AesGcmDataRightsExportEnvelopeProtector Protector() =>
        new(Options.Create(OptionsValue()));

    private static DataRightsExportArtifactOptions OptionsValue() => new()
    {
        ActiveKeyVersion = 1,
        Keys = new()
        {
            [1] = DataRightsExportArtifactOptions.DevelopmentKeyBase64
        },
        ChunkSizeBytes = 16 * 1024,
        MaximumPlaintextBytes = 1024 * 1024,
        ArtifactLifetime = TimeSpan.FromHours(24)
    };

    private static DataRightsSubjectExportProtectionContext Context() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "tenant-a",
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        DataRightsCaseType.StaffRights,
        PropertyId: null,
        DecisionRevision: 7,
        ExpiresAt);

    private sealed class StaticAssembler(byte[] content)
        : IDataRightsExportAssembler
    {
        public async Task<DataRightsExportAssemblyResult> AssembleAsync(
            DataRightsExportGenerationRequest request,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(content, cancellationToken);
            return new(SubjectCount: 1, RecordCount: 1);
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        public byte[] Content { get; private set; } = [];
        public FileStorageObjectProperties? Properties { get; private set; }
        public bool TamperReads { get; init; }
        public CancellationTokenSource? CancelAfterPut { get; init; }
        public int DeleteCount { get; private set; }
        public CancellationToken LastDeleteToken { get; private set; }

        public async Task<FileStorageObjectProperties> PutAsync(
            FileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            await using MemoryStream copy = new();
            await request.Content.CopyToAsync(copy, cancellationToken);
            this.Content = copy.ToArray();
            if (this.Content.LongLength != request.ContentLength)
            {
                throw new InvalidOperationException("Test storage length mismatch.");
            }

            this.Properties = new FileStorageObjectProperties(
                request.Key,
                request.ContentLength,
                request.ContentType,
                request.FileName,
                ETag: "test-etag",
                LastModifiedUtc:
                    new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero),
                request.Metadata);
            this.CancelAfterPut?.Cancel();
            return this.Properties;
        }

        public Task<FileStorageReadResult?> OpenReadAsync(
            FileStorageObjectKey key,
            CancellationToken cancellationToken = default)
        {
            if (this.Properties is null || this.Properties.Key != key)
            {
                return Task.FromResult<FileStorageReadResult?>(null);
            }

            byte[] copy = [.. this.Content];
            if (this.TamperReads)
            {
                copy[AesGcmDataRightsExportEnvelopeProtector.HeaderLength + 1] ^= 1;
            }

            return Task.FromResult<FileStorageReadResult?>(new(
                this.Properties,
                async (destination, token) =>
                    await destination.WriteAsync(copy, token)));
        }

        public Task<FileStorageObjectProperties?> GetPropertiesAsync(
            FileStorageObjectKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                this.Properties?.Key == key
                    ? this.Properties
                    : null);

        public Task<bool> DeleteAsync(
            FileStorageObjectKey key,
            CancellationToken cancellationToken = default)
        {
            this.DeleteCount++;
            this.LastDeleteToken = cancellationToken;
            bool deleted = this.Properties?.Key == key;
            this.Properties = null;
            CryptographicOperations.ZeroMemory(this.Content);
            this.Content = [];
            return Task.FromResult(deleted);
        }
    }
}
