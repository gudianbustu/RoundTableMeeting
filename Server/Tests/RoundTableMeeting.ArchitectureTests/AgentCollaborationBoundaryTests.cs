using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using Xunit;

namespace RoundTableMeeting.ArchitectureTests;

public sealed class AgentCollaborationBoundaryTests
{
	[Fact]
	public void CoreAgentCollaborationLayersDoNotReferenceMcpSdkOrAspNetCore()
	{
		string[] array = new string[2] { "RoundTableMeeting.Abstraction", "RoundTableMeeting.Application" };
		foreach (string assemblyString in array)
		{
			AssemblyName[] referencedAssemblies = Assembly.Load(assemblyString).GetReferencedAssemblies();
			Assert.DoesNotContain((IEnumerable<AssemblyName>)referencedAssemblies, (Predicate<AssemblyName>)((AssemblyName reference) => reference.Name?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) ?? false));
			Assert.DoesNotContain((IEnumerable<AssemblyName>)referencedAssemblies, (Predicate<AssemblyName>)((AssemblyName reference) => reference.Name == "Microsoft.AspNetCore.App"));
		}
	}

	[Fact]
	public void OnlyTheWebAdapterReadsTheMcpProtocolSessionIdentifier()
	{
		string text = FindRoot();
		string directory = Path.Combine(text, "Server", "RoundTableMeeting.Abstraction");
		string directory2 = Path.Combine(text, "Server", "RoundTableMeeting.Application");
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = text;
		buffer[1] = "Server";
		buffer[2] = "RoundTableMeeting";
		buffer[3] = "AgentCollaboration";
		buffer[4] = "McpTransportSessionAccessor.cs";
		string actualString = File.ReadAllText(Path.Combine(buffer));
		Assert.DoesNotContain("ModelContextProtocol", ReadAllCSharp(directory), StringComparison.Ordinal);
		Assert.DoesNotContain("ModelContextProtocol", ReadAllCSharp(directory2), StringComparison.Ordinal);
		Assert.Contains("session.SessionId", actualString, StringComparison.Ordinal);
		Assert.Contains("SHA256.HashData", actualString, StringComparison.Ordinal);
	}

	[Fact]
	public void AgentRepositoryUsesTheExistingScopedAutoRegistrationContract()
	{
		Assert.Contains((IEnumerable<Type>)typeof(AgentCollaborationRepository).GetInterfaces(), (Predicate<Type>)((Type type) => type == typeof(IScopedDependency<IAgentCollaborationRepository>)));
	}

	private static string ReadAllCSharp(string directory)
	{
		return string.Join(Environment.NewLine, Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
	}

	private static string FindRoot()
	{
		DirectoryInfo? directoryInfo = new DirectoryInfo(AppContext.BaseDirectory);
		while (directoryInfo != null && !File.Exists(Path.Combine(directoryInfo.FullName, "AGENTS.md")))
		{
			directoryInfo = directoryInfo.Parent;
		}
		return directoryInfo?.FullName ?? throw new DirectoryNotFoundException("Repository root could not be located.");
	}
}
