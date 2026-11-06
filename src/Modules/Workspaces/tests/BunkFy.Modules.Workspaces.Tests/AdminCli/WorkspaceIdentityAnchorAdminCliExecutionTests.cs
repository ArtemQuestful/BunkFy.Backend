namespace BunkFy.Modules.Workspaces.Tests.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using BunkFy.Modules.Workspaces.Admin.Contracts;
using BunkFy.Modules.Workspaces.AdminCli;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Collection(WorkspaceIdentityAnchorAdminCliProcessIsolation.Name)]
[Trait("Category", "Unit")]
public sealed class WorkspaceIdentityAnchorAdminCliExecutionTests
{
    private const string TenantId = "tenant-a";
    private const string ReviewerId = "authenticated-reviewer";
    private const string SourceSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StateSha256 =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OwnerSha256 =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string HistoricalEvidenceSha256 =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ReceiptSha256 =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string CanonicalSha256 =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private static readonly Guid OperationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ApplicationId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EvidenceManifestId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset ReviewedAtUtc = new(
        2026,
        8,
        11,
        18,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Historical_review_requires_its_dedicated_permission_and_explicit_confirmation()
    {
        using AdminCliHarness harness = new();
        harness.Dispatcher.Handle<
            ReviewWorkspaceStaffHistoricalNoProvisionCommand,
            WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                (_, _) => Task.FromResult(Result.Success(
                    ValidHistoricalResult())));

        AdminCliExecution denied = await harness.ExecuteAtHostBoundaryAsync(
            HistoricalArgs(includeConfirmation: true));

        Assert.Equal(AdminExitCodes.Unauthorized, denied.ExitCode);
        Assert.Empty(harness.Dispatcher.Commands);
        AuthorizationAttempt deniedAttempt = Assert.Single(
            harness.Authorization.Attempts);
        Assert.Equal(
            WorkspacesAdminPermissions
                .IdentityAnchorsHistoricalNoProvisionReview.Code,
            deniedAttempt.Permission);
        Assert.Equal(ReviewerId, deniedAttempt.ActorId);
        Assert.Equal(TenantId, deniedAttempt.TenantId);
        AdminAuditRecord deniedAudit = Assert.Single(harness.Audit.Records);
        Assert.Equal(
            WorkspacesAdminOperationNames
                .IdentityAnchorsHistoricalNoProvisionReview,
            deniedAudit.Operation);
        Assert.Equal(AdminAuditResult.Denied, deniedAudit.Result);

        harness.Authorization.Allow(
            WorkspacesAdminPermissions
                .IdentityAnchorsHistoricalNoProvisionReview.Code);

        AdminCliExecution unconfirmed =
            await harness.ExecuteAtHostBoundaryAsync(
                HistoricalArgs(includeConfirmation: false));

        Assert.Equal(AdminExitCodes.Failed, unconfirmed.ExitCode);
        Assert.Contains(
            AdminErrors.ConfirmationRequired.Message,
            unconfirmed.Error,
            StringComparison.Ordinal);
        Assert.Empty(harness.Dispatcher.Commands);
        AdminAuditRecord confirmationAudit = Assert.Single(
            harness.Audit.Records,
            record =>
                record.ErrorCode == AdminErrors.ConfirmationRequired.Code);
        Assert.Equal(AdminAuditResult.Failed, confirmationAudit.Result);
    }

    [Fact]
    public async Task Historical_review_uses_the_scoped_actor_and_waits_for_the_transactional_dispatch_result()
    {
        using AdminCliHarness harness = new();
        harness.Authorization.Allow(
            WorkspacesAdminPermissions
                .IdentityAnchorsHistoricalNoProvisionReview.Code);
        TaskCompletionSource<
            Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>>
            completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ReviewWorkspaceStaffHistoricalNoProvisionCommand>
            dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Dispatcher.Handle<
            ReviewWorkspaceStaffHistoricalNoProvisionCommand,
            WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                (command, _) =>
                {
                    dispatched.TrySetResult(command);
                    return completion.Task;
                });

        Task<AdminCliExecution> executionTask =
            harness.ExecuteAtHostBoundaryAsync(
                HistoricalArgs(includeConfirmation: true));
        ReviewWorkspaceStaffHistoricalNoProvisionCommand command =
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(executionTask.IsCompleted);
        Assert.Equal(ReviewerId, command.ReviewerId);
        Assert.Equal(OperationId, command.OperationId);
        Assert.Equal(ApplicationId, command.ApplicationId);
        Assert.IsType<ITransactionalCommand<
            WorkspaceStaffHistoricalNoProvisionDispositionResult>>(
                command,
                exactMatch: false);

