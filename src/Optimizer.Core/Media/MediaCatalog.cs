using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Optimizer.Core.Media;

/// <summary>One Windows image (.esd) of Microsoft's media catalog.</summary>
public sealed record EsdFile(string FileName, string LanguageCode, string Language, string Edition, string Architecture, long Size, string Sha256, Uri Url)
{
    /// <summary>The build, like "26300.9711": the first two numbers of the file name.</summary>
    public string Build { get; } = BuildOf(FileName);

    /// <summary>Consumer media (Home, Pro, Education); business media is volume licensing only.</summary>
    public bool IsConsumer => FileName.Contains("CLIENTCONSUMER", StringComparison.OrdinalIgnoreCase);

    private static string BuildOf(string fileName)
    {
        var match = Regex.Match(fileName, @"^\d+\.\d+");
        return match.Success ? match.Value : "";
    }
}

/// <summary>The products.cab that the catalog search points to: where to get it, how big, and its SHA-256.</summary>
public sealed record CabLocation(Uri Url, long Size, byte[] Sha256);

/// <summary>
/// Microsoft's public media catalog (the one the Media Creation Tool reads): a search answer names products.cab, which holds
/// products.xml with every Windows image per language, edition and architecture. This class only parses; downloading and
/// extracting happen elsewhere.
/// </summary>
public static class MediaCatalog
{
    public const string SearchUrl = "https://fe3.delivery.mp.microsoft.com/UpdateMetadataService/updates/search/v1/bydeviceinfo";

    /// <summary>The editions the install media page offers, in this order.</summary>
    public static readonly IReadOnlyList<string> OfferedEditions =
    [
        "Core", "CoreN", "Professional", "ProfessionalN", "Education", "EducationN",
        "ProfessionalEducation", "ProfessionalEducationN", "ProfessionalWorkstation", "ProfessionalWorkstationN",
    ];

    /// <summary>The body of the search request; <paramref name="arch"/> is "amd64" or "arm64".</summary>
    public static string RequestBody(string arch) =>
        $$"""{"Products":"PN=Windows.Products.Cab.{{arch}}&V=0.0.0.0","DeviceAttributes":"DUScan=1;OSVersion=10.0.26100.1"}""";

    /// <summary>The products.cab entry of a search answer, or null when it has none or the digest is not a SHA-256.</summary>
    public static CabLocation? ParseSearchAnswer(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var update in doc.RootElement.EnumerateArray())
            {
                if (!update.TryGetProperty("FileLocations", out var locations) || locations.ValueKind != JsonValueKind.Array) continue;
                foreach (var location in locations.EnumerateArray())
                {
                    if (!string.Equals(Text(location, "FileName"), "products.cab", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Uri.TryCreate(Text(location, "Url"), UriKind.Absolute, out var url)) return null;
                    if (!location.TryGetProperty("Size", out var size) || !size.TryGetInt64(out var bytes)) return null;
                    var digest = new byte[32];
                    if (Text(location, "Digest") is not { } text || !Convert.TryFromBase64String(text, digest, out var written) || written != 32) return null;
                    return new CabLocation(url, bytes, digest);
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Every File entry of products.xml under any root; throws <see cref="FormatException"/> on bad XML or a missing element.</summary>
    public static IReadOnlyList<EsdFile> ParseProducts(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            var result = new List<EsdFile>();
            foreach (var file in doc.Descendants("File"))
            {
                var url = Required(file, "FilePath");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new FormatException($"Bad FilePath: {url}");
                if (!long.TryParse(Required(file, "Size"), out var size)) throw new FormatException("Bad Size");
                result.Add(new EsdFile(
                    Required(file, "FileName"), Required(file, "LanguageCode"), Required(file, "Language"), Required(file, "Edition"),
                    Required(file, "Architecture"), size, Required(file, "Sha256").ToLowerInvariant(), uri));
            }
            return result;
        }
        catch (XmlException ex)
        {
            throw new FormatException("products.xml is not valid XML.", ex);
        }
    }

    private static string Required(XElement file, string name)
    {
        var value = file.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? throw new FormatException($"A File entry has no {name}.") : value;
    }

    /// <summary>The offered editions that a consumer file exists for in this language and architecture, in offer order.</summary>
    public static IReadOnlyList<string> Editions(IReadOnlyList<EsdFile> files, string languageCode, string architecture) =>
        OfferedEditions.Where(e => files.Any(f => Matches(f, languageCode, architecture, e))).ToList();

    /// <summary>The consumer file with the highest build for this language, architecture and edition, or null.</summary>
    public static EsdFile? Pick(IReadOnlyList<EsdFile> files, string languageCode, string architecture, string edition) =>
        files.Where(f => Matches(f, languageCode, architecture, edition))
             .OrderByDescending(f => Version.TryParse(f.Build, out var v) ? v : new Version())
             .FirstOrDefault();

    private static bool Matches(EsdFile f, string languageCode, string architecture, string edition) =>
        f.IsConsumer
        && string.Equals(f.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(f.Architecture, architecture, StringComparison.OrdinalIgnoreCase)
        && string.Equals(f.Edition, edition, StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog's spelling of a processor architecture.</summary>
    public static string CatalogArchitecture(Architecture a) => a switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "ARM64",
        _ => throw new NotSupportedException($"No Windows media for {a}."),
    };
}
