namespace BunkFy.Modules.Workspaces.Domain;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class WorkspaceStaffHistoricalNoProvisionReceipt
    : ScopedAggregateRoot<Guid>
{
    public const int CurrentContractVersion = 1;
    public const int Sha256Length = 64;
    public const int ReviewerIdMaxLength = 256;
    public const string SubjectPseudonymPrefix = "no-provision:";

    private WorkspaceStaffHistoricalNoProvisionReceipt() { }

    private WorkspaceStaffHistoricalNoProvisionReceipt(
        Guid id,
        string scopeId)
        : base(id, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ApplicationId { get; private set; }
    public WorkspaceStaffOnboardingSource SourceKind { get; private set; }
    public Guid SourceId { get; private set; }
    public long ExpectedApplicationVersion { get; private set; }
    public WorkspaceStaffOnboardingState ExpectedApplicationStatus
    {
        get;
        private set;
    }
    public long ResultApplicationVersion { get; private set; }
    public WorkspaceStaffOnboardingState ResultApplicationStatus
    {
        get;
        private set;
    }
    public long OrganizationsScopeRevision { get; private set; }
    public long OrganizationsSourceVersion { get; private set; }
    public WorkspaceStaffHistoricalNoProvisionAuthorityStatus
        OrganizationsSourceStatus
    { get; private set; }
    public string StaffEvidenceSha256 { get; private set; } = string.Empty;
    public Guid ExternalEvidenceManifestId { get; private set; }
    public string ExternalEvidenceSha256 { get; private set; } = string.Empty;
    public string ReviewerId { get; private set; } = string.Empty;
    public DateTimeOffset ReviewedAtUtc { get; private set; }
    public string CanonicalSha256 { get; private set; } = string.Empty;

    public static Result<WorkspaceStaffHistoricalNoProvisionReceipt> Create(
        Guid receiptId,
        string tenantId,
        Guid operationId,
        Guid applicationId,
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        long expectedApplicationVersion,
        WorkspaceStaffOnboardingState expectedApplicationStatus,
        long resultApplicationVersion,
        WorkspaceStaffOnboardingState resultApplicationStatus,
        long organizationsScopeRevision,
        long organizationsSourceVersion,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
            organizationsSourceStatus,
        string staffEvidenceSha256,
        Guid externalEvidenceManifestId,
        string externalEvidenceSha256,
        string reviewerId,
        DateTimeOffset reviewedAtUtc)
    {
        string staffDigest = NormalizeSha256(staffEvidenceSha256);
        string externalDigest = NormalizeSha256(externalEvidenceSha256);
        string reviewer = reviewerId?.Trim() ?? string.Empty;
        if (receiptId == Guid.Empty ||
            operationId == Guid.Empty ||
            applicationId == Guid.Empty ||
            sourceId == Guid.Empty ||
            externalEvidenceManifestId == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            sourceKind is not (WorkspaceStaffOnboardingSource.Invitation or
                WorkspaceStaffOnboardingSource.EnrollmentLink) ||
            !Enum.IsDefined(expectedApplicationStatus) ||
            expectedApplicationStatus == WorkspaceStaffOnboardingState.Unknown ||
            !IsTerminal(resultApplicationStatus) ||
            !IsTerminalAuthority(sourceKind, organizationsSourceStatus) ||
            !IsSha256(staffDigest) ||
            !IsSha256(externalDigest) ||
            reviewer.Length is 0 or > ReviewerIdMaxLength ||
            reviewer.Any(char.IsControl) ||
            reviewedAtUtc == default)
        {
            return Invalid();
        }

        bool expectedTerminal = IsTerminal(expectedApplicationStatus);
        bool transitionValid = expectedTerminal
            ? resultApplicationStatus == expectedApplicationStatus &&
              resultApplicationVersion is >= 1 &&
              resultApplicationVersion >= expectedApplicationVersion &&
              resultApplicationVersion <= expectedApplicationVersion + 1
            : resultApplicationStatus ==
                  WorkspaceStaffOnboardingState.Superseded &&
              resultApplicationVersion == expectedApplicationVersion + 1;
        if (expectedApplicationVersion is < 1 or long.MaxValue ||
            organizationsScopeRevision < 0 ||
            organizationsSourceVersion < 1 ||
            !transitionValid)
        {
            return Invalid();
        }

        WorkspaceStaffHistoricalNoProvisionReceipt receipt = new(
            receiptId,
            scopeId)
        {
            ContractVersion = CurrentContractVersion,
            OperationId = operationId,
            ApplicationId = applicationId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            ExpectedApplicationVersion = expectedApplicationVersion,
            ExpectedApplicationStatus = expectedApplicationStatus,
            ResultApplicationVersion = resultApplicationVersion,
            ResultApplicationStatus = resultApplicationStatus,
            OrganizationsScopeRevision = organizationsScopeRevision,
            OrganizationsSourceVersion = organizationsSourceVersion,
            OrganizationsSourceStatus = organizationsSourceStatus,
            StaffEvidenceSha256 = staffDigest,
            ExternalEvidenceManifestId = externalEvidenceManifestId,
            ExternalEvidenceSha256 = externalDigest,
            ReviewerId = reviewer,
            ReviewedAtUtc = CanonicalizeTimestamp(reviewedAtUtc)
        };
        receipt.CanonicalSha256 = receipt.ComputeCanonicalSha256();
        return Result.Success(receipt);
    }

    public bool MatchesReplay(
        string tenantId,
        Guid applicationId,
        long expectedApplicationVersion,
        WorkspaceStaffOnboardingState expectedApplicationStatus,
        long organizationsScopeRevision,
        long organizationsSourceVersion,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
            organizationsSourceStatus,
        Guid externalEvidenceManifestId,
        string externalEvidenceSha256,
        string reviewerId) =>
        this.HasValidCanonicalProof() &&
        string.Equals(this.ScopeId, tenantId, StringComparison.Ordinal) &&
        this.ApplicationId == applicationId &&
        this.ExpectedApplicationVersion == expectedApplicationVersion &&
        this.ExpectedApplicationStatus == expectedApplicationStatus &&
        this.OrganizationsScopeRevision == organizationsScopeRevision &&
        this.OrganizationsSourceVersion == organizationsSourceVersion &&
        this.OrganizationsSourceStatus == organizationsSourceStatus &&
        this.ExternalEvidenceManifestId == externalEvidenceManifestId &&
        string.Equals(
            this.ExternalEvidenceSha256,
            NormalizeSha256(externalEvidenceSha256),
            StringComparison.Ordinal) &&
        string.Equals(
            this.ReviewerId,
            reviewerId?.Trim(),
            StringComparison.Ordinal);

    public bool MatchesResult(WorkspaceStaffOnboarding application) =>
        application is not null &&
        this.HasValidCanonicalProof() &&
        IsTerminal(this.ResultApplicationStatus) &&
        string.Equals(
            this.ScopeId,
            application.ScopeId,
            StringComparison.Ordinal) &&
        this.ApplicationId == application.Id &&
        this.SourceKind == application.SourceKind &&
        this.SourceId == application.SourceId &&
        this.ResultApplicationVersion == application.Version &&
        this.ResultApplicationStatus == application.Status &&
        string.Equals(
            application.SubjectId,
            this.CreateSubjectPseudonym(),
            StringComparison.Ordinal) &&
        application.VerifiedAccountEmail is null &&
        application.DisplayName is null &&
        application.LegalName is null &&
        application.WorkEmail is null &&
        application.WorkPhone is null &&
        application.EmployeeNumber is null &&
        application.JobTitle is null &&
        application.Department is null &&
        application.FailureCode is null &&
        !application.HasIdentityAnchorState;

    public bool HasValidCanonicalProof() =>
        this.ContractVersion == CurrentContractVersion &&
        IsSha256(this.StaffEvidenceSha256) &&
        IsSha256(this.ExternalEvidenceSha256) &&
        IsSha256(this.CanonicalSha256) &&
        string.Equals(
            this.CanonicalSha256,
            this.ComputeCanonicalSha256(),
            StringComparison.Ordinal);

    public string CreateSubjectPseudonym() =>
        CreateSubjectPseudonym(this.Id);

    public static string CreateSubjectPseudonym(Guid receiptId) =>
        receiptId == Guid.Empty
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{SubjectPseudonymPrefix}{receiptId:D}");

    public static bool IsTerminalAuthority(
        WorkspaceStaffOnboardingSource sourceKind,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus status) =>
        sourceKind switch
        {
            WorkspaceStaffOnboardingSource.Invitation => status is
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationRevoked or
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationSuperseded or
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationExpired,
            WorkspaceStaffOnboardingSource.EnrollmentLink => status is
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .EnrollmentLinkDisabled or
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .EnrollmentLinkRotated or
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .EnrollmentLinkExpired,
            _ => false
        };

    private string ComputeCanonicalSha256()
    {
        StringBuilder canonical = new();
        Append(canonical, "workspaces-staff-historical-no-provision|v1");
        Append(canonical, this.ContractVersion);
        Append(canonical, this.Id);
        Append(canonical, this.ScopeId);
        Append(canonical, this.OperationId);
        Append(canonical, this.ApplicationId);
        Append(canonical, (int)this.SourceKind);
        Append(canonical, this.SourceId);
        Append(canonical, this.ExpectedApplicationVersion);
        Append(canonical, (int)this.ExpectedApplicationStatus);
        Append(canonical, this.ResultApplicationVersion);
        Append(canonical, (int)this.ResultApplicationStatus);
        Append(canonical, this.OrganizationsScopeRevision);
        Append(canonical, this.OrganizationsSourceVersion);
        Append(canonical, (int)this.OrganizationsSourceStatus);
        Append(canonical, this.StaffEvidenceSha256);
        Append(canonical, this.ExternalEvidenceManifestId);
        Append(canonical, this.ExternalEvidenceSha256);
        Append(canonical, this.ReviewerId);
        Append(canonical, this.ReviewedAtUtc);
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static bool IsTerminal(WorkspaceStaffOnboardingState status) =>
        status is WorkspaceStaffOnboardingState.Completed or
            WorkspaceStaffOnboardingState.Rejected or
            WorkspaceStaffOnboardingState.Superseded or
            WorkspaceStaffOnboardingState.Expired or
            WorkspaceStaffOnboardingState.Withdrawn;

    private static void Append(StringBuilder target, object value)
    {
        string text = value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture),
            Guid id => id.ToString("N"),
            IFormattable formattable => formattable.ToString(
                null,
                CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        target.Append(text.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(text);
    }

    private static string NormalizeSha256(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static DateTimeOffset CanonicalizeTimestamp(
        DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            utc.Ticks - (utc.Ticks % ticksPerMicrosecond),
            TimeSpan.Zero);
    }

    private static bool IsSha256(string value) =>
        value.Length == Sha256Length &&
        value.All(character => character is (>= '0' and <= '9') or
            (>= 'a' and <= 'f'));

    private static Result<WorkspaceStaffHistoricalNoProvisionReceipt>
        Invalid() =>
        Result.Failure<WorkspaceStaffHistoricalNoProvisionReceipt>(
            WorkspaceStaffHistoricalNoProvisionErrors.ReceiptInvalid);
}

public enum WorkspaceStaffHistoricalNoProvisionAuthorityStatus
{
    Unknown = 0,
    InvitationPending = 1,
    InvitationAccepted = 2,
    InvitationRevoked = 3,
    InvitationSuperseded = 4,
    InvitationExpired = 5,
    EnrollmentLinkActive = 6,
    EnrollmentLinkDisabled = 7,
    EnrollmentLinkRotated = 8,
    EnrollmentLinkExpired = 9,
    EnrollmentLinkCapacityReached = 10
}
