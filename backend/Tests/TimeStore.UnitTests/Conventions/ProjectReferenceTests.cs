using System.Xml.Linq;

namespace TimeStore.UnitTests.Conventions;

/// <summary>
/// Guards the dependency rules of AGENTS.md: references point downward only, providers are reachable only from
/// the Server, and no production project uses a package that breaks Native AOT.
/// </summary>
public sealed class ProjectReferenceTests
{
    private const string Primitives = "TimeStore.Primitives";
    private const string Cryptography = "TimeStore.Cryptography";
    private const string Protocol = "TimeStore.Protocol";
    private const string Domain = "TimeStore.Domain";
    private const string Contracts = "TimeStore.Contracts";
    private const string Application = "TimeStore.Application";
    private const string Infra = "TimeStore.Infra";
    private const string MetadataSqlite = "TimeStore.Metadata.Sqlite";
    private const string BlobFileSystem = "TimeStore.Blob.FileSystem";
    private const string Server = "TimeStore.Server";

    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        [Primitives] = [],
        [Cryptography] = [],
        [Protocol] = [Primitives, Cryptography],
        [Domain] = [Primitives],
        [Contracts] = [],
        [Application] = [Primitives, Domain, Contracts],
        [Infra] = [Cryptography, Application],
        [MetadataSqlite] = [Primitives, Domain],
        [BlobFileSystem] = [Primitives, Domain],
        [Server] = [Protocol, Infra, MetadataSqlite, BlobFileSystem],
    };

    // Package id prefixes that defeat Native AOT (reflection, EF, scanning) or that our own conventions replace.
    // A prefix also covers sub-packages (Microsoft.EntityFrameworkCore.Sqlite, Dapper.Contrib).
    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "Microsoft.EntityFrameworkCore", "Dapper", "Scrutor", "Newtonsoft.Json", "AutoMapper", "MediatR",
        "FluentResults", "FluentValidation",
    ];

    // Reviewed exceptions to the prefixes above (e.g. "Dapper.AOT" once it is reviewed per .claude/rules/aot.md).
    private static readonly string[] AllowedPackages = [];

    [Fact]
    public void EveryProductionProject_HasADependencyRule()
    {
        var unknown = ProductionProjects().Select(p => p.Name).Where(n => !AllowedReferences.ContainsKey(n)).ToArray();

        Assert.True(unknown.Length == 0, $"Add a rule for: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void ProductionProjects_ReferenceOnlyAllowedProjects()
    {
        var violations = ProductionProjects()
            .Where(p => AllowedReferences.ContainsKey(p.Name))
            .SelectMany(p => ProjectReferences(p.Document)
                .Where(r => !AllowedReferences[p.Name].Contains(r))
                .Select(r => $"{p.Name} -> {r}"))
            .ToArray();

        Assert.True(violations.Length == 0, $"Forbidden references: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ProductionProjects_UseNoForbiddenPackages()
    {
        var violations = ProductionProjects()
            .SelectMany(p => PackageReferences(p.Document)
                .Where(IsForbidden)
                .Select(package => $"{p.Name} -> {package}"))
            .ToArray();

        Assert.True(violations.Length == 0, $"Forbidden packages: {string.Join(", ", violations)}");
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", true)]
    [InlineData("Dapper", true)]
    [InlineData("Microsoft.Data.Sqlite", false)]
    public void IsForbidden_MatchesByPrefix(string package, bool expected) =>
        Assert.Equal(expected, IsForbidden(package));

    private static bool IsForbidden(string package) =>
        !AllowedPackages.Contains(package, StringComparer.OrdinalIgnoreCase)
        && ForbiddenPackagePrefixes.Any(prefix =>
            package.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || package.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Name, XDocument Document)> ProductionProjects()
    {
        var backend = BackendDirectory();
        return new[] { "Core", "Src" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(backend, folder), "*.csproj", SearchOption.AllDirectories))
            .Select(path => (Path.GetFileNameWithoutExtension(path), XDocument.Load(path)));
    }

    private static IEnumerable<string> ProjectReferences(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/')));

    private static IEnumerable<string> PackageReferences(XDocument project) =>
        project.Descendants("PackageReference").Select(r => (string)r.Attribute("Include")!);

    private static string BackendDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TimeStore.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("TimeStore.slnx not found above the test output directory.");
    }
}
