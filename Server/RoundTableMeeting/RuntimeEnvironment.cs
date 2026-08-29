using RoundTableMeeting.Abstraction.Runtime;

namespace RoundTableMeeting;

public sealed class RuntimeEnvironment(string environmentName) : IRuntimeEnvironment
{
    public string EnvironmentName { get; } = environmentName;
}
