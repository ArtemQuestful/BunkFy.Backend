namespace Architecture.Tests.DeveloperExperience;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Architecture.Tests.Support;
using Xunit;

[Trait("Category", "Architecture")]
public sealed partial class DeveloperExperienceGuardTests
{
    [Fact]
    public void Projects_under_src_and_tests_are_listed_in_solution()
    {
        string solution = RepositoryPaths.Read("BunkFy.slnx");
        string[] missing = ProjectFile.All()
            .Select(project => project.RepositoryPath)
            .Where(path => !solution.Contains(path, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Gma_projects_are_not_nested_under_product_module_solution_folders()
    {
        XDocument solution = XDocument.Load(RepositoryPaths.Resolve("BunkFy.slnx"));
        string[] offenders = solution
            .Descendants()
            .Where(element => element.Name.LocalName == "Folder")
            .Where(element => element.Attribute("Name")?.Value.StartsWith("/src/Modules/", StringComparison.Ordinal) == true)
            .SelectMany(folder => folder
                .Elements()
                .Where(element => element.Name.LocalName == "Project")
                .Select(element => new
                {
                    Folder = folder.Attribute("Name")?.Value ?? string.Empty,
                    Path = element.Attribute("Path")?.Value ?? string.Empty
                }))
            .Where(entry => entry.Path.StartsWith("gma/", StringComparison.Ordinal))
            .Select(entry => $"{entry.Folder} contains {entry.Path}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Gma_module_projects_are_listed_under_gma_solution_folders()
    {
        XDocument solution = XDocument.Load(RepositoryPaths.Resolve("BunkFy.slnx"));
        var projectEntries = solution
            .Descendants()
            .Where(element => element.Name.LocalName == "Folder")
            .SelectMany(folder => folder
                .Elements()
                .Where(element => element.Name.LocalName == "Project")
                .Select(element => new
                {
                    Folder = folder.Attribute("Name")?.Value ?? string.Empty,
                    Path = element.Attribute("Path")?.Value ?? string.Empty
                }))
            .ToArray();
        string[] expectedModuleProjects = RepositoryPaths
            .EnumerateFiles("gma/modules", "*.csproj")
            .Select(RepositoryPaths.ToRepositoryPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] missing = expectedModuleProjects
            .Where(path => !projectEntries.Any(entry => string.Equals(entry.Path, path, StringComparison.Ordinal)))
            .Select(path => $"BunkFy.slnx missing {path}")
            .ToArray();
        string[] misplaced = projectEntries
            .Where(entry => entry.Path.StartsWith("gma/modules/", StringComparison.Ordinal))
            .Where(entry => !entry.Folder.StartsWith("/gma/modules/", StringComparison.Ordinal))
            .Select(entry => $"{entry.Path} is listed under {entry.Folder}")
            .ToArray();

        Assert.Empty(missing.Concat(misplaced));
    }

    [Fact]
    public void Operational_files_are_listed_in_solution()
    {
        string solution = RepositoryPaths.Read("BunkFy.slnx");
        string[] expected = RepositoryPaths.EnumerateFiles("docs", "*.md")
            .Concat(RepositoryPaths.EnumerateFiles("eng", "*.ps1"))
            .Concat(RepositoryPaths.EnumerateFiles(".github/workflows", "*.yml"))
            .Concat(RepositoryPaths.EnumerateFiles("requests", "*.*"))
            .Select(RepositoryPaths.ToRepositoryPath)
            .Concat(
            [
                ".config/dotnet-tools.json",
                ".editorconfig",
                ".gitattributes",
                ".gitignore",
                ".gitmodules",
                ".github/dependabot.yml",
                "Directory.Build.props",
                "Directory.Packages.props",
                "global.json",
                "Gma.SourceRoots.props.example",
                "nuget.config",
                "README.md"
            ])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] missing = expected
            .Where(path => !SolutionContainsPath(solution, path))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Project_files_live_in_matching_project_folders()
    {
        string[] offenders = ProjectFile.All()
            .Where(project =>
            {
                string folderName = Path.GetFileName(Path.GetDirectoryName(project.Path)) ?? string.Empty;
                return !string.Equals(project.Name, folderName, StringComparison.Ordinal);
            })
            .Select(project => project.RepositoryPath)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Project_files_do_not_override_default_namespace_or_assembly_name()
    {
        string[] offenders = ProjectFile.All()
            .SelectMany(project => project.Document
                .Descendants()
                .Where(element => element.Name.LocalName is "RootNamespace" or "AssemblyName")
                .Select(element => $"{project.RepositoryPath}:{element.Name.LocalName}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Product_module_projects_use_bunkfy_branding_and_matching_directories()
    {
        string[] offenders = ProjectFile.All()
            .Where(project => project.RepositoryPath.StartsWith("src/Modules/", StringComparison.Ordinal))
            .Where(project =>
            {
                string module = project.RepositoryPath.Split('/')[2];
                string expectedPrefix = $"BunkFy.Modules.{module}.";
                string directory = Path.GetFileName(Path.GetDirectoryName(project.Path))!;
                return !project.Name.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                       !string.Equals(directory, project.Name, StringComparison.Ordinal);
            })
            .Select(project => project.RepositoryPath)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Product_module_source_namespaces_use_bunkfy_branding()
    {
        string[] offenders = RepositoryPaths.EnumerateFiles("src/Modules", "*.cs")
            .Select(path => new
            {
                Path = path,
                Namespace = File.ReadLines(path)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("namespace ", StringComparison.Ordinal))
            })
            .Where(file => file.Namespace is not null)
            .Where(file =>
            {
                string repositoryPath = RepositoryPaths.ToRepositoryPath(file.Path);
                string module = repositoryPath.Split('/')[2];
                return !file.Namespace!.StartsWith($"namespace BunkFy.Modules.{module}", StringComparison.Ordinal);
            })
            .Select(file => RepositoryPaths.ToRepositoryPath(file.Path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Skeleton_example_modules_are_not_product_dependencies()
    {
        string[] exampleModules = ["Catalog", "Ordering", "TaskSamples"];
        string[] existing = exampleModules
            .Where(module => Directory.Exists(RepositoryPaths.Resolve("src", "Modules", module)))
            .ToArray();
        string[] references = ProjectFile.All()
            .SelectMany(project => project.ProjectReferences
                .Where(reference => exampleModules.Any(module =>
                    reference.Contains(module, StringComparison.Ordinal)))
                .Select(reference => $"{project.RepositoryPath} -> {reference}"))
            .ToArray();

        Assert.Empty(existing.Concat(references));
    }

    [Fact]
    public void Handwritten_domain_files_stay_within_the_reviewable_size_limit()
    {
        string[] offenders = RepositoryPaths.EnumerateFiles("src/Modules", "*.cs")
            .Where(path => RepositoryPaths.ToRepositoryPath(path).Contains(".Domain/", StringComparison.Ordinal))
            .Where(path => !RepositoryPaths.ToRepositoryPath(path).Contains("/obj/", StringComparison.Ordinal))
            .Select(path => new
            {
                Path = RepositoryPaths.ToRepositoryPath(path),
                Lines = File.ReadLines(path).Count()
            })
            .Where(file => file.Lines > 400)
            .Select(file => $"{file.Path} has {file.Lines} lines")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Staff_cqrs_event_and_api_contract_files_define_one_top_level_type()
    {
        string[] ownedFolders =
        [
            ".Application/Commands/",
            ".Application/Queries/",
            ".Application/Handlers/",
            ".Application/Validation/",
            ".Domain/Events/",
            ".Contracts/Events/",
            ".Contracts/Api/"
        ];
        Regex declaration = new(
            @"^(public|internal) (sealed |static |abstract )*(class|record|interface|enum) ",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        string[] offenders = RepositoryPaths.EnumerateFiles("src/Modules/Staff", "*.cs")
            .Where(path => !RepositoryPaths.ToRepositoryPath(path).Contains("/obj/", StringComparison.Ordinal))
            .Where(path => ownedFolders.Any(folder =>
                RepositoryPaths.ToRepositoryPath(path).Contains(folder, StringComparison.Ordinal)))
            .Select(path => new
            {
                Path = RepositoryPaths.ToRepositoryPath(path),
                TypeCount = declaration.Count(File.ReadAllText(path))
            })
            .Where(file => file.TypeCount != 1)
            .Select(file => $"{file.Path} defines {file.TypeCount} top-level types")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Grouped_application_request_files_are_limited_to_the_cleanup_backlog()
    {
        HashSet<string> knownGroupedFiles = new(StringComparer.Ordinal)
        {
            "src/Modules/Guests/BunkFy.Modules.Guests.Application/Validation/GuestProfileValidators.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/DecideChangeProposalCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/LegalHoldCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/ManageAdapterConnectionCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/ManageAdapterIngressCredentialCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/ObservationReprocessingCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/RawPayloadRetentionCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/RemoteAdapterLeaseCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Commands/SensitiveHistoryRetentionCommands.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Queries/AdapterIngressCredentialQueries.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Queries/ChangeProposalQueries.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Queries/IngestionOperationsQueries.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Application/Queries/LegalHoldQueries.cs",
            "src/Modules/Reservations/BunkFy.Modules.Reservations.Application/Validation/ReservationStayCommandValidators.cs"
        };
        string[] offenders = RepositoryPaths.EnumerateFiles("src/Modules", "*.cs")
            .Select(RepositoryPaths.ToRepositoryPath)
            .Where(path => path.EndsWith("Commands.cs", StringComparison.Ordinal) ||
                           path.EndsWith("Queries.cs", StringComparison.Ordinal) ||
                           path.EndsWith("Validators.cs", StringComparison.Ordinal))
            .Where(path => !knownGroupedFiles.Contains(path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Source_roots_include_reusable_gma_sources_only()
    {
        string sourceRoots = RepositoryPaths.Read("Gma.SourceRoots.props.example");
        string[] expectedTokens =
        [
            "gma\\framework\\src\\",
            "<GmaExtensionsRoot>$(MSBuildThisFileDirectory)gma\\extensions\\src\\</GmaExtensionsRoot>",
            "<GmaModuleAccessControlRoot>$(GmaModulesRoot)access-control\\src\\</GmaModuleAccessControlRoot>",
            "<GmaModuleAuthRoot>$(GmaModulesRoot)auth\\src\\</GmaModuleAuthRoot>",
            "<GmaModuleAdministrationRoot>$(GmaModulesRoot)administration\\src\\</GmaModuleAdministrationRoot>",
            "<GmaModuleFilesRoot>$(GmaModulesRoot)files\\src\\</GmaModuleFilesRoot>",
            "<GmaModuleNotificationsRoot>$(GmaModulesRoot)notifications\\src\\</GmaModuleNotificationsRoot>",
            "<GmaModuleOrganizationsRoot>$(GmaModulesRoot)organizations\\src\\</GmaModuleOrganizationsRoot>",
            "<GmaModuleTaskRuntimeRoot>$(GmaModulesRoot)task-runtime\\src\\</GmaModuleTaskRuntimeRoot>",
            "<GmaModuleTenancyRoot>$(GmaModulesRoot)tenancy\\src\\</GmaModuleTenancyRoot>"
        ];

        string[] missing = expectedTokens
            .Where(token => !sourceRoots.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Product_projects_use_named_gma_module_source_roots()
    {
        string[] offenders = ProjectFile.All()
            .Where(project =>
                project.RepositoryPath.StartsWith("src/", StringComparison.Ordinal))
            .SelectMany(project => project.ProjectReferences
                .Where(reference =>
                    reference.Contains("$(GmaModulesRoot)", StringComparison.Ordinal))
                .Select(reference => $"{project.RepositoryPath} -> {reference}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Package_versions_are_centralized()
    {
        XDocument packages = XDocument.Load(RepositoryPaths.Resolve("Directory.Packages.props"));
        string[] centralVersions = packages
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        Assert.Contains("Microsoft.NET.Test.Sdk", centralVersions);
        Assert.Contains("Microsoft.EntityFrameworkCore", centralVersions);
        Assert.Contains("Aspire.Hosting.AppHost", centralVersions);

        string[] inlineVersions = ProjectFile.All()
            .SelectMany(project => project.Document
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Where(element => element.Attribute("Version") is not null)
                .Select(_ => project.RepositoryPath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(inlineVersions);
    }

    [Fact]
    public void Test_projects_are_marked_and_keep_runner_private()
    {
        string[] failures = ProjectFile.All()
            .Where(project => project.RepositoryPath.Contains("/tests/", StringComparison.Ordinal) ||
                              project.RepositoryPath.StartsWith("tests/", StringComparison.Ordinal))
            .SelectMany(project =>
            {
                List<string> projectFailures = [];
                bool isTestProject = project.Document
                    .Descendants()
                    .Any(element => element.Name.LocalName == "IsTestProject" &&
                                    string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
                bool isPackableFalse = project.Document
                    .Descendants()
                    .Any(element => element.Name.LocalName == "IsPackable" &&
                                    string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
                bool runnerPrivate = project.Document
                    .Descendants()
                    .Where(element => element.Name.LocalName == "PackageReference" &&
                                      string.Equals(element.Attribute("Include")?.Value, "xunit.runner.visualstudio", StringComparison.Ordinal))
                    .Elements()
                    .Any(element => element.Name.LocalName == "PrivateAssets" &&
                                    string.Equals(element.Value.Trim(), "all", StringComparison.OrdinalIgnoreCase));

                if (!isTestProject)
                {
                    projectFailures.Add($"{project.RepositoryPath} missing IsTestProject=true");
                }

                if (!isPackableFalse)
                {
                    projectFailures.Add($"{project.RepositoryPath} missing IsPackable=false");
                }

                if (!runnerPrivate)
                {
                    projectFailures.Add($"{project.RepositoryPath} must keep xunit.runner.visualstudio private");
                }

                return projectFailures;
            })
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void Repository_ignore_rules_keep_local_workspace_state_out_of_source()
    {
        string gitignore = RepositoryPaths.Read(".gitignore");
        string[] requiredTokens =
        [
            ".agents/",
            ".codex/",
            ".tmp/",
            ".vs/",
            "[Tt]est[Rr]esult*/",
            "artifacts/",
            "bin/",
            "obj/",
            "Gma.SourceRoots.props"
        ];
        string[] forbiddenTokens =
        [
            ".config/",
            "dotnet-tools.json"
        ];

        string[] offenders = requiredTokens
            .Where(token => !gitignore.Contains(token, StringComparison.Ordinal))
            .Select(token => $".gitignore missing {token}")
            .Concat(forbiddenTokens
                .Where(token => gitignore.Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(token => $".gitignore should not ignore tracked tool manifest token {token}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void OpenApi_export_is_independent_from_background_infrastructure()
    {
        string exporter = RepositoryPaths.Read("eng/export-openapi.ps1");
        string[] disabledSettings =
        [
            "Notifications__Delivery__Enabled",
            "Notifications__Retention__Enabled",
            "Notifications__DurableStreams__MonitorEnabled",
            "Auth__Retention__Enabled",
            "Organizations__Retention__Enabled",
            "MessageJournalCleanup__Enabled",
            "NatsJetStream__Enabled",
            "NatsConsumers__Enabled"
        ];

        string[] missing = disabledSettings
            .Where(setting => !exporter.Contains($"$env:{setting} = 'false'", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
        Assert.Contains("AddSeconds(60)", exporter, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSec 15", exporter, StringComparison.Ordinal);
        Assert.Contains("GetEnvironmentVariables('Process')", exporter, StringComparison.Ordinal);
        Assert.Contains("WasDefined = $wasDefined", exporter, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath \"Env:\\$($entry.Key)\"", exporter, StringComparison.Ordinal);
        Assert.Contains("$entry.Value.Value", exporter, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_local_links_resolve_to_repository_files()
    {
        string[] offenders = RepositoryPaths.EnumerateFiles("docs", "*.md")
            .Append(RepositoryPaths.Resolve("README.md"))
            .SelectMany(FindBrokenMarkdownLocalLinks)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Documentation_index_links_every_backend_docs_page()
    {
        string docsRoot = RepositoryPaths.Resolve("docs");
        string indexPath = Path.Combine(docsRoot, "README.md");
        string indexSource = File.ReadAllText(indexPath);
        string[] expectedDocs = Directory
            .EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, indexPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(docsRoot, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] indexedDocs = MarkdownLinkPattern()
            .Matches(indexSource)
            .Select(match => match.Groups["target"].Value.Trim())
            .Where(target => !IsExternalOrAnchorMarkdownTarget(target))
            .Select(RemoveAnchor)
            .Where(target => target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(target => Path.GetFullPath(Path.Combine(docsRoot, target.Replace('/', Path.DirectorySeparatorChar))))
            .Where(path => IsUnder(path, docsRoot))
            .Select(path => Path.GetRelativePath(docsRoot, path).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] missing = expectedDocs
            .Except(indexedDocs, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Product_module_inbox_stores_preserve_domain_events_in_the_inbox_transaction()
    {
        string[] offenders = RepositoryPaths
            .EnumerateFiles("src/Modules", "*InboxStore.cs")
            .Where(path => !File.ReadAllText(path).Contains(
                ": EfDomainEventInboxStore<",
                StringComparison.Ordinal))
            .Select(RepositoryPaths.ToRepositoryPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool SolutionContainsPath(string solution, string path)
    {
        string normalized = path.Replace('\\', '/');
        return solution.Contains($"Path=\"{normalized}\"", StringComparison.Ordinal);
    }

    private static IEnumerable<string> FindBrokenMarkdownLocalLinks(string path)
    {
        string source = File.ReadAllText(path);
        string sourceDirectory = Path.GetDirectoryName(path) ?? RepositoryPaths.Root;
        string repositoryPath = RepositoryPaths.ToRepositoryPath(path);

        foreach (Match match in MarkdownLinkPattern().Matches(source))
        {
            string target = match.Groups["target"].Value.Trim();
            if (IsExternalOrAnchorMarkdownTarget(target))
            {
                continue;
            }

            string fileTarget = RemoveAnchor(target);
            if (string.IsNullOrWhiteSpace(fileTarget))
            {
                continue;
            }

            string resolvedPath = Path.GetFullPath(Path.Combine(sourceDirectory, fileTarget.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                yield return $"{repositoryPath} links to missing local target '{target}'";
            }
        }
    }

    private static bool IsExternalOrAnchorMarkdownTarget(string target) =>
        target.Contains("://", StringComparison.Ordinal) ||
        target.StartsWith('#') ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    private static string RemoveAnchor(string target)
    {
        int anchorIndex = target.IndexOf('#', StringComparison.Ordinal);
        return anchorIndex < 0 ? target : target[..anchorIndex];
    }

    private static bool IsUnder(string path, string parent)
    {
        string relativePath = Path.GetRelativePath(parent, path);
        return !relativePath.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relativePath);
    }

    [GeneratedRegex(@"\[[^\]]+\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLinkPattern();
}
