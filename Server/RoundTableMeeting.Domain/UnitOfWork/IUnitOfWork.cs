using Materal.TTA.Common;

namespace RoundTableMeeting.Domain.UnitOfWork;

/// <summary>
/// 汇总实体变更并通过一次提交持久化
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// 登记实体新增
    /// </summary>
    void RegisterAdd(IEntity<Guid> entity);

    /// <summary>
    /// 登记实体修改
    /// </summary>
    void RegisterEdit(IEntity<Guid> entity);

    /// <summary>
    /// 登记实体删除
    /// </summary>
    void RegisterDelete(IEntity<Guid> entity);

    /// <summary>
    /// 提交当前工作单元中的所有变更
    /// </summary>
    Task<int> CommitAsync(CancellationToken cancellationToken = default);
}
