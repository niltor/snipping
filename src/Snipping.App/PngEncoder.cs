using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Snipping.App;

/// <summary>
/// Encodes 32-bit screenshots as lossless PNG with adaptive row filters.
/// GDI+'s default PNG encoder is intentionally replaced here because it does
/// not expose a compression setting and often produces unnecessarily large
/// screenshot files.
/// </summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] Encode(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The bitmap must have a positive size.", nameof(bitmap));

        var rowBytes = checked(width * 4);
        using var raw = new MemoryStream(checked((rowBytes + 1) * height));
        var current = new byte[rowBytes];
        var previous = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), current, 0, rowBytes);
                ConvertBgraToRgba(current);

                var bestFilter = ChooseBestFilter(current, previous, candidate, best);
                raw.WriteByte(bestFilter);
                raw.Write(best);
                Buffer.BlockCopy(current, 0, previous, 0, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[0..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..8], (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filters
        header[12] = 0; // no interlace
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static byte ChooseBestFilter(byte[] current, byte[] previous, byte[] candidate, byte[] best)
    {
        long bestScore = long.MaxValue;
        byte bestFilter = 0;

        for (byte filter = 0; filter <= 4; filter++)
        {
            var score = ApplyFilter(filter, current, previous, candidate);
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestFilter = filter;
            Buffer.BlockCopy(candidate, 0, best, 0, current.Length);
        }

        return bestFilter;
    }

    private static long ApplyFilter(byte filter, byte[] current, byte[] previous, byte[] output)
    {
        long score = 0;
        for (var i = 0; i < current.Length; i++)
        {
            var left = i >= 4 ? current[i - 4] : (byte)0;
            var up = previous[i];
            var upperLeft = i >= 4 ? previous[i - 4] : (byte)0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upperLeft),
                _ => 0
            };

            var value = (byte)(current[i] - predictor);
            output[i] = value;
            score += Math.Abs(value < 128 ? value : value - 256);
        }

        return score;
    }

    private static byte Paeth(byte left, byte up, byte upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left
            : upDistance <= upperLeftDistance ? up : upperLeft;
    }

    private static void ConvertBgraToRgba(byte[] row)
    {
        for (var i = 0; i < row.Length; i += 4)
        {
            (row[i], row[i + 2]) = (row[i + 2], row[i]);
        }
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = 0xffffffffu;
        foreach (var value in typeBytes)
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
        foreach (var value in data)
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
        crc ^= 0xffffffffu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : 0xedb88320u ^ (value >> 1);
            table[i] = value;
        }

        return table;
    }
}
