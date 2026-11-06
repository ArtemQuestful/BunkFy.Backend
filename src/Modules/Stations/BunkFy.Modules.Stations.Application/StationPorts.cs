namespace BunkFy.Modules.Stations.Application;

using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using System.Security.Cryptography;

/// <summary>Secret provider is deployment-owned; no default or database pepper is supplied.</summary>
public interface IStationPepperProvider
{
    bool TryGet(string version, out ReadOnlyMemory<byte> pepper);
}

public enum StationPinVerification { Unavailable = 0, Valid = 1, Invalid = 2, Busy = 3 }
public interface IStationPinVerifier
{
    Task<StationPinMaterial?> CreateAsync(string pin, CancellationToken cancellationToken = default);
    Task<StationPinVerification> VerifyAsync(string pin, StationStaffCredential credential, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local persistence primitives only. Callers MUST supply external owner admission before activation.
/// P1 registers no host/session validator or management/runtime services.
/// </summary>
public interface IStationsStore
{
    Task<StationCoreResult> TryUnlockCoreAsync(Guid operationId, Guid stationId, Guid browserId, Guid staffId,
        long expectedGeneration, long expectedCredentialRevision, StationAuthorityKind kind, long? expectedRegistrationRevision,
        string pin, DateTimeOffset now, StationEnrollmentBinding? enrollment = null, bool ownerRejected = false, string? opaqueCredential = null,
        CancellationToken cancellationToken = default);
    Task<StationCoreResult> LockCoreAsync(Guid operationId, Guid stationId, Guid browserId, long expectedGeneration,
        DateTimeOffset now, string? opaqueCredential = null, CancellationToken cancellationToken = default);
    Task<StationCoreResult> RevokeCredentialCoreAsync(Guid operationId, Guid staffId, long expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<StationCoreResult> RevokeStationCoreAsync(Guid operationId, Guid stationId, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<StationCoreResult> RevokeGrantCoreAsync(Guid operationId, Guid propertyId, Guid staffId, long expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<StationCoreResult> RedeemSetupCoreAsync(Guid operationId, Guid setupId, Guid browserId, StationPinMaterial material,
        DateTimeOffset now, StationEnrollmentBinding? enrollment = null, string? opaqueCredential = null, CancellationToken cancellationToken = default);
}

/// <summary>Stations-owner bootstrap only; never accepts a caller tenant or exposes a general scope runner.</summary>
public interface IStationCredentialBootstrap
{
    Task<StationDeviceReference?> FindAsync(string opaqueCredential, CancellationToken cancellationToken = default);
}
public sealed record StationDeviceReference(string ScopeId, Guid BrowserSessionId, Guid StationId, Guid PropertyId);
/// <summary>Immutable original pairing issuer. Never replaced by a recovery login or client claim.</summary>
public sealed record StationPairingHandoffFacts(StationDeviceReference Device, string IssuerSubjectId, Guid IssuerSessionId);
public interface IStationPairingHandoffReader
{
    Task<StationPairingHandoffFacts?> ReadHandoffAsync(StationDeviceReference device, CancellationToken cancellationToken = default);
}
public sealed record StationCredentialFacts(Guid StaffMemberId, long Revision, bool Revoked, StationEnrollmentBinding Enrollment);
public sealed record StationRuntimeFacts(StationSessionSnapshot Session, bool ActorCurrent,
    StationCredentialFacts? Credential, long? GrantRevision, bool GrantRevoked, bool Registered = true, long RegistrationRevision = 0);
public sealed record StationSetupFacts(Guid Id, Guid StaffMemberId, long CredentialRevision,
    StationEnrollmentBinding Enrollment, StationActorKind Intent, DateTimeOffset ExpiresAtUtc,
    StationIssuerKind IssuerKind = StationIssuerKind.Unknown, string? IssuerSubjectId = null,
    DateTimeOffset? AssuranceExpiresAtUtc = null);
public interface IStationRuntimeStore
{
    Task<StationRuntimeFacts?> ReadAsync(StationDeviceReference device, string opaqueCredential, Guid? selectedStaff,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<StationSetupFacts?> ReadSetupAsync(StationDeviceReference device, Guid setupId, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<StationCoreResult?> ReadSetupOutcomeAsync(Guid operationId, StationSetupFacts setup, StationDeviceReference device,
        CancellationToken cancellationToken = default);
    Task<StationCoreResult> ForegroundActivityAsync(Guid operationId, StationDeviceReference device, string opaqueCredential,
        StationActorCoordinate actor, StationCredentialFacts credential, long? registrationRevision, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed record StationJobGrant(long Revision, bool Revoked);
public interface IStationJobGrantReader
{
    Task<StationJobGrant?> ReadJobGrantAsync(Guid propertyId, Guid staffId, bool checkOut, CancellationToken cancellationToken = default);
}

public sealed record StationWorkspaceSettings(bool SharedStationPinEnabled, long Revision);

public interface IStationWorkspaceSettingsReader
{
    Task<StationWorkspaceSettings> ReadWorkspaceSettingsAsync(
        CancellationToken cancellationToken = default);
}

public interface IStationWorkspaceSettingsStore : IStationWorkspaceSettingsReader
{
    Task<StationWorkspaceSettingsUpdateResult> UpdateWorkspaceSettingsAsync(
        bool sharedStationPinEnabled,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public enum StationWorkspaceSettingsUpdateState
{
    Applied = 0,
    Conflict = 1,
    Unavailable = 2,
}

public sealed record StationWorkspaceSettingsUpdateResult(
    StationWorkspaceSettingsUpdateState State,
    StationWorkspaceSettings Settings);

public enum StationAdmissionState { Current = 0, Denied = 1, StateChanged = 2, Unavailable = 3 }
public sealed record StationAdmission(StationAdmissionState State, DateOnly? PropertyLocalDate = null);

public sealed record StationIssuer(string SubjectId, StationIssuerKind Kind, DateTimeOffset? AssuranceExpiresAtUtc, Guid? SessionId = null);
public sealed record StationPrimaryObservation(StationAdmissionState State, StationIssuer? Issuer = null);
public sealed record StationManagementCommand(StationOperationKind Kind, Guid PropertyId,
    Guid? StationId = null, Guid? BrowserSessionId = null, Guid? StaffMemberId = null, Guid? SetupGrantId = null,
    long ExpectedVersion = 0, string? Label = null, StationEnrollmentBinding? Enrollment = null,
    DateTimeOffset? SetupExpiresAtUtc = null);
public sealed record StationManagementWrite(StationCoreResult Result, bool Executed);
public sealed record StationRegistrationFacts(Guid StaffMemberId, long Version, long RosterReference, bool Active,
    StationCredentialFacts? Credential = null, long? GrantRevision = null, bool GrantRevoked = true);
public sealed record StationOwnPinFacts(long RegistrationVersion, StationCredentialFacts? Credential);
public interface IStationManagementStore
{
    Task<StationOwnPinFacts?> OwnPinFactsAsync(Guid propertyId, Guid staffId, CancellationToken cancellationToken = default);
    Task<StationCoreResult?> OwnPinOutcomeAsync(Guid operationId, StationIssuer issuer, Guid propertyId, Guid staffId,
        long expectedRevision, StationEnrollmentBinding binding, CancellationToken cancellationToken = default);
    Task<StationCoreResult> SetOwnPinAsync(Guid operationId, StationIssuer issuer, Guid propertyId, Guid staffId,
        long expectedRevision, long registrationVersion, StationEnrollmentBinding binding, StationPinMaterial material,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<StationManagementWrite> ExecuteAsync(Guid operationId, StationIssuer issuer, StationManagementCommand command,
        DateTimeOffset now, string? newCredentialDigest = null, CancellationToken cancellationToken = default);
    Task<StationCoreResult?> ReadOutcomeAsync(Guid operationId, string issuerSubjectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StationListItem>> ListStationsAsync(Guid propertyId, int offset, int pageSize, CancellationToken cancellationToken = default);
    Task<StationListItem?> FindStationAsync(Guid stationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StationRegistrationFacts>> RegistrationsAsync(Guid propertyId, bool activeOnly, CancellationToken cancellationToken = default);
    Task<StationStaffManagementItem?> StaffStatusAsync(Guid propertyId, Guid staffId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<StationSetupFacts?> FindSetupAsync(Guid setupId, StationDeviceReference device, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>One-time internal handoff to a future cookie adapter. Never a normal JSON body or log value.</summary>
public sealed class StationPairingHandoff(StationManagementResponse response, string? credential)
{
    public StationManagementResponse Response { get; } = response;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Credential { get; } = credential;
    public override string ToString() => nameof(StationPairingHandoff);
}

/// <summary>One canonical 32-byte opaque station credential; never diagnostics or a client-supplied scope.</summary>
public static class StationCredentialEncoding
{
    public static string? Digest(string credential)
    {
        if (credential is not { Length: 43 } || credential.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            return null;
        }
        byte[] bytes;
        try
        { bytes = Convert.FromBase64String(credential.Replace('-', '+').Replace('_', '/') + "="); }
        catch (FormatException) { return null; }
        try
        {
            if (bytes.Length != 32 || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != credential)
            {
                return null;
            }
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
