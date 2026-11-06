namespace BunkFy.Modules.Workspaces.Application.Handlers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Results;
using Gma.Modules.Organizations.Contracts;

internal sealed class WorkspaceStaffIdentityAnchorCutoverCoordinator(
    IWorkspaceStaffIdentityAnchorCutoverSourceReader sources,
    IStaffIdentityProvisioningAnchorCutover staff,
    IOrganizationScopeLifecycle organizationScopes)
{
    public async Task<Result<WorkspaceStaffIdentityAnchorCutoverStatus>>
        GetStatusAsync(
            string tenantId,
            WorkspaceStaffIdentityAnchorOwnerManifest? ownerManifest,
            CancellationToken cancellationToken)
    {
        Result<WorkspaceStaffIdentityAnchorPlan> plan =
            await this.BuildPlanAsync(
                tenantId,
                ownerManifest,
                requireOwnerManifest: false,
                retainedBatchSize: 0,
                cancellationToken).ConfigureAwait(false);
        return plan.IsSuccess
            ? Result.Success(plan.Value.Status)
            : Result.Failure<WorkspaceStaffIdentityAnchorCutoverStatus>(
                plan.Error);
    }

    public async Task<Result<WorkspaceStaffIdentityAnchorPreparedReconcile>>
        PrepareReconcileAsync(
            string tenantId,
            string expectedSourceEvidenceSha256,
            string expectedAnchorStateSha256,
            WorkspaceStaffIdentityAnchorOwnerManifest ownerManifest,
            string expectedOwnerManifestSha256,
            int batchSize,
            CancellationToken cancellationToken)
    {
        if (!IsSha256(expectedSourceEvidenceSha256) ||
            !IsSha256(expectedAnchorStateSha256) ||
            !IsSha256(expectedOwnerManifestSha256) ||
            batchSize is < 1 or
                > StaffWorkspaceOnboardingAnchorCutoverLimits.MaximumBatchSize)
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorPreparedReconcile>(
                    WorkspaceStaffIdentityAnchorCutoverErrors.RequestInvalid);
        }

        Result<WorkspaceStaffIdentityAnchorPlan> plan =
            await this.BuildPlanAsync(
                tenantId,
                ownerManifest,
                requireOwnerManifest: true,
                batchSize,
                cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorPreparedReconcile>(plan.Error);
        }

        if (!string.Equals(
                expectedSourceEvidenceSha256,
                plan.Value.Status.SourceEvidenceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorPreparedReconcile>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .SourceEvidenceChanged);
        }

        if (!string.Equals(
                expectedOwnerManifestSha256,
                plan.Value.Status.OwnerManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorPreparedReconcile>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OwnerManifestChanged);
        }

        if (!string.Equals(
                expectedAnchorStateSha256,
                plan.Value.Status.AnchorStateSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorPreparedReconcile>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .AnchorStateChanged);
        }

        if (!plan.Value.Status.CanReconcile)
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorPreparedReconcile>(
                    WorkspaceStaffIdentityAnchorCutoverErrors.Blocked);
        }

        return Result.Success(new WorkspaceStaffIdentityAnchorPreparedReconcile(
            Guid.ParseExact(tenantId, "D"),
            plan.Value.Batch,
            plan.Value.Status,
            ownerManifest,
            expectedSourceEvidenceSha256,
            expectedAnchorStateSha256,
            expectedOwnerManifestSha256));
    }

    private async Task<Result<WorkspaceStaffIdentityAnchorPlan>> BuildPlanAsync(
        string tenantId,
        WorkspaceStaffIdentityAnchorOwnerManifest? ownerManifest,
        bool requireOwnerManifest,
        int retainedBatchSize,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(tenantId, "D", out Guid organizationId) ||
            !string.Equals(
                tenantId,
                organizationId.ToString("D"),
                StringComparison.Ordinal))
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorPlan>(
                WorkspaceStaffIdentityAnchorCutoverErrors.TenantRequired);
        }

        if (requireOwnerManifest && ownerManifest is null)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorPlan>(
                WorkspaceStaffIdentityAnchorCutoverErrors
                    .OwnerManifestRequired);
        }

        string? ownerManifestSha256 = null;
        WorkspaceStaffIdentityAnchorOwnerBinding[] ownerBindings = [];
        if (ownerManifest is not null)
        {
            Result<WorkspaceStaffIdentityAnchorOwnerBinding[]> validated =
                ValidateOwnerManifest(tenantId, ownerManifest);
            if (validated.IsFailure)
            {
                return Result.Failure<WorkspaceStaffIdentityAnchorPlan>(
                    validated.Error);
            }

            ownerBindings = validated.Value;
            ownerManifestSha256 = ComputeOwnerManifestSha256(
                ownerManifest,
                ownerBindings);
        }

        Result<AuthoritativeOrganizationHeader> organizationHeaderResult =
            await this.LoadOrganizationHeaderAsync(
                organizationId,
                cancellationToken).ConfigureAwait(false);
        if (organizationHeaderResult.IsFailure)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorPlan>(
                organizationHeaderResult.Error);
        }

        AuthoritativeOrganizationHeader organizationHeader =
            organizationHeaderResult.Value;
        using WorkspaceStaffIdentityAnchorPlanAccumulator accumulator = new(
            tenantId,
            organizationHeader.ScopeRevision,
            organizationHeader.OrganizationVersion,
            retainedBatchSize);

        Result workspacesResult = await this.StreamWorkspaceSourcesAsync(
            accumulator,
            cancellationToken).ConfigureAwait(false);
        if (workspacesResult.IsFailure)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorPlan>(
                workspacesResult.Error);
        }

        Result organizationsResult =
            await this.StreamOrganizationMembershipsAsync(
                organizationId,
                organizationHeader,
                ownerBindings,
                accumulator,
                cancellationToken).ConfigureAwait(false);
        if (organizationsResult.IsFailure)
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorPlan>(
                organizationsResult.Error);
        }

        bool ownerProvided = ownerManifest is not null;
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            accumulator.CompleteStatus(
                ownerBindings.LongLength,
                ownerBindings.LongCount(binding => binding.EvidenceKind ==
                    WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                        .HistoricalOwnerExternalReview),
                ownerManifestSha256,
                ownerProvided,
                ownerManifest?.HistoricalEvidence.Kind ??
                    WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                        .Unknown,
                ownerManifest?.HistoricalEvidence.EvidenceSha256);
        return Result.Success(new WorkspaceStaffIdentityAnchorPlan(
            status,
            accumulator.GetRetainedBatch()));
    }

    private async Task<Result> StreamWorkspaceSourcesAsync(
        WorkspaceStaffIdentityAnchorPlanAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        Guid? afterApplicationId = null;
        while (true)
        {
            WorkspaceStaffIdentityAnchorSourcePage? page = await sources
                .ListRelevantPageAsync(
                    afterApplicationId,
                    WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (page?.Records is null ||
                page.Records.Count >
                    WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize ||
                (page.HasMore && page.Records.Count !=
                    WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize))
            {
                return Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .SourcePageInvalid);
            }

            List<PendingPlanItem> pending = new(page.Records.Count);
            Guid? previous = afterApplicationId;
            foreach (WorkspaceStaffIdentityAnchorSourceRecord record in
                page.Records)
            {
                string normalizedSubjectId = record.SubjectId?.Trim() ??
                    string.Empty;
                if (record.ApplicationId == Guid.Empty ||
                    record.StaffMemberId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(normalizedSubjectId) ||
                    normalizedSubjectId.Any(char.IsControl) ||
                    !string.Equals(
                        record.SubjectId,
                        normalizedSubjectId,
                        StringComparison.Ordinal) ||
                    !Enum.IsDefined(record.Status) ||
                    (previous.HasValue &&
                     record.ApplicationId.CompareTo(previous.Value) <= 0))
                {
                    return Result.Failure(
                        WorkspaceStaffIdentityAnchorCutoverErrors
                            .SourcePageInvalid);
                }

                previous = record.ApplicationId;
                accumulator.AddWorkspaceSourceEvidence(record);
                pending.Add(new PendingPlanItem(
                    new StaffIdentityProvisioningAnchorCandidate(
                        StaffIdentityProvisioningAnchorSourceKind
                            .WorkspaceOnboarding,
                        record.ApplicationId,
                        record.StaffMemberId,
                        normalizedSubjectId),
                    ObservedMembershipVersion: null,
                    ForceConflict: false));
            }

            Guid? expectedNextApplicationId = page.Records.Count == 0
                ? afterApplicationId
                : page.Records[^1].ApplicationId;
            if (page.NextApplicationId != expectedNextApplicationId ||
                (page.HasMore &&
                 (!page.NextApplicationId.HasValue ||
                  page.NextApplicationId == afterApplicationId)))
            {
                return Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .SourcePageInvalid);
            }

            Result inspected = await this.FlushInspectionAsync(
                pending,
                accumulator,
                cancellationToken).ConfigureAwait(false);
            if (inspected.IsFailure)
            {
                return inspected;
            }

            if (!page.HasMore)
            {
                return Result.Success();
            }

            afterApplicationId = page.NextApplicationId;
        }
    }

    private async Task<Result<AuthoritativeOrganizationHeader>>
        LoadOrganizationHeaderAsync(
            Guid organizationId,
            CancellationToken cancellationToken)
    {
        OrganizationScopeSnapshot snapshot = await organizationScopes
            .GetSnapshotAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.Status != OrganizationScopeStatus.Open ||
            snapshot.Revision < 0)
        {
            return Result.Failure<AuthoritativeOrganizationHeader>(
                WorkspaceStaffIdentityAnchorCutoverErrors
                    .OrganizationsUnavailable);
        }

        OrganizationScopeExportPage? page = await organizationScopes
            .ExportAsync(
                new OrganizationScopeExportRequest(
                    organizationId,
                    snapshot.Revision,
                    OrganizationScopeExportStore.Organization,
                    AfterCursor: null,
                    PageSize: 1),
                cancellationToken).ConfigureAwait(false);
        if (page is null ||
            page.Status == OrganizationScopeExportStatus.Stale ||
            page.ScopeRevision != snapshot.Revision)
        {
            return Result.Failure<AuthoritativeOrganizationHeader>(
                WorkspaceStaffIdentityAnchorCutoverErrors
                    .OrganizationsEvidenceChanged);
        }

        OrganizationScopeOrganizationExportRecord? organization =
            page.Records is { Count: 1 }
                ? page.Records[0] as
                    OrganizationScopeOrganizationExportRecord
                : null;
        if (page.Status != OrganizationScopeExportStatus.Completed ||
            page.Store != OrganizationScopeExportStore.Organization ||
            page.HasMore ||
            organization is null ||
            organization.OrganizationId != organizationId ||
            organization.Status != OrganizationStatus.Active ||
            organization.ActiveOwnerCount < 0 ||
            organization.Version < 1)
        {
            return Result.Failure<AuthoritativeOrganizationHeader>(
                WorkspaceStaffIdentityAnchorCutoverErrors
                    .OrganizationsUnavailable);
        }

        return Result.Success(new AuthoritativeOrganizationHeader(
            snapshot.Revision,
            organization.Version,
            organization.ActiveOwnerCount));
    }

    private async Task<Result> StreamOrganizationMembershipsAsync(
        Guid organizationId,
        AuthoritativeOrganizationHeader header,
        WorkspaceStaffIdentityAnchorOwnerBinding[] ownerBindings,
        WorkspaceStaffIdentityAnchorPlanAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        int bindingIndex = 0;
        string? afterCursor = null;
        Guid? previousMembershipId = null;
        List<PendingPlanItem> pending = new(
            OrganizationScopeLifecycleLimits.MaximumPageSize);
        while (true)
        {
            OrganizationScopeExportPage? page = await organizationScopes
                .ExportAsync(
                    new OrganizationScopeExportRequest(
                        organizationId,
                        header.ScopeRevision,
                        OrganizationScopeExportStore.Memberships,
                        afterCursor,
                        OrganizationScopeLifecycleLimits.MaximumPageSize),
                    cancellationToken).ConfigureAwait(false);
            if (page is null ||
                page.Status == OrganizationScopeExportStatus.Stale ||
                page.ScopeRevision != header.ScopeRevision)
            {
                return Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OrganizationsEvidenceChanged);
            }

            if (page.Status != OrganizationScopeExportStatus.Completed ||
                page.Store != OrganizationScopeExportStore.Memberships ||
                page.Records is null ||
                page.Records.Count >
                    OrganizationScopeLifecycleLimits.MaximumPageSize ||
                (page.HasMore && page.Records.Count !=
                    OrganizationScopeLifecycleLimits.MaximumPageSize))
            {
                return Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OrganizationsUnavailable);
            }

            foreach (OrganizationScopeExportRecord record in page.Records)
            {
                if (record is not OrganizationScopeMembershipExportRecord
                        membership)
                {
                    return Result.Failure(
                        WorkspaceStaffIdentityAnchorCutoverErrors
                            .OrganizationsUnavailable);
                }

                string normalizedSubjectId = membership.SubjectId?.Trim() ??
                    string.Empty;
                if (
                    membership.OrganizationId != organizationId ||
                    membership.MembershipId == Guid.Empty ||
                    membership.Version < 1 ||
                    !Enum.IsDefined(membership.Role) ||
                    !Enum.IsDefined(membership.Status) ||
                    string.IsNullOrWhiteSpace(normalizedSubjectId) ||
                    normalizedSubjectId.Any(char.IsControl) ||
                    !string.Equals(
                        membership.SubjectId,
                        normalizedSubjectId,
                        StringComparison.Ordinal) ||
                    (previousMembershipId.HasValue &&
                     membership.MembershipId.CompareTo(
                         previousMembershipId.Value) <= 0))
                {
                    return Result.Failure(
                        WorkspaceStaffIdentityAnchorCutoverErrors
                            .OrganizationsUnavailable);
                }

                while (bindingIndex < ownerBindings.Length &&
                    ownerBindings[bindingIndex].MembershipId.CompareTo(
                        membership.MembershipId) < 0)
                {
                    Result flushed = await this.FlushInspectionAsync(
                        pending,
                        accumulator,
                        cancellationToken).ConfigureAwait(false);
                    if (flushed.IsFailure)
                    {
                        return flushed;
                    }

                    accumulator.AddPlanItem(MissingBinding(
                        ownerBindings[bindingIndex]));
                    bindingIndex++;
                }

                WorkspaceStaffIdentityAnchorOwnerBinding? binding = null;
                if (bindingIndex < ownerBindings.Length &&
                    ownerBindings[bindingIndex].MembershipId ==
                        membership.MembershipId)
                {
                    binding = ownerBindings[bindingIndex];
                    bindingIndex++;
                }

                bool isActiveOwner =
                    membership.Role == OrganizationMembershipRole.Owner &&
                    membership.Status ==
                        OrganizationMembershipStatus.Active;
                accumulator.AddOrganizationMembershipEvidence(
                    membership,
                    normalizedSubjectId,
                    isActiveOwner);
                previousMembershipId = membership.MembershipId;

                if (isActiveOwner || binding is not null)
                {
                    bool forceConflict = binding is not null &&
                        (membership.Version <
                            binding.ObservedMembershipVersion ||
                         (!isActiveOwner &&
                          binding.EvidenceKind !=
                            WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                                .HistoricalOwnerExternalReview));
                    pending.Add(new PendingPlanItem(
                        new StaffIdentityProvisioningAnchorCandidate(
                            StaffIdentityProvisioningAnchorSourceKind
                                .OrganizationMembership,
                            membership.MembershipId,
                            binding?.StaffMemberId,
                            normalizedSubjectId,
                            binding?.ReviewedErasedTarget ?? false),
                        membership.Version,
                        forceConflict));
                    if (pending.Count ==
                        StaffWorkspaceOnboardingAnchorCutoverLimits
                            .MaximumBatchSize)
                    {
                        Result flushed = await this.FlushInspectionAsync(
                            pending,
                            accumulator,
                            cancellationToken).ConfigureAwait(false);
                        if (flushed.IsFailure)
                        {
                            return flushed;
                        }
                    }
                }
            }

            string? expectedCursor = previousMembershipId.HasValue
                ? "id:" + previousMembershipId.Value.ToString("D")
                : afterCursor;
            if (!string.Equals(
                    page.NextCursor,
                    expectedCursor,
                    StringComparison.Ordinal) ||
                (page.HasMore &&
                 (string.IsNullOrWhiteSpace(page.NextCursor) ||
                  string.Equals(
                      page.NextCursor,
                      afterCursor,
                      StringComparison.Ordinal))))
            {
                return Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OrganizationsUnavailable);
            }

            if (!page.HasMore)
            {
                break;
            }

            afterCursor = page.NextCursor;
        }

        Result finalBatch = await this.FlushInspectionAsync(
            pending,
            accumulator,
            cancellationToken).ConfigureAwait(false);
        if (finalBatch.IsFailure)
        {
            return finalBatch;
        }

        while (bindingIndex < ownerBindings.Length)
        {
            accumulator.AddPlanItem(MissingBinding(
                ownerBindings[bindingIndex]));
            bindingIndex++;
        }

        if (accumulator.AuthoritativeOwnerCount != header.ActiveOwnerCount)
        {
            return Result.Failure(
                WorkspaceStaffIdentityAnchorCutoverErrors
                    .OrganizationsUnavailable);
        }

        OrganizationScopeSnapshot finalSnapshot = await organizationScopes
            .GetSnapshotAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        return finalSnapshot.Status == OrganizationScopeStatus.Open &&
            finalSnapshot.Revision == header.ScopeRevision
                ? Result.Success()
                : Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OrganizationsEvidenceChanged);
    }

    private async Task<Result> FlushInspectionAsync(
        List<PendingPlanItem> pending,
        WorkspaceStaffIdentityAnchorPlanAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return Result.Success();
        }

        StaffIdentityProvisioningAnchorCandidate[] candidates = pending
            .Select(item => item.Candidate)
            .ToArray();
        StaffIdentityProvisioningAnchorInspection inspection = await staff
            .InspectAsync(candidates, cancellationToken)
            .ConfigureAwait(false);
        if (!TryValidateInspection(
                candidates,
                inspection,
                out Dictionary<(
                    StaffIdentityProvisioningAnchorSourceKind,
                    Guid), StaffIdentityProvisioningAnchorCutoverDisposition>
                    bySource))
        {
            return Result.Failure(
                WorkspaceStaffIdentityAnchorCutoverErrors.StaffUnavailable);
        }

        foreach (PendingPlanItem item in pending)
        {
            if (!bySource.TryGetValue(
                    (item.Candidate.SourceKind, item.Candidate.SourceId),
                    out StaffIdentityProvisioningAnchorCutoverDisposition
                        disposition))
            {
                return Result.Failure(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .StaffUnavailable);
            }

            accumulator.AddPlanItem(item with
            {
                Disposition = item.ForceConflict
                    ? StaffIdentityProvisioningAnchorCutoverDisposition
                        .Conflict
                    : disposition
            });
        }

        pending.Clear();
        return Result.Success();
    }

    private static PendingPlanItem MissingBinding(
        WorkspaceStaffIdentityAnchorOwnerBinding binding) => new(
            new StaffIdentityProvisioningAnchorCandidate(
                StaffIdentityProvisioningAnchorSourceKind
                    .OrganizationMembership,
                binding.MembershipId,
                binding.StaffMemberId),
            binding.ObservedMembershipVersion,
            ForceConflict: true)
        {
            Disposition =
                StaffIdentityProvisioningAnchorCutoverDisposition.Conflict
        };

    private static Result<WorkspaceStaffIdentityAnchorOwnerBinding[]>
        ValidateOwnerManifest(
            string tenantId,
            WorkspaceStaffIdentityAnchorOwnerManifest manifest)
    {
        if (manifest.ContractVersion != 1 ||
            manifest.ReviewedAtUtc == default ||
            manifest.HistoricalEvidence is null ||
            manifest.HistoricalEvidence.Kind is not (
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedHistoricalOwnerUniverse or
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources) ||
            !IsSha256(manifest.HistoricalEvidence.EvidenceSha256) ||
            manifest.Bindings is null ||
            !Guid.TryParseExact(tenantId, "D", out Guid tenantGuid) ||
            !string.Equals(
                tenantGuid.ToString("D"),
                manifest.TenantId,
                StringComparison.Ordinal))
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorOwnerBinding[]>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OwnerManifestInvalid);
        }

        WorkspaceStaffIdentityAnchorOwnerBinding[] bindings = manifest.Bindings
            .OrderBy(binding => binding.MembershipId)
            .ToArray();
        if (bindings.Select(binding => binding.MembershipId)
                .Distinct().Count() != bindings.Length ||
            bindings.Any(binding =>
                binding.OrganizationId != tenantGuid ||
                !string.Equals(
                    binding.ScopeId,
                    binding.OrganizationId.ToString("D"),
                    StringComparison.Ordinal) ||
                binding.MembershipId == Guid.Empty ||
                binding.StaffMemberId == Guid.Empty ||
                binding.ObservedMembershipVersion < 1 ||
                binding.EvidenceKind is not (
                    WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                        .CurrentActiveOwnerReview or
                    WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                        .HistoricalOwnerExternalReview)) ||
            (manifest.HistoricalEvidence.Kind ==
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources &&
             bindings.Any(binding => binding.EvidenceKind ==
                WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                    .HistoricalOwnerExternalReview)))
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorOwnerBinding[]>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OwnerManifestInvalid);
        }

        return Result.Success(bindings);
    }

    private static string ComputeOwnerManifestSha256(
        WorkspaceStaffIdentityAnchorOwnerManifest manifest,
        WorkspaceStaffIdentityAnchorOwnerBinding[] bindings)
    {
        StringBuilder canonical = new(
            "bunkfy-staff-owner-identity-anchor-manifest/v1|");
        Append(canonical, manifest.ContractVersion);
        Append(canonical, manifest.TenantId);
        Append(canonical, manifest.ReviewedAtUtc.ToUniversalTime().ToString(
            "O",
            CultureInfo.InvariantCulture));
        Append(canonical, (int)manifest.HistoricalEvidence.Kind);
        Append(canonical, manifest.HistoricalEvidence.EvidenceSha256);
        Append(canonical, bindings.Length);
        foreach (WorkspaceStaffIdentityAnchorOwnerBinding binding in bindings)
        {
            Append(canonical, binding.OrganizationId);
            Append(canonical, binding.ScopeId);
            Append(canonical, binding.MembershipId);
            Append(canonical, binding.StaffMemberId);
            Append(canonical, binding.ObservedMembershipVersion);
            Append(canonical, (int)binding.EvidenceKind);
            Append(canonical, binding.ReviewedErasedTarget ? 1 : 0);
        }

        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal static bool TryValidateInspection(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
        StaffIdentityProvisioningAnchorInspection? inspection,
        out Dictionary<(StaffIdentityProvisioningAnchorSourceKind, Guid),
            StaffIdentityProvisioningAnchorCutoverDisposition> bySource)
    {
        bySource = [];
        if (inspection is null ||
            !inspection.IsSuccess ||
            !string.IsNullOrWhiteSpace(inspection.ErrorCode) ||
            inspection.Items is null ||
            inspection.Items.Count != candidates.Count)
        {
            return false;
        }

        Dictionary<(StaffIdentityProvisioningAnchorSourceKind, Guid),
            StaffIdentityProvisioningAnchorCandidate> expected = [];
        foreach (StaffIdentityProvisioningAnchorCandidate candidate in
            candidates)
        {
            if (candidate.SourceKind is not (
                    StaffIdentityProvisioningAnchorSourceKind
                        .WorkspaceOnboarding or
                    StaffIdentityProvisioningAnchorSourceKind
                        .OrganizationMembership) ||
                candidate.SourceId == Guid.Empty ||
                !expected.TryAdd(
                    (candidate.SourceKind, candidate.SourceId),
                    candidate))
            {
                return false;
            }
        }

        foreach (StaffIdentityProvisioningAnchorCandidateInspection item in
            inspection.Items)
        {
            (StaffIdentityProvisioningAnchorSourceKind, Guid) key =
                (item.SourceKind, item.SourceId);
            if (!expected.ContainsKey(key) ||
                !IsAllowedDisposition(item.SourceKind, item.Disposition) ||
                !bySource.TryAdd(key, item.Disposition))
            {
                return false;
            }
        }

        return bySource.Count == expected.Count;
    }

    private static bool IsAllowedDisposition(
        StaffIdentityProvisioningAnchorSourceKind sourceKind,
        StaffIdentityProvisioningAnchorCutoverDisposition disposition) =>
        sourceKind switch
        {
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding =>
                disposition is
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .AlreadyAnchored or
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .SeedableFromWorkspace or
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .Ambiguous or
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .Conflict,
            StaffIdentityProvisioningAnchorSourceKind.OrganizationMembership =>
                disposition is
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .AlreadyAnchored or
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .SeedableFromReviewedOwnerMap or
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .Ambiguous or
                    StaffIdentityProvisioningAnchorCutoverDisposition
                        .Conflict,
            _ => false
        };

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value);

    private static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, long value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, Guid value) =>
        Append(builder, value.ToString("N"));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record WorkspaceStaffIdentityAnchorPlan(
        WorkspaceStaffIdentityAnchorCutoverStatus Status,
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> Batch);

    private sealed record PendingPlanItem(
        StaffIdentityProvisioningAnchorCandidate Candidate,
        long? ObservedMembershipVersion,
        bool ForceConflict)
    {
        public StaffIdentityProvisioningAnchorCutoverDisposition Disposition
        { get; init; }
    }

    private sealed record AuthoritativeOrganizationHeader(
        long ScopeRevision,
        long OrganizationVersion,
        long ActiveOwnerCount);

    private sealed class WorkspaceStaffIdentityAnchorPlanAccumulator
        : IDisposable
    {
        private readonly StreamingCanonicalSha256 sourceEvidence = new();
        private readonly StreamingCanonicalSha256 anchorState = new();
        private readonly int retainedBatchSize;
        private readonly List<StaffIdentityProvisioningAnchorCandidate>
            retainedBatch;
        private readonly List<WorkspaceStaffIdentityAnchorCutoverIssue> issues =
            new(WorkspaceStaffIdentityAnchorCutoverStatusLimits.MaximumIssues);
        private long organizationMembershipCount;
        private long planItemCount;

        public WorkspaceStaffIdentityAnchorPlanAccumulator(
            string tenantId,
            long organizationsScopeRevision,
            long organizationVersion,
            int retainedBatchSize)
        {
            this.OrganizationsScopeRevision = organizationsScopeRevision;
            this.retainedBatchSize = retainedBatchSize;
            this.retainedBatch = new(Math.Min(
                retainedBatchSize,
                StaffWorkspaceOnboardingAnchorCutoverLimits
                    .MaximumBatchSize));
            this.sourceEvidence.Append(
                "bunkfy-workspaces-staff-identity-anchor-sources/v3|");
            this.sourceEvidence.Append(tenantId);
            this.sourceEvidence.Append(organizationsScopeRevision);
            this.sourceEvidence.Append(organizationVersion);
            this.anchorState.Append(
                "bunkfy-staff-identity-anchor-state/v3|");
            this.anchorState.Append(tenantId);
            this.anchorState.Append(organizationsScopeRevision);
        }

        public long WorkspaceSourceCount { get; private set; }
        public long AlreadyAnchoredCount { get; private set; }
        public long SeedableWorkspaceCount { get; private set; }
        public long SeedableOwnerCount { get; private set; }
        public long AmbiguousCount { get; private set; }
        public long ConflictCount { get; private set; }
        public long AuthoritativeOwnerCount { get; private set; }
        public long TotalIssueCount { get; private set; }

        public void AddWorkspaceSourceEvidence(
            WorkspaceStaffIdentityAnchorSourceRecord record)
        {
            this.sourceEvidence.Append("workspace");
            this.sourceEvidence.Append(record.ApplicationId);
            this.sourceEvidence.Append(record.StaffMemberId);
            this.sourceEvidence.Append(record.SubjectId);
            this.sourceEvidence.Append((int)record.Status);
            this.WorkspaceSourceCount = checked(
                this.WorkspaceSourceCount + 1);
        }

        public void AddOrganizationMembershipEvidence(
            OrganizationScopeMembershipExportRecord membership,
            string normalizedSubjectId,
            bool isActiveOwner)
        {
            this.sourceEvidence.Append("membership");
            this.sourceEvidence.Append(membership.MembershipId);
            this.sourceEvidence.Append(normalizedSubjectId);
            this.sourceEvidence.Append((int)membership.Role);
            this.sourceEvidence.Append((int)membership.Status);
            this.sourceEvidence.Append(membership.Version);
            this.organizationMembershipCount = checked(
                this.organizationMembershipCount + 1);
            if (isActiveOwner)
            {
                this.AuthoritativeOwnerCount = checked(
                    this.AuthoritativeOwnerCount + 1);
            }
        }

        public void AddPlanItem(PendingPlanItem item)
        {
            StaffIdentityProvisioningAnchorCutoverDisposition disposition =
                item.Disposition;
            this.anchorState.Append("item");
            this.anchorState.Append((int)item.Candidate.SourceKind);
            this.anchorState.Append(item.Candidate.SourceId);
            this.anchorState.Append(item.Candidate.StaffMemberId);
            this.anchorState.AppendNullable(
                item.Candidate.ExpectedAuthSubjectId);
            this.anchorState.Append(item.Candidate.ReviewedErasedTarget);
            this.anchorState.Append(item.ObservedMembershipVersion);
            this.anchorState.Append((int)disposition);
            this.planItemCount = checked(this.planItemCount + 1);

            switch (disposition)
            {
                case StaffIdentityProvisioningAnchorCutoverDisposition
                    .AlreadyAnchored:
                    this.AlreadyAnchoredCount = checked(
                        this.AlreadyAnchoredCount + 1);
                    break;
                case StaffIdentityProvisioningAnchorCutoverDisposition
                    .SeedableFromWorkspace:
                    this.SeedableWorkspaceCount = checked(
                        this.SeedableWorkspaceCount + 1);
                    this.RetainSeedable(item.Candidate);
                    break;
                case StaffIdentityProvisioningAnchorCutoverDisposition
                    .SeedableFromReviewedOwnerMap:
                    this.SeedableOwnerCount = checked(
                        this.SeedableOwnerCount + 1);
                    this.RetainSeedable(item.Candidate);
                    break;
                case StaffIdentityProvisioningAnchorCutoverDisposition
                    .Ambiguous:
                    this.AmbiguousCount = checked(this.AmbiguousCount + 1);
                    this.AddIssue(item);
                    break;
                case StaffIdentityProvisioningAnchorCutoverDisposition
                    .Conflict:
                    this.ConflictCount = checked(this.ConflictCount + 1);
                    this.AddIssue(item);
                    break;
                case StaffIdentityProvisioningAnchorCutoverDisposition
                    .Unknown:
                default:
                    throw new InvalidOperationException(
                        "The identity-anchor cutover disposition is invalid.");
            }
        }

        public WorkspaceStaffIdentityAnchorCutoverStatus CompleteStatus(
            long ownerBindingCount,
            long historicalBindingCount,
            string? ownerManifestSha256,
            bool ownerManifestProvided,
            WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                historicalEvidenceKind,
            string? historicalEvidenceSha256)
        {
            this.sourceEvidence.Append("counts");
            this.sourceEvidence.Append(this.WorkspaceSourceCount);
            this.sourceEvidence.Append(this.organizationMembershipCount);
            this.sourceEvidence.Append(this.AuthoritativeOwnerCount);
            this.anchorState.Append("count");
            this.anchorState.Append(this.planItemCount);
            bool canReconcile = ownerManifestProvided &&
                this.AmbiguousCount == 0 &&
                this.ConflictCount == 0;
            return new WorkspaceStaffIdentityAnchorCutoverStatus(
                this.WorkspaceSourceCount,
                ownerBindingCount,
                this.AlreadyAnchoredCount,
                this.SeedableWorkspaceCount,
                this.SeedableOwnerCount,
                this.AmbiguousCount,
                this.ConflictCount,
                this.sourceEvidence.Complete(),
                this.anchorState.Complete(),
                ownerManifestSha256,
                ownerManifestProvided,
                canReconcile,
                canReconcile &&
                    this.SeedableWorkspaceCount == 0 &&
                    this.SeedableOwnerCount == 0,
                this.AuthoritativeOwnerCount,
                this.OrganizationsScopeRevision,
                historicalBindingCount,
                historicalEvidenceKind,
                historicalEvidenceSha256,
                this.TotalIssueCount,
                this.TotalIssueCount > this.issues.Count,
                this.issues.ToArray());
        }

        public long OrganizationsScopeRevision { get; private init; }

        public StaffIdentityProvisioningAnchorCandidate[] GetRetainedBatch() =>
            this.retainedBatch.ToArray();

        public void Dispose()
        {
            this.sourceEvidence.Dispose();
            this.anchorState.Dispose();
        }

        private void RetainSeedable(
            StaffIdentityProvisioningAnchorCandidate candidate)
        {
            if (this.retainedBatch.Count < this.retainedBatchSize)
            {
                this.retainedBatch.Add(candidate);
            }
        }

        private void AddIssue(PendingPlanItem item)
        {
            this.TotalIssueCount = checked(this.TotalIssueCount + 1);
            if (this.issues.Count <
                WorkspaceStaffIdentityAnchorCutoverStatusLimits.MaximumIssues)
            {
                this.issues.Add(new WorkspaceStaffIdentityAnchorCutoverIssue(
                    item.Candidate.SourceKind,
                    item.Candidate.SourceId,
                    item.Disposition,
                    item.ObservedMembershipVersion,
                    item.Candidate.StaffMemberId));
            }
        }

        private sealed class StreamingCanonicalSha256 : IDisposable
        {
            private readonly IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            private bool completed;

            public void Append(string value)
            {
                ArgumentNullException.ThrowIfNull(value);
                this.AppendRaw(value.Length.ToString(
                    CultureInfo.InvariantCulture));
                this.AppendRaw(":");
                this.AppendRaw(value);
            }

            public void Append(int value) => this.Append(
                value.ToString(CultureInfo.InvariantCulture));

            public void Append(long value) => this.Append(
                value.ToString(CultureInfo.InvariantCulture));

            public void Append(Guid value) => this.Append(value.ToString("N"));

            public void Append(Guid? value)
            {
                this.Append(value.HasValue ? 1 : 0);
                if (value.HasValue)
                {
                    this.Append(value.Value);
                }
            }

            public void Append(long? value)
            {
                this.Append(value.HasValue ? 1 : 0);
                if (value.HasValue)
                {
                    this.Append(value.Value);
                }
            }

            public void AppendNullable(string? value)
            {
                this.Append(value is null ? 0 : 1);
                if (value is not null)
                {
                    this.Append(value);
                }
            }

            public void Append(bool value) => this.Append(value ? 1 : 0);

            public string Complete()
            {
                if (this.completed)
                {
                    throw new InvalidOperationException(
                        "The canonical digest has already been completed.");
                }

                this.completed = true;
                return Convert.ToHexStringLower(this.hash.GetHashAndReset());
            }

            public void Dispose() => this.hash.Dispose();

            private void AppendRaw(string value) => this.hash.AppendData(
                Encoding.UTF8.GetBytes(value));
        }
    }
}

internal sealed record WorkspaceStaffIdentityAnchorPreparedReconcile(
    Guid OrganizationId,
    IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> Batch,
    WorkspaceStaffIdentityAnchorCutoverStatus AcceptedStatus,
    WorkspaceStaffIdentityAnchorOwnerManifest OwnerManifest,
    string AcceptedSourceEvidenceSha256,
    string AcceptedAnchorStateSha256,
    string AcceptedOwnerManifestSha256);
