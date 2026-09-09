using System;
using RoundTableMeeting.Application.AgentCollaboration;
using Xunit;

namespace RoundTableMeeting.Application.Tests;

public sealed class AgentReplyIdempotencyKeyFactoryTests
{
	[Fact]
	public void FixedUuidV5VectorMatchesTheFrozenContract()
	{
		Guid taskId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
		Assert.Equal(Guid.Parse("3502c088-f79d-5fb8-86c8-97b17c43cdc1"), AgentReplyIdempotencyKeyFactory.Create(taskId));
	}

	[Fact]
	public void UuidV5IsStableAndRejectsEmptyTaskIds()
	{
		Guid taskId = Guid.NewGuid();
		Assert.Equal(AgentReplyIdempotencyKeyFactory.Create(taskId), AgentReplyIdempotencyKeyFactory.Create(taskId));
		Assert.NotEqual(AgentReplyIdempotencyKeyFactory.Create(taskId), AgentReplyIdempotencyKeyFactory.Create(Guid.NewGuid()));
		Assert.Throws<ArgumentException>(() => AgentReplyIdempotencyKeyFactory.Create(Guid.Empty));
	}
}