        completion.SetResult(Result.Success(ValidHistoricalResult()));
        AdminCliExecution execution = await executionTask;

        Assert.Equal(AdminExitCodes.Success, execution.ExitCode);
        Assert.Contains(OperationId.ToString("D"), execution.Output);
        AdminAuditRecord audit = Assert.Single(harness.Audit.Records);
        Assert.Equal(ReviewerId, audit.ActorId);
        Assert.Equal(AdminAuditResult.Succeeded, audit.Result);

        AdminCliExecution spoofAttempt =
            await harness.ExecuteAtHostBoundaryAsync(
                [.. HistoricalArgs(includeConfirmation: true),
                    "--reviewer-id", "spoofed-reviewer"]);

        Assert.NotEqual(AdminExitCodes.Success, spoofAttempt.ExitCode);
        Assert.Single(harness.Dispatcher.Commands);
    }

    [Fact]
    public async Task Historical_review_malformed_success_and_dispatch_failure_are_stable_nonzero_results()
    {
        using AdminCliHarness harness = new();
        harness.Authorization.Allow(
            WorkspacesAdminPermissions
                .IdentityAnchorsHistoricalNoProvisionReview.Code);
        Queue<Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>>
            results = new(
            [
                Result.Success(ValidHistoricalResult() with
                {
                    ReceiptId = Guid.Empty
                }),
                Result.Failure<
                    WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                        WorkspaceStaffHistoricalNoProvisionApplicationErrors
                            .ExternalEvidenceUnavailable)
            ]);
        harness.Dispatcher.Handle<
            ReviewWorkspaceStaffHistoricalNoProvisionCommand,
            WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                (_, _) => Task.FromResult(results.Dequeue()));

        AdminCliExecution malformed =
            await harness.ExecuteAtHostBoundaryAsync(
                HistoricalArgs(includeConfirmation: true));
        AdminCliExecution failed =
            await harness.ExecuteAtHostBoundaryAsync(
                HistoricalArgs(includeConfirmation: true));

        Assert.Equal(AdminExitCodes.Failed, malformed.ExitCode);
        Assert.Contains(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors
                .Conflict.Message,
            malformed.Error,
            StringComparison.Ordinal);
        Assert.Equal(AdminExitCodes.Failed, failed.ExitCode);
        Assert.Contains(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors
                .ExternalEvidenceUnavailable.Message,
            failed.Error,
            StringComparison.Ordinal);
        Assert.Equal(2, harness.Dispatcher.Commands.Count);
        Assert.All(
            harness.Audit.Records,
            record => Assert.Equal(AdminAuditResult.Failed, record.Result));
    }

    [Fact]
    public async Task Reconcile_requires_permission_and_confirmation_before_dispatch()
    {
        using ManifestFile manifest = new();
        using AdminCliHarness harness = new();
        harness.Dispatcher.Handle<
            ReconcileWorkspaceStaffIdentityAnchorsCommand,
            WorkspaceStaffIdentityAnchorReconcileResult>(
                (_, _) => Task.FromResult(Result.Success(
                    ValidReconcileResult())));

        AdminCliExecution denied = await harness.ExecuteAtHostBoundaryAsync(
            ReconcileArgs(manifest.Path, includeConfirmation: true));

        Assert.Equal(AdminExitCodes.Unauthorized, denied.ExitCode);
        Assert.Empty(harness.Dispatcher.Commands);
        AuthorizationAttempt deniedAttempt = Assert.Single(
            harness.Authorization.Attempts);
        Assert.Equal(
            WorkspacesAdminPermissions.IdentityAnchorsReconcile.Code,
            deniedAttempt.Permission);

        harness.Authorization.Allow(
            WorkspacesAdminPermissions.IdentityAnchorsReconcile.Code);
        AdminCliExecution unconfirmed =
            await harness.ExecuteAtHostBoundaryAsync(
                ReconcileArgs(manifest.Path, includeConfirmation: false));

        Assert.Equal(AdminExitCodes.Failed, unconfirmed.ExitCode);
        Assert.Contains(
            AdminErrors.ConfirmationRequired.Message,
            unconfirmed.Error,
            StringComparison.Ordinal);
        Assert.Empty(harness.Dispatcher.Commands);
    }

    [Fact]
    public async Task Reconcile_indeterminate_malformed_and_failed_dispatch_results_are_nonzero()
    {
        using ManifestFile manifest = new();
        using AdminCliHarness harness = new();
        harness.Authorization.Allow(
            WorkspacesAdminPermissions.IdentityAnchorsReconcile.Code);
        WorkspaceStaffIdentityAnchorReconcileResult valid =
            ValidReconcileResult();
        Queue<Result<WorkspaceStaffIdentityAnchorReconcileResult>> results =
            new(
            [
                Result.Success(valid with
                {
                    Outcome = WorkspaceStaffIdentityAnchorReconcileOutcome
                        .ApplyOutcomeUnknown,
                    MustRerunStatus = true
                }),
                Result.Success(valid with
                {
                    Outcome = WorkspaceStaffIdentityAnchorReconcileOutcome
                        .Unknown
                }),
                Result.Failure<
                    WorkspaceStaffIdentityAnchorReconcileResult>(
                        WorkspaceStaffIdentityAnchorCutoverErrors
                            .SourceEvidenceChanged)
            ]);
        harness.Dispatcher.Handle<
            ReconcileWorkspaceStaffIdentityAnchorsCommand,
            WorkspaceStaffIdentityAnchorReconcileResult>(
                (_, _) => Task.FromResult(results.Dequeue()));

        AdminCliExecution indeterminate =
            await harness.ExecuteAtHostBoundaryAsync(
                ReconcileArgs(manifest.Path, includeConfirmation: true));
        AdminCliExecution malformed =
            await harness.ExecuteAtHostBoundaryAsync(
                ReconcileArgs(manifest.Path, includeConfirmation: true));
        AdminCliExecution failed =
            await harness.ExecuteAtHostBoundaryAsync(
                ReconcileArgs(manifest.Path, includeConfirmation: true));

        Assert.Equal(AdminExitCodes.Failed, indeterminate.ExitCode);
        Assert.Contains("ApplyOutcomeUnknown", indeterminate.Output);
        Assert.Contains(
            "do not retry this reconcile request",
            indeterminate.Output,
            StringComparison.Ordinal);
        Assert.Equal(AdminExitCodes.Failed, malformed.ExitCode);
        Assert.Contains(
            WorkspaceStaffIdentityAnchorCutoverErrors
                .ApplyOutcomeUnknown.Message,
            malformed.Error,
            StringComparison.Ordinal);
        Assert.Equal(AdminExitCodes.Failed, failed.ExitCode);
        Assert.Contains(
            WorkspaceStaffIdentityAnchorCutoverErrors
                .SourceEvidenceChanged.Message,
            failed.Error,
            StringComparison.Ordinal);
        Assert.Equal(3, harness.Dispatcher.Commands.Count);
    }

    [Fact]
    public async Task Both_governed_commands_map_dispatch_cancellation_to_the_host_failure_exit_code()
    {
        using ManifestFile manifest = new();
        using AdminCliHarness harness = new();
        harness.Authorization.Allow(
            WorkspacesAdminPermissions
                .IdentityAnchorsHistoricalNoProvisionReview.Code);
        harness.Authorization.Allow(
            WorkspacesAdminPermissions.IdentityAnchorsReconcile.Code);
        CancellationToken canceled = new(canceled: true);
        harness.Dispatcher.Handle<
            ReviewWorkspaceStaffHistoricalNoProvisionCommand,
            WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                (_, _) => Task.FromCanceled<Result<
                    WorkspaceStaffHistoricalNoProvisionDispositionResult>>(
                        canceled));
        harness.Dispatcher.Handle<
            ReconcileWorkspaceStaffIdentityAnchorsCommand,
            WorkspaceStaffIdentityAnchorReconcileResult>(
                (_, _) => Task.FromCanceled<Result<
                    WorkspaceStaffIdentityAnchorReconcileResult>>(canceled));

        AdminCliExecution historical =
            await harness.ExecuteAtHostBoundaryAsync(
                HistoricalArgs(includeConfirmation: true));
        AdminCliExecution reconcile =
            await harness.ExecuteAtHostBoundaryAsync(
                ReconcileArgs(manifest.Path, includeConfirmation: true));

        Assert.Equal(AdminExitCodes.Failed, historical.ExitCode);
        Assert.Equal(AdminExitCodes.Failed, reconcile.ExitCode);
        Assert.Contains("Admin command was canceled.", historical.Error);
        Assert.Contains("Admin command was canceled.", reconcile.Error);
        Assert.Equal(2, harness.Audit.Records.Count);
        Assert.All(
            harness.Audit.Records,
            record =>
            {
                Assert.Equal(AdminAuditResult.Canceled, record.Result);
                Assert.Equal(
                    AdminErrors.OperationCanceled.Code,
                    record.ErrorCode);
            });
    }

    private static string[] HistoricalArgs(bool includeConfirmation)
    {
        List<string> args =
        [
            "workspaces",
            "identity-anchors",
            "review-historical-no-provision",
            "--actor",
            ReviewerId,
            "--tenant",
            TenantId,
            "--operation-id",
            OperationId.ToString("D"),
            "--application-id",
            ApplicationId.ToString("D"),
            "--application-version",
            "3",
            "--application-status",
            WorkspaceStaffOnboardingState.Submitted.ToString(),
            "--organizations-revision",
            "7",
            "--organizations-source-version",
            "11",
            "--organizations-source-status",
            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                .InvitationRevoked.ToString(),
            "--evidence-manifest-id",
            EvidenceManifestId.ToString("D"),
            "--evidence-sha256",
            HistoricalEvidenceSha256,
            "--output",
            "json"
        ];
        if (includeConfirmation)
        {
            args.Add("--yes");
        }

        return [.. args];
    }

    private static string[] ReconcileArgs(
        string ownerManifestPath,
        bool includeConfirmation)
    {
        List<string> args =
        [
            "workspaces",
            "identity-anchors",
            "reconcile",
            "--actor",
            ReviewerId,
            "--tenant",
            TenantId,
            "--expected-source-evidence-sha256",
            SourceSha256,
            "--expected-anchor-state-sha256",
            StateSha256,
            "--owner-map",
            ownerManifestPath,
            "--expected-owner-manifest-sha256",
            OwnerSha256,
            "--batch-size",
            "100",
            "--output",
            "json"
        ];
        if (includeConfirmation)
        {
            args.Add("--yes");
        }

        return [.. args];
    }

    private static WorkspaceStaffHistoricalNoProvisionDispositionResult
        ValidHistoricalResult() => new(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            OperationId,
            ApplicationId,
            ResultApplicationVersion: 4,
            WorkspaceStaffOnboardingState.Superseded,
            ReceiptSha256,
            CanonicalSha256,
            ReviewedAtUtc,
            AlreadyReviewed: false);

    private static WorkspaceStaffIdentityAnchorReconcileResult
        ValidReconcileResult() => new(
            AppliedCount: 1,
            Status: new WorkspaceStaffIdentityAnchorCutoverStatus(
                WorkspaceSourceCount: 1,
                OwnerBindingCount: 0,
                AlreadyAnchoredCount: 1,
                SeedableWorkspaceCount: 0,
                SeedableOwnerCount: 0,
                AmbiguousCount: 0,
                ConflictCount: 0,
                SourceSha256,
                StateSha256,
                OwnerSha256,
                OwnerManifestProvided: true,
                CanReconcile: true,
                IsReady: true,
                AuthoritativeOwnerCount: 0,
                OrganizationsScopeRevision: 7,
                HistoricalBindingCount: 0,
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources,
                HistoricalEvidenceSha256,
                TotalIssueCount: 0,
                HasMoreIssues: false,
                Issues: []),
            WorkspaceStaffIdentityAnchorReconcileOutcome.AppliedAndVerified,
            MustRerunStatus: false,
            SourceSha256,
            StateSha256,
            OwnerSha256);

    private sealed class ManifestFile : IDisposable
    {
        public ManifestFile()
        {
            this.Path = System.IO.Path.GetTempFileName();
            WorkspaceStaffIdentityAnchorOwnerManifest manifest = new(
                ContractVersion: 1,
                TenantId,
                ReviewedAtUtc,
                new WorkspaceStaffIdentityAnchorHistoricalEvidence(
                    WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                        .ReviewedNoHistoricalOwnerSources,
                    HistoricalEvidenceSha256),
                Bindings: []);
            File.WriteAllText(
                this.Path,
                JsonSerializer.Serialize(manifest));
        }

        public string Path { get; }

        public void Dispose() => File.Delete(this.Path);
    }

    private sealed class AdminCliHarness : IDisposable
    {
        private readonly ServiceProvider services;
        private readonly RootCommand root;

        public AdminCliHarness()
        {
            this.Authorization = new RecordingAuthorizationService();
            this.Audit = new RecordingAuditSink();
            this.Dispatcher = new RecordingRequestDispatcher();

            ServiceCollection registrations = new();
            registrations.AddLogging();
            registrations.AddScoped<ITenantContextAccessor,
                EnabledTenantContext>();
            registrations.AddSingleton<ISystemClock, FixedClock>();
            registrations.AddSingleton<IIdGenerator, RandomIdGenerator>();
            registrations.AddGmaAdministrationCli();
            registrations.AddScoped<IAdminAuthorizationService>(
                _ => this.Authorization);
            registrations.AddScoped<IAdminAuditSink>(_ => this.Audit);
            registrations.AddSingleton<IRequestDispatcher>(this.Dispatcher);
            this.services = registrations.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });

            AdminCliGlobalOptions options = this.services
                .GetRequiredService<AdminCliGlobalOptions>();
            this.root = new RootCommand("Workspaces admin CLI")
            {
                options.ActorOption,
                options.TenantOption,
                options.OutputOption
            };
            new WorkspacesAdminCliModule().MapCommands(
                new AdminCliCommandRegistry(this.root, this.services));
        }

        public RecordingAuthorizationService Authorization { get; }

        public RecordingAuditSink Audit { get; }

        public RecordingRequestDispatcher Dispatcher { get; }

        public async Task<AdminCliExecution> ExecuteAtHostBoundaryAsync(
            string[] args)
        {
            using StringWriter output = new();
            using StringWriter error = new();
            TextWriter originalOutput = Console.Out;
            TextWriter originalError = Console.Error;
            Console.SetOut(output);
            Console.SetError(error);

            try
            {
                int exitCode;
                try
                {
                    ParseResult parseResult = this.root.Parse(args);
                    exitCode = await parseResult.InvokeAsync(
                        new InvocationConfiguration
                        {
                            EnableDefaultExceptionHandler = false
                        },
                        CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    AdminCliOutput.WriteError("Admin command was canceled.");
                    exitCode = AdminExitCodes.Failed;
                }

                return new AdminCliExecution(
                    exitCode,
                    output.ToString(),
                    error.ToString());
            }
            finally
            {
                Console.SetOut(originalOutput);
                Console.SetError(originalError);
            }
        }

        public void Dispose() => this.services.Dispose();
    }

    private sealed class RecordingAuthorizationService :
        IAdminAuthorizationService
    {
        private readonly HashSet<string> allowed = new(StringComparer.Ordinal);

        public List<AuthorizationAttempt> Attempts { get; } = [];

        public void Allow(string permission) => this.allowed.Add(permission);

        public Task<AdminAuthorizationResult> AuthorizeAsync(
            AdminActor actor,
            AdminPermission permission,
            string? tenantId,
            CancellationToken cancellationToken)
        {
            this.Attempts.Add(new(
                actor.Id,
                permission.Code,
                tenantId));
            return Task.FromResult(this.allowed.Contains(permission.Code)
                ? AdminAuthorizationResult.Allowed()
                : AdminAuthorizationResult.Denied("not granted"));
        }
    }

    private sealed class RecordingAuditSink : IAdminAuditSink
    {
        public List<AdminAuditRecord> Records { get; } = [];

        public Task RecordAsync(
            AdminAuditRecord record,
            CancellationToken cancellationToken)
        {
            this.Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRequestDispatcher : IRequestDispatcher
    {
        private readonly Dictionary<Type, Func<object, CancellationToken,
            Task<object>>> handlers = [];

        public List<object> Commands { get; } = [];

        public void Handle<TCommand, TResponse>(
            Func<TCommand, CancellationToken, Task<Result<TResponse>>> handler)
            where TCommand : ICommand<TResponse>
        {
            this.handlers[typeof(TCommand)] = async (command, token) =>
                await handler((TCommand)command, token);
        }

        public async Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.Commands.Add(command);
            if (!this.handlers.TryGetValue(
                    command.GetType(),
                    out Func<object, CancellationToken, Task<object>>?
                        handler))
            {
                throw new InvalidOperationException(
                    $"No dispatcher result is registered for {command.GetType().Name}.");
            }

            object result = await handler(command, cancellationToken);
            return (Result<TResponse>)result;
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                $"Unexpected query {query.GetType().Name}.");
    }

    private sealed class EnabledTenantContext : ITenantContextAccessor
    {
        public bool IsEnabled => true;

        public string? TenantId { get; private set; }

        public void SetTenant(string tenantId) => this.TenantId = tenantId;

        public void ClearTenant() => this.TenantId = null;
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow => ReviewedAtUtc;
    }

    private sealed class RandomIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed record AdminCliExecution(
        int ExitCode,
        string Output,
        string Error);

    private sealed record AuthorizationAttempt(
        string ActorId,
        string Permission,
        string? TenantId);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkspaceIdentityAnchorAdminCliProcessIsolation
{
    public const string Name =
        "Workspace identity-anchor AdminCli process isolation";
}
