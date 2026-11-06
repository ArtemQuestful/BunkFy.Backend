namespace BunkFy.Modules.Workspaces.Tests;

using System.Text.Json;
using BunkFy.Modules.Workspaces.AdminCli;
using BunkFy.Modules.Workspaces.Application;
using Gma.Framework.Administration.Cli;
using Xunit;

[Trait("Category", "Unit")]
[Collection(WorkspacesAdminCliConsoleIsolation.Name)]
public sealed class WorkspacesAdminCliOutputTests
{
    [Fact]
    public void Access_status_json_is_a_single_typed_result()
    {
        var status = new WorkspaceAccessBootstrapStatus(4, 4, 4, 0, 0, 0, 2);

        string json = Capture(() => WorkspacesAdminCliModule.WriteStatus(
            status,
            AdminCliOutput.Json));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(4, root.GetProperty("seedVersion").GetInt32());
        Assert.False(root.GetProperty("requiresBackfill").GetBoolean());
    }

    [Fact]
    public void Access_bootstrap_json_is_a_single_typed_result()
    {
        var result = new WorkspaceAccessBootstrapResult(4, 4, 3);

        string json = Capture(() => WorkspacesAdminCliModule.WriteBootstrapResult(
            result,
            AdminCliOutput.Json));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(4, root.GetProperty("seedVersion").GetInt32());
        Assert.Equal(4, root.GetProperty("seedProfileCount").GetInt32());
        Assert.Equal(3, root.GetProperty("migratedMemberCount").GetInt32());
    }

    private static string Capture(Action write)
    {
        using StringWriter output = new();
        TextWriter originalOutput = Console.Out;
        Console.SetOut(output);
        try
        {
            write();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        return output.ToString();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkspacesAdminCliConsoleIsolation
{
    public const string Name = "Workspaces admin CLI console isolation";
}
