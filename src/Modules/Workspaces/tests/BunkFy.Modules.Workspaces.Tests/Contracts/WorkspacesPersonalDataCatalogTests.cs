namespace BunkFy.Modules.Workspaces.Tests;

using System.Reflection;
using BunkFy.DataGovernance;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.AdminApi;
using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Workspaces.Api.Requests;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Events;
using BunkFy.Modules.Workspaces.Domain.Termination;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Gma.Framework.Messaging;
using Gma.Framework.Scoping;
using Gma.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacesPersonalDataCatalogTests
{
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();
    private static readonly Dictionary<Type, HashSet<string>> NonPersonalMembers = new()
    {
        [typeof(WorkspaceStaffOnboardingListResponse)] = PaginationMembers(),
        [typeof(WorkspaceStaffOnboardingSummaryListResponse)] = PaginationMembers(),
        [typeof(WorkspaceStaffAccessProcessListResponse)] = PaginationMembers(),
        [typeof(WorkspaceStaffJoinSourceListResponse)] = PaginationMembers(),
        [typeof(WorkspaceStaffIdentityAnchorSourcePage)] =
            new([nameof(WorkspaceStaffIdentityAnchorSourcePage.HasMore)],
                StringComparer.Ordinal)
    };

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_member()
    {
        foreach (PersonalDataMemberBinding binding in Bindings())
        {
            Assert.True(
                Assemblies.TryGetValue(binding.Assembly, out Assembly? assembly),
                $"Unknown assembly '{binding.Assembly}'.");
            Type? type = assembly.GetType(binding.Type, throwOnError: false, ignoreCase: false);
            Assert.NotNull(type);
            Assert.True(
                BindingResolves(type, binding.Member),
                $"Unknown member '{binding.Member}' on '{binding.Type}' in assembly '{binding.Assembly}'.");
        }
    }

    [Fact]
    public void Every_workspaces_owned_personal_persistence_member_is_classified()
    {
        using WorkspacesDbContext dbContext = CreateDbContext();
        foreach (Type entityType in PersistenceTypes())
        {
            IEntityType model = Assert.Single(
                dbContext.Model.GetEntityTypes(),
                candidate => candidate.ClrType == entityType);
            foreach (IProperty property in model.GetProperties().Where(property => !property.IsShadowProperty()))
            {
                AssertBinding(entityType, property.Name, PersonalDataSurface.Persistence);
            }
        }
    }

    [Fact]
    public void Every_selected_command_query_api_admin_response_and_consumed_event_member_is_classified()
    {
        foreach ((PersonalDataSurface surface, Type type) in BoundaryTypes())
        {
            AssertType(type, surface);
        }

        foreach ((PersonalDataSurface surface, Type type, string method) in
                 BoundaryMethods())
        {
            AssertMethod(type, method, surface);
        }
    }

    [Fact]
    public void Identity_anchor_cutover_evidence_is_pseudonymous_and_transient()
    {
        Assert.Equal(18, Catalogue.CatalogVersion);
        Assert.Equal(
            WorkspacesTenantTerminationMetadata.PersonalDataCatalogVersion,
            Catalogue.CatalogVersion);
        foreach (string fieldId in new[]
                 {
                     "workspaces.identity-anchor-cutover.historical-evidence-digest",
                     "workspaces.identity-anchor-cutover.owner-manifest-digest",
                     "workspaces.identity-anchor-cutover.source-evidence-digest",
                     "workspaces.identity-anchor-cutover.state-digest"
                 })
        {
            PersonalDataFieldDefinition field = Assert.Single(
                Catalogue.Fields,
                candidate => candidate.Id == fieldId);
            Assert.Equal(
                PersonalDataClassification.PseudonymousIdentifier,
                field.Classification);
            Assert.Equal(PersonalDataSensitivity.Elevated, field.Sensitivity);
            Assert.Equal("transient-response", field.RetentionPolicy);
            Assert.Equal(
                "identity-anchor-cutover-evidence-control",
                field.RightsPolicy);
        }

        AssertBinding(
            typeof(WorkspaceStaffIdentityAnchorCutoverStatus),
            nameof(WorkspaceStaffIdentityAnchorCutoverStatus
                .SourceEvidenceSha256),
            PersonalDataSurface.AdminOutput);
        AssertBinding(
            typeof(WorkspaceStaffIdentityAnchorCutoverStatus),
            nameof(WorkspaceStaffIdentityAnchorCutoverStatus
                .AnchorStateSha256),
            PersonalDataSurface.AdminOutput);
        AssertBinding(
            typeof(WorkspaceStaffIdentityAnchorCutoverStatus),
            nameof(WorkspaceStaffIdentityAnchorCutoverStatus
                .OwnerManifestSha256),
            PersonalDataSurface.AdminOutput);
        AssertBinding(
            typeof(WorkspaceStaffIdentityAnchorCutoverStatus),
            nameof(WorkspaceStaffIdentityAnchorCutoverStatus
                .HistoricalEvidenceSha256),
            PersonalDataSurface.AdminOutput);
        AssertBinding(
            typeof(WorkspaceStaffIdentityAnchorHistoricalEvidence),
            nameof(WorkspaceStaffIdentityAnchorHistoricalEvidence
                .EvidenceSha256),
            PersonalDataSurface.ApplicationCommand);
        AssertBinding(
            typeof(WorkspaceStaffIdentityAnchorHistoricalEvidence),
            nameof(WorkspaceStaffIdentityAnchorHistoricalEvidence
                .EvidenceSha256),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(ReconcileWorkspaceStaffIdentityAnchorsCommand),
            nameof(ReconcileWorkspaceStaffIdentityAnchorsCommand
                .ExpectedSourceEvidenceSha256),
            PersonalDataSurface.ApplicationCommand);
        AssertBinding(
            typeof(ReconcileWorkspaceStaffIdentityAnchorsCommand),
            nameof(ReconcileWorkspaceStaffIdentityAnchorsCommand
                .ExpectedAnchorStateSha256),
            PersonalDataSurface.ApplicationCommand);
        AssertBinding(
            typeof(ReconcileWorkspaceStaffIdentityAnchorsCommand),
            nameof(ReconcileWorkspaceStaffIdentityAnchorsCommand
                .ExpectedOwnerManifestSha256),
            PersonalDataSurface.ApplicationCommand);
    }

    [Fact]
    public void Historical_no_provision_receipt_has_a_tenant_audit_proof_policy_and_actor_attribution()
    {
        PersonalDataFieldDefinition[] proofFields = Catalogue.Fields
            .Where(field => field.Id.StartsWith(
                "workspaces.identity-anchor-historical-no-provision.",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(21, proofFields.Length);
        Assert.All(proofFields, field =>
        {
            Assert.Equal(
                "workspace-historical-no-provision-proof",
                field.RetentionPolicy);
            Assert.Equal(
                "workspace-historical-no-provision-proof-control",
                field.RightsPolicy);
            Assert.Equal(
                "workspaces-identity-anchor-historical-no-provision",
                field.AccessPolicy);
            Assert.DoesNotContain(
                field.Classification,
                new[]
                {
                    PersonalDataClassification.DirectIdentifier,
                    PersonalDataClassification.Contact,
                    PersonalDataClassification.FreeText
                });
        });

        PersonalDataFieldDefinition reviewer = Assert.Single(
            Catalogue.Fields,
            field => field.Id == "workspaces.actor-subject-id");
        Assert.Equal(
            PersonalDataClassification.AuditAttribution,
            reviewer.Classification);
        Assert.Equal("auth", reviewer.AuthoritativeOwner);
        Assert.Equal("audit-attribution", reviewer.RightsPolicy);
        AssertBinding(
            typeof(WorkspaceStaffHistoricalNoProvisionReceipt),
            nameof(WorkspaceStaffHistoricalNoProvisionReceipt.ReviewerId),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(ReviewWorkspaceStaffHistoricalNoProvisionCommand),
            nameof(ReviewWorkspaceStaffHistoricalNoProvisionCommand
                .ExpectedOrganizationsSourceStatus),
            PersonalDataSurface.ApplicationCommand);
        AssertBinding(
            typeof(
                WorkspaceStaffHistoricalNoProvisionReceiptDataRightsExport),
            nameof(WorkspaceStaffHistoricalNoProvisionReceiptDataRightsExport
                .OrganizationsSourceStatus),
            PersonalDataSurface.DataRightsExport);
    }

    [Fact]
    public void Direct_or_unstructured_applicant_data_cannot_enter_operational_outputs()
    {
        HashSet<PersonalDataClassification> restricted =
        [
            PersonalDataClassification.DirectIdentifier,
            PersonalDataClassification.Contact,
            PersonalDataClassification.Demographic,
            PersonalDataClassification.Preference,
            PersonalDataClassification.FreeText,
            PersonalDataClassification.SearchInput,
            PersonalDataClassification.StructuredPayload
        ];
        HashSet<PersonalDataSurface> prohibited =
        [
            PersonalDataSurface.IntegrationEvent,
            PersonalDataSurface.Notification,
            PersonalDataSurface.Log,
            PersonalDataSurface.Metric,
            PersonalDataSurface.Trace,
            PersonalDataSurface.SupportBundle
        ];

        PersonalDataFieldDefinition[] offenders = Catalogue.Fields
            .Where(field => restricted.Contains(field.Classification))
            .Where(field => field.AllowedSurfaces.Any(prohibited.Contains) ||
                            field.Bindings.Any(binding => prohibited.Contains(binding.Surface)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Checked_in_inventory_matches_deterministic_catalogue_rendering()
    {
        string expected = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "DataGovernance",
                "personal-data-inventory.v1.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, PersonalDataInventoryRenderer.RenderMarkdown(Catalogue));
    }

    private static void AssertType(Type type, PersonalDataSurface surface)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            bool isIntegrationEventMetadata = typeof(IIntegrationEvent).IsAssignableFrom(type) &&
                                              property.Name is "EventName" or "Version";
            if (isIntegrationEventMetadata ||
                (NonPersonalMembers.TryGetValue(type, out HashSet<string>? excluded) &&
                 excluded.Contains(property.Name)))
            {
                continue;
            }

            AssertBinding(type, property.Name, surface);
        }
    }

    private static void AssertBinding(Type type, string member, PersonalDataSurface surface)
    {
        bool found = Bindings().Any(binding =>
            string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
            string.Equals(binding.Type, type.FullName, StringComparison.Ordinal) &&
            string.Equals(binding.Member, member, StringComparison.Ordinal) &&
            binding.Surface == surface);
        Assert.True(found, $"Missing {surface} classification for {type.FullName}.{member}.");
    }

    private static void AssertMethod(
        Type type,
        string methodName,
        PersonalDataSurface surface)
    {
        MethodInfo method = Assert.Single(
            type.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            candidate => candidate.Name == methodName);
        foreach (ParameterInfo parameter in method.GetParameters()
                     .Where(parameter =>
                         parameter.ParameterType != typeof(CancellationToken)))
        {
            AssertBinding(
                type,
                $"{method.Name}.{parameter.Name}",
                surface);
        }
    }

    private static bool BindingResolves(Type type, string member)
    {
        if (type.GetProperty(
                member,
                BindingFlags.Instance | BindingFlags.Public) is not null)
        {
            return true;
        }

        int separator = member.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == member.Length - 1)
        {
            return false;
        }

        string methodName = member[..separator];
        string parameterName = member[(separator + 1)..];
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == methodName)
            .SelectMany(method => method.GetParameters())
            .Any(parameter => string.Equals(
                parameter.Name,
                parameterName,
                StringComparison.Ordinal));
    }

    private static IEnumerable<PersonalDataMemberBinding> Bindings() =>
        Catalogue.Fields.SelectMany(field => field.Bindings);

    private static Type[] PersistenceTypes() =>
    [
        typeof(WorkspaceStaffOnboarding),
        typeof(WorkspaceStaffOnboardingRecoveryReceipt),
        typeof(WorkspaceStaffDeferredClaimWithdrawal),
        typeof(WorkspaceStaffOnboardingCorrectionReceipt),
        typeof(WorkspaceStaffOnboardingProcessingRestriction),
        typeof(WorkspaceStaffOnboardingProcessingRestrictionProjection),
        typeof(WorkspaceStaffOnboardingProcessingRestrictionReceipt),
        typeof(WorkspaceStaffCorrelationAnonymisationReceipt),
        typeof(WorkspaceStaffCorrelationAnonymisationRestoreReceipt),
        typeof(WorkspaceStaffCorrelationAnonymisationTombstone),
        typeof(WorkspaceStaffAccessProcess),
        typeof(WorkspaceStaffAccessProfileSnapshot),
        typeof(WorkspaceStaffAccessPlan),
        typeof(WorkspaceStaffAccessPlanProperty),
        typeof(WorkspaceStaffRetentionCorrelationReceipt),
        typeof(WorkspaceStaffOnboardingRetentionExecution),
        typeof(WorkspaceStaffIdentityAnchorSweepCheckpoint),
        typeof(WorkspaceStaffHistoricalNoProvisionReceipt),
        typeof(WorkspaceTerminationFence),
        typeof(WorkspaceTerminationFenceReceipt),
        typeof(WorkspaceTenantDestroyOperation),
        typeof(WorkspaceTenantDestroyReceipt)
    ];

    private static IEnumerable<(PersonalDataSurface Surface, Type Type)> BoundaryTypes()
    {
        foreach (Type type in new[]
                 {
                     typeof(SubmitWorkspaceStaffOnboardingRequest),
                     typeof(IssueWorkspaceInvitationRequest),
                     typeof(IssueWorkspaceEnrollmentLinkRequest),
                     typeof(ManageWorkspaceJoinSourceRequest),
                     typeof(ReplaceWorkspaceJoinSourceRequest),
                     typeof(UpdateWorkspaceMemberAccessRequest),
                     typeof(
                         WorkspaceStaffOnboardingDataRightsCorrectionRequest),
                     typeof(WorkspaceStaffOnboardingRecoveryAttemptRequest)
                 })
        {
            yield return (PersonalDataSurface.ApiInput, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(SubmitWorkspaceStaffOnboardingCommand),
                     typeof(PrepareWorkspaceStaffAccessCommand),
                     typeof(DenyWorkspaceStaffAccessCommand),
                     typeof(RetryWorkspaceStaffAccessProcessCommand),
                     typeof(RetryWorkspaceStaffOnboardingCommand),
                     typeof(AttemptWorkspaceStaffOnboardingRecoveryCommand),
                     typeof(PrepareWorkspaceStaffAccessPlanCommand),
                     typeof(ActivateWorkspaceStaffAccessPlanCommand),
                     typeof(
                         ScrubWorkspaceStaffRetentionCorrelationCommand),
                     typeof(
                         WorkspaceStaffRetentionCorrelationScrubRequest),
                     typeof(WorkspaceInvitationIssuanceRequest),
                     typeof(WorkspaceEnrollmentLinkIssuanceRequest),
                     typeof(WorkspaceMemberAccessUpdate),
                     typeof(
                         ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand),
                     typeof(
                         ApplyWorkspaceStaffOnboardingProcessingRestrictionCommand),
                     typeof(
                         ReleaseWorkspaceStaffOnboardingProcessingRestrictionCommand)
                     ,
                     typeof(
                         ApplyWorkspaceStaffCorrelationAnonymisationCommand),
                     typeof(
                         RestoreWorkspaceStaffCorrelationAnonymisationCommand),
                     typeof(
                         WorkspaceStaffCorrelationAnonymisationApplyRequest),
                     typeof(
                         WorkspaceStaffCorrelationAnonymisationRestoreRequest)
                     ,
                     typeof(
                         BeginWorkspaceStaffOnboardingRetentionExecutionCommand),
                     typeof(
                         WorkspaceStaffOnboardingRetentionExecutionStart),
                     typeof(
                         ListWorkspaceStaffOnboardingRetentionCandidatesCommand),
                     typeof(
                         ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand),
                     typeof(
                         CompleteWorkspaceStaffOnboardingRetentionExecutionCommand),
                     typeof(
                         PrepareWorkspaceStaffIdentityAnchorSweepPageCommand),
                     typeof(
                         AdvanceWorkspaceStaffIdentityAnchorSweepCommand),
                     typeof(
                         ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand),
                     typeof(WorkspaceStaffIdentityAnchorSweepAdvance),
                     typeof(WorkspaceStaffIdentityAnchorSweepPageCounts),
                     typeof(ReconcileWorkspaceStaffIdentityAnchorsPayload),
                     typeof(
                         ReviewWorkspaceStaffHistoricalNoProvisionCommand),
                     typeof(ApplyWorkspaceTerminationFenceCommand),
                     typeof(ReleaseWorkspaceTerminationFenceCommand)
                 })
        {
            yield return (PersonalDataSurface.ApplicationCommand, type);
        }

        yield return (PersonalDataSurface.ApplicationQuery, typeof(GetOwnWorkspaceStaffOnboardingQuery));
        yield return (PersonalDataSurface.ApplicationQuery, typeof(GetWorkspaceStaffOnboardingReviewQuery));
        yield return (
            PersonalDataSurface.ApplicationQuery,
            typeof(GetLatestWorkspaceStaffOnboardingRecoveryAttemptQuery));
        yield return (
            PersonalDataSurface.ApplicationQuery,
            typeof(GetWorkspaceStaffIdentityAnchorSweepStatusQuery));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceStaffIdentityAnchorSweepCandidate));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceStaffIdentityAnchorSweepPage));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceStaffIdentityAnchorSweepCandidateResult));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceStaffIdentityAnchorSourceRecord));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceStaffIdentityAnchorSourcePage));
        yield return (
            PersonalDataSurface.ApplicationQuery,
            typeof(
                GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQuery));
        yield return (PersonalDataSurface.ProjectionExport, typeof(WorkspaceStaffAccessPreparation));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(
                WorkspaceStaffCorrelationAnonymisationSnapshot));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(
                WorkspaceStaffCorrelationAnonymisationReceiptDto));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceTerminationFenceSnapshot));
        yield return (
            PersonalDataSurface.ProjectionExport,
            typeof(WorkspaceTerminationFenceReceiptDto));

        foreach (Type type in new[]
                 {
                     typeof(WorkspaceStaffOnboardingDto),
                     typeof(WorkspaceStaffOnboardingListResponse),
                     typeof(WorkspaceStaffOnboardingSummaryListResponse),
                     typeof(WorkspaceStaffOnboardingActionableSummaryDto),
                     typeof(WorkspaceMemberAccessDto),
                     typeof(WorkspaceMemberAccessAssignmentDto),
                     typeof(WorkspaceStaffJoinSourceDto),
                     typeof(WorkspaceStaffJoinSourceListResponse),
                     typeof(WorkspaceStaffJoinSourceReplacementDto),
                     typeof(WorkspaceStaffAccessPlanDto),
                     typeof(WorkspaceStaffJoinSourceIssuanceDto),
                     typeof(
                         WorkspaceStaffOnboardingDataRightsCorrectionTargetDto),
                     typeof(
                         WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto),
                     typeof(WorkspaceStaffOnboardingRecoveryAttemptDto)
                 })
        {
            yield return (PersonalDataSurface.ApiResponse, type);
        }

        yield return (PersonalDataSurface.AdminOutput, typeof(WorkspaceStaffAccessProcessDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(WorkspaceStaffAccessProcessListResponse));
        yield return (
            PersonalDataSurface.AdminOutput,
            typeof(
                WorkspaceStaffOnboardingProcessingRestrictionReceiptDto));
        yield return (
            PersonalDataSurface.AdminOutput,
            typeof(WorkspaceStaffIdentityAnchorSweepStatus));
        yield return (
            PersonalDataSurface.AdminOutput,
            typeof(WorkspaceStaffIdentityAnchorSweepPageCounts));
        yield return (
            PersonalDataSurface.AdminOutput,
            typeof(WorkspaceStaffHistoricalNoProvisionDispositionResult));

        foreach (Type type in new[]
                 {
                     typeof(WorkspaceStaffOnboardingDataRightsExport),
                     typeof(
                         WorkspaceStaffDeferredClaimWithdrawalDataRightsExport),
                     typeof(WorkspaceStaffAccessProcessDataRightsExport),
                     typeof(WorkspaceStaffAccessProfileDataRightsExport),
                     typeof(WorkspaceStaffAccessPlanDataRightsExport),
                     typeof(
                         WorkspaceStaffAccessPlanPropertyDataRightsExport),
                     typeof(
                         WorkspaceStaffRetentionCorrelationDataRightsExport),
                     typeof(
                         WorkspaceStaffOnboardingCorrectionReceiptDataRightsExport),
                     typeof(
                         WorkspaceStaffOnboardingRecoveryReceiptDataRightsExport),
                     typeof(
                         WorkspaceStaffOnboardingProcessingRestrictionDataRightsExport),
                     typeof(
                         WorkspaceStaffOnboardingProcessingRestrictionReceiptDataRightsExport),
                     typeof(
                         WorkspaceStaffOnboardingRetentionExecutionDataRightsExport),
                     typeof(
                         WorkspaceStaffHistoricalNoProvisionReceiptDataRightsExport)
                 })
        {
            yield return (PersonalDataSurface.DataRightsExport, type);
        }

        yield return (
            PersonalDataSurface.DomainEvent,
            typeof(
                WorkspaceStaffOnboardingCorrectionAppliedDomainEvent));
        yield return (
            PersonalDataSurface.DomainEvent,
            typeof(
                WorkspaceStaffOnboardingProcessingRestrictionChangedDomainEvent));
        yield return (
            PersonalDataSurface.DomainEvent,
            typeof(
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent));
        yield return (
            PersonalDataSurface.DomainEvent,
            typeof(
                WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent));

        foreach (Type type in new[]
                 {
                     typeof(OrganizationInvitationChangedIntegrationEvent),
                     typeof(OrganizationInvitationExpiredIntegrationEvent),
                     typeof(OrganizationEnrollmentClaimChangedIntegrationEvent),
                     typeof(OrganizationEnrollmentClaimExpiredIntegrationEvent),
                     typeof(OrganizationEnrollmentClaimWithdrawnIntegrationEvent),
                     typeof(OrganizationEnrollmentLinkChangedIntegrationEvent),
                     typeof(OrganizationEnrollmentLinkExpiredIntegrationEvent),
                     typeof(OrganizationMembershipChangedIntegrationEvent),
                     typeof(StaffMemberLifecycleChangedIntegrationEvent),
                     typeof(
                         WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent),
                     typeof(
                         WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent),
                     typeof(
                         WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent)
                 })
        {
            yield return (PersonalDataSurface.IntegrationEvent, type);
        }
    }

    private static IEnumerable<(
        PersonalDataSurface Surface,
        Type Type,
        string Method)> BoundaryMethods()
    {
        yield return (
            PersonalDataSurface.ApplicationQuery,
            typeof(
                IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence),
            nameof(
                IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
                    .CanMutateAsync));
        yield return (
            PersonalDataSurface.ApplicationQuery,
            typeof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository),
            nameof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository
                .FindByOperationIdAsync));
        yield return (
            PersonalDataSurface.ApplicationQuery,
            typeof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository),
            nameof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository
                .FindByApplicationIdAsync));
        yield return (
            PersonalDataSurface.ApplicationCommand,
            typeof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository),
            nameof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository
                .AddAsync));
    }

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(WorkspacesAdminApiModule).Assembly,
            typeof(WorkspacesModule).Assembly,
            typeof(SubmitWorkspaceStaffOnboardingCommand).Assembly,
            typeof(WorkspacesModuleMetadata).Assembly,
            typeof(WorkspaceStaffOnboarding).Assembly,
            typeof(WorkspacesDbContext).Assembly,
            typeof(OrganizationInvitationChangedIntegrationEvent).Assembly,
            typeof(StaffMemberLifecycleChangedIntegrationEvent).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static HashSet<string> PaginationMembers() =>
        new(["Page", "PageSize", "HasMore"], StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));

    private static WorkspacesDbContext CreateDbContext()
    {
        DbContextOptions<WorkspacesDbContext> options = new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase($"workspaces-data-catalog-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
