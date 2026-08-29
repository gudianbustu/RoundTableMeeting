using Materal.TTA.Common;

namespace RoundTableMeeting.Domain.Entities;

public abstract class BaseEntity : IEntity<Guid>
{
    public Guid ID { get; protected set; } = Guid.NewGuid();
}
