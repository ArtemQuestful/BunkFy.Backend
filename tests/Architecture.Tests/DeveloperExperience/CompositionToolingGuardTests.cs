namespace Architecture.Tests.DeveloperExperience;

using Architecture.Tests.Support;
using Xunit;

[Trait("Category", "Architecture")]
public sealed class CompositionToolingGuardTests
{
    [Fact]
    public void Composition_tools_delegate_reusable_behavior_to_the_framework()
    {
        string[] wrappers =
        [
            "add-migration.ps1",
            "check-migrations.ps1",
            "check-source-packages.ps1",
            "check-submodule-dev-heads.ps1",
            "export-source-set.ps1",
        ];

        string[] errors = wrappers.SelectMany(wrapper =>
        {
            string path = RepositoryPaths.Resolve("eng", wrapper);
            string source = File.ReadAllText(path);
            return new[]
            {
                source.Contains("gma/framework/eng/", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"{wrapper} does not delegate to framework tooling.",
                File.ReadLines(path).Count() <= 65
                    ? null
                    : $"{wrapper} contains reusable implementation logic.",
            }.OfType<string>();
        }).ToArray();

        Assert.Empty(errors);
    }

    [Fact]
    public void Module_scaffolding_is_branded_without_a_post_generation_rewrite()
    {
        string wrapper = RepositoryPaths.Read("eng", "new-module.ps1");

        Assert.Contains("-ProjectPrefix 'BunkFy.Modules'", wrapper, StringComparison.Ordinal);
        Assert.Contains("-PublicApiHostProject 'src\\BunkFy.Host.Api", wrapper, StringComparison.Ordinal);
        Assert.False(File.Exists(RepositoryPaths.Resolve("eng", "brand-module.ps1")));
        Assert.False(File.Exists(RepositoryPaths.Resolve("eng", "new-gma-app.ps1")));
    }

    [Fact]
    public void Backend_verification_enforces_package_ownership_and_propagates_step_failures()
    {
        string source = RepositoryPaths.Read("eng", "verify.ps1");
        string common = RepositoryPaths.Read("eng", "common.ps1");

        Assert.Contains("check-source-packages.ps1", source, StringComparison.Ordinal);
        Assert.Contains("SkipRestore = $true; SkipBuild = $true", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-GmaScript", source, StringComparison.Ordinal);
        Assert.Contains("$global:LASTEXITCODE = 0", common, StringComparison.Ordinal);
        Assert.Contains("if ($exitCode -ne 0)", common, StringComparison.Ordinal);
        Assert.Contains("exit $exitCode", common, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_ci_uses_immutable_actions_and_source_set_evidence()
    {
        string workflows = string.Join(
            Environment.NewLine,
            RepositoryPaths.EnumerateFiles(".github/workflows", "*.yml").Select(File.ReadAllText));
        string[] requiredTokens =
        [
            "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
            "actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73",
            "actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9",
            "persist-credentials: false",
            "./eng/export-source-set.ps1 -RequireClean",
        ];

        Assert.DoesNotContain(requiredTokens, token => !workflows.Contains(token, StringComparison.Ordinal));
        Assert.True(File.Exists(RepositoryPaths.Resolve(".github", "dependabot.yml")));
    }

    [Fact]
    public void Root_workspace_sync_is_deterministic_and_filters_generated_projects()
    {
        string source = RepositoryPaths.Read("eng", "update-solutions.ps1");

        Assert.Contains("$sorted.Sort([System.StringComparer]::Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("$relative -match '(^|[\\\\/])(\\.tmp|bin|obj)([\\\\/]|$)'", source, StringComparison.Ordinal);
        Assert.Equal(2, source.Split(
            "$relative -match '(^|[\\\\/])(\\.tmp|bin|obj)([\\\\/]|$)'",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("SourceFileExtensions = @('.md')", source, StringComparison.Ordinal);
        Assert.Contains("'src/Modules/*/docs/*.json'", source, StringComparison.Ordinal);
        Assert.Contains("'src/Extensions/*/docs/*.json'", source, StringComparison.Ordinal);
        Assert.Equal(2, source.Split(
            "'src/Modules/*/docs/*.json'",
            StringSplitOptions.None).Length - 1);
        Assert.Equal(2, source.Split(
            "'src/Extensions/*/docs/*.json'",
            StringSplitOptions.None).Length - 1);
        Assert.Equal(2, source.Split("'.mjs'", StringSplitOptions.None).Length - 1);
        Assert.Contains("-not $isDataGovernanceCatalog", source, StringComparison.Ordinal);
        Assert.Contains("-Check:$Check", source, StringComparison.Ordinal);
        Assert.Contains("'SECURITY.md'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort-Object", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_entry_points_bound_msbuild_parallelism()
    {
        foreach (string script in new[] { "restore.ps1", "gma-validate.ps1" })
        {
            string source = RepositoryPaths.Read("eng", script);

            Assert.Contains("--disable-parallel", source, StringComparison.Ordinal);
            Assert.Contains("-m:1", source, StringComparison.Ordinal);
            Assert.Contains("-p:BuildInParallel=false", source, StringComparison.Ordinal);
        }
    }
}
