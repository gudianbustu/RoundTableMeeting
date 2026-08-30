namespace RoundTableMeeting.Authentication;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireBrowserOriginAttribute(bool requireJsonBody = false) : Attribute
{
    public bool RequireJsonBody { get; } = requireJsonBody;
}
