using System;
using System.Security.Cryptography;
using System.Text;

namespace RoundTableMeeting.Application.AgentCollaboration;

public static class AgentReplyIdempotencyKeyFactory
{
	private static readonly Guid Namespace = Guid.Parse("8b8b9e45-72bf-46a0-a9e6-6e0f43f317c0");

	public static Guid Create(Guid taskId)
	{
		if (taskId == Guid.Empty)
		{
			throw new ArgumentException("Task is required.", "taskId");
		}
		return Create(Namespace, $"agent-reply:{taskId:D}".ToLowerInvariant());
	}

	private static Guid Create(Guid namespaceId, string name)
	{
		byte[] array = namespaceId.ToByteArray();
		SwapGuidByteOrder(array);
		byte[] bytes = Encoding.UTF8.GetBytes(name);
		byte[] array2 = new byte[array.Length + bytes.Length];
		array.CopyTo(array2, 0);
		bytes.CopyTo(array2, array.Length);
		byte[] array3 = SHA1.HashData(array2);
		array3[6] = (byte)((array3[6] & 0xF) | 0x50);
		array3[8] = (byte)((array3[8] & 0x3F) | 0x80);
		byte[] subArray = array3[..16];
		SwapGuidByteOrder(subArray);
		return new Guid(subArray);
	}

	private static void SwapGuidByteOrder(Span<byte> bytes)
	{
		ref byte reference = ref bytes[0];
		ref byte reference2 = ref bytes[3];
		byte b = bytes[3];
		byte b2 = bytes[0];
		reference = b;
		reference2 = b2;
		reference = ref bytes[1];
		ref byte reference3 = ref bytes[2];
		b2 = bytes[2];
		b = bytes[1];
		reference = b2;
		reference3 = b;
		reference = ref bytes[4];
		ref byte reference4 = ref bytes[5];
		b = bytes[5];
		b2 = bytes[4];
		reference = b;
		reference4 = b2;
		reference = ref bytes[6];
		ref byte reference5 = ref bytes[7];
		b2 = bytes[7];
		b = bytes[6];
		reference = b2;
		reference5 = b;
	}
}
