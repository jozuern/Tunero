using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Optimizer.Core.Media;

/// <summary>
/// One image of a WIM or ESD as its XML metadata names it. <paramref name="Edition"/> is the EditionID ("Professional");
/// <paramref name="Architecture"/> is spelled as DISM does ("x64", "ARM64"); <paramref name="Version"/> is
/// "10.0.26300.9711" and <paramref name="Build"/> its third number.
/// </summary>
public sealed record WimImageInfo(int Index, string Name, string? Edition, string? Architecture, string? Version, int? Build,
    string? DefaultLanguage, IReadOnlyList<string> Languages);

/// <summary>
/// Reads the image list of a WIM or ESD straight from the file, without DISM: the header names the XML resource (offset 72:
/// size and flags, 80: offset, 88: original size), which WIM and ESD files keep uncompressed as UTF-16 text. Seen in the 26H2
/// ESD and in a WIM exported from it (PCO-Test, 2026-10-20). Used to show what an own ISO holds before a build; the build
/// itself checks every image again with DISM.
/// </summary>
public static class WimMetadata
{
    private const int HeaderSize = 208;
    private const int MaxXmlBytes = 16 << 20;
    private const byte CompressedFlag = 0x04;

    /// <summary>The images of the WIM or ESD in <paramref name="stream"/>, or null when it is not one or its metadata cannot be read.</summary>
    public static IReadOnlyList<WimImageInfo>? Read(Stream stream)
    {
        try
        {
            var header = new byte[HeaderSize];
            stream.Position = 0;
            if (!ReadExactly(stream, header) || !header.AsSpan(0, 8).SequenceEqual("MSWIM\0\0\0"u8) || BitConverter.ToUInt32(header, 8) != HeaderSize)
                return null;
            var sizeAndFlags = BitConverter.ToUInt64(header, 72);
            var size = (long)(sizeAndFlags & 0x00FF_FFFF_FFFF_FFFF);
            var flags = (byte)(sizeAndFlags >> 56);
            var offset = BitConverter.ToInt64(header, 80);
            var original = BitConverter.ToInt64(header, 88);
            if ((flags & CompressedFlag) != 0 || size != original || size < 2 || size > MaxXmlBytes || offset < HeaderSize || offset > stream.Length - size)
                return null;
            var xml = new byte[size];
            stream.Position = offset;
            if (!ReadExactly(stream, xml)) return null;
            var text = Encoding.Unicode.GetString(xml);
            return Parse(text.TrimStart('﻿'));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The images of the XML metadata, or null when it is not valid XML.</summary>
    public static IReadOnlyList<WimImageInfo>? Parse(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            var images = new List<WimImageInfo>();
            foreach (var image in doc.Root?.Elements("IMAGE") ?? [])
            {
                if (!int.TryParse((string?)image.Attribute("INDEX"), NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
                var windows = image.Element("WINDOWS");
                var version = windows?.Element("VERSION");
                int? Number(string name) =>
                    int.TryParse(version?.Element(name)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
                var (major, minor, build, sp) = (Number("MAJOR"), Number("MINOR"), Number("BUILD"), Number("SPBUILD"));
                var text = major is { } a && minor is { } b && build is { } c ? $"{a}.{b}.{c}" + (sp is { } d ? $".{d}" : "") : null;
                var languages = windows?.Element("LANGUAGES");
                images.Add(new WimImageInfo(
                    index,
                    image.Element("NAME")?.Value ?? "",
                    Value(windows?.Element("EDITIONID")),
                    Architecture(windows?.Element("ARCH")?.Value),
                    text,
                    build,
                    Value(languages?.Element("DEFAULT")),
                    languages?.Elements("LANGUAGE").Select(l => l.Value.Trim()).Where(l => l.Length > 0).ToList() ?? []));
            }
            return images;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Value(XElement? element) => element?.Value.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>PROCESSOR_ARCHITECTURE values: 0 x86, 5 ARM, 9 x64 (AMD64), 12 ARM64.</summary>
    private static string? Architecture(string? code) => code?.Trim() switch
    {
        "0" => "x86",
        "5" => "ARM",
        "9" => "x64",
        "12" => "ARM64",
        null or "" => null,
        var other => other,
    };

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        int n;
        while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
        return read == buffer.Length;
    }
}
