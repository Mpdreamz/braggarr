using System.Buffers.Binary;

namespace Braggarr.Services;

/// <summary>How a video is shaped: its picture as shown (a phone video stored sideways is turned upright) and how long it runs.</summary>
public sealed record VideoInfo(int Width, int Height, TimeSpan Duration)
{
	/// <summary>Taller than wide: a phone held upright, a Short.</summary>
	public bool IsVertical => Height > Width;
}

/// <summary>
/// Reads an MP4's shape from its boxes: the duration from <c>moov/mvhd</c>, the size from the first <c>moov/trak/tkhd</c> with a
/// picture, turned by a quarter when its matrix says the picture is shown rotated. Nothing is decoded, and nothing is needed but
/// the bytes.
/// </summary>
public static class Mp4
{
	public static VideoInfo? Read(ReadOnlySpan<byte> file)
	{
		var moov = Find(file, "moov");
		if (moov.IsEmpty)
		{
			return null;
		}
		var mvhd = Find(moov, "mvhd");
		if (mvhd.Length < 20)
		{
			return null;
		}
		TimeSpan duration;
		if (mvhd[0] == 1)
		{
			if (mvhd.Length < 32)
			{
				return null;
			}
			var scale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[20..]);
			duration = scale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt64BigEndian(mvhd[24..]) / (double)scale);
		}
		else
		{
			var scale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[12..]);
			duration = scale == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt32BigEndian(mvhd[16..]) / (double)scale);
		}

		foreach (var trak in All(moov, "trak"))
		{
			var tkhd = Find(trak, "tkhd");
			// After version and flags: times, track id and duration (32 bytes in version 1, 20 in 0), then 16 bytes, the matrix
			// (36) and the width and height in 16.16 fixed point.
			var matrix = tkhd.IsEmpty ? -1 : (tkhd[0] == 1 ? 4 + 32 : 4 + 20) + 16;
			if (matrix < 0 || tkhd.Length < matrix + 44)
			{
				continue;
			}
			var width = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[(matrix + 36)..]) >> 16);
			var height = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[(matrix + 40)..]) >> 16);
			if (width == 0 || height == 0)
			{
				continue; // sound
			}
			var a = BinaryPrimitives.ReadInt32BigEndian(tkhd[matrix..]);
			return a == 0 ? new VideoInfo(height, width, duration) : new VideoInfo(width, height, duration);
		}
		return null;
	}

	private static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> boxes, string type)
	{
		foreach (var (name, start, length) in Boxes(boxes))
		{
			if (name == type)
			{
				return boxes.Slice(start, length);
			}
		}
		return [];
	}

	private static List<byte[]> All(ReadOnlySpan<byte> boxes, string type)
	{
		var found = new List<byte[]>();
		foreach (var (name, start, length) in Boxes(boxes))
		{
			if (name == type)
			{
				found.Add(boxes.Slice(start, length).ToArray());
			}
		}
		return found;
	}

	/// <summary>Each box's type and where its content is; stops at the first box that does not fit.</summary>
	private static List<(string Type, int Start, int Length)> Boxes(ReadOnlySpan<byte> data)
	{
		var boxes = new List<(string, int, int)>();
		var offset = 0;
		while (offset + 8 <= data.Length)
		{
			long size = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
			var type = System.Text.Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
			var header = 8;
			if (size == 1)
			{
				if (offset + 16 > data.Length)
				{
					break;
				}
				size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 8)..]);
				header = 16;
			}
			else if (size == 0)
			{
				size = data.Length - offset;
			}
			if (size < header || offset + size > data.Length)
			{
				break;
			}
			boxes.Add((type, offset + header, (int)size - header));
			offset += (int)size;
		}
		return boxes;
	}
}
