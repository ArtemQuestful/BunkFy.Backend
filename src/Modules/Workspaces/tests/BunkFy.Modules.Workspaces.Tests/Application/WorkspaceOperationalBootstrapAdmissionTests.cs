namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceOperationalBootstrapAdmissionTests
{
    private static readonly string WorkspaceId = Guid.NewGuid().ToString("D");

    [Fact]
    public async Task Restricted_workspace_rejects_bootstrap_before_provisioning()
    {
        WorkspaceAccessProvisioner provisioner = new(
            new ForbiddenRoles(),
            new ForbiddenProfiles());
        BootstrapWorkspaceAccessCommandHandler handler = new(
            provisioner,
            WorkspaceOperationalAdmissionTestSupport.Restricted(WorkspaceId),
            new StubScopeContext(WorkspaceId),
            NullLogger<BootstrapWorkspaceAccessCommandHandler>.Instance);

        Result<WorkspaceAccessBootstrapResult> result =
            await handler.HandleAsync(
                new BootstrapWorkspaceAccessCommand(),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceOperationalAdmissionErrors.ProcessingRestricted,
            result.Error);
    }

    private sealed class StubScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId => scopeId;
    }

    private sealed class ForbiddenRoles : IAccessControlRoleProvisioner
    {
        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<AccessControlPage<AccessControlRoleAssignment>>
            ListAssignmentsAsync(
                string roleName,
                AccessScope scope,
                int page,
                int pageSize,
                CancellationToken cancellationToken = default) =>
            throw Unexpected();
    }

    private sealed class ForbiddenProfiles
        : IAccessProfileProvisioner, IScopedAccessProfileProvisioner
    {
        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<AccessProfileAssignmentReconciliation>
            ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<Guid> profileIds,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<ScopedAccessProfileAssignmentSet>
            GetSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public Task<ScopedAccessProfileAssignmentReconciliation>
            ReconcileSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            throw Unexpected();
    }

    private static InvalidOperationException Unexpected() =>
        new("Access provisioning was reached.");
}
