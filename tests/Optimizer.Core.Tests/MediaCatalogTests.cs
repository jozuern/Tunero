using System.Runtime.InteropServices;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

public class MediaCatalogTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", name));

    private static IReadOnlyList<EsdFile> Products() => MediaCatalog.ParseProducts(Fixture("products-sample.xml"));

    [Fact]
    public void RequestBodyNamesTheProductsCab() =>
        Assert.Equal("""{"Products":"PN=Windows.Products.Cab.amd64&V=0.0.0.0","DeviceAttributes":"DUScan=1;OSVersion=10.0.26100.1"}""", MediaCatalog.RequestBody("amd64"));

    [Fact]
    public void RequestBodyTakesTheArchitecture() => Assert.Contains("Windows.Products.Cab.arm64", MediaCatalog.RequestBody("arm64"));

    [Fact]
    public void SearchAnswerGivesTheCab()
    {
        var cab = MediaCatalog.ParseSearchAnswer(Fixture("search-answer.json"));
        Assert.NotNull(cab);
        Assert.Equal("tlu.dl.delivery.mp.microsoft.com", cab.Url.Host);
        Assert.Equal(43203, cab.Size);
        Assert.Equal(32, cab.Sha256.Length);
        Assert.Equal(Convert.FromBase64String("ND/Kj32kr+r+ksEme3iHd4z9SythvQrNYeZOqBBMbgA="), cab.Sha256);
    }

    [Fact]
    public void SearchAnswerWithoutProductsCabIsNull()
    {
        Assert.Null(MediaCatalog.ParseSearchAnswer("[]"));
        Assert.Null(MediaCatalog.ParseSearchAnswer("""[{"FileLocations":[{"FileName":"other.cab","Size":1,"Digest":"ND/Kj32kr+r+ksEme3iHd4z9SythvQrNYeZOqBBMbgA=","Url":"http://x.example/a"}]}]"""));
    }

    [Fact]
    public void SearchAnswerWithABadDigestIsNull()
    {
        Assert.Null(MediaCatalog.ParseSearchAnswer("""[{"FileLocations":[{"FileName":"products.cab","Size":1,"Digest":"AAAA","Url":"http://x.example/a"}]}]"""));
        Assert.Null(MediaCatalog.ParseSearchAnswer("not json"));
    }

    [Fact]
    public void ProductsParseAllFields()
    {
        var first = Products()[0];
        Assert.Equal("26300.9711.261011-1601.26h2_ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd", first.FileName);
        Assert.Equal("en-us", first.LanguageCode);
        Assert.Equal("English (United States)", first.Language);
        Assert.Equal("Professional", first.Edition);
        Assert.Equal("x64", first.Architecture);
        Assert.Equal(6338662255, first.Size);
        Assert.Equal("a7217f5e14eaf61b967b740d289a95093f430b116e1b64fbe41fd98dcd19f139", first.Sha256);
        Assert.Equal(new Uri("http://dl.delivery.mp.microsoft.com/filestreamingservice/files/1fa2fb16-757a-4c59-8371-1924cdf38384/26300.9711.261011-1601.26h2_ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd"), first.Url);
        Assert.Equal("26300.9711", first.Build);
        Assert.True(first.IsConsumer);
        Assert.Equal(9, Products().Count);
        Assert.False(Products().Single(f => f.Edition == "Enterprise").IsConsumer);
    }

    [Fact]
    public void ProductsParseWithAPublishedMediaRoot()
    {
        const string xml = """
            <PublishedMedia id="" release=""><Files><File id="">
              <FileName>26200.6584.250915-1905.25h2_ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd</FileName>
              <LanguageCode>en-us</LanguageCode><Language>English (United States)</Language><Edition>Core</Edition>
              <Architecture>x64</Architecture><Size>5</Size><Sha256>AB</Sha256><FilePath>http://dl.example/a.esd</FilePath>
            </File></Files></PublishedMedia>
            """;
        var file = Assert.Single(MediaCatalog.ParseProducts(xml));
        Assert.Equal("26200.6584", file.Build);
        Assert.Equal("ab", file.Sha256);
    }

    [Fact]
    public void EditionsAreTheOfferedOnesOfTheConsumerFile()
    {
        Assert.Equal(["Core", "Professional"], MediaCatalog.Editions(Products(), "en-us", "x64"));
        Assert.Equal(["Core", "Professional"], MediaCatalog.Editions(Products(), "de-de", "x64"));
    }

    [Fact]
    public void PickTakesTheHighestBuild()
    {
        var file = MediaCatalog.Pick(Products(), "en-us", "x64", "Professional");
        Assert.NotNull(file);
        Assert.Equal("26300.9711", file.Build);
        Assert.Equal("x64", file.Architecture);
    }

    [Fact]
    public void PickWithoutMatchIsNull()
    {
        Assert.Null(MediaCatalog.Pick(Products(), "xx-xx", "x64", "Professional"));
        Assert.Null(MediaCatalog.Pick(Products(), "en-us", "ARM64", "Core"));
        Assert.Null(MediaCatalog.Pick(Products(), "en-us", "x64", "Enterprise"));
    }

    [Fact]
    public void MalformedXmlThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => MediaCatalog.ParseProducts("<MCT><Files>"));
        Assert.Throws<FormatException>(() => MediaCatalog.ParseProducts("<PublishedMedia><Files><File><FileName>a.esd</FileName></File></Files></PublishedMedia>"));
        Assert.Throws<FormatException>(() => MediaCatalog.ParseProducts("""<!DOCTYPE x [<!ENTITY a "b">]><x/>"""));
    }

    [Fact]
    public void ArchitectureMapsToTheCatalogSpelling()
    {
        Assert.Equal("x64", MediaCatalog.CatalogArchitecture(Architecture.X64));
        Assert.Equal("ARM64", MediaCatalog.CatalogArchitecture(Architecture.Arm64));
    }
}
