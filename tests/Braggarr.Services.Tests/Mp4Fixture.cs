using System.Buffers.Binary;
using System.Text;

namespace Braggarr.Services.Tests;

/// <summary>
/// The smallest MP4 that has a shape: <c>ftyp</c>, then <c>moov</c> with <c>mvhd</c> (the duration) and, optionally a sound
/// track first, a video track whose <c>tkhd</c> holds the stored size and a matrix that may turn it a quarter. No pictures.
/// </summary>
public static class Mp4Fixture
{
	public static byte[] Make(int width, int height, double seconds, bool rotated = false, bool soundFirst = false, int padding = 0)
	{
		const uint scale = 1000;
		var mvhd = new byte[100];
		BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), scale);
		BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), (uint)(seconds * scale));

		var traks = new List<byte[]>();
		if (soundFirst)
		{
			traks.Add(Box("trak", Box("tkhd", Tkhd(0, 0, rotated: false))));
		}
		traks.Add(Box("trak", Box("tkhd", Tkhd(width, height, rotated))));
		var moov = Box("moov", [.. Box("mvhd", mvhd), .. traks.SelectMany(t => t)]);
		return [.. Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0isom")), .. moov, .. Box("mdat", new byte[padding])];
	}

	private static byte[] Tkhd(int width, int height, bool rotated)
	{
		var tkhd = new byte[84];
		var matrix = tkhd.AsSpan(40);
		// Identity, or turned a quarter: a = 0, b = 1, c = -1, d = 0.
		BinaryPrimitives.WriteInt32BigEndian(matrix, rotated ? 0 : 0x00010000);
		BinaryPrimitives.WriteInt32BigEndian(matrix[4..], rotated ? 0x00010000 : 0);
		BinaryPrimitives.WriteInt32BigEndian(matrix[12..], rotated ? unchecked((int)0xFFFF0000) : 0);
		BinaryPrimitives.WriteInt32BigEndian(matrix[16..], rotated ? 0 : 0x00010000);
		BinaryPrimitives.WriteInt32BigEndian(matrix[32..], 0x40000000);
		BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), (uint)width << 16);
		BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), (uint)height << 16);
		return tkhd;
	}

	private static byte[] Box(string type, byte[] content)
	{
		var box = new byte[8 + content.Length];
		BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
		Encoding.ASCII.GetBytes(type, box.AsSpan(4));
		content.CopyTo(box, 8);
		return box;
	}
}
