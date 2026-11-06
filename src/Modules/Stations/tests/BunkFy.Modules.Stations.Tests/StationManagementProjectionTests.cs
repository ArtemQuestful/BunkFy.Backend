namespace BunkFy.Modules.Stations.Tests;

using System.Text.Json;
using BunkFy.Modules.Stations.Api;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using Xunit;

/// <summary>Public receipt projection only; no authorization, database replay or HTTP-host proof.</summary>
public sealed class StationManagementProjectionTests
{
    private const string IssuerSubject = "cc000000-0000-0000-0000-000000000003";
    private const string Credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Station = Guid.Parse("dd000000-0000-0000-0000-000000000004");
    private static readonly Guid Browser = Guid.Parse("ee000000-0000-0000-0000-000000000005");
    private static readonly Guid Property = Guid.Parse("ff000000-0000-0000-0000-000000000006");
    private static readonly Guid Staff = Guid.Parse("aa000000-0000-0000-0000-000000000007");
    private static readonly Guid Setup = Guid.Parse("bb000000-0000-0000-0000-000000000008");
    private static readonly Guid OriginalSession = Guid.Parse("cc000000-0000-0000-0000-000000000009");
    private static readonly string[] ResponseFields = ["receipt", "state"];
    private static readonly string[] StateFields = ["state"];
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public static TheoryData<StationOperationKind, string> SparseCases() => new()
    {
        { StationOperationKind.Register, "kind,stationId,browserSessionId,propertyId,version,originalIssuerSessionId" },
        { StationOperationKind.Pair, "kind,stationId,browserSessionId,propertyId,version,originalIssuerSessionId" },
        { StationOperationKind.RevokeStation, "kind,stationId,propertyId,version" },
        { StationOperationKind.RegisterStaff, "kind,propertyId,staffMemberId,version" },
        { StationOperationKind.UnregisterStaff, "kind,propertyId,staffMemberId,version" },
        { StationOperationKind.GrantCheckIn, "kind,propertyId,staffMemberId,version" },
        { StationOperationKind.GrantCheckOut, "kind,propertyId,staffMemberId,version" },
        { StationOperationKind.RevokeCheckOutGrant, "kind,propertyId,staffMemberId,version" },
        { StationOperationKind.RevokeGrant, "kind,propertyId,staffMemberId,version" },
        { StationOperationKind.Reset, "kind,staffMemberId,version" },
        { StationOperationKind.OwnPin, "kind,staffMemberId,version" },
        { StationOperationKind.IssueSetup, "kind,stationId,browserSessionId,propertyId,staffMemberId,setupGrantId,version" },
        { StationOperationKind.CancelSetup, "kind,setupGrantId" },
    };

    public static TheoryData<StationOperationKind, string, bool> MissingCoordinates()
    {
        var data = new TheoryData<StationOperationKind, string, bool>();
        (StationOperationKind Kind, string[] Fields)[] required =
        [
            (StationOperationKind.Register, ["station", "browser", "property", "original-session"]),
            (StationOperationKind.Pair, ["station", "browser", "property", "original-session"]),
            (StationOperationKind.RevokeStation, ["station", "property"]),
            (StationOperationKind.RegisterStaff, ["property", "staff"]),
            (StationOperationKind.UnregisterStaff, ["property", "staff"]),
            (StationOperationKind.GrantCheckIn, ["property", "staff"]),
            (StationOperationKind.GrantCheckOut, ["property", "staff"]),
            (StationOperationKind.RevokeCheckOutGrant, ["property", "staff"]),
            (StationOperationKind.RevokeGrant, ["property", "staff"]),
            (StationOperationKind.Reset, ["staff"]),
            (StationOperationKind.OwnPin, ["staff"]),
            (StationOperationKind.IssueSetup, ["station", "browser", "property", "staff", "setup"]),
            (StationOperationKind.CancelSetup, ["setup"])
        ];
        foreach (var entry in required)
        {
            foreach (string field in entry.Fields)
            {
                data.Add(entry.Kind, field, false);
                data.Add(entry.Kind, field, true);
            }
        }
        return data;
    }

