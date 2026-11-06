namespace BunkFy.Modules.Retention.Application.Validation;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Naming;

internal sealed class RequestRetentionRunRetryCommandValidator
    : ICommandValidator<RequestRetentionRunRetryCommand>
{
    public IEnumerable<string> Validate(RequestRetentionRunRetryCommand command)
    {
        if (command.RunId == Guid.Empty)
        {
            yield return "RunId is required.";
        }

        if (!ScopeIds.TryNormalize(command.TenantId, out string? tenantId) ||
            !string.Equals(tenantId, command.TenantId, StringComparison.Ordinal))
        {
            yield return "TenantId must be a canonical scope id.";
        }

        RetentionRunRetryExpectation? expected =
            command.ExpectedCurrentSchedule;
        if (expected is null)
        {
            yield break;
        }

        if (!IsValidKey(expected.OwnerKey) ||
            !IsValidKey(expected.DataClassKey))
        {
            yield return "Expected retention keys must be bounded lower-case ASCII identifiers.";
        }

        if (expected.ExecutionPolicyVersion <= 0)
        {
            yield return "ExpectedExecutionPolicyVersion must be greater than zero.";
        }

        if (expected.EvidenceVersion <= 0)
        {
            yield return "ExpectedEvidenceVersion must be greater than zero.";
        }

        bool validTarget = expected.TargetScopeKind switch
        {
            RetentionTargetScopeKind.Tenant => expected.PropertyId is null,
            RetentionTargetScopeKind.Property =>
                expected.PropertyId is not null &&
                expected.PropertyId != Guid.Empty,
            _ => false
        };
        if (!validTarget)
        {
            yield return "Expected retention target coordinates are invalid.";
        }
    }

    private static bool IsValidKey(string? value) =>
        value is not null &&
        value.Length is > 0 and <= RetentionExecutionContract.KeyMaxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.') &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}
