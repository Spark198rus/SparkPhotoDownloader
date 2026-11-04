using System.Buffers.Binary;
using System.Text;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// Reads Canon's CRW files: the CIFF ("Camera Image File Format") container used by the
/// EOS D30, D60, 10D and 300D and the PowerShot G1–G6, Pro1 and S-series before CR2.
/// <para>
/// CIFF is not TIFF. It is a tree of "heaps", each ending in a pointer to a table of
/// 10-byte records (tag, size, offset). The raw sensor data comes first and the JPEG
/// preview the camera made sits after it, near the end of the file; the metadata lives in
/// its own sub-heaps. The generic preview scan looks for JPEG markers from the front, and in
/// a CRW it finds coincidental markers inside the sensor data long before the real preview,
/// which is why every CRW used to show the same grey noise.
/// </para>
/// </summary>
public static class CiffReader
{
    /// <summary>What a CRW says about itself.</summary>
    public sealed class CiffInfo
    {
        /// <summary>The largest JPEG the camera embedded (tag 0x2007, else 0x2008).</summary>
        public long PreviewOffset { get; set; }
        public int PreviewLength { get; set; }

        /// <summary>Capture time as the camera clock showed it.</summary>
        public DateTime? CaptureDate { get; set; }

        public string? Make { get; set; }
        public string? Model { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }

        /// <summary>Exif-style orientation (1, 3, 6 or 8) from the image rotation.</summary>
        public int Orientation { get; set; } = 1;
    }

    private const ushort TagRawData = 0x2005;
    private const ushort TagJpegImage = 0x2007;
    private const ushort TagJpegThumbnail = 0x2008;
    private const ushort TagMakeModel = 0x080a;
    private const ushort TagCapturedTime = 0x180e;
    private const ushort TagImageInfo = 0x1810;

    /// <summary>Directory records whose value is stored in the record itself, not the heap.</summary>
    private const ushort LocationInRecord = 0x4000;

    /// <summary>Guards against a corrupt file sending the walk round in circles.</summary>
    private const int MaxDepth = 8;
    private const int MaxEntries = 2048;

    public static bool IsCiff(string extension) =>
        string.Equals(extension, "crw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse the heap tree. Returns null when the file is not a CIFF file.</summary>
    public static CiffInfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            return Read(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static CiffInfo? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[14];
        if (stream.Length < 26 || stream.Read(header) != header.Length)
            return null;

        // Every CRW in the wild is little-endian ("II"); "MM" is in the spec but unused.
        if (header[0] != 'I' || header[1] != 'I' ||
            !header[6..14].SequenceEqual("HEAPCCDR"u8))
        {
            return null;
        }

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]);
        if (headerLength >= stream.Length)
            return null;

        var info = new CiffInfo();
        int jpegBest = 0;
        WalkHeap(stream, headerLength, stream.Length - headerLength, 0, info, ref jpegBest);
        return info;
    }

    /// <summary>Read the embedded JPEG, or null when the file has none.</summary>
    public static byte[]? ReadPreview(string path, CiffInfo info)
    {
        if (info.PreviewLength <= 0)
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            stream.Seek(info.PreviewOffset, SeekOrigin.Begin);

            var data = new byte[info.PreviewLength];
            stream.ReadExactly(data);

            // A JPEG starts FF D8; anything else means the offsets were not what they seemed.
            return data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8 ? data : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WalkHeap(
        Stream stream, long start, long length, int depth, CiffInfo info, ref int jpegBest)
    {
        if (depth > MaxDepth || length < 6 || start + length > stream.Length)
            return;

        // The last four bytes of a heap give where its record table starts, relative to it.
        var tableOffset = ReadUInt32(stream, start + length - 4);
        if (tableOffset is null || tableOffset.Value >= length)
            return;

        var tableStart = start + tableOffset.Value;
        var count = ReadUInt16(stream, tableStart);
        if (count is null || count.Value > MaxEntries)
            return;

        Span<byte> record = stackalloc byte[10];

        for (var i = 0; i < count.Value; i++)
        {
            stream.Seek(tableStart + 2 + i * 10L, SeekOrigin.Begin);
            if (stream.Read(record) != record.Length)
                return;

            var tag = BinaryPrimitives.ReadUInt16LittleEndian(record);
            if ((tag & 0xC000) == LocationInRecord)
                continue;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[6..]);
            var id = (ushort)(tag & 0x3FFF);
            var type = (tag >> 8) & 0x38;
            var absolute = start + offset;

            if (offset > length || size > length - offset)
                continue;

            // 0x28 and 0x30 mark records that are heaps themselves.
            if (type is 0x28 or 0x30)
            {
                WalkHeap(stream, absolute, size, depth + 1, info, ref jpegBest);
                continue;
            }

            switch (id)
            {
                case TagJpegImage or TagJpegThumbnail when size > jpegBest && size <= int.MaxValue:
                    // 0x2007 is the display preview; 0x2008 a postage stamp. Keep the bigger.
                    jpegBest = (int)size;
                    info.PreviewOffset = absolute;
                    info.PreviewLength = (int)size;
                    break;

                case TagMakeModel:
                    ReadMakeModel(stream, absolute, size, info);
                    break;

                case TagCapturedTime when size >= 4:
                    // Seconds since 1970 on the camera's own clock, which has no time zone:
                    // read it as wall-clock time, the way the camera displayed it.
                    if (ReadUInt32(stream, absolute) is { } seconds && seconds > 0)
                    {
                        info.CaptureDate = DateTime.SpecifyKind(
                            DateTime.UnixEpoch.AddSeconds(seconds), DateTimeKind.Unspecified);
                    }
                    break;

                case TagImageInfo when size >= 16:
                    ReadImageInfo(stream, absolute, info);
                    break;

                case TagRawData:
                    break;
            }
        }
    }

    /// <summary>"Canon\0Canon EOS 10D\0" — make and model, NUL-separated.</summary>
    private static void ReadMakeModel(Stream stream, long offset, uint size, CiffInfo info)
    {
        var length = (int)Math.Min(size, 64);
        var buffer = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        if (stream.Read(buffer) != length)
            return;

        var parts = Encoding.ASCII.GetString(buffer).Split('\0');
        if (parts.Length > 0 && parts[0].Length > 0)
            info.Make = parts[0].Trim();
        if (parts.Length > 1 && parts[1].Length > 0)
            info.Model = parts[1].Trim();
    }

    /// <summary>Image width, height, pixel aspect ratio and rotation in degrees.</summary>
    private static void ReadImageInfo(Stream stream, long offset, CiffInfo info)
    {
        Span<byte> buffer = stackalloc byte[16];
        stream.Seek(offset, SeekOrigin.Begin);
        if (stream.Read(buffer) != buffer.Length)
            return;

        var width = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var height = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        var rotation = BinaryPrimitives.ReadInt32LittleEndian(buffer[12..]);

        if (width is > 0 and < 100_000 && height is > 0 and < 100_000)
        {
            info.Width = width;
            info.Height = height;
        }

        // The rotation is clockwise and says how the shot should be turned for display.
        var degrees = ((rotation % 360) + 360) % 360;
        info.Orientation = degrees switch
        {
            90 => 6,
            180 => 3,
            270 => 8,
            _ => 1,
        };
    }

    private static uint? ReadUInt32(Stream stream, long offset)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.Seek(offset, SeekOrigin.Begin);
        return stream.Read(buffer) == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(buffer) : null;
    }

    private static ushort? ReadUInt16(Stream stream, long offset)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.Seek(offset, SeekOrigin.Begin);
        return stream.Read(buffer) == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(buffer) : null;
    }
}