    public static TheoryData<StationOperationKind, long?> InvalidVersions()
    {
        var data = new TheoryData<StationOperationKind, long?>();
        foreach (var row in SparseCases())
        {
            var kind = (StationOperationKind)row[0];
            if (kind == StationOperationKind.CancelSetup)
            { continue; }
            data.Add(kind, null);
            data.Add(kind, -1L);
            if (kind != StationOperationKind.IssueSetup)
            { data.Add(kind, 0L); }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SparseCases))]
    public void Legitimate_sparse_receipts_emit_only_operation_specific_fields(StationOperationKind kind, string expectedFields)
    {
        StationManagementReceipt input = Sparse(kind);
        StationManagementHttpResponse result = StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, input));

        Assert.Equal(StationManagementState.Applied, result.State);
        StationManagementHttpReceipt receipt = Assert.IsType<StationManagementHttpReceipt>(result.Receipt);
        Assert.Equal(kind, receipt.Kind);
        JsonElement json = JsonSerializer.SerializeToElement(result, WireJson);
        Assert.Equal(ResponseFields, json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        JsonElement publicReceipt = json.GetProperty("receipt");
        Assert.Equal(expectedFields.Split(',').Order(StringComparer.Ordinal),
            publicReceipt.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (JsonProperty field in publicReceipt.EnumerateObject())
        {
            switch (field.Name)
            {
                case "stationId":
                    Assert.Equal(Station, field.Value.GetGuid());
                    break;
                case "browserSessionId":
                    Assert.Equal(Browser, field.Value.GetGuid());
                    break;
                case "propertyId":
                    Assert.Equal(Property, field.Value.GetGuid());
                    break;
                case "staffMemberId":
                    Assert.Equal(Staff, field.Value.GetGuid());
                    break;
                case "setupGrantId":
                    Assert.Equal(Setup, field.Value.GetGuid());
                    break;
                case "originalIssuerSessionId":
                    Assert.Equal(OriginalSession, field.Value.GetGuid());
                    break;
                case "version":
                    Assert.Equal(input.Version, field.Value.GetInt64());
                    break;
                case "kind":
                    break;
                default:
                    Assert.Fail("Unexpected public receipt field: " + field.Name);
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(MissingCoordinates))]
    public void Null_or_empty_required_coordinates_are_guest_free_changed_state(StationOperationKind kind, string field, bool empty)
    {
        Guid? invalid = empty ? Guid.Empty : null;
        StationManagementReceipt valid = Sparse(kind);
        StationManagementReceipt malformed = field switch
        {
            "station" => valid with { StationId = invalid },
            "browser" => valid with { BrowserSessionId = invalid },
            "property" => valid with { PropertyId = invalid },
            "staff" => valid with { StaffMemberId = invalid },
            "setup" => valid with { SetupGrantId = invalid },
            "original-session" => valid with { IssuerSessionId = invalid },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        AssertChanged(StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, malformed)));
    }

    [Theory]
    [MemberData(nameof(InvalidVersions))]
    public void Missing_negative_or_impossible_applied_versions_are_guest_free_changed_state(StationOperationKind kind, long? version) =>
        AssertChanged(StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, Sparse(kind) with { Version = version })));

    [Fact]
    public void First_setup_revision_zero_is_valid_and_cancel_setup_needs_no_version()
    {
        var issued = StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied,
            Sparse(StationOperationKind.IssueSetup) with { Version = 0 }));
        Assert.Equal(StationManagementState.Applied, issued.State);
        Assert.Equal(0L, Assert.IsType<StationManagementHttpReceipt>(issued.Receipt).Version);

        var cancelled = StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied,
            Sparse(StationOperationKind.CancelSetup) with { Version = null }));
        Assert.Equal(StationManagementState.Applied, cancelled.State);
        Assert.Equal(Setup, Assert.IsType<StationManagementHttpReceipt>(cancelled.Receipt).SetupGrantId);
        Assert.False(JsonSerializer.SerializeToElement(cancelled, WireJson).GetProperty("receipt").TryGetProperty("version", out _));
    }

    [Theory]
    [InlineData(StationManagementState.Denied)]
    [InlineData(StationManagementState.StateChanged)]
    [InlineData(StationManagementState.Unavailable)]
    [InlineData(StationManagementState.CapacityReached)]
    [InlineData(StationManagementState.NotFound)]
    public void Every_non_applied_state_discards_even_a_populated_internal_receipt(StationManagementState state)
    {
        foreach (var row in SparseCases())
        {
            var result = StationHttpSecurity.ManagementResponse(new(state, Populated((StationOperationKind)row[0])));
            Assert.Equal(state, result.State);
            Assert.Null(result.Receipt);
            JsonElement json = JsonSerializer.SerializeToElement(result, WireJson);
            Assert.Equal(StateFields, json.EnumerateObject().Select(p => p.Name));
        }
    }

    [Fact]
    public void Applied_without_receipt_is_guest_free_changed_state() =>
        AssertChanged(StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied)));

    [Theory]
    [InlineData(StationOperationKind.Unknown)]
    [InlineData(StationOperationKind.RedeemSetup)]
    [InlineData(StationOperationKind.Unlock)]
    [InlineData(StationOperationKind.Lock)]
    [InlineData(StationOperationKind.ForegroundActivity)]
    [InlineData((StationOperationKind)999)]
    public void Unknown_or_runtime_only_kinds_cannot_become_management_receipts(StationOperationKind kind) =>
        AssertChanged(StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, Populated(kind))));

    [Theory]
    [InlineData(StationOperationKind.Register, StationSetupIssuerKind.Unknown)]
    [InlineData(StationOperationKind.Register, StationSetupIssuerKind.Self)]
    [InlineData(StationOperationKind.Pair, StationSetupIssuerKind.Unknown)]
    [InlineData(StationOperationKind.Pair, StationSetupIssuerKind.Self)]
    [InlineData(StationOperationKind.Pair, (StationSetupIssuerKind)999)]
    public void Pairing_recovery_never_exposes_a_session_from_a_non_manager_receipt(StationOperationKind kind, StationSetupIssuerKind issuer) =>
        AssertChanged(StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, Sparse(kind) with { IssuerKind = issuer })));

    [Theory]
    [MemberData(nameof(SparseCases))]
    public void Internal_identity_and_unrelated_coordinates_cannot_leak_through_populated_receipts(StationOperationKind kind, string expectedFields)
    {
        var handoff = new StationPairingHandoff(new(StationManagementState.Applied, Populated(kind)), Credential);
        var result = StationHttpSecurity.ManagementResponse(handoff.Response);
        Assert.Equal(StationManagementState.Applied, result.State);
        string serialized = JsonSerializer.Serialize(result, WireJson);
        JsonElement json = JsonSerializer.SerializeToElement(result, WireJson).GetProperty("receipt");
        Assert.Equal(expectedFields.Split(',').Order(StringComparer.Ordinal),
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (string forbidden in new[] { "issuerSubjectId", "issuerKind", "issuerSessionId", "pin", "credential", "credentialDigest", "fingerprint", "scopeId" })
        { Assert.False(json.TryGetProperty(forbidden, out _), "Public receipt must not contain " + forbidden); }
        Assert.DoesNotContain(IssuerSubject, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, serialized, StringComparison.Ordinal);
        bool pairing = kind is StationOperationKind.Register or StationOperationKind.Pair;
        Assert.Equal(pairing, json.TryGetProperty("originalIssuerSessionId", out _));
        if (pairing)
        { Assert.Equal(OriginalSession, result.Receipt!.OriginalIssuerSessionId); }
        else
        {
            Assert.Null(result.Receipt!.OriginalIssuerSessionId);
            Assert.DoesNotContain(OriginalSession.ToString("D"), serialized, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(StationOperationKind.Register)]
    [InlineData(StationOperationKind.Pair)]
    [InlineData(StationOperationKind.OwnPin)]
    public void Persisted_receipt_round_trip_preserves_public_replay_shape_without_new_identity_fields(StationOperationKind kind)
    {
        StationManagementReceipt original = Sparse(kind);
        var stored = new StationOperationReceipt(StationDomainTests.Tenant, Guid.NewGuid(), (StationMutationKind)kind,
            new string('a', 64), new(StationMutationOutcome.Applied,
                Management: new(original.StationId, original.BrowserSessionId, original.PropertyId,
                    original.StaffMemberId, original.SetupGrantId, original.Version)),
            StationDomainTests.Now, IssuerSubject,
            kind == StationOperationKind.OwnPin ? StationIssuerKind.Self : StationIssuerKind.Manager, OriginalSession);
        StationManagementCoordinates values = Assert.IsType<StationManagementCoordinates>(stored.Result().Management);
        var replay = new StationManagementReceipt(values.StationId, values.BrowserSessionId, values.PropertyId,
            values.StaffMemberId, values.SetupGrantId, values.Version, (StationOperationKind)values.Kind,
            (StationSetupIssuerKind)values.IssuerKind, values.IssuerSubjectId, values.IssuerSessionId);
        var firstResponse = StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, original));
        var replayResponse = StationHttpSecurity.ManagementResponse(new(StationManagementState.Applied, replay));

        Assert.Equal(StationManagementState.Applied, replayResponse.State);
        Assert.Equal(firstResponse, replayResponse);
        Assert.Equal(JsonSerializer.Serialize(firstResponse, WireJson), JsonSerializer.Serialize(replayResponse, WireJson));
        Assert.Equal(OriginalSession, values.IssuerSessionId);
        if (kind == StationOperationKind.OwnPin)
        { Assert.Null(replayResponse.Receipt!.OriginalIssuerSessionId); }
        else
        { Assert.Equal(OriginalSession, replayResponse.Receipt!.OriginalIssuerSessionId); }
    }

    [Fact]
    public void Every_defined_operation_kind_has_an_explicit_projection_expectation()
    {
        StationOperationKind[] rejected = [StationOperationKind.Unknown, StationOperationKind.RedeemSetup,
            StationOperationKind.Unlock, StationOperationKind.Lock, StationOperationKind.ForegroundActivity];
        var covered = SparseCases().Select(row => (StationOperationKind)row[0]).Concat(rejected).Order();
        Assert.Equal(Enum.GetValues<StationOperationKind>().Order(), covered);
    }

    private static StationManagementReceipt Sparse(StationOperationKind kind)
    {
        StationManagementReceipt receipt = kind switch
        {
            StationOperationKind.Register or StationOperationKind.Pair =>
                new(StationId: Station, BrowserSessionId: Browser, PropertyId: Property, Version: 7),
            StationOperationKind.RevokeStation => new(StationId: Station, PropertyId: Property, Version: 7),
            StationOperationKind.RegisterStaff or StationOperationKind.UnregisterStaff or
                StationOperationKind.GrantCheckIn or StationOperationKind.RevokeGrant or StationOperationKind.GrantCheckOut or StationOperationKind.RevokeCheckOutGrant =>
                new(PropertyId: Property, StaffMemberId: Staff, Version: 7),
            StationOperationKind.Reset or StationOperationKind.OwnPin => new(PropertyId: Property, StaffMemberId: Staff, Version: 7),
            StationOperationKind.IssueSetup => new(Station, Browser, Property, Staff, Setup, 0),
            StationOperationKind.CancelSetup => new(SetupGrantId: Setup),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return receipt with
        {
            Kind = kind,
            IssuerKind = kind == StationOperationKind.OwnPin ? StationSetupIssuerKind.Self : StationSetupIssuerKind.Manager,
            IssuerSubjectId = IssuerSubject,
            IssuerSessionId = kind is StationOperationKind.Register or StationOperationKind.Pair or StationOperationKind.OwnPin ? OriginalSession : null
        };
    }

    private static StationManagementReceipt Populated(StationOperationKind kind) =>
        new(Station, Browser, Property, Staff, Setup, 7, kind,
            kind == StationOperationKind.OwnPin ? StationSetupIssuerKind.Self : StationSetupIssuerKind.Manager,
            IssuerSubject, OriginalSession);

    private static void AssertChanged(StationManagementHttpResponse response)
    {
        Assert.Equal(StationManagementState.StateChanged, response.State);
        Assert.Null(response.Receipt);
        JsonElement json = JsonSerializer.SerializeToElement(response, WireJson);
        Assert.Equal(StateFields, json.EnumerateObject().Select(p => p.Name));
    }
}
