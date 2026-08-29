using System.Reflection;
using Materal.Extensions.DependencyInjection;
using Materal.TTA.Common;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using Materal.TTA.SqliteEFRepository;
using ProjectUnitOfWork = RoundTableMeeting.Domain.UnitOfWork.IUnitOfWork;

namespace RoundTableMeeting.ArchitectureTests;

public sealed class DependencyTests
{
    [Fact]
    public void ProductionProjectsKeepExpectedReferences()
    {
        var domain = Load("RoundTableMeeting.Domain");
        var abstraction = Load("RoundTableMeeting.Abstraction");
        var application = Load("RoundTableMeeting.Application");
        var repository = Load("RoundTableMeeting.Repository.Sqlite");
        var web = Load("RoundTableMeeting");

        Assert.DoesNotContain(domain.GetReferencedAssemblies(), reference => reference.Name is "RoundTableMeeting.Application" or "RoundTableMeeting.Repository.Sqlite" or "Microsoft.EntityFrameworkCore" or "Microsoft.Data.Sqlite");
        Assert.DoesNotContain(abstraction.GetReferencedAssemblies(), reference => reference.Name is "RoundTableMeeting.Domain" or "Microsoft.EntityFrameworkCore" or "Microsoft.Data.Sqlite");
        Assert.DoesNotContain(application.GetReferencedAssemblies(), reference => reference.Name is "RoundTableMeeting.Repository.Sqlite" or "Microsoft.EntityFrameworkCore" or "Microsoft.Data.Sqlite" or "Microsoft.AspNetCore.App");
        Assert.Contains(repository.GetReferencedAssemblies(), reference => reference.Name == "RoundTableMeeting.Domain");
        Assert.Contains(web.GetReferencedAssemblies(), reference => reference.Name == "RoundTableMeeting.Repository.Sqlite");
    }

