namespace BunkFy.Modules.DataRights.Tests.Persistence;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Persistence;
using Gma.Framework.FileManagement;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationExportArtifactProtectionTests
{
    private const string TenantId = "tenant-a";
    private const string CatalogSha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FrozenSha =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PolicySha =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly Guid ProcessId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CaseId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FragmentExpiresAt =
        Now.AddHours(24);
    private static readonly DateTimeOffset ArtifactExpiresAt =
        Now.AddHours(12);

    [Fact]
    public async Task Generator_stores_a_verified_deterministic_bundle()
    {
        TestFixture fixture = new();
        TenantTerminationExportFragment workspaces =
            await fixture.CreateFragmentAsync(
                "workspaces",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
                /*lang=json,strict*/
                """{"owner":"workspaces","records":[{"name":"Hostel"}]}""",
                proofRevision: 11);
        TenantTerminationExportFragment inventory =
            await fixture.CreateFragmentAsync(
                "inventory",
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222"),
                /*lang=json,strict*/
                """{"owner":"inventory","records":[{"room":"4A"}]}""",
                proofRevision: 22);
        TenantTerminationExportArtifact artifact =
            TestFixture.CreateGeneratingArtifact([workspaces, inventory]);

        TenantTerminationProtectedExportArtifact protectedArtifact =
            await fixture.Generator.GenerateAsync(
                artifact,
                [inventory, workspaces],
                CancellationToken.None);

        Assert.Equal(2, protectedArtifact.FragmentCount);
        Assert.Equal(2, protectedArtifact.RecordCount);
        Assert.Equal(
            artifact.FragmentSetSha256,
            protectedArtifact.FragmentSetSha256);
        Assert.Matches(
            "^data-rights/tenant-exports/scope-[a-f0-9]{16}/" +
            "process-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/" +
            "artifact-[a-f0-9]{32}[.]bftxa$",
            protectedArtifact.StorageKey);
        Assert.DoesNotContain(
            TenantId,
            protectedArtifact.StorageKey,
            StringComparison.Ordinal);
        Assert.Equal(
            "BFTXA001",
            Encoding.ASCII.GetString(
                fixture.Storage.GetContent(protectedArtifact.StorageKey),
                0,
                8));
        Assert.Equal(
            "bftxa-1",
            fixture.Storage.GetProperties(protectedArtifact.StorageKey)
                .Metadata["format"]);

        await using FileStream bundle = await fixture.Reader.OpenVerifiedAsync(
            ReadRequest(artifact, protectedArtifact),
            CancellationToken.None);
        using ZipArchive archive = new(bundle, ZipArchiveMode.Read);
        Assert.Equal(
            [
                "manifest.json",
                "owners/inventory.json",
                "owners/workspaces.json"
            ],
            archive.Entries.Select(entry => entry.FullName));
        Assert.Equal(
            /*lang=json,strict*/
            """{"owner":"inventory","records":[{"room":"4A"}]}""",
            await ReadEntryAsync(archive, "owners/inventory.json"));
        Assert.Equal(
            /*lang=json,strict*/
            """{"owner":"workspaces","records":[{"name":"Hostel"}]}""",
            await ReadEntryAsync(archive, "owners/workspaces.json"));

        string manifest = await ReadEntryAsync(archive, "manifest.json");
        Assert.DoesNotContain(TenantId, manifest, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(manifest);
        JsonElement root = document.RootElement;
        Assert.Equal(
            "bunkfy.tenant-termination.export-bundle",
            root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("fragmentCount").GetInt32());
        Assert.Equal(2, root.GetProperty("recordCount").GetInt64());
        Assert.Equal(
            artifact.FragmentSetSha256,
            root.GetProperty("fragmentSetSha256").GetString());
        Assert.Equal(
            "inventory",
            root.GetProperty("fragments")[0]
                .GetProperty("ownerKey").GetString());

        Assert.True(artifact.MarkAvailable(
            artifact.GenerationRunId!.Value,
            artifact.GenerationAttempt!.Value,
            protectedArtifact.FragmentCount,
            protectedArtifact.RecordCount,
            protectedArtifact.FragmentSetSha256,
            protectedArtifact.StorageKey,
            protectedArtifact.EncryptedByteLength,
            protectedArtifact.PlaintextSha256,
            protectedArtifact.EncryptionKeyVersion,
            protectedArtifact.FormatVersion,
            protectedArtifact.AvailableAtUtc,
            protectedArtifact.ExpiresAtUtc).IsSuccess);

        ProtectedTenantTerminationExportArtifactReader applicationReader =
            new(fixture.Reader);
        DataRightsExportDownload download = await applicationReader
            .OpenVerifiedAsync(artifact, CancellationToken.None);
        await using Stream verifiedContent = download.Content;
        Assert.Equal(verifiedContent.Length, download.ContentLength);
        Assert.Equal(
            $"bunkfy-tenant-termination-export-{artifact.Id:N}.zip",
            download.FileName);
        using ZipArchive verifiedArchive = new(
            verifiedContent,
            ZipArchiveMode.Read);
        Assert.Equal(3, verifiedArchive.Entries.Count);
    }

    [Fact]
    public async Task Generator_rejects_tampered_fragment_without_final_object()
    {
        TestFixture fixture = new();
        TenantTerminationExportFragment fragment =
            await fixture.CreateFragmentAsync(
                "workspaces",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
                /*lang=json,strict*/
                """{"owner":"workspaces","records":[]}""",
                proofRevision: 11);
        TenantTerminationExportArtifact artifact =
            TestFixture.CreateGeneratingArtifact([fragment]);
        fixture.Storage.Tamper(fragment.StorageKey!);

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => fixture.Generator.GenerateAsync(
                    artifact,
                    [fragment],
                    CancellationToken.None));

        Assert.Equal("artifact-authentication-failed", exception.Code);
        Assert.False(fixture.Storage.Contains(
            DataRightsExportStorageKey.CreateTenantTerminationArtifact(
                TenantId,
                ProcessId,
                artifact.Id).Value));
    }

    [Fact]
    public async Task Generator_rejects_fragment_bound_to_another_owner()
    {
        TestFixture fixture = new();
        TenantTerminationExportFragment fragment =
            await fixture.CreateFragmentAsync(
                "inventory",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
                /*lang=json,strict*/
                """{"owner":"inventory","records":[]}""",
                proofRevision: 11,
                protectionOwnerKey: "workspaces");
        TenantTerminationExportArtifact artifact =
            TestFixture.CreateGeneratingArtifact([fragment]);

        DataRightsExportGenerationException exception =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => fixture.Generator.GenerateAsync(
                    artifact,
                    [fragment],
                    CancellationToken.None));

        Assert.Equal("artifact-authentication-failed", exception.Code);
        Assert.False(fixture.Storage.Contains(
            DataRightsExportStorageKey.CreateTenantTerminationArtifact(
                TenantId,
                ProcessId,
                artifact.Id).Value));
    }

    [Fact]
    public async Task Tenant_artifact_envelope_rejects_other_export_purposes()
    {
        IOptions<DataRightsExportArtifactOptions> options =
            Options.Create(OptionsValue());
        AesGcmDataRightsExportEnvelopeProtector protector = new(options);
        byte[] content = RandomNumberGenerator.GetBytes(20_000);
        await using MemoryStream plaintext = new(content, writable: false);
        await using MemoryStream encrypted = new();
        _ = await protector.ProtectAsync(
            plaintext,
            content.Length,
            encrypted,
            ArtifactContext(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                generationAttempt: 1,
                fragmentCount: 1,
                FrozenSha),
            CancellationToken.None);

        encrypted.Position = 0;
        await using MemoryStream fragmentOutput = new();
        DataRightsExportGenerationException fragmentFailure =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => protector.UnprotectAsync(
                    encrypted,
                    fragmentOutput,
                    FragmentContext(
                        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                        "workspaces",
                        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                        generationAttempt: 1),
                    CancellationToken.None));
        Assert.Equal("artifact-envelope-invalid", fragmentFailure.Code);

        encrypted.Position = 0;
        await using MemoryStream subjectOutput = new();
        DataRightsExportGenerationException subjectFailure =
            await Assert.ThrowsAsync<DataRightsExportGenerationException>(
                () => protector.UnprotectAsync(
                    encrypted,
                    subjectOutput,
                    new DataRightsSubjectExportProtectionContext(
                        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                        TenantId,
                        CaseId,
                        DataRightsCaseType.StaffRights,
                        PropertyId: null,
                        DecisionRevision: 7,
                        ArtifactExpiresAt),
                    CancellationToken.None));
        Assert.Equal("artifact-envelope-invalid", subjectFailure.Code);
        CryptographicOperations.ZeroMemory(content);
    }

    private static async Task<string> ReadEntryAsync(
        ZipArchive archive,
        string name)
    {
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => candidate.FullName == name);
        await using Stream content = entry.Open();
        using StreamReader reader = new(content, Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    private static DataRightsProtectedExportReadRequest ReadRequest(
        TenantTerminationExportArtifact artifact,
        TenantTerminationProtectedExportArtifact protectedArtifact) => new(
        protectedArtifact.StorageKey,
        protectedArtifact.EncryptedByteLength,
        protectedArtifact.PlaintextSha256,
        protectedArtifact.EncryptionKeyVersion,
        protectedArtifact.FormatVersion,
        ArtifactContext(
            artifact.Id,
            artifact.GenerationRunId!.Value,
            artifact.GenerationAttempt!.Value,
            artifact.ExpectedFragmentCount,
            artifact.FragmentSetSha256));

    private static TenantTerminationExportArtifactProtectionContext
        ArtifactContext(
            Guid artifactId,
            Guid generationRunId,
            int generationAttempt,
            int fragmentCount,
            string fragmentSetSha256) => new(
        artifactId,
        TenantId,
        ProcessId,
        CaseId,
        ApprovalRevision: 7,
        FreezeOperationRevision: 1,
        ExportOperationRevision: 2,
        TerminationEpoch,
        FrozenSha,
        PolicySha,
        fragmentCount,
        fragmentSetSha256,
        generationRunId,
        generationAttempt,
        ArtifactExpiresAt);

    private static TenantTerminationExportFragmentProtectionContext
        FragmentContext(
            Guid fragmentId,
            string ownerKey,
            Guid generationRunId,
            int generationAttempt) => new(
        fragmentId,
        TenantId,
        ProcessId,
        CaseId,
        ApprovalRevision: 7,
        FreezeOperationRevision: 1,
        ExportOperationRevision: 2,
        TerminationEpoch,
        ownerKey,
        OwnerContractVersion: 1,
        CatalogVersion: 1,
        CatalogSha,
        FrozenSha,
        PolicySha,
        generationRunId,
        generationAttempt,
        FragmentExpiresAt);

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

    private sealed class TestFixture
    {
        private readonly IOptions<DataRightsExportArtifactOptions> options =
            Options.Create(OptionsValue());
        private readonly AesGcmDataRightsExportEnvelopeProtector protector;

        public TestFixture()
        {
            this.protector = new(this.options);
            this.Storage = new();
            this.Reader = new(
                this.protector,
                this.Storage,
                this.options);
            this.Generator = new(
                this.Reader,
                new ProtectedDataRightsExportObjectWriter(
                    this.protector,
                    this.Storage,
                    this.options,
                    new TestClock(Now.AddMinutes(5))),
                this.options);
        }

        public InMemoryFileStorage Storage { get; }
        public ProtectedDataRightsExportObjectReader Reader { get; }
        public ProtectedTenantTerminationExportArtifactGenerator Generator
        {
            get;
        }

        public async Task<TenantTerminationExportFragment>
            CreateFragmentAsync(
                string ownerKey,
                Guid fragmentId,
                Guid generationRunId,
                string content,
                long proofRevision,
                string? protectionOwnerKey = null)
        {
            TenantTerminationExportFragment fragment =
                TenantTerminationExportFragment.Request(
                    fragmentId,
                    TenantId,
                    ProcessId,
                    CaseId,
                    approvalRevision: 7,
                    freezeOperationRevision: 1,
                    exportOperationRevision: 2,
                    TerminationEpoch,
                    Guid.NewGuid(),
                    ownerKey,
                    ownerContractVersion: 1,
                    catalogVersion: 1,
                    CatalogSha,
                    FrozenSha,
                    PolicySha,
                    Now,
                    FragmentExpiresAt).Value;
            Assert.True(fragment.BeginGeneration(
                generationRunId,
                attempt: 1,
                Now.AddMinutes(1)).IsSuccess);

            byte[] bytes = Encoding.UTF8.GetBytes(content);
            byte[] hash = SHA256.HashData(bytes);
            try
            {
                await using MemoryStream plaintext = new(
                    bytes,
                    writable: false);
                ProtectedDataRightsExportObjectWriter writer = new(
                    this.protector,
                    this.Storage,
                    this.options,
                    new TestClock(Now.AddMinutes(2)));
                DataRightsProtectedExportObject protectedObject =
                    await writer.WriteAsync(
                        plaintext,
                        bytes.Length,
                        hash,
                        DataRightsExportStorageKey
                            .CreateTenantTerminationFragment(
                                TenantId,
                                ProcessId,
                                fragmentId),
                        FragmentContext(
                            fragmentId,
                            protectionOwnerKey ?? ownerKey,
                            generationRunId,
                            generationAttempt: 1),
                        "bftxf-1",
                        CancellationToken.None);
                Assert.True(fragment.MarkAvailable(
                    generationRunId,
                    attempt: 1,
                    recordCount: 1,
                    proofRevision,
                    proofRevision,
                    $"{ownerKey}.tenant-export.completed",
                    catalogVersion: 1,
                    CatalogSha,
                    protectedObject.StorageKey,
                    protectedObject.EncryptedByteLength,
                    protectedObject.PlaintextSha256,
                    protectedObject.EncryptionKeyVersion,
                    protectedObject.FormatVersion,
                    protectedObject.AvailableAtUtc,
                    protectedObject.ExpiresAtUtc).IsSuccess);
                return fragment;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                CryptographicOperations.ZeroMemory(hash);
            }
        }

        public static TenantTerminationExportArtifact CreateGeneratingArtifact(
            IReadOnlyCollection<TenantTerminationExportFragment> fragments)
        {
            List<TenantTerminationExportFragmentManifestEntry> entries = [];
            foreach (TenantTerminationExportFragment fragment in fragments)
            {
                Assert.True(
                    TenantTerminationExportFragmentManifestEntry.TryCreate(
                        fragment,
                        out TenantTerminationExportFragmentManifestEntry? entry));
                entries.Add(entry);
            }

            string fragmentSetSha256 =
                TenantTerminationExportFragmentSet.ComputeSha256(
                    new(
                        TenantId,
                        ProcessId,
                        CaseId,
                        ApprovalRevision: 7,
                        ExportOperationRevision: 2,
                        TerminationEpoch,
                        PolicySha),
                    entries);
            TenantTerminationExportArtifact artifact =
                TenantTerminationExportArtifact.Request(
                    Guid.NewGuid(),
                    TenantId,
                    ProcessId,
                    CaseId,
                    approvalRevision: 7,
                    freezeOperationRevision: 1,
                    exportOperationRevision: 2,
                    TerminationEpoch,
                    Guid.NewGuid(),
                    FrozenSha,
                    PolicySha,
                    fragments.Count,
                    fragmentSetSha256,
                    Now.AddMinutes(3),
                    ArtifactExpiresAt).Value;
            Assert.True(artifact.BeginGeneration(
                Guid.NewGuid(),
                attempt: 1,
                Now.AddMinutes(4)).IsSuccess);
            return artifact;
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, StoredObject> objects =
            new(StringComparer.Ordinal);

        public async Task<FileStorageObjectProperties> PutAsync(
            FileStorageWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            await using MemoryStream copy = new();
            await request.Content.CopyToAsync(copy, cancellationToken);
            FileStorageObjectProperties properties = new(
                request.Key,
                request.ContentLength,
                request.ContentType,
                request.FileName,
                ETag: Guid.NewGuid().ToString("N"),
                LastModifiedUtc: Now,
                request.Metadata);
            this.objects[request.Key.Value] = new(copy.ToArray(), properties);
            return properties;
        }

        public Task<FileStorageReadResult?> OpenReadAsync(
            FileStorageObjectKey key,
            CancellationToken cancellationToken = default)
        {
            if (!this.objects.TryGetValue(
                    key.Value,
                    out StoredObject? stored))
            {
                return Task.FromResult<FileStorageReadResult?>(null);
            }

            return Task.FromResult<FileStorageReadResult?>(new(
                stored.Properties,
                async (destination, token) =>
                    await destination.WriteAsync(stored.Content, token)));
        }

        public Task<FileStorageObjectProperties?> GetPropertiesAsync(
            FileStorageObjectKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                this.objects.TryGetValue(key.Value, out StoredObject? stored)
                    ? stored.Properties
                    : null);

        public Task<bool> DeleteAsync(
            FileStorageObjectKey key,
            CancellationToken cancellationToken = default)
        {
            if (!this.objects.Remove(key.Value, out StoredObject? stored))
            {
                return Task.FromResult(false);
            }

            CryptographicOperations.ZeroMemory(stored.Content);
            return Task.FromResult(true);
        }

        public bool Contains(string key) => this.objects.ContainsKey(key);

        public byte[] GetContent(string key) => this.objects[key].Content;

        public FileStorageObjectProperties GetProperties(string key) =>
            this.objects[key].Properties;

        public void Tamper(string key) => this.objects[key].Content[^1] ^= 0xff;

        private sealed record StoredObject(
            byte[] Content,
            FileStorageObjectProperties Properties);
    }
}
