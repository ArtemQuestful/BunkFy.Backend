namespace BunkFy.Modules.Ingestion.Tests.Application;

using BunkFy.DataGovernance;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Handlers;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Application.Reservations;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Domain.Runs;
using Microsoft.Extensions.DependencyInjection;
using BunkFy.Modules.Reservations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationDispatchFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Applied_create_establishes_baseline_and_staff_conflict_creates_proposal()
    {
        TestContext context = CreateContext();
        ObservationReceipt createReceipt = CreateReceipt(context.Connection, "1", 1, "Ada Guest");
        context.Receipts.Items.Add(createReceipt);

        Result<ReservationObservationDispatchResult> createResult = await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(createReceipt.Id, Observation(1, "Ada Guest")),
            CancellationToken.None);

        Assert.True(createResult.IsSuccess);
        Assert.Equal(ReservationObservationDispatchDisposition.Dispatched, createResult.Value.Disposition);
        ExternalReservationCreateRequestedIntegrationEvent createRequest =
            Assert.IsType<ExternalReservationCreateRequestedIntegrationEvent>(Assert.Single(context.Outbox.Events));
        Assert.Equal($"fake.http:{context.Connection.Id:N}", createRequest.SourceSystem);
        Guid reservationId = Guid.NewGuid();
        await context.OutcomeHandler.HandleAsync(
            Outcome(createRequest, ExternalReservationOperationOutcome.Applied, reservationId, detailsRevision: 1),
            CancellationToken.None);

        ReservationSourceLink link = Assert.Single(context.SourceLinks.Items);
        Assert.Equal(reservationId, link.ReservationId);
        Assert.Equal(1, link.LastAppliedReservationDetailsRevision);
        Assert.NotNull(link.LastAppliedOperationalBaseline);
        Assert.DoesNotContain("Ada Guest", link.LastAppliedOperationalBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("guest@example.test", link.LastAppliedOperationalBaseline, StringComparison.Ordinal);
        Assert.Equal(ReservationSourceLinkState.Linked, link.State);
        Assert.Equal(ObservationReceiptState.Processed, createReceipt.State);
        Assert.Equal(
            Now.AddDays(90),
            Assert.Single(context.Dispatches.Items).SensitiveDataRetainUntilUtc);

        context.Outbox.Events.Clear();
        ObservationReceipt changeReceipt = CreateReceipt(context.Connection, "2", 2, "Ada Updated");
        context.Receipts.Items.Add(changeReceipt);
        context.RawPayloads.Add(changeReceipt.Id, Payload(2, "Ada Updated"));
        Result<ReservationObservationDispatchResult> changeResult = await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(changeReceipt.Id, Observation(2, "Ada Updated")),
            CancellationToken.None);

        Assert.True(changeResult.IsSuccess);
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent changeRequest =
            Assert.IsType<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent>(Assert.Single(context.Outbox.Events));
        Assert.Equal(1, changeRequest.ExpectedDetailsRevision);

        await context.OutcomeHandler.HandleAsync(
            Outcome(changeRequest, ExternalReservationOperationOutcome.DetailsRevisionConflict, reservationId, detailsRevision: 2),
            CancellationToken.None);

        ChangeProposal proposal = Assert.Single(context.Proposals.Items);
        Assert.Equal(changeReceipt.Id, proposal.ReceiptId);
        Assert.Equal(reservationId, proposal.ReservationId);
        Assert.Equal(1, proposal.BaseReservationDetailsRevision);
        Assert.Equal(ChangeProposalState.Pending, proposal.State);
        Assert.Equal("reservation-details-revision-conflict", proposal.ReasonCode);
        Assert.Null(proposal.SensitiveDataRetainUntilUtc);
        Assert.Equal(ObservationReceiptState.Processed, changeReceipt.State);
        Assert.Equal(1, link.LastAppliedReservationDetailsRevision);
        Assert.Null(link.ActiveProductOperationId);

        context.Outbox.Events.Clear();
        AcceptChangeProposalCommand accept = new(context.Connection.PropertyId, proposal.Id, "staff:42", proposal.Version, 2);
        Result<ChangeProposalMutationReceiptDto> accepted = await context.AcceptHandler.HandleAsync(
            accept,
            CancellationToken.None);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(ChangeProposalState.Applying, proposal.State);
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent acceptedRequest =
            Assert.IsType<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent>(Assert.Single(context.Outbox.Events));
        Assert.Equal(2, acceptedRequest.ExpectedDetailsRevision);
        ReservationDispatch proposalDispatch = Assert.Single(
            context.Dispatches.Items,
            dispatch => dispatch.TriggerKind == ReservationDispatchTriggerKind.Proposal);
        Assert.Equal(proposal.Id, proposalDispatch.TriggerId);

        await context.OutcomeHandler.HandleAsync(
            Outcome(acceptedRequest, ExternalReservationOperationOutcome.Applied, reservationId, detailsRevision: 3),
            CancellationToken.None);

        Assert.Equal(ChangeProposalState.Applied, proposal.State);
        Assert.Equal(Now.AddDays(90), proposal.SensitiveDataRetainUntilUtc);
        Assert.Equal(proposalDispatch.Id, proposal.ProductOperationId);
        Assert.Equal(3, link.LastAppliedReservationDetailsRevision);
        Assert.True((await context.AcceptHandler.HandleAsync(accept, CancellationToken.None)).IsSuccess);
        Assert.Equal(
            IngestionApplicationErrors.ProposalDecisionConflict,
            (await context.AcceptHandler.HandleAsync(accept with { ExpectedReservationDetailsRevision = 4 }, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Rejection_is_idempotent_only_for_the_same_decision()
    {
        TestContext context = CreateContext();
        ObservationReceipt receipt = CreateReceipt(
            context.Connection,
            "1",
            1,
            "Ada Guest");
        context.Receipts.Items.Add(receipt);
        ChangeProposal proposal = ChangeProposal.Create(
            Guid.NewGuid(), "tenant-a", context.Connection.PropertyId, context.Connection.Id, receipt.Id,
            Guid.NewGuid(), receipt.RawPayloadFileId, 1, "test", "{\"change\":true}", Now).Value;
        context.Proposals.Items.Add(proposal);
        RejectChangeProposalCommand reject = new(context.Connection.PropertyId, proposal.Id, "staff:42", "Source is outdated", 1);

        Assert.True((await context.RejectHandler.HandleAsync(reject, CancellationToken.None)).IsSuccess);
        Assert.Equal(ChangeProposalState.Rejected, proposal.State);
        Assert.Equal(Now.AddDays(90), proposal.SensitiveDataRetainUntilUtc);
        Assert.True((await context.RejectHandler.HandleAsync(reject, CancellationToken.None)).IsSuccess);
        Assert.Equal(
            IngestionApplicationErrors.ProposalDecisionConflict,
            (await context.RejectHandler.HandleAsync(reject with { Reason = "Different reason" }, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Newer_suggestions_only_observation_supersedes_all_older_pending_proposals()
    {
        TestContext context = CreateContext(
            conflictPolicy: IngestionConflictPolicy.SuggestionsOnly);
        Guid reservationId = await CreateAppliedReservationAsync(context);

        ObservationReceipt firstReceipt = CreateReceipt(context.Connection, "2", 2, "Adapter First");
        context.Receipts.Items.Add(firstReceipt);
        Result<ReservationObservationDispatchResult> firstResult = await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(
                firstReceipt.Id,
                Observation(2, "Adapter First")),
            CancellationToken.None);

        Assert.True(firstResult.IsSuccess);
        Assert.Equal(ReservationObservationDispatchDisposition.ProposalRequired, firstResult.Value.Disposition);
        ChangeProposal firstProposal = Assert.Single(context.Proposals.Items);
        Assert.Equal(ChangeProposalState.Pending, firstProposal.State);

        ObservationReceipt legacyReceipt = CreateReceipt(
            context.Connection,
            "legacy-pending",
            1,
            "Adapter Legacy");
        context.Receipts.Items.Add(legacyReceipt);
        ChangeProposal legacyProposal = ChangeProposal.Create(
            Guid.NewGuid(),
            "tenant-a",
            context.Connection.PropertyId,
            context.Connection.Id,
            legacyReceipt.Id,
            reservationId,
            legacyReceipt.RawPayloadFileId,
            1,
            "legacy-pending",
            "{\"change\":true}",
            Now.AddMinutes(-1)).Value;
        context.Proposals.Items.Add(legacyProposal);

        ObservationReceipt unrelatedReceipt = CreateReceipt(
            context.Connection,
            "unrelated-pending",
            1,
            "Unrelated Adapter",
            externalId: "booking-unrelated");
        context.Receipts.Items.Add(unrelatedReceipt);
        ChangeProposal unrelatedProposal = ChangeProposal.Create(
            Guid.NewGuid(),
            "tenant-a",
            context.Connection.PropertyId,
            context.Connection.Id,
            unrelatedReceipt.Id,
            reservationId,
            unrelatedReceipt.RawPayloadFileId,
            1,
            "unrelated-pending",
            "{\"change\":true}",
            Now.AddMinutes(-1)).Value;
        context.Proposals.Items.Add(unrelatedProposal);

        ObservationReceipt secondReceipt = CreateReceipt(context.Connection, "3", 3, "Adapter Newest");
        context.Receipts.Items.Add(secondReceipt);
        Result<ReservationObservationDispatchResult> secondResult = await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(
                secondReceipt.Id,
                Observation(3, "Adapter Newest")),
            CancellationToken.None);

        Assert.True(secondResult.IsSuccess);
        Assert.Equal(ReservationObservationDispatchDisposition.ProposalRequired, secondResult.Value.Disposition);
        ChangeProposal newestProposal = Assert.Single(
            context.Proposals.Items,
            proposal => proposal.ReceiptId == secondReceipt.Id);
        Assert.Equal(ChangeProposalState.Superseded, firstProposal.State);
        Assert.Equal("system", firstProposal.DecisionActor);
        Assert.Equal(PendingChangeProposalSuperseder.SupersessionReason, firstProposal.DecisionReason);
        Assert.Equal(Now.AddDays(90), firstProposal.SensitiveDataRetainUntilUtc);
        Assert.Equal(ChangeProposalState.Superseded, legacyProposal.State);
        Assert.Equal("system", legacyProposal.DecisionActor);
        Assert.Equal(PendingChangeProposalSuperseder.SupersessionReason, legacyProposal.DecisionReason);
        Assert.Equal(Now.AddDays(90), legacyProposal.SensitiveDataRetainUntilUtc);
        Assert.Equal(ChangeProposalState.Pending, unrelatedProposal.State);
        Assert.Equal(ChangeProposalState.Pending, newestProposal.State);
        Assert.Equal(reservationId, newestProposal.ReservationId);
        Assert.Equal(
            2,
            context.Proposals.Items.Count(
                proposal => proposal.State == ChangeProposalState.Superseded));
        Assert.Equal(
            2,
            context.Proposals.Items.Count(
                proposal => proposal.State == ChangeProposalState.Pending));

        Result<ChangeProposalMutationReceiptDto> staleDecision = await context.AcceptHandler.HandleAsync(
            new AcceptChangeProposalCommand(
                context.Connection.PropertyId,
                firstProposal.Id,
                "staff:42",
                firstProposal.Version,
                ExpectedReservationDetailsRevision: 2),
            CancellationToken.None);
        Assert.Equal(IngestionApplicationErrors.ProposalDecisionConflict, staleDecision.Error);
    }

    [Fact]
    public async Task Newer_revision_conflict_supersedes_the_older_pending_proposal()
    {
        TestContext context = CreateContext();
        Guid reservationId = await CreateAppliedReservationAsync(context);

        ObservationReceipt firstReceipt = CreateReceipt(context.Connection, "2", 2, "Adapter First");
        context.Receipts.Items.Add(firstReceipt);
        Assert.True((await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(
                firstReceipt.Id,
                Observation(2, "Adapter First")),
            CancellationToken.None)).IsSuccess);
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent firstRequest =
            Assert.IsType<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent>(
                Assert.Single(context.Outbox.Events));
        await context.OutcomeHandler.HandleAsync(
            Outcome(
                firstRequest,
                ExternalReservationOperationOutcome.DetailsRevisionConflict,
                reservationId,
                detailsRevision: 2),
            CancellationToken.None);
        ChangeProposal firstProposal = Assert.Single(context.Proposals.Items);
        context.Outbox.Events.Clear();

        ObservationReceipt secondReceipt = CreateReceipt(context.Connection, "3", 3, "Adapter Newest");
        context.Receipts.Items.Add(secondReceipt);
        Assert.True((await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(
                secondReceipt.Id,
                Observation(3, "Adapter Newest")),
            CancellationToken.None)).IsSuccess);
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent secondRequest =
            Assert.IsType<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent>(
                Assert.Single(context.Outbox.Events));
        await context.OutcomeHandler.HandleAsync(
            Outcome(
                secondRequest,
                ExternalReservationOperationOutcome.DetailsRevisionConflict,
                reservationId,
                detailsRevision: 2),
            CancellationToken.None);

        ChangeProposal newestProposal = Assert.Single(
            context.Proposals.Items,
            proposal => proposal.ReceiptId == secondReceipt.Id);
        Assert.Equal(ChangeProposalState.Superseded, firstProposal.State);
        Assert.Equal(PendingChangeProposalSuperseder.SupersessionReason, firstProposal.DecisionReason);
        Assert.Equal(Now.AddDays(90), firstProposal.SensitiveDataRetainUntilUtc);
        Assert.Equal(ChangeProposalState.Pending, newestProposal.State);
        Assert.Single(
            context.Proposals.Items,
            proposal => proposal.State == ChangeProposalState.Pending);
    }

    [Fact]
    public async Task Normalized_dispatch_fails_closed_before_writing_product_state()
    {
        TestContext context = CreateContext(policyAllowed: false);
        ObservationReceipt receipt = CreateReceipt(context.Connection, "1", 1, "Ada Guest");
        context.Receipts.Items.Add(receipt);

        Result<ReservationObservationDispatchResult> result = await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(receipt.Id, Observation(1, "Ada Guest")),
            CancellationToken.None);

        Assert.Equal(
            IngestionApplicationErrors.CountryPolicyDenied(CountryPolicyDecisionReason.MissingBinding),
            result.Error);
        Assert.Equal(ObservationReceiptState.Pending, receipt.State);
        Assert.Empty(context.SourceLinks.Items);
        Assert.Empty(context.Dispatches.Items);
        Assert.Empty(context.Proposals.Items);
        Assert.Empty(context.Outbox.Events);
    }

    [Fact]
    public async Task Tombstoned_observation_cannot_recreate_a_source_link()
    {
        TestContext context = CreateContext(anonymisationBlocked: true);
        ObservationReceipt receipt = CreateReceipt(
            context.Connection,
            "1",
            1,
            "Ada Guest");
        context.Receipts.Items.Add(receipt);

        Result<ReservationObservationDispatchResult> result =
            await context.Dispatcher.HandleAsync(
                new DispatchNormalizedReservationObservationCommand(
                    receipt.Id,
                    Observation(1, "Ada Guest")),
                CancellationToken.None);

        Assert.Equal(
            IngestionApplicationErrors.AnonymisationBarrierActive,
            result.Error);
        Assert.Equal(ObservationReceiptState.Pending, receipt.State);
        Assert.Empty(context.SourceLinks.Items);
        Assert.Empty(context.Dispatches.Items);
        Assert.Empty(context.Proposals.Items);
        Assert.Empty(context.Outbox.Events);
    }

    [Fact]
    public async Task Accepted_proposal_becomes_stale_when_reservation_revision_races_again()
    {
        TestContext context = CreateContext();
        SeededProposal seeded = SeedProposal(context, Payload(2, "Adapter New"));
        AcceptChangeProposalCommand accept = new(context.Connection.PropertyId, seeded.Proposal.Id, "staff:42", 1, 2);

        Assert.True((await context.AcceptHandler.HandleAsync(accept, CancellationToken.None)).IsSuccess);
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent request =
            Assert.IsType<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent>(Assert.Single(context.Outbox.Events));
        await context.OutcomeHandler.HandleAsync(
            Outcome(request, ExternalReservationOperationOutcome.DetailsRevisionConflict, seeded.ReservationId, 3),
            CancellationToken.None);

        Assert.Equal(ChangeProposalState.Stale, seeded.Proposal.State);
        Assert.Equal(1, seeded.Link.LastAppliedReservationDetailsRevision);
        Assert.Null(seeded.Link.ActiveProductOperationId);
    }

    [Fact]
    public async Task Accepted_cancellation_proposal_completes_only_after_reservation_cancelled_fact()
    {
        TestContext context = CreateContext();
        SeededProposal seeded = SeedProposal(context, CancelPayload(2));
        AcceptChangeProposalCommand accept = new(context.Connection.PropertyId, seeded.Proposal.Id, "staff:42", 1, 2);

        Assert.True((await context.AcceptHandler.HandleAsync(accept, CancellationToken.None)).IsSuccess);
        ExternalReservationCancellationRequestedIntegrationEvent request =
            Assert.IsType<ExternalReservationCancellationRequestedIntegrationEvent>(Assert.Single(context.Outbox.Events));
        await context.OutcomeHandler.HandleAsync(
            Outcome(request, ExternalReservationOperationOutcome.Accepted, seeded.ReservationId, 2),
            CancellationToken.None);

        Assert.Equal(ChangeProposalState.Applying, seeded.Proposal.State);
        Assert.Equal(ReservationSourceLinkState.CancellationPending, seeded.Link.State);
        ReservationDispatch cancellationDispatch = Assert.Single(
            context.Dispatches.Items,
            item => item.TriggerKind == ReservationDispatchTriggerKind.Proposal);
        Assert.Null(cancellationDispatch.SensitiveDataRetainUntilUtc);
        await context.CancellationHandler.HandleAsync(
            new ReservationCancelledIntegrationEvent(
                Guid.NewGuid(), "tenant-a", Now.AddMinutes(3), seeded.ReservationId,
                context.Connection.PropertyId, reservationVersion: 4),
            CancellationToken.None);

        Assert.Equal(ChangeProposalState.Applied, seeded.Proposal.State);
        Assert.Equal(ReservationSourceLinkState.Cancelled, seeded.Link.State);
        Assert.Null(seeded.Link.LastAppliedOperationalBaseline);
        Assert.Equal(Now.AddDays(90), cancellationDispatch.SensitiveDataRetainUntilUtc);
        Assert.Equal(
            ReservationDispatchState.Applied,
            Assert.Single(context.Dispatches.Items, item => item.TriggerKind == ReservationDispatchTriggerKind.Proposal).State);
    }

    [Fact]
    public void Dispatch_classifier_separates_guest_changes_from_allocation_amendments()
    {
        TestContext context = CreateContext();
        SeededProposal seeded = SeedProposal(context, Payload(2, "Adapter New"));

        Assert.Equal(
            ReservationDispatchKind.ChangeGuestDetails,
            BunkFy.Modules.Ingestion.Application.Reservations.ReservationObservationDispatchClassifier.Classify(
                seeded.Link,
                Observation(2, "Guest Only")));
        Assert.Equal(
            ReservationDispatchKind.Amend,
            BunkFy.Modules.Ingestion.Application.Reservations.ReservationObservationDispatchClassifier.Classify(
                seeded.Link,
                Observation(2, "Date Change") with { Departure = new DateOnly(2026, 8, 4) }));
        Assert.Equal(
            ReservationDispatchKind.Amend,
            ReservationObservationDispatchClassifier.Classify(
                seeded.Link,
                Observation(2, "Inventory Change") with { InventoryUnitIds = [Guid.NewGuid()] }));
    }

    [Fact]
    public void Operational_baseline_is_deterministic_and_excludes_guest_data()
    {
        Guid first = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid second = Guid.Parse("20000000-0000-0000-0000-000000000001");
        NormalizedReservationObservation observation = Observation(4, "Sensitive Guest") with
        {
            InventoryUnitIds = [second, first],
            Email = "sensitive@example.test",
            Phone = "+1-555-0100",
            Notes = "Sensitive note"
        };

        string baseline = ReservationOperationalBaseline.Serialize(observation);

        Assert.Equal(
            $"{{\"schemaVersion\":1,\"arrival\":\"2026-08-01\",\"departure\":\"2026-08-03\",\"inventoryUnitIds\":[\"{first}\",\"{second}\"]}}",
            baseline);
        Assert.DoesNotContain("Sensitive Guest", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive@example.test", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("555", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive note", baseline, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":2,\"arrival\":\"2026-08-01\",\"departure\":\"2026-08-03\",\"inventoryUnitIds\":[\"20000000-0000-0000-0000-000000000001\"]}")]
    [InlineData("{\"schemaVersion\":1,\"arrival\":\"2026-08-01\",\"departure\":\"2026-08-03\",\"inventoryUnitIds\":[\"20000000-0000-0000-0000-000000000001\"],\"guestName\":\"must fail strict parsing\"}")]
    public void Malformed_or_unsupported_baseline_classifies_conservatively(string baseline)
    {
        TestContext context = CreateContext();
        ReservationSourceLink link = CreateLinkedSourceLink(context, baseline);

        Assert.Equal(
            ReservationDispatchKind.Amend,
            ReservationObservationDispatchClassifier.Classify(link, Observation(2, "Guest Only")));
    }

    private static TestContext CreateContext(
        bool policyAllowed = true,
        bool anonymisationBlocked = false,
        IngestionConflictPolicy conflictPolicy = IngestionConflictPolicy.AutoApplyWhenAdapterBaselineUnchanged)
    {
        AdapterConnection connection = AdapterConnection.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "fake.http",
            BunkFy.Adapter.Abstractions.AdapterExecutionMode.Polling,
            conflictPolicy,
            "configuration://fake",
            null,
            Now).Value;
        FakeReceiptRepository receipts = new();
        FakeSourceLinkRepository links = new();
        FakeDispatchRepository dispatches = new();
        FakeProposalRepository proposals = new(receipts);
        FakeRawPayloadStore rawPayloads = new();
        RecordingOutbox outbox = new();
        ServiceCollection services = new();
        services.AddSingleton<IAdapterConnectionRepository>(new FakeConnectionRepository(connection));
        services.AddSingleton<IObservationReceiptRepository>(receipts);
        services.AddSingleton<IReservationSourceLinkRepository>(links);
        services.AddSingleton<IReservationDispatchRepository>(dispatches);
        services.AddSingleton<IChangeProposalRepository>(proposals);
        services.AddSingleton<IIngestionSourceGraphLocator>(
            new FakeSourceGraphLocator(
                receipts,
                links,
                dispatches,
                proposals));
        services.AddSingleton<IIngestionRetentionPolicy>(new TestRetentionPolicy());
        services.AddSingleton<IIngestionCountryPolicyAdmission>(new TestCountryPolicyAdmission(allowed: policyAllowed));
        services.AddSingleton<IRawPayloadStore>(rawPayloads);
        services.AddSingleton<IOutboxWriterRegistry>(new RecordingOutboxRegistry(outbox));
        services.AddSingleton<ISystemClock>(new TestClock());
        services.AddSingleton<IIdGenerator>(new TestIdGenerator());
        if (anonymisationBlocked)
        {
            services.AddBlockingAnonymisationBarrier();
        }
        else
        {
            services.AddAllowingAnonymisationBarrier();
        }
        services.AddIngestionApplication();
        ServiceProvider provider = services.BuildServiceProvider();
        return new(
            connection,
            receipts,
            links,
            dispatches,
            proposals,
            rawPayloads,
            outbox,
            provider.GetRequiredService<ICommandHandler<DispatchNormalizedReservationObservationCommand, ReservationObservationDispatchResult>>(),
            provider.GetRequiredService<ReservationOperationOutcomeHandler>(),
            provider.GetRequiredService<ReservationCancelledForIngestionHandler>(),
            provider.GetRequiredService<ICommandHandler<AcceptChangeProposalCommand, ChangeProposalMutationReceiptDto>>(),
            provider.GetRequiredService<ICommandHandler<RejectChangeProposalCommand, ChangeProposalMutationReceiptDto>>());
    }

    private static ObservationReceipt CreateReceipt(
        AdapterConnection connection,
        string revision,
        long sequence,
        string guestName,
        string externalId = "booking-42")
    {
        Guid receiptId = Guid.NewGuid();
        byte[] payload = Payload(sequence, guestName);
        return ObservationReceipt.Create(
            receiptId,
            "tenant-a",
            connection.PropertyId,
            connection.Id,
            runId: null,
            Guid.NewGuid(),
            "reservation.v1",
            externalId,
            revision,
            $"reservation.v1|{externalId}|{revision}",
            AdapterPayloadHash.ComputeSha256(payload),
            TestObservationCountryPolicyEvidence.Create(Now.AddMinutes(sequence)),
            receiptId,
            Now.AddDays(30),
            Now.AddMinutes(sequence),
            Now.AddMinutes(sequence),
            Now.AddMinutes(sequence)).Value;
    }

    private static async Task<Guid> CreateAppliedReservationAsync(TestContext context)
    {
        ObservationReceipt createReceipt = CreateReceipt(context.Connection, "1", 1, "Adapter Guest");
        context.Receipts.Items.Add(createReceipt);
        Result<ReservationObservationDispatchResult> created = await context.Dispatcher.HandleAsync(
            new DispatchNormalizedReservationObservationCommand(
                createReceipt.Id,
                Observation(1, "Adapter Guest")),
            CancellationToken.None);
        Assert.True(created.IsSuccess);
        ExternalReservationCreateRequestedIntegrationEvent request =
            Assert.IsType<ExternalReservationCreateRequestedIntegrationEvent>(
                Assert.Single(context.Outbox.Events));
        Guid reservationId = Guid.NewGuid();
        await context.OutcomeHandler.HandleAsync(
            Outcome(
                request,
                ExternalReservationOperationOutcome.Applied,
                reservationId,
                detailsRevision: 1),
            CancellationToken.None);
        context.Outbox.Events.Clear();
        return reservationId;
    }

    private static byte[] Payload(long sequence, string guestName) => System.Text.Encoding.UTF8.GetBytes(
        $$"""
        {"operation":"upsert","sourceSequence":{{sequence}},"arrival":"2026-08-01","departure":"2026-08-03","inventoryUnitIds":["20000000-0000-0000-0000-000000000001"],"primaryGuestName":"{{guestName}}","email":"guest@example.test","phone":null,"guestCount":1,"notes":null}
        """);

    private static byte[] CancelPayload(long sequence) => System.Text.Encoding.UTF8.GetBytes(
        $$"""
        {"operation":"cancel","sourceSequence":{{sequence}},"arrival":null,"departure":null,"inventoryUnitIds":null,"primaryGuestName":null,"email":null,"phone":null,"guestCount":null,"notes":null}
        """);

    private static SeededProposal SeedProposal(TestContext context, byte[] payload)
    {
        Guid reservationId = Guid.NewGuid();
        Guid baseReceiptId = Guid.NewGuid();
        ReservationSourceLink link = ReservationSourceLink.Create(
            ReservationOperationIdentity.CreateSourceLinkId(
                "tenant-a",
                context.Connection.Id,
                "booking-42"),
            "tenant-a", context.Connection.PropertyId, context.Connection.Id,
            $"fake.http:{context.Connection.Id:N}", "booking-42", Now).Value;
        _ = link.Observe(baseReceiptId, "1", 1, Now, new string('a', 64), Now);
        Guid createOperationId = Guid.NewGuid();
        _ = link.BeginDispatch(createOperationId, Now);
        _ = link.CompleteDispatch(
            createOperationId, baseReceiptId, "1", 1,
            ReservationOperationalBaseline.Serialize(Observation(1, "Adapter Old")), reservationId, 1,
            keepActive: false, applied: true, cancellationPending: false, cancelled: false, Now);

        Guid receiptId = Guid.NewGuid();
        string hash = AdapterPayloadHash.ComputeSha256(payload);
        ObservationReceipt receipt = ObservationReceipt.Create(
            receiptId, "tenant-a", context.Connection.PropertyId, context.Connection.Id, runId: null,
            Guid.NewGuid(), "reservation.v1", "booking-42", "2", "reservation.v1|booking-42|2",
            hash, TestObservationCountryPolicyEvidence.Create(Now), receiptId,
            Now.AddDays(30), Now, Now, Now).Value;
        _ = link.Observe(receipt.Id, "2", 2, Now, hash, Now);
        _ = receipt.MarkProcessed(Now);
        ChangeProposal proposal = ChangeProposal.Create(
            Guid.NewGuid(), "tenant-a", context.Connection.PropertyId, context.Connection.Id, receipt.Id,
            reservationId, receipt.RawPayloadFileId, 1, "test", "{\"change\":true}", Now).Value;
        context.SourceLinks.Items.Add(link);
        context.Receipts.Items.Add(receipt);
        context.Proposals.Items.Add(proposal);
        context.RawPayloads.Add(receipt.Id, payload);
        return new(proposal, link, reservationId);
    }

    private static ReservationSourceLink CreateLinkedSourceLink(TestContext context, string baseline)
    {
        Guid receiptId = Guid.NewGuid();
        ReservationSourceLink link = ReservationSourceLink.Create(
            ReservationOperationIdentity.CreateSourceLinkId(
                "tenant-a",
                context.Connection.Id,
                "booking-conservative"),
            "tenant-a", context.Connection.PropertyId, context.Connection.Id,
            $"fake.http:{context.Connection.Id:N}", "booking-conservative", Now).Value;
        _ = link.Observe(receiptId, "1", 1, Now, new string('a', 64), Now);
        Guid operationId = Guid.NewGuid();
        _ = link.BeginDispatch(operationId, Now);
        Assert.True(link.CompleteDispatch(
            operationId, receiptId, "1", 1, baseline, Guid.NewGuid(), 1,
            keepActive: false, applied: true, cancellationPending: false, cancelled: false, Now).IsSuccess);
        return link;
    }

    private static NormalizedReservationObservation Observation(long sequence, string guestName) => new(
        NormalizedReservationObservationKind.Upsert,
        sequence,
        new DateOnly(2026, 8, 1),
        new DateOnly(2026, 8, 3),
        [Guid.Parse("20000000-0000-0000-0000-000000000001")],
        guestName,
        "guest@example.test",
        null,
        1,
        null);

    private static ExternalReservationOperationCompletedIntegrationEvent Outcome(
        ExternalReservationCreateRequestedIntegrationEvent request,
        ExternalReservationOperationOutcome outcome,
        Guid reservationId,
        long detailsRevision) => new(
            Guid.NewGuid(), request.ScopeId, Now, request.OperationId, request.ReceiptId, request.ConnectionId,
            request.PropertyId, ExternalReservationOperationKind.Create, outcome, reservationId, detailsRevision,
            reservationVersion: 2, errorCode: null);

    private static ExternalReservationOperationCompletedIntegrationEvent Outcome(
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent request,
        ExternalReservationOperationOutcome outcome,
        Guid reservationId,
        long detailsRevision) => new(
            Guid.NewGuid(), request.ScopeId, Now, request.OperationId, request.ReceiptId, request.ConnectionId,
            request.PropertyId, ExternalReservationOperationKind.ChangeGuestDetails, outcome, reservationId,
            detailsRevision,
            reservationVersion: 3,
            outcome == ExternalReservationOperationOutcome.DetailsRevisionConflict
                ? "Reservations.DetailsRevisionConflict"
                : null);

    private static ExternalReservationOperationCompletedIntegrationEvent Outcome(
        ExternalReservationCancellationRequestedIntegrationEvent request,
        ExternalReservationOperationOutcome outcome,
        Guid reservationId,
        long detailsRevision) => new(
            Guid.NewGuid(), request.ScopeId, Now, request.OperationId, request.ReceiptId, request.ConnectionId,
            request.PropertyId, ExternalReservationOperationKind.Cancel, outcome, reservationId, detailsRevision,
            reservationVersion: 3, errorCode: null);

    private sealed record TestContext(
        AdapterConnection Connection,
        FakeReceiptRepository Receipts,
        FakeSourceLinkRepository SourceLinks,
        FakeDispatchRepository Dispatches,
        FakeProposalRepository Proposals,
        FakeRawPayloadStore RawPayloads,
        RecordingOutbox Outbox,
        ICommandHandler<DispatchNormalizedReservationObservationCommand, ReservationObservationDispatchResult> Dispatcher,
        IIntegrationEventHandler<ExternalReservationOperationCompletedIntegrationEvent> OutcomeHandler,
        IIntegrationEventHandler<ReservationCancelledIntegrationEvent> CancellationHandler,
        ICommandHandler<AcceptChangeProposalCommand, ChangeProposalMutationReceiptDto> AcceptHandler,
        ICommandHandler<RejectChangeProposalCommand, ChangeProposalMutationReceiptDto> RejectHandler);

    private sealed record SeededProposal(
        ChangeProposal Proposal,
        ReservationSourceLink Link,
        Guid ReservationId);

    private sealed class TestRetentionPolicy : IIngestionRetentionPolicy
    {
        public DateTimeOffset GetRawPayloadRetainUntilUtc(
            Guid propertyId,
            Guid connectionId,
            DateTimeOffset receivedAtUtc) => receivedAtUtc.AddDays(30);

        public DateTimeOffset GetSensitiveHistoryRetainUntilUtc(
            Guid propertyId,
            Guid connectionId,
            DateTimeOffset terminalAtUtc) => terminalAtUtc.AddDays(90);

        public DateTimeOffset GetLegalHoldReviewDueAtUtc(
            DateTimeOffset placedAtUtc) => placedAtUtc.AddDays(30);
    }

    private sealed class FakeConnectionRepository(AdapterConnection connection) : IAdapterConnectionRepository
    {
        public Task<AdapterConnection?> GetAsync(Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<AdapterConnection?>(connection.Id == connectionId ? connection : null);

        public Task<AdapterConnection?> GetAsync(Guid propertyId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<AdapterConnection?>(propertyId == connection.PropertyId && connection.Id == connectionId ? connection : null);

        public Task AddAsync(AdapterConnection added, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReceiptRepository : IObservationReceiptRepository
    {
        public List<ObservationReceipt> Items { get; } = [];
        public Task<ObservationReceipt?> GetAsync(Guid receiptId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(receipt => receipt.Id == receiptId));
        public Task<ObservationReceipt?> FindByOperationAsync(Guid connectionId, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(receipt => receipt.ConnectionId == connectionId && receipt.OperationId == operationId));
        public Task<ObservationReceipt?> FindByDeduplicationKeyAsync(Guid connectionId, string deduplicationKey, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(receipt => receipt.ConnectionId == connectionId && receipt.DeduplicationKey == deduplicationKey));
        public Task AddAsync(ObservationReceipt receipt, CancellationToken cancellationToken)
        {
            this.Items.Add(receipt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSourceLinkRepository : IReservationSourceLinkRepository
    {
        public List<ReservationSourceLink> Items { get; } = [];
        public Task<ReservationSourceLink?> GetAsync(Guid sourceLinkId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(link => link.Id == sourceLinkId));
        public Task<ReservationSourceLink?> FindBySourceAsync(Guid connectionId, string sourceReference, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(link => link.ConnectionId == connectionId && link.SourceReference == sourceReference));
        public Task<ReservationSourceLink?> FindByReservationAsync(Guid reservationId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(link => link.ReservationId == reservationId));
        public Task AddAsync(ReservationSourceLink sourceLink, CancellationToken cancellationToken)
        {
            this.Items.Add(sourceLink);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDispatchRepository : IReservationDispatchRepository
    {
        public List<ReservationDispatch> Items { get; } = [];
        public Task<ReservationDispatch?> GetAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(dispatch => dispatch.Id == operationId));
        public Task<ReservationDispatch?> FindByTriggerAsync(
            ReservationDispatchTriggerKind triggerKind,
            Guid triggerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(dispatch =>
                dispatch.TriggerKind == triggerKind && dispatch.TriggerId == triggerId));
        public Task<ReservationDispatch?> FindAcceptedCancellationAsync(Guid reservationId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(dispatch => dispatch.ReservationId == reservationId && dispatch.State == ReservationDispatchState.Accepted));
        public Task AddAsync(ReservationDispatch dispatch, CancellationToken cancellationToken)
        {
            this.Items.Add(dispatch);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProposalRepository(FakeReceiptRepository receipts) : IChangeProposalRepository
    {
        public List<ChangeProposal> Items { get; } = [];
        public Task<ChangeProposal?> GetAsync(Guid proposalId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(proposal => proposal.Id == proposalId));
        public Task<ChangeProposal?> FindByReceiptAsync(Guid receiptId, CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(proposal => proposal.ReceiptId == receiptId));
        public Task<IReadOnlyCollection<ChangeProposal>> ListPendingForSourceAsync(
            Guid connectionId,
            string externalId,
            Guid reservationId,
            Guid excludingReceiptId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<ChangeProposal>>(
            (from proposal in this.Items
             join receipt in receipts.Items on proposal.ReceiptId equals receipt.Id
             where
                    proposal.ConnectionId == connectionId &&
                    proposal.ReservationId == reservationId &&
                    proposal.ReceiptId != excludingReceiptId &&
                    proposal.State == ChangeProposalState.Pending &&
                    receipt.ConnectionId == connectionId &&
                    receipt.ExternalId == externalId
             select proposal)
                .OrderBy(proposal => proposal.CreatedAtUtc)
                .ThenBy(proposal => proposal.Id)
                .ToArray());
        public Task AddAsync(ChangeProposal proposal, CancellationToken cancellationToken)
        {
            this.Items.Add(proposal);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSourceGraphLocator(
        FakeReceiptRepository receipts,
        FakeSourceLinkRepository sourceLinks,
        FakeDispatchRepository dispatches,
        FakeProposalRepository proposals)
        : IIngestionSourceGraphLocator
    {
        public Task<IngestionSourceGraphCoordinate?> FindReceiptAsync(
            Guid receiptId,
            CancellationToken cancellationToken) => Task.FromResult(
            ToCoordinate(receipts.Items.SingleOrDefault(
                receipt => receipt.Id == receiptId)));

        public Task<IngestionSourceGraphCoordinate?> FindProposalAsync(
            Guid proposalId,
            CancellationToken cancellationToken)
        {
            ChangeProposal? proposal = proposals.Items.SingleOrDefault(
                item => item.Id == proposalId);
            ObservationReceipt? receipt = proposal is null
                ? null
                : receipts.Items.SingleOrDefault(
                    item => item.Id == proposal.ReceiptId);
            return Task.FromResult(ToCoordinate(receipt, proposalId));
        }

        public Task<IngestionSourceGraphCoordinate?> FindDispatchAsync(
            Guid dispatchId,
            CancellationToken cancellationToken)
        {
            ReservationDispatch? dispatch = dispatches.Items.SingleOrDefault(
                item => item.Id == dispatchId);
            return Task.FromResult(dispatch is null
                ? null
                : new IngestionSourceGraphCoordinate(
                    dispatch.Id,
                    dispatch.ConnectionId,
                    dispatch.SourceLinkId));
        }

        public Task<IngestionSourceGraphCoordinate?> FindReprocessingAttemptAsync(
            Guid attemptId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionSourceGraphCoordinate?>(null);

        public Task<IngestionSourceGraphCoordinate?> FindSourceLinkAsync(
            Guid sourceLinkId,
            CancellationToken cancellationToken)
        {
            ReservationSourceLink? link = sourceLinks.Items.SingleOrDefault(
                item => item.Id == sourceLinkId);
            return Task.FromResult(link is null
                ? null
                : new IngestionSourceGraphCoordinate(
                    link.Id,
                    link.ConnectionId,
                    link.Id));
        }

        public Task<IngestionSourceGraphCoordinate?>
            FindAcceptedCancellationAsync(
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            ReservationDispatch? dispatch = dispatches.Items.SingleOrDefault(
                item => item.ReservationId == reservationId &&
                    item.Kind == ReservationDispatchKind.Cancel &&
                    item.State == ReservationDispatchState.Accepted);
            return Task.FromResult(dispatch is null
                ? null
                : new IngestionSourceGraphCoordinate(
                    dispatch.Id,
                    dispatch.ConnectionId,
                    dispatch.SourceLinkId));
        }

        private static IngestionSourceGraphCoordinate? ToCoordinate(
            ObservationReceipt? receipt,
            Guid? recordId = null) => receipt is null
            ? null
            : new(
                recordId ?? receipt.Id,
                receipt.ConnectionId,
                ReservationOperationIdentity.CreateSourceLinkId(
                    receipt.ScopeId,
                    receipt.ConnectionId,
                    receipt.ExternalId));
    }

    private sealed class FakeRawPayloadStore : IRawPayloadStore
    {
        private readonly Dictionary<Guid, RawPayloadRead> payloads = [];

        public void Add(Guid payloadId, byte[] content) => this.payloads.Add(
            payloadId,
            new RawPayloadRead("application/json", content, AdapterPayloadHash.ComputeSha256(content)));

        public Task StoreAsync(RawPayloadWrite write, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RawPayloadRead?> ReadAsync(
            Guid payloadId,
            string scopeId,
            Guid connectionId,
            CancellationToken cancellationToken) => Task.FromResult(this.payloads.GetValueOrDefault(payloadId));

        public Task<bool> DeleteAsync(
            Guid payloadId,
            string scopeId,
            Guid connectionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => IngestionModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];
        public Task EnqueueAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox) : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => outbox;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}
