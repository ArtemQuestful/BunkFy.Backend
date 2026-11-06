namespace BunkFy.Modules.DataRights.Tests.Contracts;

using System.Reflection;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Api;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using Xunit;
using DomainSubjectCoordinate = DataRights.Domain.Entities.DataRightsSubjectCoordinate;
using ExportAuditEntry =
    DataRights.Domain.Entities.DataRightsExportAuditEntry;
using FrozenTenantOwner =
    DataRights.Domain.Entities.TenantTerminationFrozenOwner;
using FrozenTenantOwnerDescriptor =
    DataRights.Domain.Models.TenantTerminationFrozenOwnerDescriptor;
using ProcessingLedgerEntry =
    DataRights.Domain.Entities.DataRightsProcessingLedgerEntry;
using ProcessingLedgerSnapshot =
    DataRights.Domain.Models.DataRightsProcessingLedgerSnapshot;
using RestoreCheckpoint = DataRights.Domain.Entities.DataRightsRestoreCheckpoint;

[Trait("Category", "Unit")]
public sealed class DataRightsPersonalDataCatalogTests
{
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_public_member()
    {
        foreach (PersonalDataMemberBinding binding in Bindings())
        {
            Assert.True(
                Assemblies.TryGetValue(binding.Assembly, out Assembly? assembly),
                $"Unknown assembly '{binding.Assembly}'.");
            Type? type = assembly.GetType(binding.Type, throwOnError: false, ignoreCase: false);
            Assert.NotNull(type);
            Assert.NotNull(type.GetProperty(binding.Member, BindingFlags.Instance | BindingFlags.Public));
        }
    }

