using Materal.TTA.Common;

namespace RoundTableMeeting.Domain.Entities;

/// <summary>
/// 为领域实体提供统一的 GUID 主键
/// </summary>
public abstract class BaseEntity : IEntity<Guid>
{
    /// <summary>
    /// 获取实体标识
    /// </summary>
    public Guid ID { get; protected set; } = Guid.NewGuid();
}
