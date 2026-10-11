using System.Xml.Linq;
using Xunit;

namespace ComicManagement.ArchitectureTests;

public sealed class ProjectDependencyTests
{
    private static readonly string[] Modules =
    [
        "ComicManagement.Identity",
        "ComicManagement.Catalog",
        "ComicManagement.Reading",
        "ComicManagement.Interaction",
        "ComicManagement.Billing",
        "ComicManagement.Wallet",
        "ComicManagement.Membership",
        "ComicManagement.Missions",
        "ComicManagement.Administration"
    ];

    private static readonly string[] ProductionProjects =
    [
        "ComicManagement.Api",
        "ComicManagement.Workflows",
        "ComicManagement.Persistence",
        "ComicManagement.Shared",
        .. Modules
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> ExpectedReferences =
        CreateExpectedReferences();

    [Fact]
    public void Production_project_set_is_exactly_the_approved_thirteen_projects()
    {
        var actual = Directory
            .EnumerateFiles(SourceDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = ProductionProjects.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_production_project_targets_dotnet_10_from_the_shared_build_configuration()
    {
        var buildProperties = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var targetFrameworks = buildProperties
            .Descendants("TargetFramework")
            .Select(element => element.Value)
            .ToArray();

        Assert.Equal(["net10.0"], targetFrameworks);
    }

    [Fact]
    public void Production_projects_use_the_expected_sdk()
    {
        foreach (var project in LoadProductionProjects())
        {
            var expectedSdk = project.Key == "ComicManagement.Api"
                ? "Microsoft.NET.Sdk.Web"
                : "Microsoft.NET.Sdk";

            Assert.Equal(expectedSdk, project.Value.Root?.Attribute("Sdk")?.Value);
        }
    }

    [Fact]
    public void Production_project_references_match_the_approved_dependency_matrix()
    {
        foreach (var project in LoadProductionProjects())
        {
            var actualReferences = project.Value
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .Where(path => path is not null)
                .Select(path => Path.GetFileNameWithoutExtension(path!))
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                ExpectedReferences[project.Key].SetEquals(actualReferences),
                $"{project.Key} references [{string.Join(", ", actualReferences.Order())}] " +
                $"but must reference [{string.Join(", ", ExpectedReferences[project.Key].Order())}].");
        }
    }

    [Fact]
    public void Production_project_reference_graph_is_acyclic()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in ProductionProjects)
        {
            Visit(project, visited, visiting);
        }
    }

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string SourceDirectory => Path.Combine(RepositoryRoot, "backend", "src");

    private static IReadOnlyDictionary<string, XDocument> LoadProductionProjects() => Directory
        .EnumerateFiles(SourceDirectory, "*.csproj", SearchOption.AllDirectories)
        .ToDictionary(
            path => Path.GetFileNameWithoutExtension(path)
                ?? throw new InvalidDataException($"Project path has no file name: {path}"),
            XDocument.Load,
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, HashSet<string>> CreateExpectedReferences()
    {
        var references = ProductionProjects.ToDictionary(
            project => project,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var module in Modules)
        {
            references[module].Add("ComicManagement.Shared");
        }

        references["ComicManagement.Workflows"].UnionWith(Modules);
        references["ComicManagement.Workflows"].Add("ComicManagement.Shared");

        references["ComicManagement.Persistence"].UnionWith(Modules);
        references["ComicManagement.Persistence"].Add("ComicManagement.Shared");

        references["ComicManagement.Api"].UnionWith(Modules);
        references["ComicManagement.Api"].Add("ComicManagement.Workflows");
        references["ComicManagement.Api"].Add("ComicManagement.Persistence");

        return references;
    }

    private static void Visit(
        string project,
        ISet<string> visited,
        ISet<string> visiting)
    {
        if (visited.Contains(project))
        {
            return;
        }

        Assert.True(visiting.Add(project), $"Circular project reference detected at {project}.");

        foreach (var dependency in ExpectedReferences[project])
        {
            Visit(dependency, visited, visiting);
        }

        visiting.Remove(project);
        visited.Add(project);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ComicManagement.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
