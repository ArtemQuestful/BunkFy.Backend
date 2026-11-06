namespace BunkFy.Modules.Staff.Tests;

using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;
using BunkFy.Modules.Staff.Application;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Application.Validation;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.Errors;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffCommandHandlerTests
{
    [Fact]
    public async Task Create_requires_an_enabled_tenant_scope()
    {
        FakeStaffMemberRepository members = new();
        using ServiceProvider provider = CreateProvider(members, new FakePropertyProjectionRepository(),
            new TestScopeContext(false, null));
        ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto> handler = provider
            .GetRequiredService<ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto>>();

        Result<StaffDirectoryMemberDto> result = await handler.HandleAsync(
            CreateCommand(), CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.TenantRequired, result.Error);
        Assert.Null(members.AddedMember);
    }

    [Fact]
    public async Task Create_rejects_duplicate_employee_number_before_adding()
    {
        FakeStaffMemberRepository members = new()
        {
            ExistingEmployeeNumber = "EMP-100"
        };
        using ServiceProvider provider = CreateProvider(members, new FakePropertyProjectionRepository());
        ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto> handler = provider
            .GetRequiredService<ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto>>();

        Result<StaffDirectoryMemberDto> employeeConflict = await handler.HandleAsync(
            CreateCommand(employeeNumber: " EMP-100 "), CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.EmployeeNumberConflict, employeeConflict.Error);
        Assert.Equal(0, members.AuthSubjectExistsCount);
        Assert.Null(members.AddedMember);
    }

    [Fact]
    public async Task Create_uses_the_operation_id_and_replays_an_equivalent_normalized_profile()
    {
        Guid operationId = Guid.NewGuid();
        StaffMember existing = CreateMemberForCreation(operationId);
        FakeStaffMemberRepository members = new(existing);
        RecordingCreationOperationLock creationLock = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            creationLock: creationLock);
        var handler = provider.GetRequiredService<
            ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto>>();
        CreateStaffMemberCommand command = CreateCommand(
            operationId,
            displayName: " Ada Operator ",
            workEmail: " ADA@EXAMPLE.TEST ",
            employeeNumber: " EMP-100 ");
        int eventCount = existing.DomainEvents.Count;

        Result<StaffDirectoryMemberDto> result = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(operationId, result.Value.StaffMemberId);
        Assert.Equal([("tenant-a", operationId)], creationLock.Acquisitions);
        Assert.Equal(0, members.AddCount);
        Assert.Equal(eventCount, existing.DomainEvents.Count);
    }

    [Fact]
    public async Task Create_rejects_reusing_an_operation_for_different_profile_data()
    {
        Guid operationId = Guid.NewGuid();
        FakeStaffMemberRepository members = new(
            CreateMemberForCreation(operationId));
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto>>();

        Result<StaffDirectoryMemberDto> result = await handler.HandleAsync(
            CreateCommand(operationId, displayName: "Different operator"),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.CreationOperationConflict, result.Error);
        Assert.Equal(0, members.AddCount);
    }

    [Fact]
    public async Task Create_does_not_disclose_or_reuse_a_hidden_operation_coordinate()
    {
        Guid operationId = Guid.NewGuid();
        FakeStaffMemberRepository members = new(
            CreateMemberForCreation(operationId))
        {
            OperationallyVisible = false
        };
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto>>();

        Result<StaffDirectoryMemberDto> result = await handler.HandleAsync(
            CreateCommand(operationId),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.StaffMemberNotFound, result.Error);
        Assert.Equal(1, members.OperationalGetCount);
        Assert.Equal(1, members.SafetyGetCount);
        Assert.Equal(0, members.AddCount);
    }

    [Fact]
    public async Task Create_acquires_the_operation_coordinate_before_any_member_read()
    {
        List<string> calls = [];
        FakeStaffMemberRepository members = new() { Calls = calls };
        RecordingCreationOperationLock creationLock = new(calls);
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            creationLock: creationLock);
        var handler = provider.GetRequiredService<
            ICommandHandler<CreateStaffMemberCommand, StaffDirectoryMemberDto>>();
        Guid operationId = Guid.NewGuid();

        Result<StaffDirectoryMemberDto> result = await handler.HandleAsync(
            CreateCommand(operationId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("creation-lock", calls[0]);
        Assert.Equal("operational-read", calls[1]);
        Assert.Equal("safety-read", calls[2]);
        Assert.Equal(operationId, members.AddedMember?.Id);
        Assert.Null(members.AddedMember?.AuthSubjectId);
        Assert.Equal(0, members.AuthSubjectExistsCount);
    }

    [Fact]
    public async Task Assignment_rejects_a_property_that_is_not_active()
    {
        StaffMember member = CreateMember();
        FakeStaffMemberRepository members = new(member);
        using ServiceProvider provider = CreateProvider(members, new FakePropertyProjectionRepository());
        ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto> handler = provider
            .GetRequiredService<ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new AssignStaffPropertyCommand(Guid.NewGuid(), member.Id, Guid.NewGuid(), null, false,
                new DateOnly(2026, 7, 12), member.Version, "user:owner"),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.PropertyUnavailable, result.Error);
        Assert.Empty(member.Assignments);
    }

    [Fact]
    public async Task Assignment_uses_the_projected_property_and_preserves_actor_and_time_provenance()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId));
        ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto> handler = provider
            .GetRequiredService<ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new AssignStaffPropertyCommand(Guid.NewGuid(), member.Id, propertyId, "Duty Manager", true,
                new DateOnly(2026, 7, 12), member.Version, " user:owner "),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal(TestClock.Now, Assert.Single(member.Assignments).AssignedAtUtc);
        Assert.Equal("user:owner", Assert.Single(member.Assignments).AssignedBy);
    }

    [Fact]
    public async Task Assignment_replays_the_exact_operation_without_advancing_history()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
        AssignStaffPropertyCommand command = new(
            operationId,
            member.Id,
            propertyId,
            " Duty Manager ",
            true,
            new DateOnly(2026, 7, 12),
            member.Version,
            "user:owner");

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(2, member.Version);
        Assert.Single(member.Assignments);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Equal(
            StaffMemberMutationKind.AssignProperty,
            Assert.Single(operations.Records).Kind);
    }

    [Fact]
    public async Task Assignment_records_a_receipt_for_an_exact_current_state_no_op()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        Assert.True(member.AssignProperty(
            Guid.NewGuid(),
            propertyId,
            "Duty Manager",
            true,
            new DateOnly(2026, 7, 1),
            member.Version,
            "user:owner",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        long selectedVersion = member.Version;
        int eventCount = member.DomainEvents.Count;
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new AssignStaffPropertyCommand(
                Guid.NewGuid(),
                member.Id,
                propertyId,
                " Duty Manager ",
                true,
                new DateOnly(2026, 7, 1),
                selectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(selectedVersion, result.Value.Version);
        Assert.Equal(selectedVersion, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        StaffMemberMutationOperationRecord operation = Assert.Single(operations.Records);
        Assert.Equal(selectedVersion, operation.ExpectedVersion);
        Assert.Equal(selectedVersion, operation.ResultVersion);
        Assert.Equal(StaffMemberMutationKind.AssignProperty, operation.Kind);
    }

    [Fact]
    public async Task Assignment_replay_does_not_bypass_current_property_visibility()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        RecordingMemberMutationOperations operations = new();
        AssignStaffPropertyCommand command = new(
            Guid.NewGuid(),
            member.Id,
            propertyId,
            null,
            false,
            new DateOnly(2026, 7, 12),
            member.Version,
            "user:owner");

        using (ServiceProvider available = CreateProvider(
                   new FakeStaffMemberRepository(member),
                   new FakePropertyProjectionRepository(propertyId),
                   memberMutationOperations: operations))
        {
            var handler = available.GetRequiredService<
                ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
            Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
                command,
                CancellationToken.None);
            Assert.True(first.IsSuccess, first.Error.Code);
        }

        using ServiceProvider unavailable = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var replay = unavailable.GetRequiredService<
            ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
        Result<StaffMemberMutationReceiptDto> rejected = await replay.HandleAsync(
            command,
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.PropertyUnavailable, rejected.Error);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Assignment_rejects_changed_or_cross_kind_operation_reuse()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        long selectedVersion = member.Version;
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId),
            memberMutationOperations: operations);
        var assign = provider.GetRequiredService<
            ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
        var unassign = provider.GetRequiredService<
            ICommandHandler<UnassignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> first = await assign.HandleAsync(
            new AssignStaffPropertyCommand(
                operationId,
                member.Id,
                propertyId,
                "Duty Manager",
                false,
                new DateOnly(2026, 7, 12),
                selectedVersion,
                "user:owner"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> changed = await assign.HandleAsync(
            new AssignStaffPropertyCommand(
                operationId,
                member.Id,
                propertyId,
                "Front Desk",
                false,
                new DateOnly(2026, 7, 12),
                selectedVersion,
                "user:owner"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> crossKind = await unassign.HandleAsync(
            new UnassignStaffPropertyCommand(
                operationId,
                member.Id,
                propertyId,
                new DateOnly(2026, 7, 12),
                "Transferred",
                selectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(StaffApplicationErrors.AssignmentOperationConflict, changed.Error);
        Assert.Equal(StaffApplicationErrors.AssignmentOperationConflict, crossKind.Error);
        Assert.Single(operations.Records);
        Assert.Equal(2, member.Version);
    }

    [Fact]
    public async Task Failed_assignment_attempt_does_not_bind_the_operation_id()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        RecordingMemberMutationOperations operations = new();
        AssignStaffPropertyCommand command = new(
            operationId,
            member.Id,
            propertyId,
            null,
            false,
            new DateOnly(2026, 7, 12),
            member.Version,
            "user:owner");

        using (ServiceProvider unavailable = CreateProvider(
                   new FakeStaffMemberRepository(member),
                   new FakePropertyProjectionRepository(),
                   memberMutationOperations: operations))
        {
            var handler = unavailable.GetRequiredService<
                ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
            Result<StaffMemberMutationReceiptDto> rejected = await handler.HandleAsync(
                command,
                CancellationToken.None);
            Assert.Equal(StaffApplicationErrors.PropertyUnavailable, rejected.Error);
        }

        using ServiceProvider available = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId),
            memberMutationOperations: operations);
        var retry = available.GetRequiredService<
            ICommandHandler<AssignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
        Result<StaffMemberMutationReceiptDto> accepted = await retry.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Code);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Unassignment_replays_only_the_matching_completed_operation()
    {
        StaffMember member = CreateMember();
        Guid propertyId = Guid.NewGuid();
        Assert.True(member.AssignProperty(
            Guid.NewGuid(),
            propertyId,
            null,
            false,
            new DateOnly(2026, 7, 1),
            member.Version,
            "user:owner",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        Guid operationId = Guid.NewGuid();
        long selectedVersion = member.Version;
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UnassignStaffPropertyCommand, StaffMemberMutationReceiptDto>>();
        UnassignStaffPropertyCommand command = new(
            operationId,
            member.Id,
            propertyId,
            new DateOnly(2026, 7, 12),
            " Transferred ",
            selectedVersion,
            "user:owner");

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> distinct = await handler.HandleAsync(
            command with
            {
                OperationId = Guid.NewGuid(),
                ExpectedVersion = member.Version
            },
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(StaffDomainErrors.AssignmentNotFound, distinct.Error);
        Assert.Equal(3, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Equal(
            StaffMemberMutationKind.UnassignProperty,
            Assert.Single(operations.Records).Kind);
    }

    [Fact]
    public async Task Operational_mutation_rechecks_visibility_after_acquiring_the_member_lock()
    {
        StaffMember member = CreateMember();
        FakeStaffMemberRepository members = new(member);
        RecordingOperationLock operationLock = new(
            () => members.OperationallyVisible = false);
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            operationLock: operationLock);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        long selectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new UpdateStaffMemberCommand(
                Guid.NewGuid(),
                member.Id,
                "Changed after restriction",
                null,
                "changed@example.test",
                null,
                "EMP-100",
                "Manager",
                "Operations",
                selectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.StaffMemberNotFound, result.Error);
        Assert.Equal(selectedVersion, member.Version);
        Assert.Equal("Ada Operator", member.DisplayName);
        Assert.Equal((member.ScopeId, member.Id), Assert.Single(operationLock.Acquisitions));
        Assert.Equal(1, members.OperationalGetCount);
    }

    [Fact]
    public async Task Profile_update_replays_an_equivalent_request_without_a_second_mutation()
    {
        StaffMember member = CreateMember();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;
        UpdateStaffMemberCommand command = UpdateCommand(
            member,
            operationId,
            expectedVersion,
            displayName: "  Grace Operator  ");

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(expectedVersion + 1, member.Version);
        Assert.Equal("Grace Operator", member.DisplayName);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Profile_update_rejects_changed_reuse_and_a_distinct_stale_operation()
    {
        StaffMember member = CreateMember();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            UpdateCommand(member, operationId, expectedVersion, "Grace Operator"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> changedReuse = await handler.HandleAsync(
            UpdateCommand(member, operationId, expectedVersion, "Different Operator"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> stale = await handler.HandleAsync(
            UpdateCommand(member, Guid.NewGuid(), expectedVersion, "Different Operator"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(StaffApplicationErrors.ProfileUpdateOperationConflict, changedReuse.Error);
        Assert.Equal(StaffApplicationErrors.VersionConflict, stale.Error);
        Assert.Equal("Grace Operator", member.DisplayName);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Profile_update_records_an_exact_no_op_without_an_event_or_version_change()
    {
        StaffMember member = CreateMember();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        int eventCount = member.DomainEvents.Count;
        long expectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            UpdateCommand(member, Guid.NewGuid(), expectedVersion, member.DisplayName),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(expectedVersion, result.Value.Version);
        Assert.Equal(expectedVersion, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Profile_update_validates_actor_before_lock_or_replay_lookup()
    {
        StaffMember member = CreateMember();
        RecordingOperationLock operationLock = new();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            operationLock: operationLock,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            UpdateCommand(
                member,
                Guid.NewGuid(),
                member.Version,
                member.DisplayName) with
            {
                ActorId = " "
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(operationLock.Acquisitions);
        Assert.Equal(0, operations.GetCount);
        Assert.Empty(operations.Records);
    }

    [Fact]
    public async Task Unsafe_auth_subject_transitions_record_no_receipt_or_event()
    {
        StaffMember member = CreateMember("user-100");
        FakeStaffMemberRepository members = new(member);
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand, StaffMemberMutationReceiptDto>>();
        long version = member.Version;
        int eventCount = member.DomainEvents.Count;

        Result<StaffMemberMutationReceiptDto> unlink = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                Guid.NewGuid(),
                member.Id,
                null,
                version,
                "user:owner"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> replace = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                Guid.NewGuid(),
                member.Id,
                "user-200",
                version,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.AuthSubjectUnlinkRequiresSuspension,
            unlink.Error);
        Assert.Equal(
            StaffApplicationErrors.AuthSubjectReplacementRequiresUnlink,
            replace.Error);
        Assert.Equal("user-100", member.AuthSubjectId);
        Assert.Equal(version, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Equal(0, members.EmployeeNumberExistsCount);
        Assert.Equal(0, members.AuthSubjectExistsCount);
        Assert.Empty(operations.Records);
    }

    [Fact]
    public async Task Auth_subject_change_supports_suspend_clear_resume_then_link()
    {
        StaffMember member = CreateMember("user-100");
        FakeStaffMemberRepository members = new(member);
        RecordingMemberMutationOperations operations = new();
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.Allowed);
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy,
            memberMutationOperations: operations);
        var authHandler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand, StaffMemberMutationReceiptDto>>();
        var suspendHandler = provider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        var resumeHandler = provider.GetRequiredService<
            ICommandHandler<ResumeStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> suspended = await suspendHandler.HandleAsync(
            new SuspendStaffMemberCommand(
                Guid.NewGuid(),
                member.Id,
                "Preparing an account transition",
                member.Version,
                "user:owner"),
            CancellationToken.None);
        Assert.True(suspended.IsSuccess, suspended.Error.Code);

        Result<StaffMemberMutationReceiptDto> cleared = await authHandler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                Guid.NewGuid(),
                member.Id,
                null,
                member.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.True(cleared.IsSuccess, cleared.Error.Code);
        Assert.Null(member.AuthSubjectId);
        Result<StaffMemberMutationReceiptDto> resumed = await resumeHandler.HandleAsync(
            new ResumeStaffMemberCommand(
                Guid.NewGuid(),
                member.Id,
                "Account link cleared",
                member.Version,
                "user:owner"),
            CancellationToken.None);
        Assert.True(resumed.IsSuccess, resumed.Error.Code);

        Result<StaffMemberMutationReceiptDto> linked = await authHandler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                Guid.NewGuid(),
                member.Id,
                "user-200",
                member.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.True(linked.IsSuccess, linked.Error.Code);
        Assert.Equal("user-200", member.AuthSubjectId);
        Assert.Equal(
            [StaffMemberMutationKind.Suspend,
             StaffMemberMutationKind.AuthSubjectChange,
             StaffMemberMutationKind.Resume,
             StaffMemberMutationKind.AuthSubjectChange],
            operations.Records.Select(operation => operation.Kind));
        Assert.Equal("user-100", policy.Contexts[0].AuthSubjectId);
        Assert.Null(policy.Contexts[1].AuthSubjectId);
        Assert.Equal(0, members.EmployeeNumberExistsCount);
        Assert.Equal(1, members.AuthSubjectExistsCount);
    }

    [Fact]
    public async Task Workspace_anchor_blocks_auth_subject_change_even_after_resolution()
    {
        StaffMember member = CreateMember("user-100");
        Assert.True(member.Suspend(
            member.Version,
            "user:owner",
            "Preparing an account transition",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        RecordingMemberMutationOperations operations = new();
        RecordingIdentityProvisioningAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            Guid.NewGuid(),
            member.Id,
            TestClock.Now,
            Guid.NewGuid()));
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations,
            identityAnchors: anchors);
        var handler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand,
                StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.NewGuid(),
                    member.Id,
                    null,
                    member.Version,
                    "user:owner"),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorAccessClosureRequired,
            result.Error);
        Assert.Equal("user-100", member.AuthSubjectId);
        Assert.Empty(operations.Records);
    }

    [Fact]
    public async Task Workspace_anchor_allows_exact_auth_subject_noop_replay()
    {
        StaffMember member = CreateMember("user-100");
        RecordingIdentityProvisioningAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding,
            Guid.NewGuid(),
            member.Id,
            TestClock.Now,
            Guid.NewGuid()));
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            identityAnchors: anchors,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<ICommandHandler<
            SetStaffAuthSubjectCommand,
            StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.NewGuid(),
                    member.Id,
                    " user-100 ",
                    member.Version,
                    "user:owner"),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("user-100", member.AuthSubjectId);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Organization_membership_anchor_does_not_block_auth_subject_change()
    {
        StaffMember member = CreateMember("user-100");
        Assert.True(member.Suspend(
            member.Version,
            "user:owner",
            "Preparing an account transition",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        RecordingIdentityProvisioningAnchors anchors = new();
        anchors.Records.Add(new(
            "tenant-a",
            StaffIdentityProvisioningSourceKind.OrganizationMembership,
            Guid.NewGuid(),
            member.Id,
            TestClock.Now));
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            identityAnchors: anchors);
        var handler = provider.GetRequiredService<ICommandHandler<
            SetStaffAuthSubjectCommand,
            StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.NewGuid(),
                    member.Id,
                    null,
                    member.Version,
                    "user:owner"),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Null(member.AuthSubjectId);
    }

    [Fact]
    public async Task Auth_subject_change_replays_normalized_input_without_a_second_event()
    {
        StaffMember member = CreateMember();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                operationId,
                member.Id,
                " user-200 ",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                operationId,
                member.Id,
                "user-200",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal("user-200", member.AuthSubjectId);
        Assert.Equal(expectedVersion + 1, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        StaffMemberMutationOperationRecord operation =
            Assert.Single(operations.Records);
        Assert.Equal(
            StaffMemberMutationKind.AuthSubjectChange,
            operation.Kind);
    }

    [Fact]
    public async Task Auth_subject_change_rejects_changed_or_cross_kind_operation_reuse()
    {
        StaffMember member = CreateMember();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                operationId,
                member.Id,
                "user-200",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> changed = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                operationId,
                member.Id,
                "user-300",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(
            StaffApplicationErrors.AuthSubjectOperationConflict,
            changed.Error);

        Guid profileOperationId = Guid.NewGuid();
        operations.Records.Add(new StaffMemberMutationOperationRecord(
            profileOperationId,
            member.ScopeId,
            member.Id,
            StaffMemberMutationKind.ProfileUpdate,
            member.Version,
            new string('a', 64),
            StaffStatus.Active,
            member.Version,
            TestClock.Now));
        Result<StaffMemberMutationReceiptDto> crossKind =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    profileOperationId,
                    member.Id,
                    "user-200",
                    member.Version,
                    "user:owner"),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.AuthSubjectOperationConflict,
            crossKind.Error);
    }

    [Fact]
    public async Task Auth_subject_no_op_records_a_receipt_but_a_distinct_stale_no_op_fails()
    {
        StaffMember member = CreateMember("user-100");
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand, StaffMemberMutationReceiptDto>>();
        long selectedVersion = member.Version;
        int eventCount = member.DomainEvents.Count;

        Result<StaffMemberMutationReceiptDto> noOp = await handler.HandleAsync(
            new SetStaffAuthSubjectCommand(
                Guid.NewGuid(),
                member.Id,
                " user-100 ",
                selectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(noOp.IsSuccess, noOp.Error.Code);
        Assert.Equal(selectedVersion, noOp.Value.Version);
        Assert.Equal(selectedVersion, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);

        Result changed = member.Suspend(
            member.Version,
            "system:identity-sync",
            "Advance the profile version",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(1));
        Result<StaffMemberMutationReceiptDto> staleNoOp =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.NewGuid(),
                    member.Id,
                    "user-100",
                    selectedVersion,
                    "user:owner"),
                CancellationToken.None);

        Assert.True(changed.IsSuccess, changed.Error.Code);
        Assert.Equal(StaffApplicationErrors.VersionConflict, staleNoOp.Error);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Auth_subject_change_validates_actor_and_visibility_before_receipt_lookup()
    {
        StaffMember member = CreateMember("user-100");
        FakeStaffMemberRepository members = new(member)
        {
            OperationallyVisible = false
        };
        RecordingOperationLock operationLock = new();
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            operationLock: operationLock,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<SetStaffAuthSubjectCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> invalidOperation =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.Empty,
                    member.Id,
                    "user-200",
                    member.Version,
                    "user:owner"),
                CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> invalidActor =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.NewGuid(),
                    member.Id,
                    "user-200",
                    member.Version,
                    " "),
                CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> hidden =
            await handler.HandleAsync(
                new SetStaffAuthSubjectCommand(
                    Guid.NewGuid(),
                    member.Id,
                    "user-200",
                    member.Version,
                    "user:owner"),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.AuthSubjectOperationInvalid,
            invalidOperation.Error);
        Assert.True(invalidActor.IsFailure);
        Assert.Equal(
            StaffApplicationErrors.StaffMemberNotFound,
            hidden.Error);
        Assert.Single(operationLock.Acquisitions);
        Assert.Equal(0, operations.GetCount);
        Assert.Empty(operations.Records);
    }

    [Fact]
    public async Task Self_service_profile_update_rechecks_auth_subject_after_locking()
    {
        StaffMember member = CreateMember("user-100");
        RecordingMemberMutationOperations operations = new();
        RecordingOperationLock operationLock = new(() =>
        {
            Result suspended = member.Suspend(
                member.Version,
                "system:identity-sync",
                "Preparing an account transition",
                Guid.NewGuid(),
                TestClock.Now.AddMinutes(1));
            Assert.True(suspended.IsSuccess, suspended.Error.Code);
            Result cleared = member.SetAuthSubject(
                null,
                member.Version,
                "system:identity-sync",
                Guid.NewGuid(),
                TestClock.Now.AddMinutes(2));
            Assert.True(cleared.IsSuccess, cleared.Error.Code);
            Result resumed = member.Resume(
                member.Version,
                "system:identity-sync",
                "Account link cleared",
                Guid.NewGuid(),
                TestClock.Now.AddMinutes(3));
            Assert.True(resumed.IsSuccess, resumed.Error.Code);
            Result linked = member.SetAuthSubject(
                "user-200",
                member.Version,
                "system:identity-sync",
                Guid.NewGuid(),
                TestClock.Now.AddMinutes(4));
            Assert.True(linked.IsSuccess, linked.Error.Code);
        });
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            operationLock: operationLock,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateCurrentStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new UpdateCurrentStaffMemberCommand(
                Guid.NewGuid(),
                "user-100",
                "Changed after relink",
                member.LegalName,
                member.WorkEmail,
                member.WorkPhone,
                member.JobTitle,
                member.Department,
                member.Version,
                "user:user-100"),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.StaffMemberNotFound, result.Error);
        Assert.Equal("user-200", member.AuthSubjectId);
        Assert.Equal((member.ScopeId, member.Id), Assert.Single(operationLock.Acquisitions));
        Assert.Equal(0, operations.GetCount);
        Assert.Empty(operations.Records);
    }

    [Fact]
    public async Task Self_service_profile_update_preserves_employee_number()
    {
        StaffMember member = CreateMember("user-100");
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateCurrentStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new UpdateCurrentStaffMemberCommand(
                Guid.NewGuid(),
                "user-100",
                "Self Edited",
                member.LegalName,
                member.WorkEmail,
                member.WorkPhone,
                member.JobTitle,
                member.Department,
                member.Version,
                "user:user-100"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Self Edited", member.DisplayName);
        Assert.Equal("EMP-100", member.EmployeeNumber);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Self_service_profile_replay_survives_a_later_management_owned_change()
    {
        StaffMember member = CreateMember("user-100");
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<UpdateCurrentStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;
        UpdateCurrentStaffMemberCommand command = new(
            operationId,
            "user-100",
            "Self Edited",
            member.LegalName,
            member.WorkEmail,
            member.WorkPhone,
            member.JobTitle,
            member.Department,
            expectedVersion,
            "user:user-100");

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result managementChange = member.UpdateProfile(
            member.DisplayName,
            member.LegalName,
            member.WorkEmail,
            member.WorkPhone,
            "EMP-200",
            member.JobTitle,
            member.Department,
            member.Version,
            "user:manager",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(1));
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(managementChange.IsSuccess, managementChange.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal("EMP-200", member.EmployeeNumber);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Self_service_profile_operation_rejects_changed_or_management_reuse()
    {
        StaffMember member = CreateMember("user-100");
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            memberMutationOperations: operations);
        var selfHandler = provider.GetRequiredService<
            ICommandHandler<UpdateCurrentStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        var managementHandler = provider.GetRequiredService<
            ICommandHandler<UpdateStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid selfOperationId = Guid.NewGuid();
        long selfVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> first = await selfHandler.HandleAsync(
            new UpdateCurrentStaffMemberCommand(
                selfOperationId,
                "user-100",
                "Self Edited",
                member.LegalName,
                member.WorkEmail,
                member.WorkPhone,
                member.JobTitle,
                member.Department,
                selfVersion,
                "user:user-100"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> changed = await selfHandler.HandleAsync(
            new UpdateCurrentStaffMemberCommand(
                selfOperationId,
                "user-100",
                "Different Edit",
                member.LegalName,
                member.WorkEmail,
                member.WorkPhone,
                member.JobTitle,
                member.Department,
                selfVersion,
                "user:user-100"),
            CancellationToken.None);
        Guid managementOperationId = Guid.NewGuid();
        long managementVersion = member.Version;
        Result<StaffMemberMutationReceiptDto> management = await managementHandler.HandleAsync(
            UpdateCommand(
                member,
                managementOperationId,
                managementVersion,
                "Manager Edited"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> crossSurface = await selfHandler.HandleAsync(
            new UpdateCurrentStaffMemberCommand(
                managementOperationId,
                "user-100",
                "Manager Edited",
                member.LegalName,
                member.WorkEmail,
                member.WorkPhone,
                member.JobTitle,
                member.Department,
                managementVersion,
                "user:user-100"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(StaffApplicationErrors.ProfileUpdateOperationConflict, changed.Error);
        Assert.True(management.IsSuccess, management.Error.Code);
        Assert.Equal(
            StaffApplicationErrors.ProfileUpdateOperationConflict,
            crossSurface.Error);
        Assert.Equal(2, operations.Records.Count);
    }

    [Fact]
    public async Task Safety_transition_reloads_through_the_restriction_bypass_after_locking()
    {
        StaffMember member = CreateMember("member-100");
        FakeStaffMemberRepository members = new(member)
        {
            OperationallyVisible = false
        };
        RecordingOperationLock operationLock = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: new RecordingLifecyclePolicy(
                StaffLifecyclePolicyDecision.Allowed),
            operationLock: operationLock);
        var handler = provider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new SuspendStaffMemberCommand(
                Guid.NewGuid(),
                member.Id,
                "Privacy-safe access reduction",
                member.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(StaffStatus.Suspended, result.Value.Status);
        Assert.Equal((member.ScopeId, member.Id), Assert.Single(operationLock.Acquisitions));
        Assert.Equal(1, members.SafetyGetCount);
        Assert.Equal(0, members.OperationalGetCount);
    }

    [Theory]
    [InlineData(typeof(UpdateStaffMemberCommandHandler))]
    [InlineData(typeof(UpdateCurrentStaffMemberCommandHandler))]
    [InlineData(typeof(SetStaffAuthSubjectCommandHandler))]
    [InlineData(typeof(AssignStaffPropertyCommandHandler))]
    [InlineData(typeof(ResumeStaffMemberCommandHandler))]
    [InlineData(typeof(SuspendStaffMemberCommandHandler))]
    [InlineData(typeof(UnassignStaffPropertyCommandHandler))]
    [InlineData(typeof(DepartStaffMemberCommandHandler))]
    [InlineData(typeof(StaffOnboardingProvisioningCoordinator))]
    [InlineData(typeof(ReconcileStaffPropertyAssignmentsCommandHandler))]
    public void Existing_member_mutations_use_the_shared_serialization_boundary(
        Type handlerType)
    {
        System.Reflection.ConstructorInfo constructor = Assert.Single(
            handlerType.GetConstructors(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic));

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType ==
                typeof(StaffMemberMutationCoordinator));
    }

    [Fact]
    public async Task Identity_bootstrap_creates_one_active_staff_profile_for_the_source_operation()
    {
        FakeStaffMemberRepository members = new();
        RecordingCreationOperationLock creationLock = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            creationLock: creationLock);
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();
        Guid operationId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        BootstrapStaffIdentityCommand command = new(
            operationId,
            sourceId,
            "member-100",
            "ada@example.test",
            "ada@example.test",
            "integration:organizations");

        Result<Unit> first = await handler.HandleAsync(command, CancellationToken.None);
        Result<Unit> repeated = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(repeated.IsSuccess, repeated.Error.Code);
        Assert.NotNull(members.AddedMember);
        Assert.Equal(operationId, members.AddedMember.Id);
        Assert.Equal("member-100", members.AddedMember.AuthSubjectId);
        Assert.Equal(StaffMemberState.Active, members.AddedMember.Status);
        Assert.Equal(1, members.AddCount);
        Assert.Equal(2, creationLock.Acquisitions.Count);
        Assert.All(
            creationLock.Acquisitions,
            acquisition => Assert.Equal(sourceId, acquisition.OperationId));
    }

    [Fact]
    public void Identity_bootstrap_validator_only_requires_the_replay_envelope()
    {
        BootstrapStaffIdentityCommandValidator validator = new();

        string[] errors = validator.Validate(new BootstrapStaffIdentityCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            " member-100 ",
            string.Empty,
            new string('e', StaffContractLimits.EmailMaxLength + 1),
            string.Empty)).ToArray();

        Assert.Empty(errors);
    }

    [Fact]
    public void Identity_bootstrap_validator_rejects_an_invalid_replay_envelope()
    {
        BootstrapStaffIdentityCommandValidator validator = new();

        string[] emptyOperationErrors = validator.Validate(
            new BootstrapStaffIdentityCommand(
                Guid.Empty,
                Guid.NewGuid(),
                "member-100",
                "Ada Operator",
                null,
                "integration:organizations")).ToArray();
        string[] emptySubjectErrors = validator.Validate(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "   ",
                "Ada Operator",
                null,
                "integration:organizations")).ToArray();
        string[] oversizedSubjectErrors = validator.Validate(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new string('s', StaffContractLimits.AuthSubjectIdMaxLength + 1),
                "Ada Operator",
                null,
                "integration:organizations")).ToArray();

        Assert.Equal("OperationId is required.", Assert.Single(emptyOperationErrors));
        Assert.Equal(
            "AuthSubjectId is required and must be within the supported limit.",
            Assert.Single(emptySubjectErrors));
        Assert.Equal(
            "AuthSubjectId is required and must be within the supported limit.",
            Assert.Single(oversizedSubjectErrors));
    }

    [Fact]
    public async Task Identity_bootstrap_exact_replay_ignores_mutation_only_payload()
    {
        Guid operationId = Guid.NewGuid();
        StaffMember operationOwner = CreateMemberWithId(operationId, "member-100");
        FakeStaffMemberRepository members = new(operationOwner);
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> result = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                operationId,
                Guid.NewGuid(),
                " member-100 ",
                string.Empty,
                new string('e', StaffContractLimits.EmailMaxLength + 1),
                string.Empty),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(0, members.AddCount);
    }

    [Fact]
    public async Task Identity_bootstrap_existing_identity_ignores_mutation_only_payload()
    {
        StaffMember member = CreateMember("member-100");
        FakeStaffMemberRepository members = new(member);
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> result = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                " member-100 ",
                string.Empty,
                new string('e', StaffContractLimits.EmailMaxLength + 1),
                string.Empty),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(0, members.AddCount);
    }

    [Fact]
    public async Task Identity_bootstrap_rechecks_subject_after_acquiring_the_existing_target_lock()
    {
        StaffMember member = CreateMember("member-100");
        FakeStaffMemberRepository members = new(member);
        RecordingIdentityProvisioningAnchors anchors = new();
        RecordingOperationLock memberLock = new()
        {
            OnAcquireById = staffMemberId =>
            {
                if (staffMemberId != member.Id)
                {
                    return;
                }

                if (member.AuthSubjectId != "member-100")
                {
                    return;
                }

                Assert.True(member.Suspend(
                    member.Version,
                    "system:concurrent-transition",
                    "Concurrent identity transition.",
                    Guid.NewGuid(),
                    TestClock.Now.AddMinutes(1)).IsSuccess);
                Assert.True(member.SetAuthSubject(
                    null,
                    member.Version,
                    "system:concurrent-transition",
                    Guid.NewGuid(),
                    TestClock.Now.AddMinutes(2)).IsSuccess);
            }
        };
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            operationLock: memberLock,
            identityAnchors: anchors);
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> result = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "member-100",
                "Ada Operator",
                "ada@example.test",
                "integration:organizations"),
            CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.CreationOperationConflict,
            result.Error);
        Assert.Empty(anchors.Records);
        Assert.Contains(
            memberLock.Acquisitions,
            acquisition => acquisition.StaffMemberId == member.Id);
    }

    [Fact]
    public async Task Identity_bootstrap_new_identity_still_requires_valid_mutation_payload()
    {
        FakeStaffMemberRepository members = new();
        RecordingCreationOperationLock creationLock = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            creationLock: creationLock);
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> invalidProfile = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "member-profile",
                string.Empty,
                null,
                "integration:organizations"),
            CancellationToken.None);
        Result<Unit> invalidActor = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "member-actor",
                "Ada Operator",
                "ada@example.test",
                string.Empty),
            CancellationToken.None);

        Assert.Equal(StaffDomainErrors.DisplayNameInvalid, invalidProfile.Error);
        Assert.Equal(StaffDomainErrors.ActorInvalid, invalidActor.Error);
        Assert.Equal(0, members.AddCount);
        Assert.Equal(2, creationLock.Acquisitions.Count);
    }

    [Fact]
    public async Task Identity_bootstrap_preserves_existing_profile_and_employment_state()
    {
        StaffMember member = CreateMember("member-100");
        Assert.True(member.UpdateProfile(
            "Operator Edited Name",
            member.LegalName,
            member.WorkEmail,
            member.WorkPhone,
            member.EmployeeNumber,
            member.JobTitle,
            member.Department,
            member.Version,
            "user:operator",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(1)).IsSuccess);
        Assert.True(member.Suspend(
            member.Version,
            "user:operator",
            "Approved leave.",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(2)).IsSuccess);
        long version = member.Version;
        FakeStaffMemberRepository members = new(member);
        using ServiceProvider provider = CreateProvider(members, new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> result = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "member-100",
                "Stale Owner Name",
                "stale@example.test",
                "integration:organizations"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Operator Edited Name", member.DisplayName);
        Assert.Equal(StaffMemberState.Suspended, member.Status);
        Assert.Equal(version, member.Version);
        Assert.Equal(0, members.AddCount);
    }

    [Fact]
    public async Task Identity_bootstrap_acknowledges_a_hidden_existing_staff_profile()
    {
        StaffMember member = CreateMember("member-100");
        FakeStaffMemberRepository members = new(member)
        {
            OperationallyVisible = false
        };
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> result = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "member-100",
                "Ada Operator",
                "ada@example.test",
                "integration:organizations"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Null(members.AddedMember);
    }

    [Fact]
    public async Task Identity_bootstrap_rejects_source_operation_reuse_for_another_subject()
    {
        Guid operationId = Guid.NewGuid();
        StaffMember operationOwner = CreateMemberWithId(
            operationId,
            "member-original");
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(operationOwner),
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<BootstrapStaffIdentityCommand, Unit>>();

        Result<Unit> result = await handler.HandleAsync(
            new BootstrapStaffIdentityCommand(
                operationId,
                Guid.NewGuid(),
                "member-different",
                "Different Owner",
                "different@example.test",
                "integration:organizations"),
            CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.CreationOperationConflict,
            result.Error);
    }

    [Fact]
    public async Task Onboarding_replay_preserves_newer_profile_and_fails_closed_after_suspension()
    {
        FakeStaffMemberRepository members = new();
        RecordingOnboardingOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            members,
            new FakePropertyProjectionRepository(),
            onboardingOperations: operations);
        ICommandHandler<ProvisionStaffOnboardingCommand, StaffMemberDto> handler = provider
            .GetRequiredService<ICommandHandler<ProvisionStaffOnboardingCommand, StaffMemberDto>>();
        ProvisionStaffOnboardingCommand command = new(
            Guid.NewGuid(),
            "member-100", "Ada Operator", "Ada Lovelace", "ada@example.test", null,
            "EMP-100", "Manager", "Operations", "integration:organizations");

        Result<StaffMemberDto> first = await handler.HandleAsync(command, CancellationToken.None);
        Result<StaffMemberDto> replayed = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replayed.IsSuccess, replayed.Error.Code);
        Assert.Equal(first.Value.StaffMemberId, replayed.Value.StaffMemberId);
        Assert.Equal(1, members.AddCount);
        Assert.Equal(first.Value.Version, replayed.Value.Version);
        Assert.Equal("Ada Lovelace", replayed.Value.LegalName);
        Assert.Equal("EMP-100", replayed.Value.EmployeeNumber);

        StaffMember member = Assert.IsType<StaffMember>(members.AddedMember);
        Assert.True(member.UpdateProfile(
            "Ada Operator Edited",
            member.LegalName,
            member.WorkEmail,
            member.WorkPhone,
            member.EmployeeNumber,
            member.JobTitle,
            member.Department,
            member.Version,
            "user:operator",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(1)).IsSuccess);
        long editedVersion = member.Version;

        Result<StaffMemberDto> replayedAfterEdit = await handler.HandleAsync(
            command,
            CancellationToken.None);
        Result<StaffMemberDto> conflicting = await handler.HandleAsync(
            command with { DisplayName = "Different applicant" },
            CancellationToken.None);

        Assert.True(replayedAfterEdit.IsSuccess, replayedAfterEdit.Error.Code);
        Assert.Equal("Ada Operator Edited", replayedAfterEdit.Value.DisplayName);
        Assert.Equal(editedVersion, replayedAfterEdit.Value.Version);
        Assert.Equal(
            StaffApplicationErrors.OnboardingOperationConflict,
            conflicting.Error);

        members.OperationallyVisible = false;
        Result<StaffMemberDto> replayedWhileHidden =
            await handler.HandleAsync(command, CancellationToken.None);
        Assert.Equal(
            StaffApplicationErrors.OnboardingReplayUnavailable,
            replayedWhileHidden.Error);
        members.OperationallyVisible = true;

        Assert.True(member.Suspend(
            member.Version,
            "user:operator",
            "Temporary leave.",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(2)).IsSuccess);
        Result<StaffMemberDto> replayedAfterSuspension =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.OnboardingReplayUnavailable,
            replayedAfterSuspension.Error);
        Assert.Equal(StaffMemberState.Suspended, member.Status);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task First_onboarding_operation_requires_explicit_resume_for_a_suspended_member()
    {
        StaffMember member = CreateMember("member-100");
        Assert.True(member.Suspend(
            member.Version,
            "user:owner",
            "Temporary leave.",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        long startingVersion = member.Version;
        string startingDisplayName = member.DisplayName;
        RecordingOnboardingOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            onboardingOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<ProvisionStaffOnboardingCommand, StaffMemberDto>>();
        ProvisionStaffOnboardingCommand command = new(
            Guid.NewGuid(),
            "member-100",
            "Ada Returned",
            "Ada Lovelace",
            "ada@example.test",
            null,
            "EMP-100",
            "Manager",
            "Operations",
            "integration:organizations");

        Result<StaffMemberDto> denied = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.StaffSuspended, denied.Error);
        Assert.Equal(StaffMemberState.Suspended, member.Status);
        Assert.Equal(startingVersion, member.Version);
        Assert.Equal(startingDisplayName, member.DisplayName);
        Assert.Empty(operations.Records);

        Assert.True(member.Resume(
            member.Version,
            "user:lifecycle-manager",
            "Approved return.",
            Guid.NewGuid(),
            TestClock.Now.AddMinutes(1)).IsSuccess);
        long resumedVersion = member.Version;

        Result<StaffMemberDto> result = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(StaffStatus.Active, result.Value.Status);
        Assert.Equal("Ada Returned", result.Value.DisplayName);
        Assert.Equal(resumedVersion + 1, result.Value.Version);
        StaffMemberMutationOperationRecord receipt =
            Assert.Single(operations.Records);
        Assert.Equal(resumedVersion, receipt.ExpectedVersion);
        Assert.Equal(result.Value.Version, receipt.ResultVersion);
    }

    [Fact]
    public async Task Property_plan_reconciles_the_exact_active_set_and_preserves_history()
    {
        StaffMember member = CreateMember("member-100");
        Guid retainedPropertyId = Guid.NewGuid();
        Guid removedPropertyId = Guid.NewGuid();
        Guid addedPropertyId = Guid.NewGuid();
        Assert.True(member.AssignProperty(
            Guid.NewGuid(), retainedPropertyId, null, false, new DateOnly(2026, 7, 1),
            member.Version, "user:owner", Guid.NewGuid(), TestClock.Now).IsSuccess);
        Assert.True(member.AssignProperty(
            Guid.NewGuid(), removedPropertyId, null, false, new DateOnly(2026, 7, 1),
            member.Version, "user:owner", Guid.NewGuid(), TestClock.Now).IsSuccess);
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(retainedPropertyId, addedPropertyId));
        var handler = provider.GetRequiredService<
            ICommandHandler<ReconcileStaffPropertyAssignmentsCommand, IReadOnlyCollection<Guid>>>();

        Result<IReadOnlyCollection<Guid>> result = await handler.HandleAsync(
            new ReconcileStaffPropertyAssignmentsCommand(
                member.Id,
                [addedPropertyId, retainedPropertyId, addedPropertyId],
                "integration:workspaces",
                "Workspace access plan changed."),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            new[] { addedPropertyId, retainedPropertyId }.Order(),
            result.Value.Order());
        Assert.Equal(
            new[] { addedPropertyId, retainedPropertyId }.Order(),
            member.Assignments.Where(assignment => assignment.IsCurrent)
                .Select(assignment => assignment.PropertyId).Order());
        Assert.Contains(member.Assignments, assignment =>
            assignment.PropertyId == removedPropertyId && !assignment.IsCurrent);
    }

    [Fact]
    public async Task Property_plan_replay_does_not_advance_staff_history()
    {
        StaffMember member = CreateMember("member-100");
        Guid propertyId = Guid.NewGuid();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId));
        var handler = provider.GetRequiredService<
            ICommandHandler<ReconcileStaffPropertyAssignmentsCommand, IReadOnlyCollection<Guid>>>();
        ReconcileStaffPropertyAssignmentsCommand command = new(
            member.Id,
            [propertyId],
            "integration:workspaces",
            "Workspace access plan applied.");

        Result<IReadOnlyCollection<Guid>> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        long appliedVersion = member.Version;
        Result<IReadOnlyCollection<Guid>> replayed = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replayed.IsSuccess, replayed.Error.Code);
        Assert.Equal(appliedVersion, member.Version);
        Assert.Single(member.Assignments);
    }

    [Fact]
    public async Task Property_plan_rejects_an_inactive_property_before_mutating_staff()
    {
        StaffMember member = CreateMember("member-100");
        Guid currentPropertyId = Guid.NewGuid();
        Assert.True(member.AssignProperty(
            Guid.NewGuid(), currentPropertyId, null, false, new DateOnly(2026, 7, 1),
            member.Version, "user:owner", Guid.NewGuid(), TestClock.Now).IsSuccess);
        long originalVersion = member.Version;
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(currentPropertyId));
        var handler = provider.GetRequiredService<
            ICommandHandler<ReconcileStaffPropertyAssignmentsCommand, IReadOnlyCollection<Guid>>>();

        Result<IReadOnlyCollection<Guid>> result = await handler.HandleAsync(
            new ReconcileStaffPropertyAssignmentsCommand(
                member.Id,
                [Guid.NewGuid()],
                "integration:workspaces",
                "Workspace access plan changed."),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.PropertyUnavailable, result.Error);
        Assert.Equal(originalVersion, member.Version);
        Assert.True(Assert.Single(member.Assignments).IsCurrent);
    }

    [Fact]
    public async Task Property_plan_rejects_legacy_future_assignments_before_mutating_staff()
    {
        StaffMember member = CreateMember("member-100");
        Guid currentPropertyId = Guid.NewGuid();
        Guid futurePropertyId = Guid.NewGuid();
        Assert.True(member.AssignProperty(
            Guid.NewGuid(), currentPropertyId, null, false, new DateOnly(2026, 7, 1),
            member.Version, "user:owner", Guid.NewGuid(), TestClock.Now).IsSuccess);
        Assert.True(member.AssignProperty(
            Guid.NewGuid(), futurePropertyId, null, false, new DateOnly(2026, 7, 13),
            member.Version, "user:owner", Guid.NewGuid(), TestClock.Now.AddDays(1)).IsSuccess);
        long originalVersion = member.Version;
        int originalEventCount = member.DomainEvents.Count;
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository());
        var handler = provider.GetRequiredService<
            ICommandHandler<ReconcileStaffPropertyAssignmentsCommand, IReadOnlyCollection<Guid>>>();

        Result<IReadOnlyCollection<Guid>> result = await handler.HandleAsync(
            new ReconcileStaffPropertyAssignmentsCommand(
                member.Id,
                [],
                "integration:workspaces",
                "Workspace access plan changed."),
            CancellationToken.None);

        Assert.Equal(StaffDomainErrors.AssignmentDateInvalid, result.Error);
        Assert.Equal(originalVersion, member.Version);
        Assert.Equal(originalEventCount, member.DomainEvents.Count);
        Assert.All(member.Assignments, assignment => Assert.True(assignment.IsCurrent));
    }

    [Fact]
    public async Task Suspend_prepares_workspace_lifecycle_before_returning_success()
    {
        StaffMember member = CreateMember("member-100");
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.Allowed);
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy);
        var handler = provider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new SuspendStaffMemberCommand(
                Guid.NewGuid(), member.Id, "Leave", member.Version, "user:owner"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.NotNull(policy.Context);
        Assert.Equal(StaffLifecycleTransition.Suspend, policy.Context.Transition);
        Assert.Equal(StaffStatus.Active, policy.Context.PreviousStatus);
        Assert.Equal(StaffStatus.Suspended, policy.Context.TargetStatus);
        Assert.Equal("member-100", policy.Context.AuthSubjectId);
        Assert.Equal(2, policy.Context.TargetVersion);
    }

    [Fact]
    public async Task Suspend_replays_an_equivalent_operation_without_repeating_policy_or_event()
    {
        StaffMember member = CreateMember("member-100");
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.Allowed);
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            new SuspendStaffMemberCommand(
                operationId,
                member.Id,
                "  Approved leave  ",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            new SuspendStaffMemberCommand(
                operationId,
                member.Id,
                "Approved leave",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(StaffStatus.Suspended, replay.Value.Status);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Single(policy.Contexts);
        Assert.Equal(
            StaffMemberMutationKind.Suspend,
            Assert.Single(operations.Records).Kind);
    }

    [Fact]
    public async Task Lifecycle_operation_rejects_changed_and_cross_kind_reuse()
    {
        StaffMember member = CreateMember("member-100");
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: new RecordingLifecyclePolicy(
                StaffLifecyclePolicyDecision.Allowed),
            memberMutationOperations: operations);
        var suspend = provider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        var resume = provider.GetRequiredService<
            ICommandHandler<ResumeStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;

        Result<StaffMemberMutationReceiptDto> first = await suspend.HandleAsync(
            new SuspendStaffMemberCommand(
                operationId,
                member.Id,
                "Approved leave",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> changed = await suspend.HandleAsync(
            new SuspendStaffMemberCommand(
                operationId,
                member.Id,
                "Investigation",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);
        Result<StaffMemberMutationReceiptDto> crossKind = await resume.HandleAsync(
            new ResumeStaffMemberCommand(
                operationId,
                member.Id,
                "Returned",
                expectedVersion,
                "user:owner"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal(StaffApplicationErrors.LifecycleOperationConflict, changed.Error);
        Assert.Equal(StaffApplicationErrors.LifecycleOperationConflict, crossKind.Error);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Failed_lifecycle_coordination_does_not_bind_the_operation()
    {
        Guid staffMemberId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        RecordingMemberMutationOperations operations = new();
        StaffMember rejectedMember = CreateMemberWithId(staffMemberId, "member-100");
        SuspendStaffMemberCommand command = new(
            operationId,
            staffMemberId,
            "Approved leave",
            rejectedMember.Version,
            "user:owner");
        using (ServiceProvider rejectedProvider = CreateProvider(
            new FakeStaffMemberRepository(rejectedMember),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: new RecordingLifecyclePolicy(
                StaffLifecyclePolicyDecision.RetryRequired),
            memberMutationOperations: operations))
        {
            var rejectedHandler = rejectedProvider.GetRequiredService<
                ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();
            Result<StaffMemberMutationReceiptDto> rejected =
                await rejectedHandler.HandleAsync(command, CancellationToken.None);

            Assert.Equal(
                StaffApplicationErrors.LifecycleCoordinationPending,
                rejected.Error);
            Assert.Empty(operations.Records);
        }

        StaffMember retriedMember = CreateMemberWithId(staffMemberId, "member-100");
        using ServiceProvider retriedProvider = CreateProvider(
            new FakeStaffMemberRepository(retriedMember),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: new RecordingLifecyclePolicy(
                StaffLifecyclePolicyDecision.Allowed),
            memberMutationOperations: operations);
        var retriedHandler = retriedProvider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> retried =
            await retriedHandler.HandleAsync(command, CancellationToken.None);

        Assert.True(retried.IsSuccess, retried.Error.Code);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Resume_maps_retryable_workspace_coordination_to_a_stable_staff_error()
    {
        StaffMember member = CreateMember("member-100");
        Assert.True(member.Suspend(
            member.Version, "user:owner", "Leave", Guid.NewGuid(), TestClock.Now).IsSuccess);
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.RetryRequired);
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy);
        var handler = provider.GetRequiredService<
            ICommandHandler<ResumeStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new ResumeStaffMemberCommand(
                Guid.NewGuid(), member.Id, "Returned", member.Version, "user:owner"),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.LifecycleCoordinationPending, result.Error);
        Assert.NotNull(policy.Context);
        Assert.Equal(StaffLifecycleTransition.Resume, policy.Context.Transition);
        Assert.Equal(StaffStatus.Suspended, policy.Context.PreviousStatus);
        Assert.Equal(StaffStatus.Active, policy.Context.TargetStatus);
    }

    [Fact]
    public async Task Unresolved_workspace_anchor_blocks_resume_before_workspace_preparation()
    {
        StaffMember member = CreateMember("member-100");
        Assert.True(member.Suspend(
            member.Version,
            "user:owner",
            "Leave",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        long suspendedVersion = member.Version;
        int eventCount = member.DomainEvents.Count;
        RecordingLifecyclePolicy policy = new(
            StaffLifecyclePolicyDecision.Allowed);
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy,
            identityAnchorResolutions:
                new StubStaffIdentityProvisioningAnchorResolutionRepository(
                    hasUnresolved: true),
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<ICommandHandler<
            ResumeStaffMemberCommand,
            StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result =
            await handler.HandleAsync(
                new ResumeStaffMemberCommand(
                    Guid.NewGuid(),
                    member.Id,
                    "Returned",
                    member.Version,
                    "user:owner"),
                CancellationToken.None);

        Assert.Equal(
            StaffApplicationErrors.IdentityAnchorResolutionRequired,
            result.Error);
        Assert.Equal(StaffMemberState.Suspended, member.Status);
        Assert.Equal(suspendedVersion, member.Version);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Empty(policy.Contexts);
        Assert.Empty(operations.Records);
    }

    [Fact]
    public async Task Resume_replays_without_repeating_workspace_preparation()
    {
        StaffMember member = CreateMember("member-100");
        Assert.True(member.Suspend(
            member.Version,
            "user:owner",
            "Leave",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.Allowed);
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<ResumeStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        long expectedVersion = member.Version;
        ResumeStaffMemberCommand command = new(
            operationId,
            member.Id,
            "Returned",
            expectedVersion,
            "user:owner");

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.Single(policy.Contexts);
        Assert.Equal(
            StaffMemberMutationKind.Resume,
            Assert.Single(operations.Records).Kind);
    }

    [Fact]
    public async Task Depart_forwards_the_effective_date_to_workspace_lifecycle_policy()
    {
        StaffMember member = CreateMember("member-100");
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.Allowed);
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy);
        var handler = provider.GetRequiredService<
            ICommandHandler<DepartStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        DateOnly effectiveOn = DateOnly.FromDateTime(TestClock.Now.UtcDateTime);

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new DepartStaffMemberCommand(
                Guid.NewGuid(), member.Id, effectiveOn, "Contract ended",
                member.Version, "user:owner"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.NotNull(policy.Context);
        Assert.Equal(StaffLifecycleTransition.Depart, policy.Context.Transition);
        Assert.Equal(StaffStatus.Departed, policy.Context.TargetStatus);
        Assert.Equal(effectiveOn, policy.Context.EffectiveOn);
    }

    [Fact]
    public async Task Depart_replays_without_closing_assignments_or_emitting_events_twice()
    {
        StaffMember member = CreateMember("member-100");
        Guid propertyId = Guid.NewGuid();
        Assert.True(member.AssignProperty(
            Guid.NewGuid(),
            propertyId,
            null,
            false,
            new DateOnly(2026, 7, 1),
            member.Version,
            "user:owner",
            Guid.NewGuid(),
            TestClock.Now).IsSuccess);
        RecordingLifecyclePolicy policy = new(StaffLifecyclePolicyDecision.Allowed);
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(propertyId),
            lifecyclePolicy: policy,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<DepartStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        DepartStaffMemberCommand command = new(
            Guid.NewGuid(),
            member.Id,
            DateOnly.FromDateTime(TestClock.Now.UtcDateTime),
            "Contract ended",
            member.Version,
            "user:owner");

        Result<StaffMemberMutationReceiptDto> first = await handler.HandleAsync(
            command,
            CancellationToken.None);
        int eventCount = member.DomainEvents.Count;
        Result<StaffMemberMutationReceiptDto> replay = await handler.HandleAsync(
            command,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(StaffStatus.Departed, replay.Value.Status);
        Assert.Equal(eventCount, member.DomainEvents.Count);
        Assert.False(Assert.Single(member.Assignments).IsCurrent);
        Assert.Single(policy.Contexts);
        Assert.Equal(
            StaffMemberMutationKind.Depart,
            Assert.Single(operations.Records).Kind);
    }

    [Fact]
    public async Task Future_departure_does_not_bind_operation_or_prepare_workspace()
    {
        StaffMember member = CreateMember("member-100");
        RecordingLifecyclePolicy policy = new(
            StaffLifecyclePolicyDecision.Allowed);
        RecordingMemberMutationOperations operations = new();
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: policy,
            memberMutationOperations: operations);
        var handler = provider.GetRequiredService<
            ICommandHandler<DepartStaffMemberCommand, StaffMemberMutationReceiptDto>>();
        Guid operationId = Guid.NewGuid();
        DateOnly today = DateOnly.FromDateTime(TestClock.Now.UtcDateTime);

        Result<StaffMemberMutationReceiptDto> denied = await handler.HandleAsync(
            new DepartStaffMemberCommand(
                operationId,
                member.Id,
                today.AddDays(1),
                "Contract ended",
                member.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.Equal(StaffDomainErrors.AssignmentDateInvalid, denied.Error);
        Assert.Equal(StaffMemberState.Active, member.Status);
        Assert.Empty(policy.Contexts);
        Assert.Empty(operations.Records);

        Result<StaffMemberMutationReceiptDto> retried = await handler.HandleAsync(
            new DepartStaffMemberCommand(
                operationId,
                member.Id,
                today,
                "Contract ended",
                member.Version,
                "user:owner"),
            CancellationToken.None);

        Assert.True(retried.IsSuccess, retried.Error.Code);
        Assert.Equal(StaffStatus.Departed, retried.Value.Status);
        Assert.Single(policy.Contexts);
        Assert.Single(operations.Records);
    }

    [Fact]
    public async Task Owner_protection_is_reported_without_exposing_cross_module_details()
    {
        StaffMember member = CreateMember("member-100");
        using ServiceProvider provider = CreateProvider(
            new FakeStaffMemberRepository(member),
            new FakePropertyProjectionRepository(),
            lifecyclePolicy: new RecordingLifecyclePolicy(StaffLifecyclePolicyDecision.OwnerProtected));
        var handler = provider.GetRequiredService<
            ICommandHandler<SuspendStaffMemberCommand, StaffMemberMutationReceiptDto>>();

        Result<StaffMemberMutationReceiptDto> result = await handler.HandleAsync(
            new SuspendStaffMemberCommand(
                Guid.NewGuid(), member.Id, "Leave", member.Version, "user:owner"),
            CancellationToken.None);

        Assert.Equal(StaffApplicationErrors.WorkspaceOwnerProtected, result.Error);
    }

    private static ServiceProvider CreateProvider(
        IStaffMemberRepository members,
        IStaffPropertyProjectionRepository properties,
        IScopeContext? scope = null,
        IStaffLifecyclePolicy? lifecyclePolicy = null,
        IStaffOperationLock? operationLock = null,
        IStaffCreationOperationLock? creationLock = null,
        IStaffIdentityProvisioningAnchorRepository? identityAnchors = null,
        IStaffIdentityProvisioningAnchorResolutionRepository?
            identityAnchorResolutions = null,
        IStaffMemberMutationOperationRepository? memberMutationOperations = null,
        IStaffOnboardingProvisioningOperationRepository?
            onboardingOperations = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(members);
        services.AddSingleton(properties);
        services.AddSingleton(scope ?? new TestScopeContext(true, "tenant-a"));
        services.AddSingleton<ISystemClock>(new TestClock());
        services.AddSingleton<IIdGenerator>(new TestIdGenerator());
        services.AddSingleton(operationLock ?? new NoopStaffOperationLock());
        services.AddSingleton(
            creationLock ?? new NoopStaffCreationOperationLock());
        IStaffIdentityProvisioningAnchorRepository anchorRepository =
            identityAnchors ?? new RecordingIdentityProvisioningAnchors();
        services.AddSingleton(anchorRepository);
        services.AddSingleton<IStaffIdentityProvisioningAnchorWriter>(
            new StubStaffIdentityProvisioningAnchorWriter(anchorRepository));
        services.AddSingleton<
            IStaffIdentityProvisioningAnchorResolutionRepository>(
                identityAnchorResolutions ??
                new StubStaffIdentityProvisioningAnchorResolutionRepository());
        services.AddSingleton(
            memberMutationOperations ?? new RecordingMemberMutationOperations());
        services.AddSingleton(
            onboardingOperations ?? new RecordingOnboardingOperations());
        if (lifecyclePolicy is not null)
        {
            services.AddSingleton(lifecyclePolicy);
        }
        services.AddStaffApplication();
        return services.BuildServiceProvider();
    }

    private static CreateStaffMemberCommand CreateCommand(
        Guid? operationId = null,
        string displayName = "Ada Operator",
        string workEmail = "ada@example.test",
        string? employeeNumber = "EMP-100") => new(
        operationId ?? Guid.NewGuid(), displayName, null, workEmail, null, employeeNumber,
        "Manager", "Operations", "user:owner");

    private static UpdateStaffMemberCommand UpdateCommand(
        StaffMember member,
        Guid operationId,
        long expectedVersion,
        string displayName) => new(
        operationId,
        member.Id,
        displayName,
        member.LegalName,
        member.WorkEmail,
        member.WorkPhone,
        member.EmployeeNumber,
        member.JobTitle,
        member.Department,
        expectedVersion,
        "user:owner");

    private static StaffMember CreateMember(string? authSubjectId = null) => StaffMember.Create(
        Guid.NewGuid(), "tenant-a", "Ada Operator", null, "ada@example.test", null,
        "EMP-100", "Manager", "Operations", authSubjectId, "user:owner", Guid.NewGuid(), TestClock.Now).Value;

    private static StaffMember CreateMemberWithId(
        Guid staffMemberId,
        string? authSubjectId = null) => StaffMember.Create(
        staffMemberId, "tenant-a", "Ada Operator", null, "ada@example.test", null,
        "EMP-100", "Manager", "Operations", authSubjectId, "user:owner",
        Guid.NewGuid(), TestClock.Now).Value;

    private static StaffMember CreateMemberForCreation(Guid id) => StaffMember.Create(
        id,
        "tenant-a",
        "Ada Operator",
        legalName: null,
        "ada@example.test",
        workPhone: null,
        "EMP-100",
        "Manager",
        "Operations",
        authSubjectId: null,
        "user:original-owner",
        Guid.NewGuid(),
        TestClock.Now).Value;

    private sealed class FakeStaffMemberRepository(StaffMember? member = null) : IStaffMemberRepository
    {
        public string? ExistingEmployeeNumber { get; init; }
        public string? ExistingAuthSubjectId { get; init; }
        public StaffMember? AddedMember { get; private set; }
        public int AddCount { get; private set; }
        public bool OperationallyVisible { get; set; } = true;
        public int OperationalGetCount { get; private set; }
        public int SafetyGetCount { get; private set; }
        public int EmployeeNumberExistsCount { get; private set; }
        public int AuthSubjectExistsCount { get; private set; }
        public List<string>? Calls { get; init; }

        public Task AddAsync(StaffMember value, CancellationToken cancellationToken)
        {
            this.AddedMember = value;
            this.AddCount++;
            return Task.CompletedTask;
        }

        public Task<StaffMember?> GetAsync(Guid staffMemberId, CancellationToken cancellationToken)
        {
            this.Calls?.Add("operational-read");
            this.OperationalGetCount++;
            return Task.FromResult(
                this.OperationallyVisible
                    ? this.Candidates().FirstOrDefault(candidate => candidate.Id == staffMemberId)
                    : null);
        }

        public Task<StaffMember?> GetForDataRightsAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Candidates().FirstOrDefault(
                candidate => candidate.Id == staffMemberId));

        public Task<StaffMember?> GetForSafetyTransitionAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.Calls?.Add("safety-read");
            this.SafetyGetCount++;
            return Task.FromResult(this.Candidates().FirstOrDefault(
                candidate => candidate.Id == staffMemberId));
        }

        public Task<StaffMember?> GetForSafetyTransitionByAuthSubjectAsync(
            string authSubjectId,
            CancellationToken cancellationToken)
        {
            this.Calls?.Add("safety-subject-read");
            this.SafetyGetCount++;
            string normalized = authSubjectId.Trim();
            return Task.FromResult(this.Candidates().FirstOrDefault(
                candidate => string.Equals(
                    candidate.AuthSubjectId,
                    normalized,
                    StringComparison.Ordinal)));
        }

        public Task<StaffMember?> GetByAuthSubjectAsync(string authSubjectId,
            CancellationToken cancellationToken)
        {
            this.OperationalGetCount++;
            return Task.FromResult(
                this.OperationallyVisible
                    ? this.Candidates().FirstOrDefault(candidate => string.Equals(
                        candidate.AuthSubjectId,
                        authSubjectId.Trim(),
                        StringComparison.Ordinal))
                    : null);
        }

        public Task<StaffDirectoryMemberDto?> GetDirectoryAsync(Guid staffMemberId,
            CancellationToken cancellationToken) => Task.FromResult(
            member?.Id == staffMemberId ? ToDirectory(member) : null);

        public Task<StaffDirectoryMemberDto?> GetDirectoryAtPropertyAsync(Guid propertyId,
            Guid staffMemberId, CancellationToken cancellationToken) => Task.FromResult(
            member?.Id == staffMemberId ? ToDirectory(member, propertyId) : null);

        public Task<StaffDirectoryListResponse> ListDirectoryAsync(string? search, StaffStatus? status,
            PageRequest pageRequest, CancellationToken cancellationToken) =>
            Task.FromResult(new StaffDirectoryListResponse(
                [], pageRequest.Page, pageRequest.PageSize, false));

        public Task<StaffPropertyDirectoryListResponse> ListDirectoryAtPropertyAsync(Guid propertyId,
            string? search, StaffStatus? status, PageRequest pageRequest,
            CancellationToken cancellationToken) => Task.FromResult(
            new StaffPropertyDirectoryListResponse(
                [], pageRequest.Page, pageRequest.PageSize, false));

        public Task<StaffAccountDirectoryResponse> ResolveAccountDirectoryAsync(
            IReadOnlyCollection<string> authSubjectIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StaffAccountDirectoryResponse([]));

        public Task<bool> EmployeeNumberExistsAsync(string employeeNumber, Guid? exceptStaffMemberId,
            CancellationToken cancellationToken)
        {
            this.EmployeeNumberExistsCount++;
            return Task.FromResult(
                string.Equals(employeeNumber, this.ExistingEmployeeNumber, StringComparison.Ordinal) ||
                this.Candidates().Any(candidate =>
                    candidate.Id != exceptStaffMemberId &&
                    string.Equals(
                        candidate.EmployeeNumber,
                        employeeNumber,
                        StringComparison.OrdinalIgnoreCase)));
        }

        public Task<bool> AuthSubjectExistsAsync(string authSubjectId, Guid? exceptStaffMemberId,
            CancellationToken cancellationToken)
        {
            this.AuthSubjectExistsCount++;
            return Task.FromResult(
                string.Equals(authSubjectId, this.ExistingAuthSubjectId, StringComparison.Ordinal) ||
                this.Candidates().Any(candidate =>
                    candidate.Id != exceptStaffMemberId &&
                    string.Equals(
                        candidate.AuthSubjectId,
                        authSubjectId,
                        StringComparison.Ordinal)));
        }

        private IEnumerable<StaffMember> Candidates() =>
            new[] { member, this.AddedMember }.OfType<StaffMember>().Distinct();

        private static StaffDirectoryMemberDto ToDirectory(StaffMember value, Guid? propertyId = null) => new(
            value.Id,
            value.DisplayName,
            value.JobTitle,
            value.Department,
            (StaffStatus)value.Status,
            value.Version,
            value.Assignments
                .Where(assignment => assignment.IsCurrent &&
                    (!propertyId.HasValue || assignment.PropertyId == propertyId.Value))
                .Select(assignment => new StaffDirectoryAssignmentDto(
                    assignment.Id,
                    assignment.PropertyId,
                    assignment.PropertyJobTitle,
                    assignment.IsPrimary,
                    assignment.EffectiveFrom))
                .ToArray());
    }

    private sealed class FakePropertyProjectionRepository(params Guid[] activeProperties)
        : IStaffPropertyProjectionRepository
    {
        public Task<bool> IsActiveAsync(Guid propertyId, CancellationToken cancellationToken) =>
            Task.FromResult(activeProperties.Contains(propertyId));

        public Task<bool> AreAllActiveAsync(
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken cancellationToken) => Task.FromResult(
            propertyIds.All(activeProperties.Contains));

        public Task ApplyAsync(StaffPropertyProjectionWriteModel property,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestScopeContext(bool enabled, string? scopeId) : IScopeContext
    {
        public bool IsEnabled => enabled;
        public string? ScopeId => scopeId;
    }

    private sealed class TestClock : ISystemClock
    {
        public static DateTimeOffset Now { get; } = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class RecordingLifecyclePolicy(StaffLifecyclePolicyDecision decision)
        : IStaffLifecyclePolicy
    {
        public List<StaffLifecyclePolicyContext> Contexts { get; } = [];
        public StaffLifecyclePolicyContext? Context => this.Contexts.LastOrDefault();

        public ValueTask<StaffLifecyclePolicyDecision> PrepareAsync(
            StaffLifecyclePolicyContext context,
            CancellationToken cancellationToken = default)
        {
            this.Contexts.Add(context);
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class RecordingOperationLock(Action? onAcquire = null)
        : IStaffOperationLock
    {
        public List<(string TenantId, Guid StaffMemberId)> Acquisitions { get; } = [];
        public Action<Guid>? OnAcquireById { get; set; }

        public Task<long?> GetStaffMemberRevisionAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken) => Task.FromResult<long?>(1);

        public Task<bool> TryAcquireStaffMemberAsync(
            string tenantId,
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.Acquisitions.Add((tenantId, staffMemberId));
            onAcquire?.Invoke();
            this.OnAcquireById?.Invoke(staffMemberId);
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingCreationOperationLock(
        ICollection<string>? calls = null) : IStaffCreationOperationLock
    {
        public List<(string TenantId, Guid OperationId)> Acquisitions { get; } = [];

        public Task AcquireAsync(
            string tenantId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            calls?.Add("creation-lock");
            this.Acquisitions.Add((tenantId, operationId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMemberMutationOperations
        : IStaffMemberMutationOperationRepository
    {
        public List<StaffMemberMutationOperationRecord> Records { get; } = [];
        public int GetCount { get; private set; }

        public Task<StaffMemberMutationOperationRecord?> GetAsync(
            Guid staffMemberId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            this.GetCount++;
            return Task.FromResult(this.Records.SingleOrDefault(record =>
                record.StaffMemberId == staffMemberId &&
                record.OperationId == operationId));
        }

        public Task AddAsync(
            StaffMemberMutationOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.Records.Add(operation);
            return Task.CompletedTask;
        }

        public Task DeleteForStaffMemberAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            this.Records.RemoveAll(record =>
                record.StaffMemberId == staffMemberId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIdentityProvisioningAnchors
        : IStaffIdentityProvisioningAnchorRepository
    {
        public List<StaffIdentityProvisioningAnchorRecord> Records { get; } = [];

        public Task<StaffIdentityProvisioningAnchorRecord?> GetAsync(
            StaffIdentityProvisioningSourceKind sourceKind,
            Guid sourceId,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Records.SingleOrDefault(record =>
                record.SourceKind == sourceKind &&
                record.SourceId == sourceId));

        public Task<bool> HasWorkspaceOnboardingAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Records.Any(record =>
                record.SourceKind ==
                    StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
                record.StaffMemberId == staffMemberId));

        public Task AddAsync(
            StaffIdentityProvisioningAnchorRecord anchor,
            CancellationToken cancellationToken)
        {
            this.Records.Add(anchor);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOnboardingOperations
        : IStaffOnboardingProvisioningOperationRepository
    {
        public List<StaffMemberMutationOperationRecord> Records { get; } = [];

        public Task<StaffMemberMutationOperationRecord?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(
            this.Records.SingleOrDefault(record =>
                record.OperationId == operationId));

        public Task AddAsync(
            StaffMemberMutationOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.Records.Add(operation);
            return Task.CompletedTask;
        }
    }
}