    [Fact]
    public void Every_case_actor_coordinate_is_classified()
    {
        Type[] commands =
        [
            typeof(BeginDataRightsDiscoveryCommand),
            typeof(CancelDataRightsCaseCommand),
            typeof(CreateDataRightsCaseCommand),
            typeof(ExecuteDataRightsRestrictionCommand),
            typeof(RecordControllerRoutingCommand),
            typeof(RecordRequesterVerificationCommand),
            typeof(PrepareDataRightsExportDownloadCommand),
            typeof(RequestDataRightsExportCommand),
            typeof(RequestTenantTerminationCommand),
            typeof(DecideTenantTerminationCommand),
            typeof(StartTenantTerminationCommand),
            typeof(ConfirmTenantTerminationExportCommand),
            typeof(PrepareTenantTerminationExportDownloadCommand),
            typeof(RetryTenantTerminationCommand),
            typeof(RequestTenantTerminationCancellationCommand),
            typeof(RecoverTenantTerminationCommand),
            typeof(RequireDataRightsReviewCommand),
            typeof(SelectDataRightsSubjectCommand),
            typeof(StartDataRightsAnonymisationExecutionCommand),
            typeof(StartDataRightsCorrectionExecutionCommand),
            typeof(UnselectDataRightsSubjectCommand)
        ];

        foreach (Type command in commands)
        {
            AssertBinding(command, "ActorId", PersonalDataSurface.ApplicationCommand);
        }

        AssertBinding(
            typeof(DataRightsOperationApprovalRequest),
            nameof(DataRightsOperationApprovalRequest.ExecutingActorId),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(DataRightsAnonymisationContributionRequest),
            nameof(DataRightsAnonymisationContributionRequest.ExecutingActorId),
            PersonalDataSurface.IntegrationCommand);
        AssertBinding(
            typeof(DataRightsRestrictionContributionRequest),
            nameof(DataRightsRestrictionContributionRequest.ExecutingActorId),
            PersonalDataSurface.IntegrationCommand);
        AssertBinding(
            typeof(DataRightsCorrectionExecutionGateRequest),
            nameof(DataRightsCorrectionExecutionGateRequest.ExecutingActorId),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(GetDataRightsCorrectionExecutionQuery),
            nameof(GetDataRightsCorrectionExecutionQuery.ActorId),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(DataRightsCorrectionExecutionDetailsDto),
            nameof(DataRightsCorrectionExecutionDetailsDto.ClaimedBy),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(TenantTerminationReplayIntent),
            nameof(TenantTerminationReplayIntent.RequestedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationReplayIntent),
            nameof(TenantTerminationReplayIntent.ApprovedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationReplayIntent),
            nameof(TenantTerminationReplayIntent.ExecutingActorId),
            PersonalDataSurface.Persistence);
        AssertBinding(typeof(DataRightsCase), nameof(DataRightsCase.CreatedBy), PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsCase),
            nameof(DataRightsCase.ExecutionStartedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(typeof(DataRightsCase), nameof(DataRightsCase.LastChangedBy), PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsExportArtifact),
            nameof(DataRightsExportArtifact.RequestedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsExportArtifact),
            nameof(DataRightsExportArtifact.GenerationActor),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(ExportAuditEntry),
            nameof(ExportAuditEntry.ActorId),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsExecutionBatch),
            nameof(DataRightsExecutionBatch.CreatedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsExecutionWorkItem),
            nameof(DataRightsExecutionWorkItem.CreatedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DomainSubjectCoordinate),
            nameof(DomainSubjectCoordinate.SelectedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsRestrictionExecutionProof),
            nameof(DataRightsRestrictionExecutionProof.ExecutedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsCorrectionExecution),
            nameof(DataRightsCorrectionExecution.ExecutedBy),
            PersonalDataSurface.Persistence);
    }

    [Fact]
    public void Tenant_termination_operator_controls_are_explicitly_classified()
    {
        AssertPublicPropertiesClassified(
            typeof(RequestTenantTerminationCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(RequestTenantTerminationCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(DecideTenantTerminationCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(DecideTenantTerminationCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(StartTenantTerminationCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(StartTenantTerminationCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(ConfirmTenantTerminationExportCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(ConfirmTenantTerminationExportCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(PrepareTenantTerminationExportDownloadCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(PrepareTenantTerminationExportDownloadCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(RetryTenantTerminationCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(RetryTenantTerminationCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(RequestTenantTerminationCancellationCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(RequestTenantTerminationCancellationCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(RecoverTenantTerminationCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(RecoverTenantTerminationCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(GetTenantTerminationOperatorStatusQuery),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationApprovalEvidence),
            PersonalDataSurface.ApplicationCommand);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationReplayIntent),
            PersonalDataSurface.Persistence,
            nameof(TenantTerminationReplayIntent.RequestedBy),
            nameof(TenantTerminationReplayIntent.ApprovedBy),
            nameof(TenantTerminationReplayIntent.ExecutingActorId));
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationCaseDto),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationProcessDto),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationStartDto),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationOperatorStatusDto),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationExportHandoffDto),
            PersonalDataSurface.ApplicationQuery,
            nameof(TenantTerminationExportHandoffDto.ConfirmedBy));
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationOwnerWorkItemDto),
            PersonalDataSurface.ApplicationQuery);
    }

    [Fact]
    public void Discovery_personal_data_is_explicitly_classified()
    {
        Type apiRequest = typeof(DataRightsModule).Assembly.GetType(
            "BunkFy.Modules.DataRights.Api.DataRightsDiscoveryEndpoints+" +
            "DiscoverDataRightsSubjectsRequest",
            throwOnError: true)!;
        foreach (string member in new[]
                 {
                     "RecordId",
                     "Email",
                     "Phone",
                     "Name",
                     "DateOfBirth",
                     "AccountSubjectId"
                 })
        {
            AssertBinding(apiRequest, member, PersonalDataSurface.ApiInput);
            AssertBinding(typeof(DataRightsSubjectLookup), member, PersonalDataSurface.ApplicationQuery);
        }

        AssertBinding(
            typeof(DataRightsSubjectCoordinate),
            nameof(DataRightsSubjectCoordinate.RecordId),
            PersonalDataSurface.ApiInput);
        AssertBinding(
            typeof(DataRightsSubjectCoordinate),
            nameof(DataRightsSubjectCoordinate.RecordId),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DataRightsSubjectCoordinateKey),
            nameof(DataRightsSubjectCoordinateKey.RecordId),
            PersonalDataSurface.ApiInput);
        AssertBinding(
            typeof(DomainSubjectCoordinate),
            nameof(DomainSubjectCoordinate.RecordId),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsExecutionWorkItem),
            nameof(DataRightsExecutionWorkItem.RecordId),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsSelectedSubjectDto),
            nameof(DataRightsSelectedSubjectDto.RecordId),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DataRightsExecutionWorkItemDto),
            nameof(DataRightsExecutionWorkItemDto.RecordId),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DataRightsAnonymisationContributionRequest),
            nameof(DataRightsAnonymisationContributionRequest.Coordinate),
            PersonalDataSurface.IntegrationCommand);
        AssertBinding(
            typeof(DataRightsRestrictionContributionRequest),
            nameof(DataRightsRestrictionContributionRequest.Coordinate),
            PersonalDataSurface.IntegrationCommand);
        AssertBinding(
            typeof(DataRightsRestrictionExecutionProof),
            nameof(DataRightsRestrictionExecutionProof.RecordId),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsCorrectionExecutionGateRequest),
            nameof(DataRightsCorrectionExecutionGateRequest.Coordinate),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(DataRightsCorrectionAppliedIntegrationEvent),
            nameof(DataRightsCorrectionAppliedIntegrationEvent.RecordId),
            PersonalDataSurface.IntegrationEvent);
        AssertBinding(
            typeof(DataRightsCorrectionExecutionDetailsDto),
            nameof(DataRightsCorrectionExecutionDetailsDto.Subject),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DataRightsCorrectionExecution),
            nameof(DataRightsCorrectionExecution.RecordId),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsSelectedSubjectDto),
            nameof(DataRightsSelectedSubjectDto.SelectedAtUtc),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DomainSubjectCoordinate),
            nameof(DomainSubjectCoordinate.SelectedAtUtc),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsSubjectCandidate),
            nameof(DataRightsSubjectCandidate.DisplayName),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DataRightsSubjectCandidate),
            nameof(DataRightsSubjectCandidate.EmailHint),
            PersonalDataSurface.ApiResponse);
        AssertBinding(
            typeof(DataRightsSubjectCandidate),
            nameof(DataRightsSubjectCandidate.PhoneHint),
            PersonalDataSurface.ApiResponse);
    }

    [Fact]
    public void Owner_export_envelope_is_explicitly_classified()
    {
        foreach (string member in new[] { "RecordType", "RecordId", "RecordVersion", "Fields" })
        {
            AssertBinding(typeof(DataRightsExportRecord), member, PersonalDataSurface.DataRightsExport);
        }

        AssertBinding(
            typeof(DataRightsExportField),
            nameof(DataRightsExportField.FieldId),
            PersonalDataSurface.DataRightsExport);
        AssertBinding(
            typeof(DataRightsExportField),
            nameof(DataRightsExportField.Value),
            PersonalDataSurface.DataRightsExport);
    }

    [Fact]
    public void Protected_export_digests_are_explicitly_classified()
    {
        AssertBinding(
            typeof(DataRightsExportArtifact),
            nameof(DataRightsExportArtifact.SelectionSha256),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsExportArtifact),
            nameof(DataRightsExportArtifact.PlaintextSha256),
            PersonalDataSurface.Persistence);
    }

    [Fact]
    public void Restriction_execution_proof_is_explicitly_classified()
    {
        foreach (PropertyInfo property in typeof(DataRightsRestrictionContributionRequest)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.Name is not
                         nameof(DataRightsRestrictionContributionRequest.Coordinate) and not
                         nameof(DataRightsRestrictionContributionRequest.ExecutingActorId)))
        {
            AssertBinding(
                typeof(DataRightsRestrictionContributionRequest),
                property.Name,
                PersonalDataSurface.IntegrationCommand);
        }

        AssertPublicPropertiesClassified(
            typeof(DataRightsRestrictionTargetResolutionRequest),
            PersonalDataSurface.IntegrationCommand,
            nameof(DataRightsRestrictionTargetResolutionRequest.Coordinate));
        AssertPublicPropertiesClassified(
            typeof(DataRights.Contracts
                .DataRightsRestrictionReleaseTarget),
            PersonalDataSurface.ProjectionExport);
        AssertPublicPropertiesClassified(
            typeof(DataRightsRestrictionTargetResolutionResult),
            PersonalDataSurface.ProjectionExport);
        AssertPublicPropertiesClassified(
            typeof(DataRightsRestrictionReleaseTargetDto),
            PersonalDataSurface.ApiResponse);
        AssertPublicPropertiesClassified(
            typeof(DataRightsRestrictionReleaseTargetCandidateDto),
            PersonalDataSurface.ApiResponse);
        AssertPublicPropertiesClassified(
            typeof(DataRightsRestrictionReleaseTargetListResponse),
            PersonalDataSurface.ApiResponse);

        foreach (PropertyInfo property in typeof(DataRightsRestrictionOwnerProof)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            AssertBinding(
                typeof(DataRightsRestrictionOwnerProof),
                property.Name,
                PersonalDataSurface.ProjectionExport);
        }

        AssertBinding(
            typeof(DataRightsRestrictionContributionResult),
            nameof(DataRightsRestrictionContributionResult.OwnerProof),
            PersonalDataSurface.ProjectionExport);
        AssertBinding(
            typeof(DataRightsRestrictionExecutionDto),
            nameof(DataRightsRestrictionExecutionDto.Proof),
            PersonalDataSurface.ApiResponse);
        foreach (PropertyInfo property in typeof(DataRightsRestrictionExecutionProofDto)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            AssertBinding(
                typeof(DataRightsRestrictionExecutionProofDto),
                property.Name,
                PersonalDataSurface.ApiResponse);
        }

        foreach (PropertyInfo property in typeof(DataRightsRestrictionExecutionProof)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.Name is not
                         nameof(DataRightsRestrictionExecutionProof.RecordId) and not
                         nameof(DataRightsRestrictionExecutionProof.ExecutedBy)))
        {
            AssertBinding(
                typeof(DataRightsRestrictionExecutionProof),
                property.Name,
                PersonalDataSurface.Persistence);
        }

        AssertPublicPropertiesClassified(
            typeof(DataRights.Domain.ValueObjects
                .DataRightsRestrictionReleaseTarget),
            PersonalDataSurface.Persistence,
            nameof(DataRights.Domain.ValueObjects
                .DataRightsRestrictionReleaseTarget.SelectedBy));
    }

    [Fact]
    public void Correction_execution_proof_is_explicitly_classified()
    {
        Type apiRequest = typeof(DataRightsModule).Assembly.GetType(
            "BunkFy.Modules.DataRights.Api.DataRightsCorrectionEndpoints+" +
            "StartDataRightsCorrectionRequest",
            throwOnError: true)!;
        AssertPublicPropertiesClassified(
            apiRequest,
            PersonalDataSurface.ApiInput);
        AssertPublicPropertiesClassified(
            typeof(StartDataRightsCorrectionExecutionCommand),
            PersonalDataSurface.ApplicationCommand,
            nameof(StartDataRightsCorrectionExecutionCommand.ActorId));
        AssertPublicPropertiesClassified(
            typeof(GetDataRightsCorrectionExecutionQuery),
            PersonalDataSurface.ApplicationQuery,
            nameof(GetDataRightsCorrectionExecutionQuery.ActorId));
        AssertPublicPropertiesClassified(
            typeof(DataRightsCorrectionExecutionGateRequest),
            PersonalDataSurface.ApplicationQuery,
            nameof(DataRightsCorrectionExecutionGateRequest.Coordinate),
            nameof(DataRightsCorrectionExecutionGateRequest.ExecutingActorId));
        AssertPublicPropertiesClassified(
            typeof(DataRightsCorrectionExecutionGateResult),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(DataRightsCorrectionExecutionDto),
            PersonalDataSurface.ApiResponse);
        AssertPublicPropertiesClassified(
            typeof(DataRightsCorrectionExecutionDetailsDto),
            PersonalDataSurface.ApiResponse,
            nameof(DataRightsCorrectionExecutionDetailsDto.Subject),
            nameof(DataRightsCorrectionExecutionDetailsDto.ClaimedBy));
        AssertPublicPropertiesClassified(
            typeof(DataRightsCorrectionAppliedIntegrationEvent),
            PersonalDataSurface.IntegrationEvent,
            nameof(DataRightsCorrectionAppliedIntegrationEvent.EventName),
            nameof(DataRightsCorrectionAppliedIntegrationEvent.Version),
            nameof(DataRightsCorrectionAppliedIntegrationEvent.RecordId));
        AssertPublicPropertiesClassified(
            typeof(DataRightsCorrectionExecution),
            PersonalDataSurface.Persistence,
            nameof(DataRightsCorrectionExecution.DomainEvents),
            nameof(DataRightsCorrectionExecution.RecordId),
            nameof(DataRightsCorrectionExecution.ExecutedBy));
    }

    [Fact]
    public void Tenant_termination_contract_and_proof_are_explicitly_classified()
    {
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationCoordinationRequestedIntegrationEvent),
            PersonalDataSurface.IntegrationEvent,
            nameof(TenantTerminationCoordinationRequestedIntegrationEvent.EventName),
            nameof(TenantTerminationCoordinationRequestedIntegrationEvent.Version));
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationContributionRequest),
            PersonalDataSurface.IntegrationCommand,
            nameof(TenantTerminationContributionRequest.ExecutingActorId));
        AssertBinding(
            typeof(TenantTerminationContributionRequest),
            nameof(TenantTerminationContributionRequest.ExecutingActorId),
            PersonalDataSurface.IntegrationCommand);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationContributionResult),
            PersonalDataSurface.ProjectionExport);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationProcess),
            PersonalDataSurface.Persistence,
            nameof(TenantTerminationProcess.DomainEvents),
            nameof(TenantTerminationProcess.ApprovedBy),
            nameof(TenantTerminationProcess.CreatedBy),
            nameof(TenantTerminationProcess.ExportConfirmedBy),
            nameof(TenantTerminationProcess.FrozenBy),
            nameof(TenantTerminationProcess.LastChangedBy),
            nameof(TenantTerminationProcess.VerificationConfirmedBy));
        AssertBinding(
            typeof(TenantTerminationProcess),
            nameof(TenantTerminationProcess.ApprovedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationProcess),
            nameof(TenantTerminationProcess.CreatedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationProcess),
            nameof(TenantTerminationProcess.LastChangedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationExportFragmentAssemblyRequest),
            nameof(TenantTerminationExportFragmentAssemblyRequest.ExecutingActorId),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(TenantTerminationProcess),
            nameof(TenantTerminationProcess.ExportConfirmedBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationProcess),
            nameof(TenantTerminationProcess.FrozenBy),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(TenantTerminationProcess),
            nameof(TenantTerminationProcess.VerificationConfirmedBy),
            PersonalDataSurface.Persistence);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationTerminalReceipt),
            PersonalDataSurface.Persistence,
            nameof(TenantTerminationTerminalReceipt.DomainEvents),
            nameof(TenantTerminationTerminalReceipt.SealedBy));
        AssertBinding(
            typeof(TenantTerminationTerminalReceipt),
            nameof(TenantTerminationTerminalReceipt.SealedBy),
            PersonalDataSurface.Persistence);
        AssertPublicPropertiesClassified(
            typeof(FrozenTenantOwner),
            PersonalDataSurface.Persistence);
        AssertPublicPropertiesClassified(
            typeof(FrozenTenantOwnerDescriptor),
            PersonalDataSurface.ApplicationQuery);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationExportRequest),
            PersonalDataSurface.IntegrationCommand);
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationExportFragmentAssemblyRequest),
            PersonalDataSurface.ApplicationQuery,
            nameof(TenantTerminationExportFragmentAssemblyRequest.ExecutingActorId));
        Type[] applicationProofTypes =
        [
            typeof(TenantTerminationExportOwnerWork),
            typeof(TenantTerminationExportOwnerCatalogEntry),
            typeof(TenantTerminationFrozenRevision),
            typeof(TenantTerminationExportFragmentAssemblyResult),
            typeof(TenantTerminationExportOwnerResult),
            typeof(TenantTerminationExportFragmentGenerationRequest),
            typeof(TenantTerminationProtectedExportFragment),
            typeof(TenantTerminationProtectedExportArtifact),
            typeof(TenantTerminationExportFragmentSetCoordinates),
            typeof(TenantTerminationExportFragmentManifestEntry)
        ];
        foreach (Type proofType in applicationProofTypes)
        {
            AssertPublicPropertiesClassified(
                proofType,
                PersonalDataSurface.ApplicationQuery);
        }

        AssertPublicPropertiesClassified(
            typeof(TenantTerminationExportFragment),
            PersonalDataSurface.Persistence,
            nameof(TenantTerminationExportFragment.DomainEvents));
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationExportArtifact),
            PersonalDataSurface.Persistence,
            nameof(TenantTerminationExportArtifact.DomainEvents));
        AssertPublicPropertiesClassified(
            typeof(TenantTerminationOwnerWorkItem),
            PersonalDataSurface.Persistence,
            nameof(TenantTerminationOwnerWorkItem.DomainEvents));

        Type[] boundaryTypes =
        [
            typeof(TenantTerminationContributorDescriptor),
            typeof(TenantTerminationContributionRequest),
            typeof(TenantTerminationContributionResult),
            typeof(TenantTerminationExportRequest)
        ];
        string[] prohibitedNameParts =
        [
            "Address",
            "Birth",
            "Contact",
            "Document",
            "Email",
            "FreeText",
            "GuestName",
            "LegalName",
            "Nationality",
            "Note",
            "Passport",
            "Phone",
            "SearchText",
            "Snapshot"
        ];
        Assert.Empty(boundaryTypes
            .SelectMany(type => type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public))
            .Where(property => prohibitedNameParts.Any(part =>
                property.Name.Contains(
                    part,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(property =>
                $"{property.DeclaringType!.FullName}.{property.Name}"));
    }

    [Fact]
    public void Case_contracts_do_not_carry_direct_guest_payloads()
    {
        Type[] boundaryTypes =
        [
            typeof(DataRightsModule.CreateDataRightsCaseRequest),
            typeof(DataRightsModule.RecordDataRightsDecisionRequest),
            typeof(DataRightsModule.RecordRequesterVerificationRequest),
            typeof(DataRightsModule.VersionedDataRightsCaseRequest),
            typeof(BeginDataRightsDecisionCommand),
            typeof(BeginDataRightsDiscoveryCommand),
            typeof(CancelDataRightsCaseCommand),
            typeof(CreateDataRightsCaseCommand),
            typeof(RecordControllerRoutingCommand),
            typeof(RecordDataRightsDecisionCommand),
            typeof(RecordRequesterVerificationCommand),
            typeof(RequireDataRightsReviewCommand),
            typeof(SelectDataRightsSubjectCommand),
            typeof(UnselectDataRightsSubjectCommand),
            typeof(DataRightsSelectedSubjectDto),
            typeof(DataRightsCaseDto)
        ];
        string[] prohibitedNameParts =
        [
            "Address",
            "Birth",
            "Contact",
            "Document",
            "Email",
            "FreeText",
            "GuestName",
            "LegalName",
            "Nationality",
            "Note",
            "Passport",
            "Phone",
            "SearchText"
        ];

        string[] offenders = boundaryTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(property => prohibitedNameParts.Any(part =>
                property.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .ToArray();

        Assert.Empty(offenders);
        Assert.Equal(
            [
                "CreatedBy",
                "DecidedBy",
                "ExecutionStartedBy",
                "LastChangedBy",
                "ScopeId",
                "TenantTerminationPolicyEvidenceSha256"
            ],
            typeof(DataRightsCase)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Processing_ledger_pseudonym_is_explicitly_classified()
    {
        AssertBinding(
            typeof(DataRightsRecordPseudonym),
            nameof(DataRightsRecordPseudonym.Sha256),
            PersonalDataSurface.ApplicationQuery);
        AssertBinding(
            typeof(ProcessingLedgerEntry),
            nameof(ProcessingLedgerEntry.RecordPseudonymSha256),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(ProcessingLedgerSnapshot),
            nameof(ProcessingLedgerSnapshot.RecordPseudonymSha256),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(DataRightsProtectedReplayEnvelope),
            nameof(DataRightsProtectedReplayEnvelope.CiphertextBase64),
            PersonalDataSurface.Persistence);
    }

    [Fact]
    public void Restore_contracts_and_checkpoint_are_explicitly_classified()
    {
        AssertBinding(
            typeof(DataRightsAnonymisationRestoreRequest),
            nameof(DataRightsAnonymisationRestoreRequest.RecordId),
            PersonalDataSurface.IntegrationCommand);

        foreach (PropertyInfo property in typeof(DataRightsAnonymisationRestoreRequest)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.Name != nameof(DataRightsAnonymisationRestoreRequest.RecordId)))
        {
            AssertBinding(
                typeof(DataRightsAnonymisationRestoreRequest),
                property.Name,
                PersonalDataSurface.IntegrationCommand);
        }

        AssertBinding(
            typeof(DataRightsAnonymisationRestoreRequestV3),
            nameof(DataRightsAnonymisationRestoreRequestV3.RecordId),
            PersonalDataSurface.IntegrationCommand);

        foreach (PropertyInfo property in typeof(DataRightsAnonymisationRestoreRequestV3)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.Name != nameof(DataRightsAnonymisationRestoreRequestV3.RecordId)))
        {
            AssertBinding(
                typeof(DataRightsAnonymisationRestoreRequestV3),
                property.Name,
                PersonalDataSurface.IntegrationCommand);
        }

        foreach (PropertyInfo property in typeof(DataRightsAnonymisationRestoreProof)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            AssertBinding(
                typeof(DataRightsAnonymisationRestoreProof),
                property.Name,
                PersonalDataSurface.ProjectionExport);
        }

        AssertBinding(
            typeof(ProcessingLedgerEntry),
            nameof(ProcessingLedgerEntry.ResultingRecordVersion),
            PersonalDataSurface.Persistence);
        AssertBinding(
            typeof(ProcessingLedgerSnapshot),
            nameof(ProcessingLedgerSnapshot.ResultingRecordVersion),
            PersonalDataSurface.Persistence);

        AssertBinding(
            typeof(DataRightsAnonymisationRestoreResult),
            nameof(DataRightsAnonymisationRestoreResult.Proof),
            PersonalDataSurface.ProjectionExport);

        foreach (PropertyInfo property in typeof(RestoreCheckpoint)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.Name != nameof(RestoreCheckpoint.Cursor)))
        {
            AssertBinding(
                typeof(RestoreCheckpoint),
                property.Name,
                PersonalDataSurface.Persistence);
        }
    }

    [Fact]
    public void Anonymisation_event_and_task_payload_are_pii_free_coordinates()
    {
        Assert.Equal(
            [
                "BatchId",
                "CaseId",
                "EventId",
                "EventName",
                "ExecutionRevision",
                "OccurredAtUtc",
                "PropertyId",
                "ScopeId",
                "TenantId",
                "Version",
                "WorkItemId"
            ],
            typeof(DataRightsAnonymisationWorkItemTerminalIntegrationEvent)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ApprovalRevision",
                "CaseId",
                "EventId",
                "EventName",
                "ExecutionRevision",
                "OccurredAtUtc",
                "PropertyId",
                "ScopeId",
                "TenantId",
                "Version",
                "WorkItemId"
            ],
            typeof(DataRightsAnonymisationExecutionPreparedIntegrationEvent)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ApprovalRevision",
                "CaseId",
                "ExecutionRevision",
                "PropertyId",
                "WorkItemId"
            ],
            typeof(ExecuteDataRightsAnonymisationPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ArtifactId",
                "CaseId",
                "CaseType",
                "DecisionRevision",
                "EventId",
                "EventName",
                "ExpiresAtUtc",
                "OccurredAtUtc",
                "PropertyId",
                "ScopeId",
                "TenantId",
                "Version"
            ],
            typeof(DataRightsExportArtifactRequestedIntegrationEvent)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ArtifactId",
                "CaseId",
                "CaseType",
                "DecisionRevision",
                "PropertyId"
            ],
            typeof(GenerateDataRightsExportPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ArtifactId",
                "CaseId",
                "CaseType",
                "DecisionRevision",
                "ExpiresAtUtc",
                "PropertyId"
            ],
            typeof(DeleteExpiredDataRightsExportArtifactPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "OperationRevision",
                "OwnerKey",
                "Phase",
                "ProcessId",
                "TenantId",
                "WorkItemId"
            ],
            typeof(ExecuteGlobalTenantTerminationOwnerWorkPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "OperationRevision",
                "OwnerKey",
                "Phase",
                "ProcessId",
                "WorkItemId"
            ],
            typeof(ExecuteTenantTerminationOwnerWorkPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "OperationRevision",
                "OwnerKey",
                "ProcessId",
                "WorkItemId"
            ],
            typeof(ExecuteTenantTerminationExportOwnerWorkPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["OperationRevision", "ProcessId"],
            typeof(GenerateTenantTerminationExportArtifactPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ArtifactId",
                "ExpiresAtUtc",
                "ExportOperationRevision",
                "ProcessId",
                "TenantId"
            ],
            typeof(DeleteExpiredTenantTerminationExportArtifactPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "ExpiresAtUtc",
                "ExportOperationRevision",
                "FragmentId",
                "ProcessId",
                "TenantId"
            ],
            typeof(DeleteExpiredTenantTerminationExportFragmentPayload)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
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

    private static void AssertBinding(Type type, string member, PersonalDataSurface surface)
    {
        bool found = Bindings().Any(binding =>
            string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
            string.Equals(binding.Type, type.FullName, StringComparison.Ordinal) &&
            string.Equals(binding.Member, member, StringComparison.Ordinal) &&
            binding.Surface == surface);
        Assert.True(found, $"Missing {surface} classification for {type.FullName}.{member}.");
    }

    private static void AssertPublicPropertiesClassified(
        Type type,
        PersonalDataSurface surface,
        params string[] excludedMembers)
    {
        HashSet<string> excluded = excludedMembers.ToHashSet(StringComparer.Ordinal);
        foreach (PropertyInfo property in type
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => !excluded.Contains(property.Name)))
        {
            AssertBinding(type, property.Name, surface);
        }
    }

    private static IEnumerable<PersonalDataMemberBinding> Bindings() =>
        Catalogue.Fields.SelectMany(field => field.Bindings);

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(DataRightsModule).Assembly,
            typeof(CreateDataRightsCaseCommand).Assembly,
            typeof(DataRightsCaseDto).Assembly,
            typeof(DataRightsCase).Assembly,
            typeof(DataRightsDbContext).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));
}