    [Fact]
    public void CompositionRootUsesDefaultContainerAndExplicitScanning()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "Server", "RoundTableMeeting", "Hosting", "ServiceCollectionExtensions.cs"));
        var program = File.ReadAllText(Path.Combine(FindRoot(), "Server", "RoundTableMeeting", "Program.cs"));
        Assert.Contains("AddAutoService", source, StringComparison.Ordinal);
        Assert.Contains("Application.AssemblyMarker", source, StringComparison.Ordinal);
        Assert.Contains("Repository.Sqlite.AssemblyMarker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddMateralServiceProvider", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddTTARepository", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddTTASqliteEFRepository", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddMateralServiceProvider", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameworkServicesUseExpectedScopedMarkers()
    {
        Assert.Contains(typeof(RoundTableMeeting.Application.SystemStatus.SystemStatusService).GetInterfaces(), type => type == typeof(IScopedDependency<ISystemStatusService>));
        Assert.Contains(typeof(RoundTableMeeting.Repository.Sqlite.UnitOfWork.UnitOfWorkImpl).GetInterfaces(), type => type == typeof(IScopedDependency<ProjectUnitOfWork>));
        Assert.Contains(typeof(SqliteDatabaseInitializer).GetInterfaces(), type => type == typeof(IScopedDependency<IDatabaseInitializer>));
        Assert.Contains(typeof(SqliteDatabaseAvailabilityChecker).GetInterfaces(), type => type == typeof(IScopedDependency<IDatabaseAvailabilityChecker>));
    }

    [Fact]
    public void ApplicationConstructorsDoNotInjectInfrastructure()
    {
        foreach (var type in Load("RoundTableMeeting.Application").GetTypes())
        {
            foreach (var constructor in type.GetConstructors())
            {
                Assert.DoesNotContain(constructor.GetParameters(), parameter =>
                    parameter.ParameterType.FullName?.Contains("EntityFrameworkCore", StringComparison.Ordinal) == true
                    || parameter.ParameterType.FullName?.Contains("Repository.Sqlite", StringComparison.Ordinal) == true
                    || parameter.ParameterType == typeof(DbContext)
                    || parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IRepository<,>));
            }
        }
    }

    [Fact]
    public void ApplicationTypesDoNotExposeInfrastructureTypes()
    {
        foreach (var type in Load("RoundTableMeeting.Application").GetTypes())
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.DoesNotContain(members, member =>
            {
                var memberType = member switch
                {
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    MethodInfo method => method.ReturnType,
                    _ => null
                };
                return memberType?.FullName?.Contains("EntityFrameworkCore", StringComparison.Ordinal) == true
                    || memberType?.FullName?.Contains("IQueryable", StringComparison.Ordinal) == true
                    || memberType?.Name is "DbContext" or "EntityState" || memberType?.Name?.StartsWith("DbSet", StringComparison.Ordinal) == true;
            });
        }
    }

    [Fact]
    public void DomainRepositoriesDoNotExposeWriteMethods()
    {
        foreach (var type in Load("RoundTableMeeting.Domain").GetTypes().Where(type => type.IsInterface && type.Name.EndsWith("Repository", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(type.GetMethods(), method => method.Name.Contains("Add", StringComparison.Ordinal) || method.Name.Contains("Update", StringComparison.Ordinal) || method.Name.Contains("Delete", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PackageVersionsAreExactAndGeneratedImportsStayBehindApiLayer()
    {
        var packageFiles = Directory.GetFiles(FindRoot(), "*.csproj", SearchOption.AllDirectories);
        foreach (var file in packageFiles)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Version=\"*", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Version=\"latest", source, StringComparison.OrdinalIgnoreCase);
        }

        var packageJson = File.ReadAllText(Path.Combine(FindRoot(), "Client", "roundTableMeeting", "package.json"));
        Assert.Contains("\"typescript\": \"5.9.3\"", packageJson, StringComparison.Ordinal);
        Assert.Contains("@microsoft/kiota-abstractions", packageJson, StringComparison.Ordinal);
        Assert.Contains("@microsoft/kiota-http-fetchlibrary", packageJson, StringComparison.Ordinal);
        Assert.Contains("@microsoft/kiota-serialization-json", packageJson, StringComparison.Ordinal);
        Assert.Contains("1.0.0-preview.103", packageJson, StringComparison.Ordinal);
        var generated = Path.Combine(FindRoot(), "Client", "roundTableMeeting", "src", "api", "generated");
        Assert.True(Directory.Exists(generated));
        Assert.NotEmpty(Directory.GetFiles(generated, "*.ts", SearchOption.AllDirectories));

        var clientRoot = Path.Combine(FindRoot(), "Client", "roundTableMeeting", "src");
        foreach (var file in Directory.GetFiles(clientRoot, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
            .Where(file =>
            {
                var relativePath = Path.GetRelativePath(clientRoot, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var isGenerated = segments.Length >= 2
                    && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(segments[1], "generated", StringComparison.OrdinalIgnoreCase);
                var isApiClient = segments.Length >= 2
                    && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(segments[1], "client", StringComparison.OrdinalIgnoreCase);
                var isFeatureApiAdapter = segments.Length >= 3
                    && string.Equals(segments[0], "features", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(segments[2], "api", StringComparison.OrdinalIgnoreCase);
                return !isGenerated && !isApiClient && !isFeatureApiAdapter;
            }))
        {
            Assert.DoesNotContain("api/generated", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SqliteRepositoryBasePreservesMateralGenericContract()
    {
        var type = typeof(SqliteRepository<,>);
        Assert.True(type.IsAbstract);
        Assert.Equal(2, type.GetGenericArguments().Length);
        Assert.Equal(typeof(SqliteEFRepositoryImpl<,,>), type.BaseType?.GetGenericTypeDefinition());
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), method =>
            method.DeclaringType == type && method.Name is "Add" or "Update" or "Delete" or "Save");
    }

    [Fact]
    public void SqliteDbContextAppliesConfigurationsFromItsAssembly()
    {
        var sourcePath = Path.Combine(FindRoot(), "Server", "RoundTableMeeting.Repository.Sqlite", "Database", "RoundTableMeetingDbContext.cs");
        var source = File.ReadAllText(sourcePath);
        Assert.Contains("protected override void OnModelCreating", source, StringComparison.Ordinal);
        Assert.Contains("base.OnModelCreating(modelBuilder)", source, StringComparison.Ordinal);
        Assert.Contains("ApplyConfigurationsFromAssembly(typeof(RoundTableMeetingDbContext).Assembly)", source, StringComparison.Ordinal);
    }

    private static Assembly Load(string name) => Assembly.Load(name);
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root could not be located.");
    }
}
