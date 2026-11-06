namespace BunkFy.Modules.Properties.Application.Handlers;

using System.Security.Cryptography;
using System.Text;
using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using BunkFy.TimeZones;
using DomainAcknowledgement = BunkFy.Modules.Properties.Domain.ValueObjects.PropertyGovernanceAcknowledgement;

internal sealed class ActivatePropertyProcessingCommandHandler(
    PropertiesMutationCoordinator mutations,
    PropertyMutationOperationJournal journal,
    IPropertyGovernanceRevisionWriter revisions,
    CountryPolicyRegistry countryPolicies,
    ISystemClock clock,
    IIdGenerator idGenerator,
    IEnumerable<IPropertyProcessingLifecyclePolicy>?
        lifecyclePolicies = null,
    TimeZoneRuntimeCompatibilityProbe? runtimeTimeZones = null)
    : ICommandHandler<ActivatePropertyProcessingCommand, PropertyMutationReceiptDto>
{
    internal const string AccommodationType = "hostel";
    internal const string ActivationPurpose = "property-activation";
    internal const string OperatorProvenance = "authorized-workspace-operator";

    public async Task<Result<PropertyMutationReceiptDto>> HandleAsync(
        ActivatePropertyProcessingCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.ManagementOperationInvalid);
        }

        if (!command.Confirmed)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.ConfirmationRequired);
        }

        Result<PropertyMutationActor> actorResult =
            PropertyMutationActor.Required(command.ActorId);
        if (actorResult.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                actorResult.Error);
        }

        PropertyMutationActor actor = actorResult.Value;
        string fingerprint =
            PropertyLifecycleMutationFingerprint.ComputeActivation(command);
        Property? property = await mutations
            .AcquirePropertyAsync(
                command.PropertyId,
                cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<PropertyMutationReceiptDto>(PropertiesDomainErrors.PropertyNotFound);
        }

        PropertyMutationReplayDecision<PropertyMutationReceiptDto> replay =
            await journal.InspectPropertyAsync(
            property,
            command.OperationId,
            PropertyMutationKind.ProcessingActivation,
            command.ExpectedVersion,
            fingerprint,
            cancellationToken).ConfigureAwait(false);
        if (replay.Exists)
        {
            return replay.ToResult();
        }

        Result precondition = property.EvaluateProcessingActivation(
            command.ExpectedVersion);
        if (precondition.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                precondition.Error);
        }

        Result lifecycleAdmission = await AuthorizeLifecycleAsync(
            lifecyclePolicies,
            property.ScopeId,
            property.Id,
            cancellationToken).ConfigureAwait(false);
        if (lifecycleAdmission.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(lifecycleAdmission.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(nowUtc))
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        if (TimeZoneCatalog.Default.TryResolve(
                property.TimeZoneId.Value,
                out TimeZoneCatalogResolution? timeZoneResolution) &&
            !(runtimeTimeZones ??
                TimeZoneRuntimeCompatibilityProbe.Default).IsCompatible(
                    timeZoneResolution.CanonicalTimeZoneId,
                    nowUtc))
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.TimeZoneRuntimeUnavailable);
        }

        CountryPolicyAcknowledgement[] requestedAcknowledgements = command.AcceptedAcknowledgements?
            .Select(acknowledgement => new CountryPolicyAcknowledgement(
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion))
            .ToArray() ?? [];
        CountryPolicyDecision decision = countryPolicies.EvaluateActivation(new CountryPolicyActivationRequest(
            command.OperatingCountryCode,
            command.PolicyId,
            command.PolicyVersion,
            command.DataRegionId,
            command.TransferProfileId,
            command.RetentionPolicyId,
            command.RetentionPolicyVersion,
            requestedAcknowledgements,
            AccommodationType,
            ActivationPurpose,
            OperatorProvenance,
            nowUtc));
        if (!decision.IsAllowed || decision.Evidence is null)
        {
            return Result.Failure<PropertyMutationReceiptDto>(PropertiesApplicationErrors.CountryPolicyDenied(decision.Reason));
        }

        Result<PropertyGovernanceBinding> bindingResult = CreateBinding(decision.Evidence);
        if (bindingResult.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(bindingResult.Error);
        }

        Result<IReadOnlyCollection<DomainAcknowledgement>> acknowledgementResult =
            CreateAcknowledgements(decision.Evidence.AcceptedAcknowledgements);
        if (acknowledgementResult.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(acknowledgementResult.Error);
        }

        CountryPolicyTimeZoneDecision timeZoneDecision =
            countryPolicies.EvaluateTimeZoneCompatibility(new(
                decision.Evidence.ToBinding(),
                AccommodationType,
                property.TimeZoneId.Value,
                nowUtc));
        if (!timeZoneDecision.IsAllowed)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.CountryPolicyDenied(
                    timeZoneDecision.Reason));
        }

        PropertyGovernanceRevisionCoordinates? previous = ToCoordinates(
            property.GovernanceBinding,
            property.GovernanceAcknowledgements);
        PropertyProcessingState previousState = property.ProcessingState;
        Result activation = property.ActivateProcessing(
            bindingResult.Value,
            acknowledgementResult.Value,
            command.ExpectedVersion,
            idGenerator.NewId(),
            nowUtc,
            actor.Value!);
        if (activation.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(activation.Error);
        }

        PropertyGovernanceRevisionAction action = previous is null
            ? PropertyGovernanceRevisionAction.Activated
            : previousState == PropertyProcessingState.Suspended &&
              previous == ToCoordinates(property.GovernanceBinding, property.GovernanceAcknowledgements)
                ? PropertyGovernanceRevisionAction.Reactivated
                : PropertyGovernanceRevisionAction.Rebound;
        await revisions.AppendAsync(
            new PropertyGovernanceRevisionWriteModel(
                idGenerator.NewId(),
                property.ScopeId,
                property.Id,
                property.Version,
                action,
                CountryPolicyDecisionReason.Allowed.ToString(),
                previous,
                ToCoordinates(property.GovernanceBinding, property.GovernanceAcknowledgements),
                actor.Value!,
                nowUtc),
            cancellationToken).ConfigureAwait(false);

        PropertyMutationReceiptDto receipt = await journal.RecordPropertyAsync(
            property,
            command.OperationId,
            PropertyMutationKind.ProcessingActivation,
            command.ExpectedVersion,
            fingerprint,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }

    private static async ValueTask<Result> AuthorizeLifecycleAsync(
        IEnumerable<IPropertyProcessingLifecyclePolicy>? policies,
        string tenantId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        foreach (IPropertyProcessingLifecyclePolicy policy in policies ?? [])
        {
            PropertyProcessingLifecycleDecision decision;
            try
            {
                decision = await policy.AuthorizeActivationAsync(
                        tenantId,
                        propertyId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                return Result.Failure(
                    PropertiesApplicationErrors
                        .ProcessingLifecycleAdmissionUnavailable);
            }

            if (decision.Outcome ==
                PropertyProcessingLifecycleOutcome.Restricted)
            {
                return Result.Failure(
                    PropertiesApplicationErrors
                        .ProcessingLifecycleRestricted);
            }

            if (decision.Outcome !=
                PropertyProcessingLifecycleOutcome.Allowed)
            {
                return Result.Failure(
                    PropertiesApplicationErrors
                        .ProcessingLifecycleAdmissionUnavailable);
            }
        }

        return Result.Success();
    }

    private static Result<PropertyGovernanceBinding> CreateBinding(CountryPolicyEvidence evidence) =>
        PropertyGovernanceBinding.Create(
            evidence.OperatingCountryCode,
            evidence.PolicyId,
            evidence.PolicyVersion,
            evidence.DataRegionId,
            evidence.TransferProfileId,
            evidence.RetentionPolicyId,
            evidence.RetentionPolicyVersion,
            evidence.ContentSha256,
            evidence.EffectiveAtUtc,
            evidence.ExpiresAtUtc,
            evidence.EvaluatedAtUtc);

    private static Result<IReadOnlyCollection<DomainAcknowledgement>> CreateAcknowledgements(
        IReadOnlyCollection<CountryPolicyAcknowledgement> acknowledgements)
    {
        List<DomainAcknowledgement> values = [];
        foreach (CountryPolicyAcknowledgement acknowledgement in acknowledgements)
        {
            Result<DomainAcknowledgement> result = DomainAcknowledgement.Create(
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion);
            if (result.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<DomainAcknowledgement>>(result.Error);
            }

            values.Add(result.Value);
        }

        return Result.Success<IReadOnlyCollection<DomainAcknowledgement>>(values);
    }

    internal static PropertyGovernanceRevisionCoordinates? ToCoordinates(
        PropertyGovernanceBinding? binding,
        IReadOnlyCollection<DomainAcknowledgement> acknowledgements) =>
        binding is null
            ? null
            : new PropertyGovernanceRevisionCoordinates(
                binding.OperatingCountryCode,
                binding.PolicyId,
                binding.PolicyVersion,
                binding.DataRegionId,
                binding.TransferProfileId,
                binding.RetentionPolicyId,
                binding.RetentionPolicyVersion,
                binding.ContentSha256,
                HashAcknowledgements(acknowledgements));

    private static string HashAcknowledgements(
        IReadOnlyCollection<DomainAcknowledgement> acknowledgements)
    {
        string canonical = string.Join(
            '\n',
            acknowledgements.OrderBy(item => item.AcknowledgementId, StringComparer.Ordinal)
                .ThenBy(item => item.AcknowledgementVersion)
                .Select(item => $"{item.AcknowledgementId}:{item.AcknowledgementVersion}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
