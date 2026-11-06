namespace BunkFy.Modules.Properties.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.TimeZones;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class SetPropertyTimeZoneCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Alias_canonicalization_is_audited_without_confirmation()
    {
        Property property = CreateWithPersistedTimeZone("UTC");
        RecordingRevisionStore revisions = new();
        RecordingPropertyRepository properties = new(property);
        RecordingOperationLock operationLock = new();
        SetPropertyTimeZoneCommand command = new(
            property.Id,
            Guid.NewGuid(),
            " UTC ",
            Confirmed: false,
            property.Version,
            " operator:one ");

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            properties,
            operationLock,
            revisions,
            revisions,
            new RecordingIdGenerator()).HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            PropertyTimeZoneChangeKind.Canonicalized,
            result.Value.ChangeKind);
        Assert.Equal("UTC", result.Value.RequestedTimeZoneId);
        Assert.Equal("UTC", result.Value.PreviousTimeZoneId);
        Assert.Equal("Etc/UTC", result.Value.TimeZoneId);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(2, property.Version);
        Assert.Equal("operator:one", result.Value.ActorId);
        PropertyTimeZoneRevisionWriteModel revision =
            Assert.Single(revisions.Writes);
        Assert.Equal(result.Value.OperationId, revision.OperationId);
        Assert.Equal(1, revision.ExpectedVersion);
        Assert.Equal(2, revision.ResultVersion);
        Assert.Equal(2, revisions.Reads);
        Assert.Single(operationLock.PropertyAcquisitions);
        Assert.Equal(1, properties.Reads);
    }

    [Fact]
    public async Task Alias_canonicalization_skips_semantic_country_policy_re_evaluation()
    {
        Property property = CreateWithPersistedTimeZone("UTC");
        PropertyGovernanceBinding binding = PropertyGovernanceBinding.Create(
            "GB",
            "retired-policy",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            new string('a', Property.TimeZoneIdMaxLength / 2),
            Now.AddDays(-1),
            Now.AddDays(1),
            Now.AddMinutes(-1)).Value;
        Assert.True(property.ActivateProcessing(
            binding,
            [],
            property.Version,
            Guid.NewGuid(),
            Now.AddMinutes(-1),
            "operator:original").IsSuccess);
        property.ClearDomainEvents();
        RecordingRevisionStore revisions = new();
        SetPropertyTimeZoneCommandHandler handler = CreateHandler(
            property,
            revisions,
            revisions,
            new RecordingIdGenerator());

        Result<SetPropertyTimeZoneReceiptDto> semanticChange =
            await handler.HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Europe/London",
                    Confirmed: true,
                    property.Version,
                    "operator:one"),
                CancellationToken.None);

        Result<SetPropertyTimeZoneReceiptDto> result = await handler.HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Etc/UTC",
                    Confirmed: false,
                    property.Version,
                    "operator:one"),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.CountryPolicyDenied(
                CountryPolicyDecisionReason.UnknownPolicy),
            semanticChange.Error);
        Assert.True(result.IsSuccess);
        Assert.Equal(
            PropertyTimeZoneChangeKind.Canonicalized,
            result.Value.ChangeKind);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(PropertyProcessingState.Enabled, property.ProcessingState);
    }

    [Fact]
    public async Task Semantic_change_requires_confirmation_after_domain_preconditions()
    {
        Property property = CreateWithPersistedTimeZone("Etc/UTC");
        RecordingRevisionStore revisions = new();
        SetPropertyTimeZoneCommand command = new(
            property.Id,
            Guid.NewGuid(),
            "Europe/London",
            Confirmed: false,
            property.Version,
            "operator:one");

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            property,
            revisions,
            revisions,
            new ThrowingIdGenerator()).HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ConfirmationRequired,
            result.Error);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(revisions.Writes);
    }

    [Fact]
    public async Task Stale_semantic_change_returns_version_conflict_before_confirmation()
    {
        Property property = CreateWithPersistedTimeZone("Etc/UTC");
        Assert.True(property.RegisterRoom(property.Version).IsSuccess);
        RecordingRevisionStore revisions = new();
        SetPropertyTimeZoneCommand command = new(
            property.Id,
            Guid.NewGuid(),
            "Europe/London",
            Confirmed: false,
            ExpectedVersion: 1,
            "operator:one");

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            property,
            revisions,
            revisions,
            new ThrowingIdGenerator()).HandleAsync(
                command,
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.VersionConflict,
            result.Error);
        Assert.Empty(revisions.Writes);
    }

    [Fact]
    public async Task Runtime_incompatible_current_zone_can_move_to_a_compatible_primary()
    {
        Property property = CreateWithPersistedTimeZone(
            "America/Edmonton");
        RecordingRevisionStore revisions = new();
        TimeZoneRuntimeCompatibilityProbe probe =
            TimeZoneRuntimeCompatibilityProbe.CreateForTesting(identifier =>
                identifier == "America/Edmonton"
                    ? TimeZoneInfo.CreateCustomTimeZone(
                        "Mismatched Edmonton test rules",
                        TimeSpan.FromHours(9),
                        "Mismatched Edmonton test rules",
                        "Mismatched Edmonton test rules")
                    : TimeZoneInfo.FindSystemTimeZoneById(identifier));
        PropertyTimeZoneHealth before =
            PropertyTimeZoneHealthClassifier.Classify(
                property.TimeZoneId.Value,
                correctionAllowed: true,
                Now,
                probe);

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            new RecordingOperationLock(),
            revisions,
            revisions,
            new RecordingIdGenerator(),
            probe).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Europe/London",
                    Confirmed: true,
                    property.Version,
                    "operator:one"),
                CancellationToken.None);

        Assert.Equal(
            PropertyTimeZoneStatus.RuntimeUnavailable,
            before.Status);
        Assert.True(before.CorrectionAllowed);
        Assert.True(result.IsSuccess);
        Assert.Equal("Europe/London", property.TimeZoneId.Value);
    }

    [Fact]
    public async Task Set_observes_runtime_and_timestamp_after_operation_lock()
    {
        DateTimeOffset afterLock = Now.AddYears(1);
        var clock = new MutableClock(Now);
        Property property = CreateWithPersistedTimeZone("Etc/UTC");
        RecordingRevisionStore revisions = new();
        RecordingOperationLock operationLock = new(
            () => clock.UtcNowValue = afterLock);
        DateTimeOffset? observedRuntimeAt = null;
        TimeZoneRuntimeCompatibilityProbe probe =
            TimeZoneRuntimeCompatibilityProbe.CreateForTesting(identifier =>
            {
                observedRuntimeAt = clock.UtcNowValue;
                return TimeZoneInfo.FindSystemTimeZoneById(identifier);
            });

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            operationLock,
            revisions,
            revisions,
            new RecordingIdGenerator(),
            probe,
            clock).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Europe/London",
                    Confirmed: true,
                    property.Version,
                    "operator:one"),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(afterLock, observedRuntimeAt);
        Assert.Equal(afterLock, result.Value.CompletedAtUtc);
        Assert.Equal(afterLock, property.UpdatedAtUtc);
        Assert.Equal(afterLock, Assert.Single(revisions.Writes).OccurredAtUtc);
    }

    [Fact]
    public async Task Set_fails_closed_on_an_invalid_server_clock()
    {
        Property property = CreateWithPersistedTimeZone("Etc/UTC");
        RecordingRevisionStore revisions = new();

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            new RecordingPropertyRepository(property),
            new RecordingOperationLock(),
            revisions,
            revisions,
            new ThrowingIdGenerator(),
            clock: new MutableClock(default)).HandleAsync(
                new(
                    property.Id,
                    Guid.NewGuid(),
                    "Europe/London",
                    Confirmed: true,
                    property.Version,
                    "operator:one"),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(revisions.Writes);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Exact_retry_replays_after_later_changes_and_retirement()
    {
        Property property = CreateWithPersistedTimeZone("Etc/UTC");
        Guid operationId = Guid.NewGuid();
        PropertyTimeZoneRevisionReadModel committed = new(
            Guid.NewGuid(),
            property.ScopeId,
            property.Id,
            operationId,
            PropertyTimeZoneChangeKind.Changed,
            "Europe/London",
            "Etc/UTC",
            "Europe/London",
            TimeZoneCatalog.Default.CatalogVersion,
            ExpectedVersion: 1,
            ResultVersion: 2,
            "operator:original",
            Now);
        RecordingRevisionStore revisions = new(committed);
        Assert.True(property.SetTimeZone(
            PropertyTimeZoneId.Create("Europe/London").Value,
            property.Version,
            Guid.NewGuid(),
            Now).IsSuccess);
        Assert.True(property.SetTimeZone(
            PropertyTimeZoneId.Create("Europe/Paris").Value,
            property.Version,
            Guid.NewGuid(),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(property.Retire(
            property.Version,
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        RecordingPropertyRepository properties = new(property);
        RecordingOperationLock operationLock = new();
        SetPropertyTimeZoneCommandHandler handler = CreateHandler(
            properties,
            operationLock,
            revisions,
            revisions,
            new ThrowingIdGenerator());

        Result<SetPropertyTimeZoneReceiptDto> result = await handler.HandleAsync(
            new(
                property.Id,
                operationId,
                " Europe/London ",
                Confirmed: false,
                ExpectedVersion: 1,
                "operator:different"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("operator:original", result.Value.ActorId);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal("Europe/London", result.Value.TimeZoneId);
        Assert.Equal(0, properties.Reads);
        Assert.Empty(operationLock.PropertyAcquisitions);
        Assert.Equal(1, revisions.Reads);
        Assert.Empty(revisions.Writes);
        Assert.Equal("Europe/Paris", property.TimeZoneId.Value);
        Assert.Equal(PropertyState.Retired, property.Status);
    }

    [Fact]
    public async Task Changed_reuse_conflicts_without_loading_current_property()
    {
        Property property = CreateWithPersistedTimeZone("Etc/UTC");
        Guid operationId = Guid.NewGuid();
        RecordingRevisionStore revisions = new(new(
            Guid.NewGuid(),
            property.ScopeId,
            property.Id,
            operationId,
            PropertyTimeZoneChangeKind.Changed,
            "Europe/London",
            "Etc/UTC",
            "Europe/London",
            TimeZoneCatalog.Default.CatalogVersion,
            1,
            2,
            "operator:original",
            Now));
        RecordingPropertyRepository properties = new(property);
        RecordingOperationLock operationLock = new();

        Result<SetPropertyTimeZoneReceiptDto> result = await CreateHandler(
            properties,
            operationLock,
            revisions,
            revisions,
            new ThrowingIdGenerator()).HandleAsync(
                new(
                    property.Id,
                    operationId,
                    "Europe/Paris",
                    Confirmed: true,
                    ExpectedVersion: 1,
                    "operator:one"),
                CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            result.Error);
        Assert.Equal(0, properties.Reads);
        Assert.Empty(operationLock.PropertyAcquisitions);
        Assert.Equal(1, revisions.Reads);
        Assert.Empty(revisions.Writes);
    }

    private static SetPropertyTimeZoneCommandHandler CreateHandler(
        Property property,
        IPropertyTimeZoneRevisionReader reader,
        IPropertyTimeZoneRevisionWriter writer,
        IIdGenerator ids) => CreateHandler(
            new RecordingPropertyRepository(property),
            new RecordingOperationLock(),
            reader,
            writer,
            ids);

    private static SetPropertyTimeZoneCommandHandler CreateHandler(
        RecordingPropertyRepository properties,
        RecordingOperationLock operationLock,
        IPropertyTimeZoneRevisionReader reader,
        IPropertyTimeZoneRevisionWriter writer,
        IIdGenerator ids,
        TimeZoneRuntimeCompatibilityProbe? runtimeTimeZones = null,
        ISystemClock? clock = null) => new(
            PropertiesMutationTestSupport.Create(
                properties: properties,
                operationLock: operationLock,
                scopeContext: new TestScopeContext()),
            reader,
            writer,
            CountryPolicyRegistry.Create(
                [],
                [],
                CountryPolicyRuntimeMode.Production,
                EmbeddedTzdbCountryPolicyTimeZoneRules.Instance),
            clock ?? new TestClock(),
            ids,
            runtimeTimeZones ??
                PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

    private static Property CreateWithPersistedTimeZone(string timeZoneId)
    {
        Property property = LegacyPropertyTestFactory.Create(
            Guid.NewGuid(),
            TestScopeContext.TenantId,
            "Hostel One",
            "hostel-one",
            timeZoneId,
            Now.AddDays(-1));
        return property;
    }

    private sealed class RecordingPropertyRepository(Property property)
        : IPropertyRepository
    {
        public int Reads { get; private set; }

        public Task AddAsync(
            Property value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Property?> GetAsync(
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(
                property.Id == propertyId ? property : null);
        }

        public Task<bool> CodeExistsAsync(
            string code,
            Guid? excludingPropertyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOperationLock(
        Action? onPropertyAcquired = null) : IPropertiesOperationLock
    {
        public List<(string TenantId, Guid PropertyId)> PropertyAcquisitions
        { get; } = [];

        public Task<bool> TryAcquirePropertyAsync(
            string tenantId,
            Guid propertyId,
            CancellationToken cancellationToken)
        {
            this.PropertyAcquisitions.Add((tenantId, propertyId));
            onPropertyAcquired?.Invoke();
            return Task.FromResult(true);
        }

        public Task<bool> TryAcquireRoomAsync(
            string tenantId,
            Guid roomId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRevisionStore(
        PropertyTimeZoneRevisionReadModel? existing = null)
        : IPropertyTimeZoneRevisionReader,
          IPropertyTimeZoneRevisionWriter
    {
        public List<PropertyTimeZoneRevisionWriteModel> Writes { get; } = [];
        public int Reads { get; private set; }

        public Task<PropertyTimeZoneRevisionReadModel?> GetAsync(
            Guid propertyId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(
                existing is not null &&
                existing.PropertyId == propertyId &&
                existing.OperationId == operationId
                    ? existing
                    : null);
        }

        public Task AppendAsync(
            PropertyTimeZoneRevisionWriteModel revision,
            CancellationToken cancellationToken)
        {
            this.Writes.Add(revision);
            return Task.CompletedTask;
        }
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
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class ThrowingIdGenerator : IIdGenerator
    {
        public Guid NewId() => throw new InvalidOperationException(
            "This path must not allocate an id.");
    }
}
