namespace BunkFy.Modules.Retention.Application.Commands;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Models;

public sealed record RetentionExecutionStart(
    bool DispatchRequired,
    bool AttemptAdvanced,
    RetentionExecutionState State,
    RetentionContributionRequest Request);
