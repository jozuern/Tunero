using System.Text;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>
/// The image list of a WIM or ESD read from its XML metadata, without DISM. The fixtures are the real XML resources of the
/// 26H2 ESD and of an install.wim exported from it (copied read-only out of PCO-Test) and the ESD's real header.
/// </summary>
public sealed class WimMetadataTests : IDisposable
{
    private readonly string _root = TestFolders.Create("wimmeta");

    public void Dispose() => TestFolders.Delete(_root);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(RepoPaths.Root, "tests", "Optimizer.Core.Tests", "Fixtures", "media", name));

    /// <summary>The real ESD header with its XML resource moved right behind it, then the XML: a WIM file in 208 + n bytes.</summary>
    private static byte[] WimFile(byte[] xml, byte flags = 0x02, long? original = null)
    {
        var header = Fixture("wim-header-esd.bin");
        BitConverter.GetBytes((ulong)(uint)xml.Length | ((ulong)flags << 56)).CopyTo(header, 72);
        BitConverter.GetBytes(208L).CopyTo(header, 80);
        BitConverter.GetBytes(original ?? xml.Length).CopyTo(header, 88);
        return [.. header, .. xml];
    }

    [Fact]
    public void ReadsEveryImageOfTheEsd()
    {
        var images = WimMetadata.Read(new MemoryStream(WimFile(Fixture("wim-xml-esd.bin"))));
        Assert.NotNull(images);
        Assert.Equal(10, images.Count);
        var setup = images[0];
        Assert.Equal((1, "Windows Setup Media", null), (setup.Index, setup.Name, setup.Edition));
        var pro = images.Single(i => i.Index == 9);
        Assert.Equal("Windows 11 Pro", pro.Name);
        Assert.Equal("Professional", pro.Edition);
        Assert.Equal("x64", pro.Architecture);
        Assert.Equal("10.0.26300.9711", pro.Version);
        Assert.Equal(26300, pro.Build);
        Assert.Equal("en-US", pro.DefaultLanguage);
        Assert.Equal(["en-US"], pro.Languages);
        Assert.Equal("WindowsPE", images[1].Edition);
    }

    [Fact]
    public void ReadsAnInstallWim()
    {
        var image = Assert.Single(WimMetadata.Read(new MemoryStream(WimFile(Fixture("wim-xml-install.bin"))))!);
        Assert.Equal((1, "Professional", "x64", 26300), (image.Index, image.Edition, image.Architecture, image.Build));
    }

    [Theory]
    [InlineData(0, "x86")]
    [InlineData(5, "ARM")]
    [InlineData(9, "x64")]
    [InlineData(12, "ARM64")]
    public void ArchitectureCodes(int code, string name)
    {
        var xml = $"<WIM><IMAGE INDEX=\"1\"><WINDOWS><ARCH>{code}</ARCH><EDITIONID>Professional</EDITIONID><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD><SPBUILD>1</SPBUILD></VERSION></WINDOWS><NAME>X</NAME></IMAGE></WIM>";
        Assert.Equal(name, Assert.Single(WimMetadata.Parse(xml)!).Architecture);
    }

    [Fact]
    public void TheDefaultLanguageIsTheOneMarkedSo()
    {
        const string xml = "<WIM><IMAGE INDEX=\"1\"><WINDOWS><ARCH>9</ARCH><LANGUAGES><LANGUAGE>en-GB</LANGUAGE><LANGUAGE>de-DE</LANGUAGE><DEFAULT>de-DE</DEFAULT></LANGUAGES></WINDOWS><NAME>X</NAME></IMAGE></WIM>";
        var image = Assert.Single(WimMetadata.Parse(xml)!);
        Assert.Equal("de-DE", image.DefaultLanguage);
        Assert.Equal(["en-GB", "de-DE"], image.Languages);
    }

    [Fact]
    public void NotAWimOrUnreadableMetadataIsNull()
    {
        Assert.Null(WimMetadata.Read(new MemoryStream(new byte[300])));
        // A compressed XML resource (flag 0x04) or one whose sizes differ is not read as text.
        Assert.Null(WimMetadata.Read(new MemoryStream(WimFile(Fixture("wim-xml-install.bin"), flags: 0x06))));
        Assert.Null(WimMetadata.Read(new MemoryStream(WimFile(Fixture("wim-xml-install.bin"), original: 99))));
        // An XML resource past the end of the file.
        var cut = WimFile(Fixture("wim-xml-install.bin"));
        Assert.Null(WimMetadata.Read(new MemoryStream(cut[..^10])));
        Assert.Null(WimMetadata.Read(new MemoryStream(WimFile(Encoding.Unicode.GetBytes("﻿<WIM><IMAGE")))));
        Assert.Null(WimMetadata.Parse("<!DOCTYPE x [<!ENTITY a \"b\">]><WIM />"));
    }

    // ---- the media folder of an ISO ----

    private void Put(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    [Fact]
    public void IsoInfoFromTheMediaFolder()
    {
        Put(@"sources\install.esd", WimFile(Fixture("wim-xml-esd.bin")));
        Put(@"sources\boot.wim", WimFile(Fixture("wim-xml-install.bin")));
        var info = IsoReader.ReadInfo(_root);
        Assert.Equal("install.esd", info.ImageFile);
        Assert.True(info.HasBootWim);
        Assert.Equal(10, info.Images.Count);
        // Windows images only: not the setup files or the boot images an ESD also holds.
        Assert.Equal(["Core", "CoreN", "CoreSingleLanguage", "Education", "EducationN", "Professional", "ProfessionalN"],
            info.Editions.Select(i => i.Edition));
    }

    [Fact]
    public void IsoInfoWithoutOrWithTwoInstallImages()
    {
        var none = IsoReader.ReadInfo(_root);
        Assert.Null(none.ImageFile);
        Assert.Empty(none.Images);
        Assert.False(none.HasBootWim);

        Put(@"sources\install.wim", WimFile(Fixture("wim-xml-install.bin")));
        Put(@"sources\install.esd", WimFile(Fixture("wim-xml-esd.bin")));
        Assert.Null(IsoReader.ReadInfo(_root).ImageFile);
    }
}
