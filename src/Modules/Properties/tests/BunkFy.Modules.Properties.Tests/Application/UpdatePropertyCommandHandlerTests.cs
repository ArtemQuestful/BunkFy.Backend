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
public sealed class UpdatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Material_update_locks_reads_checks_code_and_records_receipt()
    {
        List<string> sequence = [];
        Property property = CreateProperty();
        RecordingPropertyRepository properties = new(property, sequence);
        RecordingMutationOperationRepository operations = new(sequence: sequence);
        RecordingOperationLock operationLock = new(sequence);
        RecordingUniqueCoordinateLock uniqueCoordinates = new(sequence);
        Guid operationId = Guid.NewGuid();
        RecordingIdGenerator ids = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            properties,
            operations,
            operationLock,
            uniqueCoordinates,
            ids).HandleAsync(
                UpdateCommand(operationId),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal("updated-house", property.Code.Value);
        Assert.Equal(
            [
                "property-lock",
                "property-read",
                "operation-read",
                "code-lock",
                "code-exists",
                "operation-add"
            ],
            sequence);
        PropertyMutationOperationRecord operation =
            Assert.Single(operations.Added);
        Assert.Equal(operationId, operation.OperationId);
        Assert.Equal(PropertyMutationKind.DetailsUpdate, operation.Kind);
        Assert.Equal(1, operation.ExpectedVersion);
        Assert.Equal(2, operation.ResultVersion);
        Assert.Equal(Now, operation.CompletedAtUtc);
        Assert.Equal(1, ids.Calls);
        Assert.Single(property.DomainEvents);
    }

    [Fact]
    public async Task Normalized_retry_returns_immutable_receipt_without_work()
    {
        Guid operationId = Guid.NewGuid();
        Property property = CreateProperty();
        PropertyDetails updated = UpdatedDetails();
        Assert.True(property.UpdateDetails(
            updated,
            property.Version,
            Guid.NewGuid(),
            Now).IsSuccess);
        property.ClearDomainEvents();
        PropertyMutationOperationRecord existing = OperationRecord(
            property.Id,
            operationId,
            expectedVersion: 1,
            updated,
            resultVersion: 2);
        Assert.True(property.RegisterRoom(property.Version).IsSuccess);
        RecordingMutationOperationRepository operations = new(existing);
        RecordingUniqueCoordinateLock uniqueCoordinates = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            uniqueCoordinates,
            new ThrowingIdGenerator()).HandleAsync(
                new UpdatePropertyCommand(
                    property.Id,
                    operationId,
                    "  Updated House  ",
                    " UPDATED-HOUSE ",
                    " Etc/UTC ",
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal(3, property.Version);
        Assert.Empty(uniqueCoordinates.PropertyCodeAcquisitions);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Changed_reuse_of_operation_conflicts()
    {
        Guid operationId = Guid.NewGuid();
        Property property = CreateProperty();
        PropertyDetails updated = UpdatedDetails();
        RecordingMutationOperationRepository operations = new(
            OperationRecord(
                property.Id,
                operationId,
                expectedVersion: 1,
                updated,
                resultVersion: 2));

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                UpdateCommand(operationId) with
                {
                    Name = "Different House"
                },
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            result.Error);
        Assert.Empty(operations.Added);
        Assert.Equal(1, property.Version);
    }

    [Fact]
    public async Task Invalid_operation_is_rejected_before_locking()
    {
        RecordingOperationLock operationLock = new();
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(CreateProperty()),
            operations,
            operationLock,
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                UpdateCommand(Guid.Empty),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationInvalid,
            result.Error);
        Assert.Empty(operationLock.PropertyAcquisitions);
        Assert.Equal(0, operations.Reads);
    }

    [Fact]
    public async Task No_change_update_records_receipt_without_version_or_event()
    {
        Property property = CreateProperty();
        property.ClearDomainEvents();
        RecordingMutationOperationRepository operations = new();
        RecordingUniqueCoordinateLock uniqueCoordinates = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            uniqueCoordinates,
            new ThrowingIdGenerator()).HandleAsync(
                new UpdatePropertyCommand(
                    property.Id,
                    Guid.NewGuid(),
                    "  Hostel One  ",
                    " HOSTEL-ONE ",
                    " Etc/UTC ",
                    property.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(1, property.Version);
        Assert.Empty(property.DomainEvents);
        Assert.Empty(uniqueCoordinates.PropertyCodeAcquisitions);
        Assert.Equal(1, Assert.Single(operations.Added).ResultVersion);
    }

    [Fact]
    public async Task Failed_code_conflict_does_not_bind_or_mutate_operation()
    {
        Property property = CreateProperty();
        RecordingPropertyRepository properties = new(property)
        {
            CodeExists = true
        };
        RecordingMutationOperationRepository operations = new();
        UpdatePropertyCommandHandler handler = CreateHandler(
            properties,
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new RecordingIdGenerator());
        Guid operationId = Guid.NewGuid();

        Result<PropertyMutationReceiptDto> failed = await handler.HandleAsync(
            UpdateCommand(operationId),
            CancellationToken.None);
        properties.CodeExists = false;
        Result<PropertyMutationReceiptDto> retry = await handler.HandleAsync(
            UpdateCommand(operationId),
            CancellationToken.None);

        Assert.Equal(
            PropertiesDomainErrors.PropertyCodeAlreadyExists,
            failed.Error);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, property.Version);
        Assert.Single(operations.Added);
        Assert.Single(property.DomainEvents);
    }

    [Fact]
    public async Task Stale_no_change_request_does_not_become_a_success()
    {
        Property property = CreateProperty();
        PropertyDetails updated = UpdatedDetails();
        Assert.True(property.UpdateDetails(
            updated,
            property.Version,
            Guid.NewGuid(),
            Now).IsSuccess);
        property.ClearDomainEvents();
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                UpdateCommand(Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(PropertiesDomainErrors.VersionConflict, result.Error);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Generic_update_preserves_the_exact_current_legacy_zone()
    {
        Property property = CreatePropertyWithPersistedTimeZone(
            "Pacific Standard Time");
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new RecordingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Updated House",
                    "updated-house",
                    " Pacific Standard Time ",
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Pacific Standard Time", property.TimeZoneId.Value);
        Assert.Equal("Updated House", property.Name.Value);
        Assert.Equal(2, property.Version);
        Assert.Single(operations.Added);
    }

    [Fact]
    public async Task Omitted_time_zone_updates_details_and_preserves_current_primary()
    {
        Property property = CreateProperty();
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new RecordingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Updated House",
                    "updated-house",
                    TimeZoneId: null,
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal("Updated House", property.Name.Value);
        Assert.Equal(2, property.Version);
        Assert.Single(operations.Added);
    }

    [Fact]
    public async Task Omitted_time_zone_updates_details_and_preserves_current_legacy_value()
    {
        Property property = CreatePropertyWithPersistedTimeZone(
            "Pacific Standard Time");
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new RecordingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Updated House",
                    "updated-house",
                    TimeZoneId: null,
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Pacific Standard Time", property.TimeZoneId.Value);
        Assert.Equal(2, property.Version);
        Assert.Single(operations.Added);
    }

    [Fact]
    public async Task Omitted_time_zone_retry_is_independent_of_current_property_state()
    {
        Property property = CreateProperty();
        Guid operationId = Guid.NewGuid();
        PropertyDetails originalDetails = UpdatedDetails();
        PropertyMutationOperationRecord committed =
            PropertyMutationOperationRecord.ForProperty(
                operationId,
                TestScopeContext.TenantId,
                property.Id,
                PropertyMutationKind.DetailsUpdate,
                expectedVersion: 1,
                PropertyDetailsUpdateFingerprint.ComputeV3(
                    property.Id,
                    expectedVersion: 1,
                    originalDetails,
                    requestedTimeZoneId: null),
                new PropertyMutationReceiptDto(
                    property.Id,
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Unconfigured,
                    Version: 2),
                Now);
        Assert.True(property.SetTimeZone(
            PropertyTimeZoneId.Create("Europe/London").Value,
            property.Version,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(property.Retire(
            property.Version,
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        property.ClearDomainEvents();
        RecordingMutationOperationRepository operations = new(committed);

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    operationId,
                    " Updated House ",
                    " UPDATED-HOUSE ",
                    TimeZoneId: null,
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal("Europe/London", property.TimeZoneId.Value);
        Assert.Equal(PropertyState.Retired, property.Status);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Omitted_and_supplied_time_zone_requests_do_not_share_an_operation()
    {
        Property property = CreateProperty();
        Guid operationId = Guid.NewGuid();
        RecordingMutationOperationRepository operations = new(
            OperationRecord(
                property.Id,
                operationId,
                expectedVersion: 1,
                UpdatedDetails(),
                resultVersion: 2));

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    operationId,
                    "Updated House",
                    "updated-house",
                    TimeZoneId: null,
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            result.Error);
    }

    [Fact]
    public async Task Generic_update_does_not_canonicalize_the_current_alias()
    {
        Property property = CreatePropertyWithPersistedTimeZone("UTC");
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Hostel One",
                    "hostel-one",
                    "Etc/UTC",
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeZoneDedicatedOperationRequired,
            result.Error);
        Assert.Equal("UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Version_one_fingerprint_replays_after_the_v2_upgrade()
    {
        Property property = CreateProperty();
        Guid operationId = Guid.NewGuid();
        PropertyDetails updated = UpdatedDetails();
        PropertyMutationOperationRecord legacy =
            PropertyMutationOperationRecord.ForProperty(
                operationId,
                TestScopeContext.TenantId,
                property.Id,
                PropertyMutationKind.DetailsUpdate,
                expectedVersion: 1,
                PropertyDetailsUpdateFingerprint.ComputeV1(
                    property.Id,
                    expectedVersion: 1,
                    updated,
                    updated.TimeZoneId.Value),
                new PropertyMutationReceiptDto(
                    property.Id,
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Unconfigured,
                    Version: 2),
                Now);
        RecordingMutationOperationRepository operations = new(legacy);

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                UpdateCommand(operationId),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal(1, property.Version);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Version_two_canonical_alias_fingerprint_replays_after_v3()
    {
        Property property = CreatePropertyWithPersistedTimeZone("UTC");
        Guid operationId = Guid.NewGuid();
        PropertyDetails updated = PropertyDetails.RestorePersistedTimeZone(
            "Updated House",
            "updated-house",
            "UTC").Value;
        string versionTwoFingerprint = Assert.IsType<string>(
            PropertyDetailsUpdateFingerprint.ComputeV2(
                property.Id,
                expectedVersion: 1,
                updated));
        PropertyMutationOperationRecord committed =
            PropertyMutationOperationRecord.ForProperty(
                operationId,
                TestScopeContext.TenantId,
                property.Id,
                PropertyMutationKind.DetailsUpdate,
                expectedVersion: 1,
                versionTwoFingerprint,
                new PropertyMutationReceiptDto(
                    property.Id,
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Unconfigured,
                    Version: 2),
                Now);
        RecordingMutationOperationRepository operations = new(committed);

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    operationId,
                    "Updated House",
                    "updated-house",
                    " UTC ",
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal(1, property.Version);
        Assert.Empty(operations.Added);
    }

    [Fact]
    public async Task Version_one_alias_fingerprint_replays_after_the_v2_upgrade()
    {
        Property property = CreatePropertyWithPersistedTimeZone("UTC");
        Guid operationId = Guid.NewGuid();
        string legacyFingerprint = PropertiesMutationFingerprint.Compute(
            "bunkfy-properties-details-update/v1",
            property.Id.ToString("N"),
            "1",
            "Updated House",
            "updated-house",
            "UTC");
        PropertyMutationOperationRecord legacy =
            PropertyMutationOperationRecord.ForProperty(
                operationId,
                TestScopeContext.TenantId,
                property.Id,
                PropertyMutationKind.DetailsUpdate,
                expectedVersion: 1,
                legacyFingerprint,
                new PropertyMutationReceiptDto(
                    property.Id,
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Unconfigured,
                    Version: 2),
                Now);
        RecordingMutationOperationRepository operations = new(legacy);

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator()).HandleAsync(
                new UpdatePropertyCommand(
                    property.Id,
                    operationId,
                    "Updated House",
                    "updated-house",
                    " UTC ",
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal("UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Details_update_fails_closed_on_an_invalid_server_clock()
    {
        Property property = CreateProperty();
        RecordingMutationOperationRepository operations = new();

        Result<PropertyMutationReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operations,
            new RecordingOperationLock(),
            new RecordingUniqueCoordinateLock(),
            new ThrowingIdGenerator(),
            new FixedClock(default)).HandleAsync(
                UpdateCommand(Guid.NewGuid()),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
        Assert.Equal("Hostel One", property.Name.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(operations.Added);
        Assert.Empty(property.DomainEvents);
    }

    private static UpdatePropertyCommandHandler CreateHandler(
        RecordingPropertyRepository properties,
        RecordingMutationOperationRepository operations,
        RecordingOperationLock operationLock,
        RecordingUniqueCoordinateLock uniqueCoordinates,
        IIdGenerator ids,
        ISystemClock? clock = null)
    {
        TestScopeContext scopeContext = new();
        PropertiesMutationCoordinator mutations =
            PropertiesMutationTestSupport.Create(
                properties: properties,
                operationLock: operationLock,
                uniqueCoordinates: uniqueCoordinates,
                scopeContext: scopeContext);
        PropertyDetailsUpdateCoordinator updates = new(
            properties,
            new PropertyMutationOperationJournal(operations),
            mutations,
            clock ?? new TestClock(),
            ids);
        return new(mutations, updates);
    }

    private static UpdatePropertyCommand UpdateCommand(Guid operationId) =>
        new(
            PropertyId,
            operationId,
            "Updated House",
            "updated-house",
            "Etc/UTC",
            ExpectedVersion: 1);

    private static PropertyDetails UpdatedDetails() =>
        PropertyDetails.Create(
            "Updated House",
            "updated-house",
            "Etc/UTC").Value;

    private static PropertyMutationOperationRecord OperationRecord(
        Guid propertyId,
        Guid operationId,
        long expectedVersion,
        PropertyDetails details,
        long resultVersion) => PropertyMutationOperationRecord.ForProperty(
            operationId,
            TestScopeContext.TenantId,
            propertyId,
            PropertyMutationKind.DetailsUpdate,
            expectedVersion,
            PropertyDetailsUpdateFingerprint.ComputeV3(
                propertyId,
                expectedVersion,
                details,
                details.TimeZoneId.Value),
            new PropertyMutationReceiptDto(
                propertyId,
                PropertyStatus.Active,
                PropertyProcessingStatus.Unconfigured,
                resultVersion),
            Now);

    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static Property CreateProperty()
    {
        Property property = Property.Create(
            PropertyId,
            TestScopeContext.TenantId,
            "Hostel One",
            "hostel-one",
            "UTC",
            Guid.NewGuid(),
            Now.AddDays(-1)).Value;
        property.ClearDomainEvents();
        return property;
    }

    private static Property CreatePropertyWithPersistedTimeZone(
        string timeZoneId)
    {
        Property property = LegacyPropertyTestFactory.Create(
            PropertyId,
            TestScopeContext.TenantId,
            "Hostel One",
            "hostel-one",
            timeZoneId,
            Now.AddDays(-1));
        return property;
    }

    private sealed class RecordingPropertyRepository(
        Property property,
        List<string>? sequence = null)
        : IPropertyRepository
    {
        public bool CodeExists { get; set; }

        public Task AddAsync(
            Property value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Property?> GetAsync(
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            sequence?.Add("property-read");
            return Task.FromResult(
                property.Id == propertyId ? property : null);
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

    private sealed class RecordingMutationOperationRepository(
        PropertyMutationOperationRecord? existing = null,
        List<string>? sequence = null)
        : IPropertyMutationOperationRepository
    {
        private PropertyMutationOperationRecord? current = existing;

        public List<PropertyMutationOperationRecord> Added { get; } = [];
        public int Reads { get; private set; }

        public Task<PropertyMutationOperationRecord?> GetAsync(
            PropertyMutationResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            sequence?.Add("operation-read");
            this.Reads++;
            return Task.FromResult(
                this.current is { } operation &&
                operation.ResourceKind == resourceKind &&
                operation.ResourceId == resourceId &&
                operation.OperationId == operationId
                    ? operation
                    : null);
        }

        public Task AddAsync(
            PropertyMutationOperationRecord operation,
            CancellationToken cancellationToken)
        {
            sequence?.Add("operation-add");
            this.Added.Add(operation);
            this.current = operation;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOperationLock(
        List<string>? sequence = null)
        : IPropertiesOperationLock
    {
        public List<(string TenantId, Guid PropertyId)>
            PropertyAcquisitions
        { get; } = [];

        public Task<bool> TryAcquirePropertyAsync(
            string tenantId,
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            sequence?.Add("property-lock");
            this.PropertyAcquisitions.Add((tenantId, propertyId));
            return Task.FromResult(true);
        }

        public Task<bool> TryAcquireRoomAsync(
            string tenantId,
            Guid roomId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingUniqueCoordinateLock(
        List<string>? sequence = null)
        : IPropertiesUniqueCoordinateLock
    {
        public List<(string TenantId, string Code)>
            PropertyCodeAcquisitions
        { get; } = [];

        public Task AcquirePropertyCodeAsync(
            string tenantId,
            string propertyCode,
            CancellationToken cancellationToken)
        {
            sequence?.Add("code-lock");
            this.PropertyCodeAcquisitions.Add((tenantId, propertyCode));
            return Task.CompletedTask;
        }

        public Task AcquireRoomNameAsync(
            string tenantId,
            Guid propertyId,
            string roomName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

    private sealed class FixedClock(DateTimeOffset value) : ISystemClock
    {
        public DateTimeOffset UtcNow => value;
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
            "A replay or no-change update must not allocate an event id.");
    }
}
