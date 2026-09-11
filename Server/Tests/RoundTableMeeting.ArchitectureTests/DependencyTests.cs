using System.Reflection;
using Materal.Extensions.DependencyInjection;
using Materal.TTA.Common;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Persistence;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using Materal.TTA.SqliteEFRepository;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Controllers;
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
    public void DomainRepositoriesOnlyExposeExplicitAccountColumnWrites()
    {
        foreach (var type in Load("RoundTableMeeting.Domain").GetTypes().Where(type => type.IsInterface && type.Name.EndsWith("Repository", StringComparison.Ordinal)))
        {
            var allowed = type == typeof(IUserAccountRepository)
                ? new[] { "UpdateNicknameAsync", "UpdatePasswordHashAsync", "UpdateAdministratorAsync" } : [];
            Assert.DoesNotContain(type.GetMethods(), method => !allowed.Contains(method.Name)
                && (method.Name.StartsWith("Add", StringComparison.Ordinal) || method.Name.StartsWith("Update", StringComparison.Ordinal) || method.Name.StartsWith("Delete", StringComparison.Ordinal)));
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
    public void UserAccountRepositoryUsesDomainAndExplicitColumnContracts()
    {
        Assert.True(typeof(IUserAccountRepository).IsAssignableTo(typeof(IRepository<UserAccount, Guid>)));
        Assert.True(typeof(UserAccountRepository).IsAssignableTo(typeof(SqliteRepository<UserAccount, Guid>)));
        Assert.Contains(typeof(UserAccountRepository).GetInterfaces(), type => type == typeof(IScopedDependency<IUserAccountRepository>));
        Assert.DoesNotContain(typeof(IUserAccountRepository).GetMethods(), method => method.Name.Contains("Add", StringComparison.Ordinal) || method.Name.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationTypesDoNotExposeWebOptionsOrAuthenticationTypes()
    {
        foreach (var type in Load("RoundTableMeeting.Application").GetTypes())
        {
            var allTypes = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(member => member switch
                {
                    MethodInfo method => new[] { method.ReturnType }.Concat(method.GetParameters().Select(parameter => parameter.ParameterType)),
                    PropertyInfo property => [property.PropertyType],
                    FieldInfo field => [field.FieldType],
                    _ => Array.Empty<Type>()
                });
            Assert.DoesNotContain(allTypes, candidate => candidate.FullName is not null &&
                (candidate.FullName.Contains("HttpContext", StringComparison.Ordinal)
                 || candidate.FullName.Contains("ClaimsPrincipal", StringComparison.Ordinal)
                 || candidate.FullName.Contains("IConfiguration", StringComparison.Ordinal)
                 || candidate.FullName.Contains("IOptions", StringComparison.Ordinal)
                 || candidate.FullName.Contains("Cookie", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void AuthenticationCompositionUsesApplicationContractsWithoutFullIdentityStore()
    {
        var web = Load("RoundTableMeeting");
        var application = Load("RoundTableMeeting.Application");
        Assert.Contains(web.GetTypes(), type => type.Name == "AuthenticationController");
        Assert.Contains(application.GetTypes(), type => type.Name == "AuthenticationService");
        Assert.DoesNotContain(web.GetTypes(), type => type.Name.Contains("UserManager", StringComparison.Ordinal) || type.Name.Contains("RoleManager", StringComparison.Ordinal) || type.Name.Contains("IdentityDbContext", StringComparison.Ordinal));
        Assert.DoesNotContain(application.GetTypes(), type => type.Name.Contains("UserManager", StringComparison.Ordinal) || type.Name.Contains("RoleManager", StringComparison.Ordinal) || type.Name.Contains("IdentityDbContext", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthenticationOptionsAndHttpContextStayInTheWebAssembly()
    {
        var application = Load("RoundTableMeeting.Application");
        var domain = Load("RoundTableMeeting.Domain");
        Assert.DoesNotContain(application.GetReferencedAssemblies(), reference => reference.Name is "RoundTableMeeting" or "Microsoft.AspNetCore.App");
        Assert.DoesNotContain(domain.GetReferencedAssemblies(), reference => reference.Name is "Microsoft.AspNetCore.App" or "Microsoft.Extensions.Options");
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

    [Fact]
    public void AuthenticationPipelineAndOriginMetadataHaveTheFrozenOrder()
    {
        var root = FindRoot();
        var source = File.ReadAllText(Path.Combine(root, "Server", "RoundTableMeeting", "Program.cs"));
        var routing = source.IndexOf("app.UseRouting();", StringComparison.Ordinal);
        var responseHeaders = source.IndexOf("app.UseMiddleware<SensitiveResponseHeadersMiddleware>();", StringComparison.Ordinal);
        var origin = source.IndexOf("app.UseMiddleware<OriginValidationMiddleware>();", StringComparison.Ordinal);
        var authentication = source.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var authorization = source.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);

        Assert.True(routing >= 0);
        Assert.True(routing < responseHeaders);
        Assert.True(responseHeaders < origin);
        Assert.True(origin < authentication);
        Assert.True(authentication < authorization);
        Assert.DoesNotContain("AuthenticationResponseHeadersMiddleware", source, StringComparison.Ordinal);
        Assert.NotNull(typeof(AuthenticationController).GetCustomAttribute<NoStoreResponseAttribute>());
        Assert.NotNull(typeof(UsersController).GetCustomAttribute<NoStoreResponseAttribute>());
        var loginOrigin = typeof(AuthenticationController).GetMethod("Login")!.GetCustomAttributes(true).OfType<RequireBrowserOriginAttribute>().Single();
        var logoutOrigin = typeof(AuthenticationController).GetMethod("Logout")!.GetCustomAttributes(true).OfType<RequireBrowserOriginAttribute>().Single();
        var createOrigin = typeof(UsersController).GetMethod("Create")!.GetCustomAttributes(true).OfType<RequireBrowserOriginAttribute>().Single();
        var nicknameOrigin = typeof(UsersController).GetMethod("UpdateNickname")!.GetCustomAttributes(true).OfType<RequireBrowserOriginAttribute>().Single();
        var resetOrigin = typeof(UsersController).GetMethod("ResetPassword")!.GetCustomAttributes(true).OfType<RequireBrowserOriginAttribute>().Single();
        Assert.True(loginOrigin.RequireJsonBody);
        Assert.False(logoutOrigin.RequireJsonBody);
        Assert.True(createOrigin.RequireJsonBody);
        Assert.True(nicknameOrigin.RequireJsonBody);
        Assert.False(resetOrigin.RequireJsonBody);
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
