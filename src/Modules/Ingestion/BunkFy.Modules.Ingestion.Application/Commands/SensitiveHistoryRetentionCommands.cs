namespace BunkFy.Modules.Ingestion.Application.Commands;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Ingestion.Application.Ports;

public sealed record RedactExpiredSensitiveHistoryCommand(
    int BatchSize,
    Guid? RetentionExecutionId = null,
    int? RetentionAttempt = null)
    : ITransactionalCommand<SensitiveHistoryRedactionBatchResult>;
