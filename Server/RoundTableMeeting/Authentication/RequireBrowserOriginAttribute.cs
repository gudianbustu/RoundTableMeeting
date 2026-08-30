namespace RoundTableMeeting.Authentication;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireBrowserOriginAttribute(bool requireJsonBody = false) : Attribute
{
    public bool RequireJsonBody { get; } = requireJsonBody;
}
