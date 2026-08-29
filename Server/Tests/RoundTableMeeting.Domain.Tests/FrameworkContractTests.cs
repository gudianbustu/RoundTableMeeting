using Materal.TTA.Common;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Exceptions;
using ProjectUnitOfWork = RoundTableMeeting.Domain.UnitOfWork.IUnitOfWork;

namespace RoundTableMeeting.Domain.Tests;

public sealed class FrameworkContractTests
{
    [Fact]
    public void BaseEntityGeneratesUniqueProtectedIds()
    {
        var first = new TestEntity();
        var second = new TestEntity();
        Assert.NotEqual(Guid.Empty, first.ID);
        Assert.NotEqual(first.ID, second.ID);
        Assert.False(typeof(BaseEntity).GetProperty(nameof(BaseEntity.ID))?.SetMethod?.IsPublic ?? false);
    }

    [Fact]
    public void UnitOfWorkContractIsFrozen()
    {
        var methods = typeof(ProjectUnitOfWork).GetMethods().ToDictionary(method => method.Name);
        Assert.Equal(typeof(void), methods["RegisterAdd"].ReturnType);
        Assert.Equal(typeof(void), methods["RegisterEdit"].ReturnType);
        Assert.Equal(typeof(void), methods["RegisterDelete"].ReturnType);
        Assert.Equal(typeof(Task<int>), methods["CommitAsync"].ReturnType);
        Assert.Equal(typeof(IEntity<Guid>), methods["RegisterAdd"].GetParameters()[0].ParameterType);
        Assert.Single(typeof(ConcurrencyConflictException).GetConstructors());
    }

    [Fact]
    public void ConcurrencyConflictDoesNotReferenceEfCore()
    {
        Assert.DoesNotContain(typeof(ConcurrencyConflictException).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.Contains("EntityFrameworkCore", StringComparison.Ordinal) == true);
    }

    private sealed class TestEntity : BaseEntity;
}
