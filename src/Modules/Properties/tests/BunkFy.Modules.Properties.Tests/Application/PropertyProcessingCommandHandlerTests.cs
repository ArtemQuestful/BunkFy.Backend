namespace BunkFy.Modules.Properties.Tests;

using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Handlers;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyProcessingCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Activation_persists_the_exact_policy_evidence_and_append_only_revision()
    {
        Property property = CreateProperty();
        RecordingRevisionWriter revisions = new();
        CountryPolicyPackArtifact artifact = CreateArtifact();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(),
            revisions,
            CreateRegistry(artifact),
            new TestClock(),
            new TestIdGenerator());

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            new ActivatePropertyProcessingCommand(
                property.Id,
                Guid.NewGuid(),
                "GB",
                "gb-hostel",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                1,
                [],
                true,
                property.Version,
                " user:owner "),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyProcessingState.Enabled, property.ProcessingState);
        Assert.Equal(PropertyProcessingStatus.Enabled, result.Value.ProcessingStatus);
        Assert.Equal(artifact.ContentSha256, property.GovernanceBinding!.ContentSha256);
        Assert.Equal(property.Version, result.Value.Version);
        PropertyGovernanceRevisionWriteModel revision = Assert.Single(revisions.Items);
        Assert.Equal(PropertyGovernanceRevisionAction.Activated, revision.Action);
        Assert.Null(revision.Previous);
        Assert.Equal(artifact.ContentSha256, revision.Current!.ContentSha256);
        Assert.Equal("user:owner", revision.ActorId);
        Assert.Equal(property.Version, revision.PropertyVersion);
    }

    [Fact]
    public async Task Activation_denial_does_not_mutate_or_write_a_revision()
    {
        Property property = CreateProperty();
        FakePropertyRepository properties = new(property);
        RecordingPropertyMutationOperationRepository operations = new();
        PropertyMutationOperationJournal journal = CreateJournal(operations);
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: properties),
            journal,
            revisions,
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock(),
            new TestIdGenerator());
        ActivatePropertyProcessingCommand command = CreateActivationCommand(
            property,
            Guid.NewGuid());

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.CountryPolicyDenied(CountryPolicyDecisionReason.UnknownPolicy),
            result.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Null(property.GovernanceBinding);
        Assert.Empty(revisions.Items);
        Assert.Empty(operations.Added);

        ActivatePropertyProcessingCommandHandler correctedHandler = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator(),
            runtimeTimeZones: Application
                .PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyMutationReceiptDto> corrected =
            await correctedHandler.HandleAsync(command, CancellationToken.None);

        Assert.True(corrected.IsSuccess);
        Assert.Single(operations.Added);
    }

    [Fact]
    public async Task Activation_rejects_a_legacy_alias_before_domain_effects()
    {
        Property property = LegacyPropertyTestFactory.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Hostel One",
            "hostel-one",
            "UTC",
            Now.AddDays(-10));
        RecordingPropertyMutationOperationRepository operations = new();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(operations),
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator(),
            runtimeTimeZones: Application
                .PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            CreateActivationCommand(property, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.CountryPolicyDenied(
                CountryPolicyDecisionReason.InvalidRequest),
            result.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Null(property.GovernanceBinding);
        Assert.Equal(1, property.Version);
        Assert.Empty(property.DomainEvents);
        Assert.Empty(revisions.Items);
        Assert.Empty(operations.Added);
    }

    [Fact]
    public async Task Activation_rejects_a_runtime_rule_mismatch_before_domain_effects()
    {
        Property property = CreateProperty();
        property.ClearDomainEvents();
        RecordingPropertyMutationOperationRepository operations = new();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(operations),
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator(),
            runtimeTimeZones: Application
                .PropertyTimeZoneHealthClassifierTests.IncompatibleProbe());

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            CreateActivationCommand(property, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeZoneRuntimeUnavailable,
            result.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Null(property.GovernanceBinding);
        Assert.Equal(1, property.Version);
        Assert.Empty(property.DomainEvents);
        Assert.Empty(revisions.Items);
        Assert.Empty(operations.Added);
    }

    [Fact]
    public async Task Activation_fails_closed_on_an_invalid_server_clock()
    {
        Property property = CreateProperty();
        property.ClearDomainEvents();
        RecordingPropertyMutationOperationRepository operations = new();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(operations),
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(default(DateTimeOffset)),
            new TestIdGenerator(),
            runtimeTimeZones: Application
                .PropertyTimeZoneHealthClassifierTests.CompatibleProbe());

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            CreateActivationCommand(property, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.TimeSourceUnavailable,
            result.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Equal(1, property.Version);
        Assert.Empty(property.DomainEvents);
        Assert.Empty(revisions.Items);
        Assert.Empty(operations.Added);
    }

    [Fact]
    public async Task Exact_activation_replay_returns_the_original_receipt_after_the_property_advances()
    {
        Property property = CreateProperty();
        FakePropertyRepository properties = new(property);
        RecordingPropertyMutationOperationRepository operations = new();
        PropertyMutationOperationJournal journal = CreateJournal(operations);
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommand command = CreateActivationCommand(
            property,
            Guid.NewGuid());
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator());

        Result<PropertyMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Assert.True(property.RegisterRoom(property.Version).IsSuccess);
        property.ClearDomainEvents();
        revisions.Items.Clear();
        ActivatePropertyProcessingCommandHandler replayHandler = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock(Now.AddDays(31)),
            new TestIdGenerator(),
            [new TestLifecyclePolicy(exception: new InvalidOperationException())]);

        Result<PropertyMutationReceiptDto> replay = await replayHandler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(2, replay.Value.Version);
        Assert.Equal(3, property.Version);
        Assert.Empty(revisions.Items);
        Assert.Empty(property.DomainEvents);
        Assert.Single(operations.Added);
    }

    [Fact]
    public async Task Changed_activation_reuse_conflicts_before_policy_evaluation()
    {
        Property property = CreateProperty();
        RecordingPropertyMutationOperationRepository operations = new();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(operations),
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator());
        ActivatePropertyProcessingCommand command = CreateActivationCommand(
            property,
            Guid.NewGuid());

        Assert.True((await handler.HandleAsync(
            command,
            CancellationToken.None)).IsSuccess);
        Result<PropertyMutationReceiptDto> reuse = await handler.HandleAsync(
            command with { DataRegionId = "eu-west-1" },
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            reuse.Error);
        Assert.Single(revisions.Items);
        Assert.Single(operations.Added);
        Assert.Equal(2, property.Version);
    }

    [Theory]
    [InlineData(
        PropertyProcessingLifecycleOutcome.Restricted,
        "Properties.ProcessingLifecycleRestricted")]
    [InlineData(
        PropertyProcessingLifecycleOutcome.Unavailable,
        "Properties.ProcessingLifecycleAdmissionUnavailable")]
    public async Task Workspace_lifecycle_denial_precedes_policy_mutation(
        PropertyProcessingLifecycleOutcome outcome,
        string expectedErrorCode)
    {
        Property property = CreateProperty();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(),
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator(),
            [new TestLifecyclePolicy(
                new PropertyProcessingLifecycleDecision(outcome))]);

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            new ActivatePropertyProcessingCommand(
                property.Id,
                Guid.NewGuid(),
                "GB",
                "gb-hostel",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                1,
                [],
                true,
                property.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedErrorCode, result.Error.Code);
        Assert.Equal(
            PropertyProcessingState.Unconfigured,
            property.ProcessingState);
        Assert.Empty(revisions.Items);
    }

    [Fact]
    public async Task Workspace_lifecycle_provider_exception_fails_closed()
    {
        Property property = CreateProperty();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(),
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator(),
            [new TestLifecyclePolicy(
                exception: new InvalidOperationException())]);

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            new ActivatePropertyProcessingCommand(
                property.Id,
                Guid.NewGuid(),
                "GB",
                "gb-hostel",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                1,
                [],
                true,
                property.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors
                .ProcessingLifecycleAdmissionUnavailable,
            result.Error);
        Assert.Equal(
            PropertyProcessingState.Unconfigured,
            property.ProcessingState);
        Assert.Empty(revisions.Items);
    }

    [Fact]
    public async Task Activation_requires_server_side_confirmation_before_policy_evaluation()
    {
        Property property = CreateProperty();
        RecordingPropertyMutationOperationRepository operations = new();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler handler = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(operations),
            revisions,
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock(),
            new TestIdGenerator());

        Result<PropertyMutationReceiptDto> result = await handler.HandleAsync(
            new ActivatePropertyProcessingCommand(
                property.Id,
                Guid.NewGuid(),
                "GB",
                "gb-hostel",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                1,
                [],
                false,
                property.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(PropertiesApplicationErrors.ConfirmationRequired, result.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Empty(revisions.Items);
        Assert.Equal(0, operations.ReadCount);
    }

    [Fact]
    public async Task Effective_state_is_authoritative_for_unconfigured_enabled_expired_revoked_and_suspended_bindings()
    {
        Property property = CreateProperty();
        FakePropertyRepository repository = new(property);
        CountryPolicyPackArtifact artifact = CreateArtifact();
        CountryPolicyRegistry registry = CreateRegistry(artifact);

        PropertyProcessingStateDto unconfigured = (await new GetPropertyProcessingStateQueryHandler(
            repository,
            registry,
            new TestClock()).HandleAsync(
                new GetPropertyProcessingStateQuery(property.Id),
                CancellationToken.None)).Value;
        Assert.Equal(PropertyProcessingEffectiveStatus.Unconfigured, unconfigured.EffectiveStatus);
        Assert.Equal(GetPropertyProcessingStateQueryHandler.UnconfiguredReasonCode, unconfigured.ReasonCode);

        Assert.True((await new ActivatePropertyProcessingCommandHandler(
            PropertiesMutationTestSupport.Create(properties: repository),
            CreateJournal(),
            new RecordingRevisionWriter(),
            registry,
            new TestClock(),
            new TestIdGenerator()).HandleAsync(
                new ActivatePropertyProcessingCommand(
                    property.Id,
                    Guid.NewGuid(),
                    "GB",
                    "gb-hostel",
                    1,
                    "eu-west-2",
                    "uk-no-transfer",
                    "guest-operational",
                    1,
                    [],
                    true,
                    property.Version,
                    "user:owner"),
                CancellationToken.None)).IsSuccess);

        PropertyProcessingStateDto enabled = (await new GetPropertyProcessingStateQueryHandler(
            repository,
            registry,
            new TestClock()).HandleAsync(
                new GetPropertyProcessingStateQuery(property.Id),
                CancellationToken.None)).Value;
        Assert.Equal(PropertyProcessingEffectiveStatus.Enabled, enabled.EffectiveStatus);
        Assert.Equal(GetPropertyProcessingStateQueryHandler.AllowedReasonCode, enabled.ReasonCode);

        PropertyProcessingStateDto expired = (await new GetPropertyProcessingStateQueryHandler(
            repository,
            registry,
            new TestClock(Now.AddDays(31))).HandleAsync(
                new GetPropertyProcessingStateQuery(property.Id),
                CancellationToken.None)).Value;
        Assert.Equal(PropertyProcessingEffectiveStatus.Expired, expired.EffectiveStatus);
        Assert.Equal("Properties.CountryPolicy.PolicyExpired", expired.ReasonCode);

        PropertyProcessingStateDto revoked = (await new GetPropertyProcessingStateQueryHandler(
            repository,
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock()).HandleAsync(
                new GetPropertyProcessingStateQuery(property.Id),
                CancellationToken.None)).Value;
        Assert.Equal(PropertyProcessingEffectiveStatus.Revoked, revoked.EffectiveStatus);
        Assert.Equal("Properties.CountryPolicy.UnknownPolicy", revoked.ReasonCode);

        Assert.True(property.SuspendProcessing(
            property.Version,
            Guid.NewGuid(),
            Now,
            "user:owner").IsSuccess);
        PropertyProcessingStateDto suspended = (await new GetPropertyProcessingStateQueryHandler(
            repository,
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock()).HandleAsync(
                new GetPropertyProcessingStateQuery(property.Id),
                CancellationToken.None)).Value;
        Assert.Equal(PropertyProcessingEffectiveStatus.Suspended, suspended.EffectiveStatus);
        Assert.Equal(GetPropertyProcessingStateQueryHandler.SuspendedReasonCode, suspended.ReasonCode);
    }

    [Theory]
    [InlineData(PropertyProcessingState.Unconfigured)]
    [InlineData(PropertyProcessingState.Enabled)]
    [InlineData(PropertyProcessingState.Suspended)]
    public async Task Retirement_preserves_configuration_but_suspends_effective_processing_and_rejects_new_commands(
        PropertyProcessingState configuredState)
    {
        Property property = CreateProperty();
        FakePropertyRepository repository = new(property);
        RecordingRevisionWriter revisions = new();
        RecordingPropertyMutationOperationRepository operations = new();
        PropertyMutationOperationJournal journal = CreateJournal(operations);
        ActivatePropertyProcessingCommandHandler activate = new(
            PropertiesMutationTestSupport.Create(properties: repository),
            journal,
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator());
        if (configuredState != PropertyProcessingState.Unconfigured)
        {
            Assert.True((await activate.HandleAsync(
                CreateActivationCommand(property, Guid.NewGuid()),
                CancellationToken.None)).IsSuccess);
        }

        if (configuredState == PropertyProcessingState.Suspended)
        {
            Assert.True(property.SuspendProcessing(
                property.Version, Guid.NewGuid(), Now, "user:owner").IsSuccess);
        }

        PropertyGovernanceBinding? binding = property.GovernanceBinding;
        Assert.True(property.Retire(
            property.Version, Guid.NewGuid(), Now, "user:owner").IsSuccess);
        long retiredVersion = property.Version;
        int revisionCount = revisions.Items.Count;
        int operationCount = operations.Added.Count;
        property.ClearDomainEvents();

        // No policies are registered: retirement must take precedence over
        // Unconfigured/UnknownPolicy, not pretend the saved binding was erased.
        PropertyProcessingStateDto state = (await new GetPropertyProcessingStateQueryHandler(
            repository,
            CountryPolicyRegistry.Create([], [], CountryPolicyRuntimeMode.Production),
            new TestClock()).HandleAsync(
                new GetPropertyProcessingStateQuery(property.Id),
                CancellationToken.None)).Value;
        Assert.Equal(PropertyProcessingEffectiveStatus.Suspended, state.EffectiveStatus);
        Assert.Equal(GetPropertyProcessingStateQueryHandler.RetiredReasonCode, state.ReasonCode);
        Assert.Equal(configuredState.ToString(), state.ConfiguredStatus.ToString());
        Assert.Equal(retiredVersion, state.PropertyVersion);

        Result<PropertyMutationReceiptDto> activation = await activate.HandleAsync(
            CreateActivationCommand(property, Guid.NewGuid()), CancellationToken.None);
        Result<PropertyMutationReceiptDto> suspension = await new SuspendPropertyProcessingCommandHandler(
            PropertiesMutationTestSupport.Create(properties: repository), journal,
            revisions, new TestClock(), new TestIdGenerator()).HandleAsync(
                new SuspendPropertyProcessingCommand(property.Id, Guid.NewGuid(), true, retiredVersion, "user:owner"),
                CancellationToken.None);
        Assert.Equal(PropertiesDomainErrors.PropertyRetired, activation.Error);
        Assert.Equal(PropertiesDomainErrors.PropertyRetired, suspension.Error);
        Assert.Equal(configuredState, property.ProcessingState);
        Assert.Same(binding, property.GovernanceBinding);
        Assert.Equal(retiredVersion, property.Version);
        Assert.Equal(revisionCount, revisions.Items.Count);
        Assert.Equal(operationCount, operations.Added.Count);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public async Task Suspension_keeps_policy_coordinates_and_appends_a_revision()
    {
        Property property = CreateProperty();
        CountryPolicyPackArtifact artifact = CreateArtifact();
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler activate = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(),
            revisions,
            CreateRegistry(artifact),
            new TestClock(),
            new TestIdGenerator());
        Assert.True((await activate.HandleAsync(
            new ActivatePropertyProcessingCommand(
                property.Id,
                Guid.NewGuid(),
                "GB",
                "gb-hostel",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "guest-operational",
                1,
                [],
                true,
                property.Version,
                "user:owner"),
            CancellationToken.None)).IsSuccess);
        revisions.Items.Clear();
        SuspendPropertyProcessingCommandHandler suspend = new(
            PropertiesMutationTestSupport.Create(
                properties: new FakePropertyRepository(property)),
            CreateJournal(),
            revisions,
            new TestClock(),
            new TestIdGenerator());

        Result<PropertyMutationReceiptDto> result = await suspend.HandleAsync(
            new SuspendPropertyProcessingCommand(
                property.Id,
                Guid.NewGuid(),
                true,
                property.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyProcessingState.Suspended, property.ProcessingState);
        Assert.Equal(artifact.ContentSha256, property.GovernanceBinding!.ContentSha256);
        PropertyGovernanceRevisionWriteModel revision = Assert.Single(revisions.Items);
        Assert.Equal(PropertyGovernanceRevisionAction.Suspended, revision.Action);
        Assert.Equal(revision.Previous, revision.Current);
    }

    [Fact]
    public async Task Exact_suspension_replay_returns_the_original_receipt_after_the_property_advances()
    {
        Property property = CreateProperty();
        FakePropertyRepository properties = new(property);
        RecordingPropertyMutationOperationRepository operations = new();
        PropertyMutationOperationJournal journal = CreateJournal(operations);
        RecordingRevisionWriter revisions = new();
        ActivatePropertyProcessingCommandHandler activate = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator());
        Assert.True((await activate.HandleAsync(
            CreateActivationCommand(property, Guid.NewGuid()),
            CancellationToken.None)).IsSuccess);
        revisions.Items.Clear();
        SuspendPropertyProcessingCommandHandler suspend = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            new TestClock(),
            new TestIdGenerator());
        SuspendPropertyProcessingCommand command = new(
            property.Id,
            Guid.NewGuid(),
            true,
            property.Version,
            "user:owner");

        Result<PropertyMutationReceiptDto> first = await suspend.HandleAsync(
            command,
            CancellationToken.None);
        Assert.True(property.RegisterRoom(property.Version).IsSuccess);
        property.ClearDomainEvents();
        revisions.Items.Clear();
        Result<PropertyMutationReceiptDto> replay = await suspend.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(3, replay.Value.Version);
        Assert.Equal(4, property.Version);
        Assert.Empty(revisions.Items);
        Assert.Empty(property.DomainEvents);
        Assert.Equal(2, operations.Added.Count);
    }

    [Fact]
    public async Task Operation_id_cannot_be_reused_across_processing_actions()
    {
        Property property = CreateProperty();
        FakePropertyRepository properties = new(property);
        RecordingPropertyMutationOperationRepository operations = new();
        PropertyMutationOperationJournal journal = CreateJournal(operations);
        RecordingRevisionWriter revisions = new();
        Guid operationId = Guid.NewGuid();
        ActivatePropertyProcessingCommandHandler activate = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            CreateRegistry(CreateArtifact()),
            new TestClock(),
            new TestIdGenerator());
        Assert.True((await activate.HandleAsync(
            CreateActivationCommand(property, operationId),
            CancellationToken.None)).IsSuccess);
        SuspendPropertyProcessingCommandHandler suspend = new(
            PropertiesMutationTestSupport.Create(properties: properties),
            journal,
            revisions,
            new TestClock(),
            new TestIdGenerator());

        Result<PropertyMutationReceiptDto> result = await suspend.HandleAsync(
            new SuspendPropertyProcessingCommand(
                property.Id,
                operationId,
                true,
                property.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            result.Error);
        Assert.Equal(PropertyProcessingState.Enabled, property.ProcessingState);
        Assert.Single(operations.Added);
        Assert.Single(revisions.Items);
    }

    private static CountryPolicyRegistry CreateRegistry(CountryPolicyPackArtifact artifact) =>
        CountryPolicyRegistry.Create(
            [artifact],
            [new("GB", "gb-hostel", 1, artifact.ContentSha256, CountryLaunchStatus.Approved)],
            CountryPolicyRuntimeMode.Production,
            EmbeddedTzdbCountryPolicyTimeZoneRules.Instance);

    private static PropertyMutationOperationJournal CreateJournal(
        RecordingPropertyMutationOperationRepository? operations = null) =>
        new(operations ?? new RecordingPropertyMutationOperationRepository());

    private static ActivatePropertyProcessingCommand CreateActivationCommand(
        Property property,
        Guid operationId) => new(
            property.Id,
            operationId,
            "GB",
            "gb-hostel",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            [],
            true,
            property.Version,
            "user:owner");

    private static CountryPolicyPackArtifact CreateArtifact()
    {
        CountryPolicyPackDocument document = new()
        {
            SchemaVersion = 1,
            PolicyId = "gb-hostel",
            PolicyVersion = 1,
            OperatingCountryCode = "GB",
            ApprovalState = CountryPolicyApprovalState.Approved,
            EffectiveAtUtc = Now.AddDays(-1),
            ExpiresAtUtc = Now.AddDays(30),
            AccommodationTypes = ["hostel"],
            GuestCategories = ["ordinary-guest"],
            FieldRules =
            [
                new()
                {
                    FieldPolicyKey = "guest.primary-name",
                    GuestCategory = "ordinary-guest",
                    Requirement = CountryPolicyFieldRequirement.Required,
                    PurposeCodes = ["property-activation"]
                }
            ],
            PurposeRules =
            [
                new()
                {
                    PurposeCode = "property-activation",
                    LegalRuleReferenceKeys = ["operator-approval"],
                    AllowedSurfaces = [CountryPolicySurface.PropertyActivation],
                    AllowedSourceProvenance = ["authorized-workspace-operator"]
                }
            ],
            RetentionRules =
            [
                new()
                {
                    RetentionPolicyId = "guest-operational",
                    RetentionPolicyVersion = 1,
                    DataClass = "guest-operational",
                    Trigger = "stay-ended",
                    Period = "365.00:00:00"
                }
            ],
            RightsRule = new()
            {
                Registration = "standard-registration",
                Export = "standard-export",
                Correction = "standard-correction",
                Restriction = "standard-restriction",
                Erasure = "review-before-erasure"
            },
            Restrictions = new()
            {
                Minors = "not-assessed",
                Documents = "document-images-prohibited",
                SpecialCategoryData = "prohibited"
            },
            PermittedDataRegions = ["eu-west-2"],
            PermittedTransferProfiles = ["uk-no-transfer"],
            RequiredAcknowledgements = [],
            Approval = new()
            {
                OwnerReference = "private-owner",
                ReviewerReference = "private-reviewer",
                ReviewedAtUtc = Now.AddDays(-2),
                Sources = [new() { ReferenceId = "source-1", Uri = "https://example.test/policy" }],
                DetachedSignatureReference = "signature-1"
            }
        };
        CountryPolicyPackValidator.ValidateAndThrow(document);
        return new(document, new string('a', 64));
    }

    private static Property CreateProperty() =>
        Property.Create(
            Guid.NewGuid(),
            "tenant-a",
            "Hostel One",
            "hostel-one",
            "UTC",
            Guid.NewGuid(),
            Now.AddDays(-10)).Value;

    private sealed class FakePropertyRepository(Property property) : IPropertyRepository
    {
        public Task AddAsync(Property value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Property?> GetAsync(Guid propertyId, CancellationToken cancellationToken) =>
            Task.FromResult(property.Id == propertyId ? property : null);

        public Task<bool> CodeExistsAsync(
            string code,
            Guid? excludingPropertyId,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RecordingRevisionWriter : IPropertyGovernanceRevisionWriter
    {
        public List<PropertyGovernanceRevisionWriteModel> Items { get; } = [];

        public Task AppendAsync(
            PropertyGovernanceRevisionWriteModel revision,
            CancellationToken cancellationToken)
        {
            this.Items.Add(revision);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock(DateTimeOffset? now = null) : ISystemClock
    {
        public DateTimeOffset UtcNow => now ?? Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class TestLifecyclePolicy(
        PropertyProcessingLifecycleDecision? decision = null,
        Exception? exception = null)
        : IPropertyProcessingLifecyclePolicy
    {
        public ValueTask<PropertyProcessingLifecycleDecision>
            AuthorizeActivationAsync(
                string tenantId,
                Guid propertyId,
                CancellationToken cancellationToken = default) =>
            exception is null
                ? ValueTask.FromResult(
                    decision ??
                    PropertyProcessingLifecycleDecision.Allowed)
                : ValueTask.FromException<
                    PropertyProcessingLifecycleDecision>(exception);
    }
}
