using System;

namespace RoundTableMeeting.Abstraction.Services;

public readonly record struct McpTransportSessionKey
{
	private readonly Guid value;

	private McpTransportSessionKey(Guid value)
	{
		this.value = value;
	}

	public bool IsEmpty => value == Guid.Empty;

	public string ToStorageBinding()
	{
		if (value == Guid.Empty)
		{
			throw new InvalidOperationException("An empty MCP session key cannot be persisted.");
		}

		return value.ToString("N");
	}

	public static McpTransportSessionKey FromDigest(ReadOnlySpan<byte> digest)
	{
		if (digest.Length < 16)
		{
			throw new ArgumentException("A session digest must contain at least 16 bytes.", nameof(digest));
		}

		return new McpTransportSessionKey(new Guid(digest[..16]));
	}

	public override string ToString() => "mcp-session-key";
}
