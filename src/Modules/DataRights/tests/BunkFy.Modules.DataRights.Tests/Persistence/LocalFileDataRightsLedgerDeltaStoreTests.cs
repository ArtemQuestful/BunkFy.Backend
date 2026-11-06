namespace BunkFy.Modules.DataRights.Tests.Persistence;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class LocalFileDataRightsLedgerDeltaStoreTests
{
    [Fact]
    public async Task Append_is_idempotent_and_tenant_reads_are_bounded()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        Guid firstRecordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry firstLedger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                firstRecordId);
        DataRightsLedgerDelta first = ProtectedLedgerTestData.CreateDelta(
            fixture.Protector,
            firstLedger,
            firstRecordId);
        Guid secondRecordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry secondLedger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 2,
                firstLedger.EntrySha256,
                secondRecordId);
        DataRightsLedgerDelta second = ProtectedLedgerTestData.CreateDelta(
            fixture.Protector,
            secondLedger,
            secondRecordId);

        DataRightsLedgerDeltaAppendReceipt firstReceipt =
            await fixture.Store.AppendAsync(first, CancellationToken.None);
        DataRightsLedgerDeltaAppendReceipt replayReceipt =
            await fixture.Store.AppendAsync(first, CancellationToken.None);
        _ = await fixture.Store.AppendAsync(
            second,
            CancellationToken.None);
        DataRightsLedgerDeltaPage firstPage =
            await fixture.Store.ReadAfterAsync(
                "tenant-a",
                DataRightsLedgerDeltaCursor.Genesis,
                pageSize: 1,
                CancellationToken.None);
        DataRightsLedgerDeltaPage secondPage =
            await fixture.Store.ReadAfterAsync(
                "tenant-a",
                firstPage.NextCursor,
                pageSize: 1,
                CancellationToken.None);
        DataRightsLedgerDeltaCheckpoint checkpoint =
            await fixture.Store.ReadTrustedCheckpointAsync(
                "tenant-a",
                CancellationToken.None);

        Assert.Equal(firstReceipt, replayReceipt);
        Assert.Single(firstPage.Deltas);
        Assert.True(firstPage.HasMore);
        Assert.Single(secondPage.Deltas);
        Assert.False(secondPage.HasMore);
        Assert.Equal(2, checkpoint.Cursor.TenantSequence);
        Assert.Equal(secondLedger.EntrySha256, checkpoint.Cursor.EntrySha256);

        string persisted = string.Join(
            '\n',
            Directory.GetFiles(
                    directory.Path,
                    "*",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain(
            firstRecordId.ToString("N"),
            persisted,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            firstRecordId.ToString("D"),
            persisted,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_checkpoint_is_recovered_from_the_durable_chain()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                ledger,
                recordId),
            CancellationToken.None);
        string checkpointPath = Directory.GetFiles(
            directory.Path,
            "checkpoint.json",
            SearchOption.AllDirectories).Single();
        File.Delete(checkpointPath);

        DataRightsLedgerDeltaCheckpoint recovered =
            await fixture.Store.ReadTrustedCheckpointAsync(
                "tenant-a",
                CancellationToken.None);

        Assert.Equal(1, recovered.Cursor.TenantSequence);
        Assert.Equal(ledger.EntrySha256, recovered.Cursor.EntrySha256);
        Assert.True(File.Exists(checkpointPath));
    }

    [Fact]
    public async Task Legacy_version_one_record_uses_its_original_delta_bytes()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        DataRightsProcessingLedgerEntry ledger =
            CreateLegacyVersionOneLedger(fixture);
        Guid recordId =
            Guid.Parse("66666666-6666-6666-6666-666666666666");
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                ledger,
                recordId),
            CancellationToken.None);
        string recordPath = Directory.GetFiles(
            directory.Path,
            "*.delta.json",
            SearchOption.AllDirectories).Single();
        RewriteAsLegacyVersionOneRecord(recordPath);
        File.Delete(Directory.GetFiles(
            directory.Path,
            "checkpoint.json",
            SearchOption.AllDirectories).Single());

        DataRightsLedgerDeltaCheckpoint checkpoint =
            await fixture.Store.ReadTrustedCheckpointAsync(
                "tenant-a",
                CancellationToken.None);
        DataRightsLedgerDeltaPage page = await fixture.Store.ReadAfterAsync(
            "tenant-a",
            DataRightsLedgerDeltaCursor.Genesis,
            pageSize: 1,
            CancellationToken.None);

        Assert.Equal(1, checkpoint.Cursor.TenantSequence);
        Assert.Equal(ledger.EntrySha256, checkpoint.Cursor.EntrySha256);
        Assert.Equal(1, Assert.Single(page.Deltas).Ledger.ContractVersion);
    }

    [Fact]
    public async Task Legacy_version_one_record_rejects_delta_tampering()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        DataRightsProcessingLedgerEntry ledger =
            CreateLegacyVersionOneLedger(fixture);
        Guid recordId =
            Guid.Parse("66666666-6666-6666-6666-666666666666");
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                ledger,
                recordId),
            CancellationToken.None);
        string recordPath = Directory.GetFiles(
            directory.Path,
            "*.delta.json",
            SearchOption.AllDirectories).Single();
        RewriteAsLegacyVersionOneRecord(recordPath);
        JsonObject record = JsonNode.Parse(
            await File.ReadAllTextAsync(
                recordPath,
                CancellationToken.None))!.AsObject();
        record["delta"]!["ledger"]!["reasonCode"] =
            "guests.tampered";
        await File.WriteAllTextAsync(
            recordPath,
            record.ToJsonString(),
            CancellationToken.None);
        File.Delete(Directory.GetFiles(
            directory.Path,
            "checkpoint.json",
            SearchOption.AllDirectories).Single());

        DataRightsLedgerDeltaStoreException failure =
            await Assert.ThrowsAsync<DataRightsLedgerDeltaStoreException>(
                () => fixture.Store.ReadTrustedCheckpointAsync(
                    "tenant-a",
                    CancellationToken.None));

        Assert.Equal(
            LocalFileDataRightsLedgerDeltaStore.IntegrityCode,
            failure.Code);
    }

    [Fact]
    public async Task Scoped_version_three_record_round_trips_and_rejects_tampering()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateStaffLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                ledger,
                recordId),
            CancellationToken.None);

        DataRightsLedgerDeltaPage page = await fixture.Store.ReadAfterAsync(
            "tenant-a",
            DataRightsLedgerDeltaCursor.Genesis,
            pageSize: 1,
            CancellationToken.None);

        DataRightsProcessingLedgerSnapshot restored =
            Assert.Single(page.Deltas).Ledger;
        Assert.Equal(3, restored.ContractVersion);
        Assert.Equal(DataRightsCaseKind.StaffRights, restored.CaseKind);
        Assert.Equal(DataRightsCaseScopeKind.Tenant, restored.ScopeKind);
        Assert.Null(restored.RoutingPropertyId);
        Assert.Equal("GB", restored.PolicyOperatingCountryCode);
        Assert.NotNull(restored.PolicyStateBindingsJson);

        string recordPath = Directory.GetFiles(
            directory.Path,
            "*.delta.json",
            SearchOption.AllDirectories).Single();
        JsonObject persisted = JsonNode.Parse(
            await File.ReadAllTextAsync(
                recordPath,
                CancellationToken.None))!.AsObject();
        persisted["delta"]!["ledger"]!["policyOperatingCountryCode"] =
            "US";
        await File.WriteAllTextAsync(
            recordPath,
            persisted.ToJsonString(),
            CancellationToken.None);
        File.Delete(Directory.GetFiles(
            directory.Path,
            "checkpoint.json",
            SearchOption.AllDirectories).Single());

        DataRightsLedgerDeltaStoreException failure =
            await Assert.ThrowsAsync<DataRightsLedgerDeltaStoreException>(
                () => fixture.Store.ReadTrustedCheckpointAsync(
                    "tenant-a",
                    CancellationToken.None));

        Assert.Equal(
            LocalFileDataRightsLedgerDeltaStore.IntegrityCode,
            failure.Code);
    }

    [Fact]
    public async Task Conflicting_sequence_and_tampered_storage_fail_closed()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        Guid recordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry ledger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId);
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                ledger,
                recordId),
            CancellationToken.None);
        Guid conflictingRecordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry conflictingLedger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                conflictingRecordId);

        DataRightsLedgerDeltaStoreException conflict =
            await Assert.ThrowsAsync<DataRightsLedgerDeltaStoreException>(
                () => fixture.Store.AppendAsync(
                    ProtectedLedgerTestData.CreateDelta(
                        fixture.Protector,
                        conflictingLedger,
                        conflictingRecordId),
                    CancellationToken.None));
        Assert.Equal(
            LocalFileDataRightsLedgerDeltaStore.ConflictCode,
            conflict.Code);

        string recordPath = Directory.GetFiles(
            directory.Path,
            "*.delta.json",
            SearchOption.AllDirectories).Single();
        JsonObject json = JsonNode.Parse(
            await File.ReadAllTextAsync(
                recordPath,
                CancellationToken.None))!.AsObject();
        json["storageMacSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(
            recordPath,
            json.ToJsonString(),
            CancellationToken.None);

        DataRightsLedgerDeltaStoreException integrity =
            await Assert.ThrowsAsync<DataRightsLedgerDeltaStoreException>(
                () => fixture.Store.ReadTrustedCheckpointAsync(
                    "tenant-a",
                    CancellationToken.None));
        Assert.Equal(
            LocalFileDataRightsLedgerDeltaStore.IntegrityCode,
            integrity.Code);
    }

    [Fact]
    public async Task Restore_scope_snapshot_is_bounded_and_detects_later_appends()
    {
        await using TestDirectory directory = new();
        StoreFixture fixture = CreateFixture(directory.Path);
        Guid firstRecordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry firstLedger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                firstRecordId);
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                firstLedger,
                firstRecordId),
            CancellationToken.None);
        DataRightsRestoreScopeSnapshot snapshot =
            await fixture.Store.OpenSnapshotAsync(CancellationToken.None);
        DataRightsRestoreScopePage page = await fixture.Store.ReadScopesAsync(
            snapshot,
            afterScopeId: null,
            pageSize: 1,
            CancellationToken.None);

        DataRightsRestoreScope scope = Assert.Single(page.Scopes);
        Assert.Equal("tenant-a", scope.ScopeId);
        Assert.Equal(1, scope.TrustedCheckpoint.Cursor.TenantSequence);
        Assert.False(page.HasMore);
        Assert.True(await fixture.Store.IsCurrentAsync(
            snapshot,
            CancellationToken.None));

        Guid secondRecordId = Guid.NewGuid();
        DataRightsProcessingLedgerEntry secondLedger =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-b",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                secondRecordId);
        _ = await fixture.Store.AppendAsync(
            ProtectedLedgerTestData.CreateDelta(
                fixture.Protector,
                secondLedger,
                secondRecordId),
            CancellationToken.None);

        Assert.False(await fixture.Store.IsCurrentAsync(
            snapshot,
            CancellationToken.None));
        DataRightsRestoreScopeSourceException changed =
            await Assert.ThrowsAsync<DataRightsRestoreScopeSourceException>(
                () => fixture.Store.ReadScopesAsync(
                    snapshot,
                    afterScopeId: null,
                    pageSize: 1,
                    CancellationToken.None));
        Assert.Equal(
            DataRightsRestoreScopeSourceException.SnapshotChangedCode,
            changed.Code);
    }

    [Fact]
    public async Task Startup_gate_requires_an_external_production_grade_store()
    {
        DataRightsLedgerDeltaOptions options = new()
        {
            Provider = DataRightsLedgerDeltaProvider.External
        };
        ServiceCollection services = new();
        await using ServiceProvider provider =
            services.BuildServiceProvider();
        TestHostEnvironment environment = new(
            Path.GetTempPath(),
            Environments.Production);
        DataRightsLedgerDeltaStartupValidator missing =
            new(Options.Create(options), provider, environment);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => missing.StartAsync(
                CancellationToken.None));

        services.AddSingleton<IDataRightsLedgerDeltaStore>(
            new ReadinessStore(isProductionGrade: false));
        await using ServiceProvider nonProductionGrade =
            services.BuildServiceProvider();
        DataRightsLedgerDeltaStartupValidator weak =
            new(Options.Create(options), nonProductionGrade, environment);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => weak.StartAsync(
                CancellationToken.None));
    }

    [Fact]
    public async Task Missing_provider_fails_closed_with_a_stable_code()
    {
        MissingDataRightsLedgerDeltaStore store = new();

        DataRightsLedgerDeltaStoreReadiness readiness =
            await store.CheckReadinessAsync(CancellationToken.None);
        DataRightsLedgerDeltaStoreException failure =
            await Assert.ThrowsAsync<DataRightsLedgerDeltaStoreException>(
                () => store.ReadTrustedCheckpointAsync(
                    "tenant-a",
                    CancellationToken.None));

        Assert.False(readiness.IsReady);
        Assert.False(readiness.IsProductionGrade);
        Assert.Equal(
            MissingDataRightsLedgerDeltaStore.FailureCode,
            readiness.FailureCode);
        Assert.Equal(
            MissingDataRightsLedgerDeltaStore.FailureCode,
            failure.Code);
    }

    [Fact]
    public async Task Restore_startup_gate_marks_an_empty_stable_snapshot_ready()
    {
        ServiceCollection services = new();
        await using ServiceProvider provider =
            services.BuildServiceProvider();
        DateTimeOffset now =
            ProtectedLedgerTestData.Now.AddHours(1);
        DataRightsRestoreReadinessState state = new();
        DataRightsRestoreStartupGate gate = new(
            new EmptyRestoreScopeSource(),
            new ReadinessStore(isProductionGrade: true),
            provider.GetRequiredService<IServiceScopeFactory>(),
            state,
            new TestHostEnvironment(
                Path.GetTempPath(),
                Environments.Production),
            new FixedTimeProvider(now));

        await gate.StartAsync(CancellationToken.None);

        Assert.True(state.Snapshot.IsReady);
        Assert.Equal(
            "data-rights.restore.ready",
            state.Snapshot.StatusCode);
        Assert.Equal(now, state.Snapshot.LastVerifiedAtUtc);
        Assert.Equal(new string('a', 64), state.Snapshot.SnapshotSha256);
    }

    [Fact]
    public async Task Restore_startup_gate_does_not_continue_after_provider_cancels_normally()
    {
        ServiceCollection services = new();
        await using ServiceProvider provider =
            services.BuildServiceProvider();
        using CancellationTokenSource source = new();
        DataRightsRestoreReadinessState state = new();
        EmptyRestoreScopeSource scopeSource = new();
        DataRightsRestoreStartupGate gate = new(
            scopeSource,
            new ReadinessStore(
                isProductionGrade: true,
                _ => source.Cancel()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            state,
            new TestHostEnvironment(
                Path.GetTempPath(),
                Environments.Production),
            new FixedTimeProvider(ProtectedLedgerTestData.Now));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            gate.StartAsync(source.Token));

        Assert.Equal(0, scopeSource.ReadinessCheckCount);
        Assert.Equal(0, scopeSource.OpenCount);
        Assert.False(state.Snapshot.IsReady);
        Assert.Equal(
            "data-rights.restore.cancelled",
            state.Snapshot.StatusCode);
    }

    [Fact]
    public async Task Restore_startup_gate_retries_a_changed_scope_snapshot()
    {
        ServiceCollection services = new();
        await using ServiceProvider provider =
            services.BuildServiceProvider();
        DataRightsRestoreReadinessState state = new();
        ChangingRestoreScopeSource source = new();
        DataRightsRestoreStartupGate gate = new(
            source,
            new ReadinessStore(isProductionGrade: true),
            provider.GetRequiredService<IServiceScopeFactory>(),
            state,
            new TestHostEnvironment(
                Path.GetTempPath(),
                Environments.Production),
            new FixedTimeProvider(ProtectedLedgerTestData.Now));

        await gate.StartAsync(CancellationToken.None);

        Assert.Equal(2, source.OpenCount);
        Assert.True(state.Snapshot.IsReady);
        Assert.Equal(
            "data-rights.restore.ready",
            state.Snapshot.StatusCode);
    }

    [Fact]
    public void Production_configuration_rejects_the_local_provider()
    {
        DataRightsLedgerDeltaOptions options = new()
        {
            Provider = DataRightsLedgerDeltaProvider.LocalFile,
            LocalFilePath = "unused",
            ActiveIntegrityKeyVersion = 1,
            IntegrityKeys =
            {
                [1] = ProtectedLedgerTestData.Key('i')
            }
        };

        Assert.True(
            new DataRightsLedgerDeltaOptionsValidator(isProduction: true)
                .Validate(Options.DefaultName, options)
                .Failed);
    }

    private static StoreFixture CreateFixture(string path)
    {
        HmacDataRightsRecordPseudonymizer pseudonymizer =
            ProtectedLedgerTestData.CreatePseudonymizer((1, 'a'));
        AesGcmDataRightsReplayEnvelopeProtector protector =
            ProtectedLedgerTestData.CreateProtector(
                pseudonymizer,
                activeKeyVersion: 1,
                (1, 'r'));
        DataRightsLedgerDeltaOptions options = new()
        {
            Provider = DataRightsLedgerDeltaProvider.LocalFile,
            LocalFilePath = path,
            MaximumPageSize = 2,
            ActiveIntegrityKeyVersion = 1,
            IntegrityKeys =
            {
                [1] = ProtectedLedgerTestData.Key('i')
            }
        };
        LocalFileDataRightsLedgerDeltaStore store = new(
            Options.Create(options),
            new TestHostEnvironment(path, Environments.Development),
            new FixedTimeProvider(ProtectedLedgerTestData.Now));
        return new(store, pseudonymizer, protector);
    }

    private static DataRightsProcessingLedgerEntry
        CreateLegacyVersionOneLedger(StoreFixture fixture)
    {
        Guid recordId =
            Guid.Parse("66666666-6666-6666-6666-666666666666");
        DataRightsProcessingLedgerSnapshot current =
            ProtectedLedgerTestData.CreateLedger(
                fixture.Pseudonymizer,
                "tenant-a",
                sequence: 1,
                DataRightsProcessingLedgerEntry.GenesisEntrySha256,
                recordId).Freeze();
        DataRightsProcessingLedgerSnapshot unsigned = current with
        {
            ContractVersion = 1,
            ResultingRecordVersion = null,
            EntrySha256 =
                DataRightsProcessingLedgerEntry.GenesisEntrySha256
        };
        DataRightsProcessingLedgerSnapshot legacy = unsigned with
        {
            EntrySha256 = ComputeLegacyVersionOneLedgerSha256(unsigned)
        };
        return DataRightsProcessingLedgerEntry.Restore(legacy).Value;
    }

    private static void RewriteAsLegacyVersionOneRecord(string recordPath)
    {
        JsonObject record = JsonNode.Parse(
            File.ReadAllText(recordPath))!.AsObject();
        JsonObject delta = record["delta"]!.AsObject();
        _ = delta["ledger"]!.AsObject().Remove(
            "resultingRecordVersion");
        string rawDelta = delta.ToJsonString();
        string canonical = string.Join(
            '\n',
            "bunkfy.data-rights.local-ledger-delta.record.v1",
            record["storageContractVersion"]!.GetValue<int>().ToString(
                CultureInfo.InvariantCulture),
            record["integrityKeyVersion"]!.GetValue<int>().ToString(
                CultureInfo.InvariantCulture),
            record["previousStorageMacSha256"]!.GetValue<string>(),
            record["flushedAtUtc"]!.GetValue<DateTimeOffset>()
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(rawDelta)));
        byte[] key = Encoding.UTF8.GetBytes(new string('i', 32));
        byte[] digest = HMACSHA256.HashData(
            key,
            Encoding.UTF8.GetBytes(canonical));
        try
        {
            record["storageMacSha256"] =
                Convert.ToHexStringLower(digest);
            File.WriteAllText(recordPath, record.ToJsonString());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string ComputeLegacyVersionOneLedgerSha256(
        DataRightsProcessingLedgerSnapshot snapshot)
    {
        StringBuilder canonical = new();
        Append(canonical, snapshot.ContractVersion);
        Append(canonical, snapshot.EntryId.ToString("N"));
        Append(canonical, snapshot.ScopeId);
        Append(canonical, snapshot.TenantSequence);
        Append(canonical, snapshot.WorkItemId.ToString("N"));
        Append(canonical, snapshot.CaseId.ToString("N"));
        Append(canonical, snapshot.ApprovalRevision);
        Append(canonical, snapshot.OperationRevision);
        Append(canonical, (int)snapshot.Operation);
        Append(canonical, snapshot.RoutingPropertyId!.Value.ToString("N"));
        Append(canonical, snapshot.OwnerKey);
        Append(canonical, snapshot.RecordType);
        Append(canonical, snapshot.RecordPseudonymKeyVersion);
        Append(canonical, snapshot.RecordPseudonymSha256);
        Append(canonical, snapshot.DispositionCode);
        Append(canonical, snapshot.ReasonCode);
        Append(
            canonical,
            snapshot.CompletedAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, snapshot.PolicyEvidenceSchemaVersion);
        Append(canonical, snapshot.PolicyId);
        Append(canonical, snapshot.PolicyVersion);
        Append(canonical, snapshot.PolicyContentSha256);
        Append(canonical, snapshot.RetentionPolicyId);
        Append(canonical, snapshot.RetentionPolicyVersion);
        Append(canonical, snapshot.OwnerReceiptContractVersion);
        Append(canonical, snapshot.OwnerReceiptId.ToString("N"));
        Append(canonical, snapshot.OwnerReceiptSha256);
        Append(canonical, snapshot.PreviousEntrySha256);
        Append(canonical, "-");
        Append(canonical, "-");
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString()));
        try
        {
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void Append(StringBuilder target, long value) =>
        Append(
            target,
            value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder target, string value)
    {
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private sealed record StoreFixture(
        LocalFileDataRightsLedgerDeltaStore Store,
        HmacDataRightsRecordPseudonymizer Pseudonymizer,
        AesGcmDataRightsReplayEnvelopeProtector Protector);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestHostEnvironment(
        string contentRootPath,
        string environmentName)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "DataRights.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private sealed class ReadinessStore(
        bool isProductionGrade,
        Action<CancellationToken>? afterCheck = null)
        : IDataRightsLedgerDeltaStore
    {
        public Task<DataRightsLedgerDeltaStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken)
        {
            DataRightsLedgerDeltaStoreReadiness result = new(
                "external-test",
                IsReady: true,
                isProductionGrade,
                FailureCode: null);
            afterCheck?.Invoke(cancellationToken);
            return Task.FromResult(result);
        }

        public Task<DataRightsLedgerDeltaAppendReceipt> AppendAsync(
            DataRightsLedgerDelta delta,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsLedgerDeltaCheckpoint>
            ReadTrustedCheckpointAsync(
                string tenantId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsLedgerDeltaPage> ReadAfterAsync(
            string tenantId,
            DataRightsLedgerDeltaCursor cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyRestoreScopeSource
        : IDataRightsRestoreScopeSource
    {
        private static readonly DataRightsRestoreScopeSnapshot Snapshot =
            new(
                DataRightsRestoreScopeSnapshot.CurrentContractVersion,
                new string('a', 64),
                ProtectedLedgerTestData.Now);

        public int ReadinessCheckCount { get; private set; }
        public int OpenCount { get; private set; }

        public Task<DataRightsRestoreScopeSourceReadiness>
            CheckReadinessAsync(
                CancellationToken cancellationToken)
        {
            this.ReadinessCheckCount++;
            return Task.FromResult(new DataRightsRestoreScopeSourceReadiness(
                "external-test",
                IsReady: true,
                IsProductionGrade: true,
                FailureCode: null));
        }

        public Task<DataRightsRestoreScopeSnapshot> OpenSnapshotAsync(
            CancellationToken cancellationToken)
        {
            this.OpenCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<DataRightsRestoreScopePage> ReadScopesAsync(
            DataRightsRestoreScopeSnapshot snapshot,
            string? afterScopeId,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataRightsRestoreScopePage(
                DataRightsRestoreScopePage.CurrentContractVersion,
                Scopes: [],
                NextScopeId: null,
                HasMore: false));

        public Task<bool> IsCurrentAsync(
            DataRightsRestoreScopeSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot == Snapshot);
    }

    private sealed class ChangingRestoreScopeSource
        : IDataRightsRestoreScopeSource
    {
        private static readonly DataRightsRestoreScopeSnapshot Snapshot =
            new(
                DataRightsRestoreScopeSnapshot.CurrentContractVersion,
                new string('a', 64),
                ProtectedLedgerTestData.Now);

        public int OpenCount { get; private set; }

        public Task<DataRightsRestoreScopeSourceReadiness>
            CheckReadinessAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult(new DataRightsRestoreScopeSourceReadiness(
                "external-test",
                IsReady: true,
                IsProductionGrade: true,
                FailureCode: null));

        public Task<DataRightsRestoreScopeSnapshot> OpenSnapshotAsync(
            CancellationToken cancellationToken)
        {
            this.OpenCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<DataRightsRestoreScopePage> ReadScopesAsync(
            DataRightsRestoreScopeSnapshot snapshot,
            string? afterScopeId,
            int pageSize,
            CancellationToken cancellationToken) =>
            this.OpenCount == 1
                ? Task.FromException<DataRightsRestoreScopePage>(
                    new DataRightsRestoreScopeSourceException(
                        DataRightsRestoreScopeSourceException
                            .SnapshotChangedCode,
                        "The restore scope changed."))
                : Task.FromResult(new DataRightsRestoreScopePage(
                    DataRightsRestoreScopePage.CurrentContractVersion,
                    Scopes: [],
                    NextScopeId: null,
                    HasMore: false));

        public Task<bool> IsCurrentAsync(
            DataRightsRestoreScopeSnapshot snapshot,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.OpenCount > 1);
    }

    private sealed class TestDirectory : IAsyncDisposable
    {
        public TestDirectory() =>
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"bunkfy-data-rights-ledger-{Guid.NewGuid():N}");

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
