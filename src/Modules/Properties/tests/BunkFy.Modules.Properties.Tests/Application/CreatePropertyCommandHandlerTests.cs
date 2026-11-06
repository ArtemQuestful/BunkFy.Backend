namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class CreatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task New_creation_locks_reads_and_checks_code_before_adding()
    {
        List<string> sequence = [];
        RecordingPropertyRepository properties = new(sequence: sequence);
        RecordingCreationOperationLock creationLock = new(sequence);
        RecordingUniqueCoordinateLock uniqueCoordinates = new(sequence);
        RecordingTimeZoneRevisionStore revisions = new();
        Guid operationId = Guid.NewGuid();
        RecordingIdGenerator ids = new();
        CreatePropertyCommandHandler handler = CreateHandler(
            properties,
            creationLock,
            uniqueCoordinates,
            ids,
            revisions);

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            CreateCommand(operationId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(operationId, result.Value.PropertyId);
        Assert.Equal(operationId, properties.Added?.Id);
        Assert.Equal("harbour-house", properties.Added?.Code.Value);
        Assert.Equal(
            ["creation-lock", "read", "code-lock", "code-exists", "add"],
            sequence);
        Assert.Equal((TestScopeContext.TenantId, operationId),
            Assert.Single(creationLock.Acquisitions));
        Assert.Equal(2, ids.Calls);
        Assert.Single(properties.Added!.DomainEvents);
        PropertyTimeZoneRevisionWriteModel revision =
            Assert.Single(revisions.Revisions);
        Assert.Equal(PropertyTimeZoneChangeKind.Created, revision.ChangeKind);
        Assert.Equal("UTC", revision.RequestedTimeZoneId);
        Assert.Equal("Etc/UTC", revision.TimeZoneId);
        Assert.Equal("operator-1", revision.ActorId);
    }

    [Fact]
    public async Task Normalized_retry_returns_current_receipt_without_recreating()
    {
        Guid operationId = Guid.NewGuid();
        Property existing = CreateProperty(operationId);
        Assert.True(existing.Retire(
            existing.Version,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        List<string> sequence = [];
        RecordingPropertyRepository properties = new(
            existing,
            sequence: sequence);
        RecordingCreationOperationLock creationLock = new(sequence);
        RecordingUniqueCoordinateLock uniqueCoordinates = new(sequence);

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            creationLock,
            uniqueCoordinates,
            new ThrowingIdGenerator(),
            RevisionStoreFor(existing, "UTC")).HandleAsync(
                new CreatePropertyCommand(
                    operationId,
                    "  Harbour House  ",
                    " HARBOUR-HOUSE ",
                    " UTC ",
                    "operator-1"),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyStatus.Retired, result.Value.Status);
        Assert.Equal(existing.Version, result.Value.Version);
        Assert.Equal(["creation-lock", "read"], sequence);
        Assert.Null(properties.Added);
        Assert.Empty(uniqueCoordinates.PropertyCodeAcquisitions);
    }

    [Fact]
    public async Task Native_creation_retry_binds_the_normalized_raw_identifier()
    {
        Guid operationId = Guid.NewGuid();
        Property existing = CreateProperty(operationId);
        RecordingPropertyRepository properties = new(existing);
        RecordingTimeZoneRevisionStore revisions = RevisionStoreFor(
            existing,
            "UTC");

        Result<PropertyMutationReceiptDto> exact = await CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator(),
            revisions).HandleAsync(
                CreateCommand(operationId) with { TimeZoneId = " UTC " },
                CancellationToken.None);
        Result<PropertyMutationReceiptDto> differentAlias =
            await CreateHandler(
                properties,
                new RecordingCreationOperationLock(),
                new RecordingUniqueCoordinateLock(),
                new ThrowingIdGenerator(),
                revisions).HandleAsync(
                    CreateCommand(operationId) with { TimeZoneId = "UCT" },
                    CancellationToken.None);

        Assert.True(exact.IsSuccess);
        Assert.Equal(
            PropertiesApplicationErrors.CreationOperationConflict,
            differentAlias.Error);
    }

    [Fact]
    public async Task Native_creation_retry_uses_its_versioned_ledger_resolution()
    {
        Guid operationId = Guid.NewGuid();
        Property existing = CreateProperty(operationId);
        RecordingTimeZoneRevisionStore revisions = new(new(
            Guid.NewGuid(),
            existing.ScopeId,
            existing.Id,
            operationId,
            PropertyTimeZoneChangeKind.Created,
            "Retired/LegacyAlias",
            null,
            "Etc/UTC",
            "TZDB: historical-fixture",
            ExpectedVersion: 0,
            ResultVersion: 1,
            "operator-1",
            Now));

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(existing),
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator(),
            revisions).HandleAsync(
                CreateCommand(operationId) with
                {
                    TimeZoneId = " Retired/LegacyAlias "
                },
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value.PropertyId);
        Assert.Empty(revisions.Revisions);
    }

    [Fact]
    public async Task Pre_upgrade_raw_alias_exact_retry_succeeds()
    {
        Guid operationId = Guid.NewGuid();
        Property existing = LegacyPropertyTestFactory.Create(
            operationId,
            TestScopeContext.TenantId,
            "Harbour House",
            "harbour-house",
            "UTC",
            Now);
        RecordingPropertyRepository properties = new(existing);

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                CreateCommand(operationId),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("UTC", existing.TimeZoneId.Value);
        Assert.Null(properties.Added);
    }

    [Fact]
    public async Task Reusing_operation_for_changed_details_conflicts()
    {
        Guid operationId = Guid.NewGuid();
        RecordingPropertyRepository properties = new(
            CreateProperty(operationId));

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                CreateCommand(operationId) with
                {
                    Name = "Different House"
                },
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.CreationOperationConflict,
            result.Error);
        Assert.Null(properties.Added);
    }

    [Fact]
    public async Task Reusing_operation_for_materially_changed_zone_conflicts()
    {
        Guid operationId = Guid.NewGuid();
        RecordingPropertyRepository properties = new(
            CreateProperty(operationId));

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                CreateCommand(operationId) with
                {
                    TimeZoneId = "Europe/London"
                },
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.CreationOperationConflict,
            result.Error);
        Assert.Null(properties.Added);
    }

    [Fact]
    public async Task Invalid_operation_is_rejected_before_locking()
    {
        RecordingCreationOperationLock creationLock = new();
        RecordingPropertyRepository properties = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            creationLock,
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                CreateCommand(Guid.Empty),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.CreationOperationInvalid,
            result.Error);
        Assert.Empty(creationLock.Acquisitions);
        Assert.Equal(0, properties.Reads);
    }

    [Fact]
    public async Task Failed_code_conflict_does_not_bind_the_operation()
    {
        Guid operationId = Guid.NewGuid();
        RecordingPropertyRepository properties = new()
        {
            CodeExists = true
        };
        CreatePropertyCommandHandler handler = CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new RecordingIdGenerator());

        Result<PropertyMutationReceiptDto> failed = await handler.HandleAsync(
            CreateCommand(operationId),
            CancellationToken.None);
        properties.CodeExists = false;
        Result<PropertyMutationReceiptDto> retry = await handler.HandleAsync(
            CreateCommand(operationId),
            CancellationToken.None);

        Assert.Equal(PropertiesDomainErrors.PropertyCodeAlreadyExists, failed.Error);
        Assert.True(retry.IsSuccess);
        Assert.Equal(operationId, retry.Value.PropertyId);
        Assert.Equal(operationId, properties.Added?.Id);
    }

    [Fact]
    public async Task Creation_observes_runtime_and_timestamps_after_code_lock()
    {
        DateTimeOffset beforeLock =
            new(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
        DateTimeOffset afterLock =
            new(2027, 1, 1, 0, 0, 1, TimeSpan.Zero);
        var clock = new MutableClock(beforeLock);
        var coordinates = new RecordingUniqueCoordinateLock(
            onPropertyCodeAcquired: () => clock.UtcNowValue = afterLock);
        RecordingPropertyRepository properties = new();
        RecordingTimeZoneRevisionStore revisions = new();
        DateTimeOffset? observedRuntimeAt = null;
        var runtime = BunkFy.TimeZones
            .TimeZoneRuntimeCompatibilityProbe.CreateForTesting(identifier =>
            {
                observedRuntimeAt = clock.UtcNowValue;
                return TimeZoneInfo.FindSystemTimeZoneById(identifier);
            });

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            coordinates,
            new RecordingIdGenerator(),
            revisions,
            clock,
            runtime).HandleAsync(
                CreateCommand(Guid.NewGuid()),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(afterLock, observedRuntimeAt);
        Assert.Equal(afterLock, properties.Added!.CreatedAtUtc);
        Assert.Equal(afterLock, Assert.Single(revisions.Revisions).OccurredAtUtc);
    }

    [Fact]
    public async Task Creation_fails_closed_on_an_invalid_server_clock()
    {
        RecordingPropertyRepository properties = new();
        RecordingTimeZoneRevisionStore revisions = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            new RecordingCreationOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator(),
            revisions,
            new MutableClock(default)).HandleAsync(
                CreateCommand(Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
        Assert.Null(properties.Added);
        Assert.Empty(revisions.Revisions);
    }

    private static CreatePropertyCommandHandler CreateHandler(
        RecordingPropertyRepository properties,
        IPropertiesCreationOperationLock creationLock,
        RecordingUniqueCoordinateLock uniqueCoordinates,
        IIdGenerator ids,
        RecordingTimeZoneRevisionStore? revisions = null,
        ISystemClock? clock = null,
        BunkFy.TimeZones.TimeZoneRuntimeCompatibilityProbe?
            runtimeTimeZones = null)
    {
        TestScopeContext scopeContext = new();
        revisions ??= new RecordingTimeZoneRevisionStore();
        return new(
            properties,
            PropertiesMutationTestSupport.Create(
                properties: properties,
                uniqueCoordinates: uniqueCoordinates,
                scopeContext: scopeContext),
            creationLock,
            revisions,
            revisions,
            scopeContext,
            clock ?? new TestClock(),
            ids,
            runtimeTimeZones ?? Application
                .PropertyTimeZoneHealthClassifierTests.CompatibleProbe());
    }

    private static RecordingTimeZoneRevisionStore RevisionStoreFor(
        Property property,
        string requestedTimeZoneId) => new(new(
            Guid.NewGuid(),
            property.ScopeId,
            property.Id,
            property.Id,
            PropertyTimeZoneChangeKind.Created,
            requestedTimeZoneId,
            null,
            property.TimeZoneId.Value,
            BunkFy.TimeZones.TimeZoneCatalog.Default.CatalogVersion,
            0,
            1,
            "operator-1",
            Now));

    private static CreatePropertyCommand CreateCommand(Guid operationId) =>
        new(
            operationId,
            "Harbour House",
            "harbour-house",
            "UTC",
            "operator-1");

    private static Property CreateProperty(Guid propertyId) =>
        Property.Create(
            propertyId,
            TestScopeContext.TenantId,
            "Harbour House",
            "harbour-house",
            "UTC",
            Guid.NewGuid(),
            Now).Value;

    private sealed class RecordingPropertyRepository(
        Property? existing = null,
        List<string>? sequence = null)
        : IPropertyRepository
    {
        public Property? Existing { get; private set; } = existing;
        public Property? Added { get; private set; }
        public bool CodeExists { get; set; }
        public int Reads { get; private set; }

        public Task AddAsync(
            Property property,
            CancellationToken cancellationToken)
        {
            sequence?.Add("add");
            this.Added = property;
            this.Existing = property;
            return Task.CompletedTask;
        }

        public Task<Property?> GetAsync(
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            sequence?.Add("read");
            this.Reads++;
            return Task.FromResult(
                this.Existing?.Id == propertyId
                    ? this.Existing
                    : null);
        }

        public Task<bool> CodeExistsAsync(
            string code,
            Guid? excludingPropertyId,
            CancellationToken cancellationToken)
        {
            sequence?.Add("code-exists");
            return Task.FromResult(this.CodeExists);
        }
    }

    private sealed class RecordingCreationOperationLock(
        List<string>? sequence = null)
        : IPropertiesCreationOperationLock
    {
        public List<(string TenantId, Guid OperationId)> Acquisitions { get; } = [];

        public Task AcquireAsync(
            string tenantId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            sequence?.Add("creation-lock");
            this.Acquisitions.Add((tenantId, operationId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUniqueCoordinateLock(
        List<string>? sequence = null,
        Action? onPropertyCodeAcquired = null)
        : IPropertiesUniqueCoordinateLock
    {
        public List<(string TenantId, string Code)> PropertyCodeAcquisitions { get; } = [];

        public Task AcquirePropertyCodeAsync(
            string tenantId,
            string propertyCode,
            CancellationToken cancellationToken)
        {
            sequence?.Add("code-lock");
            this.PropertyCodeAcquisitions.Add((tenantId, propertyCode));
            onPropertyCodeAcquired?.Invoke();
            return Task.CompletedTask;
        }

        public Task AcquireRoomNameAsync(
            string tenantId,
            Guid propertyId,
            string roomName,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingTimeZoneRevisionStore(
        PropertyTimeZoneRevisionReadModel? existing = null)
        : IPropertyTimeZoneRevisionReader,
          IPropertyTimeZoneRevisionWriter
    {
        public List<PropertyTimeZoneRevisionWriteModel> Revisions { get; } = [];

        public Task AppendAsync(
            PropertyTimeZoneRevisionWriteModel revision,
            CancellationToken cancellationToken)
        {
            this.Revisions.Add(revision);
            return Task.CompletedTask;
        }

        public Task<PropertyTimeZoneRevisionReadModel?> GetAsync(
            Guid propertyId,
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                existing is not null &&
                existing.PropertyId == propertyId &&
                existing.OperationId == operationId
                    ? existing
                    : null);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public const string TenantId = "tenant-a";
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNowValue { get; set; } = utcNow;
        public DateTimeOffset UtcNow => this.UtcNowValue;
    }

    private sealed class RecordingIdGenerator : IIdGenerator
    {
        public int Calls { get; private set; }

        public Guid NewId()
        {
            this.Calls++;
            return Guid.NewGuid();
        }
    }

    private sealed class ThrowingIdGenerator : IIdGenerator
    {
        public Guid NewId() => throw new InvalidOperationException(
            "An exact replay must not allocate another event id.");
    }
}
