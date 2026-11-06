namespace BunkFy.Modules.Ingestion.Application.Commands;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Ingestion.Application.Ports;

public sealed record ClaimExpiredRawPayloadsCommand(
    Guid ClaimId,
    int BatchSize,
    int StaleClaimMinutes,
    Guid? RetentionExecutionId = null,
    int? RetentionAttempt = null)
    : ITransactionalCommand<IReadOnlyList<RawPayloadPurgeCandidate>>;

public sealed record CompleteRawPayloadPurgeCommand(
    Guid ReceiptId,
    Guid ClaimId,
    Guid? RetentionExecutionId = null,
    int? RetentionAttempt = null) : ITransactionalCommand<Unit>;
