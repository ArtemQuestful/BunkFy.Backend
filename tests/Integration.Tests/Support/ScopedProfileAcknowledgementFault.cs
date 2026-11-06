namespace Integration.Tests.Support;

using System.Collections.Concurrent;
using Gma.Framework.AccessControl;
using Gma.Framework.Results;
using Gma.Modules.AccessControl.Contracts;
using Microsoft.Extensions.DependencyInjection;

// Simulates a lost response only after the actual AccessControl owner succeeds.
// Reads and the authoritative reconciliation use the original registered service.
internal sealed class ScopedProfileAcknowledgementFault
{
    private readonly ConcurrentDictionary<AccessSubject, int> attempts = new();

    public void Arm(Guid subjectId) =>
        this.attempts.TryAdd(AccessSubject.User(subjectId.ToString("D")), 0);

    public int AttemptCount(Guid subjectId) =>
        this.attempts.GetValueOrDefault(
            AccessSubject.User(subjectId.ToString("D")));

    public void Install(IServiceCollection services)
    {
        ServiceDescriptor original = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IScopedAccessProfileManager));
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(
            typeof(IScopedAccessProfileManager),
            provider => new LostAcknowledgementManager(
                (IScopedAccessProfileManager)(original.ImplementationInstance ??
                    original.ImplementationFactory?.Invoke(provider) ??
                    ActivatorUtilities.CreateInstance(
                        provider,
                        original.ImplementationType!)),
                this),
            original.Lifetime));
    }

    private bool ShouldFail(AccessSubject subject)
    {
        if (!this.attempts.ContainsKey(subject))
        {
            return false;
        }

        this.attempts.AddOrUpdate(subject, 1, (_, count) => count + 1);
        return true;
    }

    private sealed class LostAcknowledgementManager(
        IScopedAccessProfileManager original,
        ScopedProfileAcknowledgementFault fault) : IScopedAccessProfileManager
    {
        public Task<Result<ScopedAccessProfileAssignmentSet>>
            GetSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            original.GetSubjectAssignmentsAsync(
                subject, ownerScope, actor, cancellationToken);

        public async Task<Result<ScopedAccessProfileAssignmentReconciliation>>
            ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default)
        {
            Result<ScopedAccessProfileAssignmentReconciliation> result =
                await original.ReconcileSubjectAssignmentsAsync(
                    subject, ownerScope, targets, actor, cancellationToken)
                    .ConfigureAwait(false);
            return result.IsSuccess && fault.ShouldFail(subject)
                ? Result.Failure<ScopedAccessProfileAssignmentReconciliation>(
                    new("Test.AccessAcknowledgementLost",
                        "The access owner committed before its response was lost."))
                : result;
        }
    }
}
