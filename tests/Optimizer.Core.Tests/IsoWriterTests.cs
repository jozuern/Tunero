using System.Text;
using Optimizer.Core.Media;

namespace Optimizer.Core.Tests;

/// <summary>IMAPI2 writes into temp folders only. The reader (attaching an ISO) changes the PC and is tested in the VM.</summary>
public class IsoWriterTests
{
    private const int Sector = 2048;

    private static void Put(string folder, string relative, byte[] data)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Ascii(byte[] iso, int sector, int offset, int length) =>
        Encoding.ASCII.GetString(iso, sector * Sector + offset, length);

    /// <summary>Media with fake 2048-byte boot images (different contents, so a swapped image is noticed).</summary>
    private static string BootableMedia(string root)
    {
        var media = Path.Combine(root, "media");
        Put(media, @"boot\etfsboot.com", Random(Sector, 1));
        Put(media, @"efi\microsoft\boot\efisys.bin", Random(Sector, 2));
        Put(media, @"efi\boot\bootx64.efi", Random(100, 5));
        Put(media, @"sources\readme.txt", Encoding.ASCII.GetBytes("hello"));
        return media;
    }

    [Fact]
    public void BootEntriesFollowTheFiles()
    {
        var root = TestFolders.Create("isoboot");
        try
        {
            var both = Path.Combine(root, "both");
            Put(both, @"boot\etfsboot.com", new byte[Sector]);
            Put(both, @"efi\microsoft\boot\efisys.bin", new byte[Sector]);
            Assert.Equal([(@"boot\etfsboot.com", 0x00), (@"efi\microsoft\boot\efisys.bin", 0xEF)], IsoBoot.Entries(both));

            var uefi = Path.Combine(root, "uefi");
            Put(uefi, @"efi\microsoft\boot\efisys.bin", new byte[Sector]);
            Assert.Equal([(@"efi\microsoft\boot\efisys.bin", 0xEF)], IsoBoot.Entries(uefi));

            var none = Path.Combine(root, "none");
            Put(none, @"sources\install.wim", new byte[16]);
            Assert.Empty(IsoBoot.Entries(none));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public void VolumeNameIsCleaned()
    {
        // Upper case, everything outside [A-Z0-9_] becomes '_', cut to 32 characters.
        Assert.Equal("CCCOMA_X64_FRE_EN_US_DV9_EXTRA_L", IsoBoot.VolumeName("CCCOMA x64-fre en-us DV9 extra long name 123"));
        Assert.Equal(32, IsoBoot.VolumeName(new string('a', 40)).Length);
        Assert.Equal("CCCOMA_X64FRE_EN_US_DV9", IsoBoot.VolumeName("CCCOMA_X64FRE_EN-US_DV9"));
        Assert.Equal("WIN_11_", IsoBoot.VolumeName("Win 11ä"));
        Assert.Equal("", IsoBoot.VolumeName(""));
    }

    [Fact]
    public async Task WritesAUdfIso()
    {
        var root = TestFolders.Create("isoudf");
        try
        {
            var media = Path.Combine(root, "media");
            Put(media, "a.txt", Encoding.ASCII.GetBytes("hello"));
            Put(media, @"sub\b.bin", Random(5000, 3));
            var iso = Path.Combine(root, "out.iso");

            await new IsoWriter().WriteAsync(media, iso, "TEST", null, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(iso);
            Assert.Equal(0, bytes.Length % Sector);
            // The UDF volume recognition sequence (ECMA-167): BEA01, then NSR02 or NSR03, then TEA01, from sector 16 on.
            var ids = Enumerable.Range(16, 17).Where(s => (s + 1) * Sector <= bytes.Length).Select(s => Ascii(bytes, s, 1, 5)).ToList();
            Assert.Contains("BEA01", ids);
            Assert.True(ids.Contains("NSR02") || ids.Contains("NSR03"), string.Join(",", ids));
            Assert.Contains("TEA01", ids);
            Assert.False(File.Exists(iso + ".part"));
            // Without boot files there is no El Torito catalog.
            await using var stream = File.OpenRead(iso);
            Assert.Empty(IsoBoot.ReadPlatforms(stream));
            // An ISO that cannot boot a UEFI PC never passes the check, even when the media asks for no boot entry.
            Assert.False(IsoBoot.CatalogMatches(stream, media));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public async Task WritesElToritoWithBootFiles()
    {
        var root = TestFolders.Create("isoboot");
        try
        {
            var media = BootableMedia(root);
            var iso = Path.Combine(root, "out.iso");
            var reports = new List<double>();

            await new IsoWriter().WriteAsync(media, iso, "CCCOMA x64", new SyncProgress(reports.Add), CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(iso);
            Assert.Equal("CD001", Ascii(bytes, 16, 1, 5));
            Assert.Equal("EL TORITO SPECIFICATION", Ascii(bytes, 17, 7, 23));
            // The primary volume descriptor carries the cleaned volume name.
            Assert.Equal("CCCOMA_X64", Ascii(bytes, 16, 40, 32).TrimEnd());
            await using (var stream = File.OpenRead(iso))
            {
                Assert.Equal([0x00, 0xEF], IsoBoot.ReadPlatforms(stream));
                Assert.True(IsoBoot.CatalogMatches(stream, media));
            }
            Assert.NotEmpty(reports);
            Assert.Equal(1.0, reports[^1]);
            Assert.True(reports.Zip(reports.Skip(1)).All(p => p.First <= p.Second), "progress went backwards");

            // A different UEFI boot image than the media's own is noticed.
            Put(media, @"efi\microsoft\boot\efisys.bin", Random(Sector, 9));
            await using (var stream = File.OpenRead(iso))
                Assert.False(IsoBoot.CatalogMatches(stream, media));
            // So is a media folder that expects no BIOS entry.
            File.Delete(Path.Combine(media, @"boot\etfsboot.com"));
            Put(media, @"efi\microsoft\boot\efisys.bin", Random(Sector, 2));
            await using (var stream = File.OpenRead(iso))
                Assert.False(IsoBoot.CatalogMatches(stream, media));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public async Task ABiosOnlyIsoFailsTheCheck()
    {
        var root = TestFolders.Create("isobios");
        try
        {
            var media = Path.Combine(root, "media");
            Put(media, @"boot\etfsboot.com", Random(Sector, 1));
            Put(media, @"sources\readme.txt", Encoding.ASCII.GetBytes("hello"));
            var iso = Path.Combine(root, "out.iso");

            await new IsoWriter().WriteAsync(media, iso, "TEST", null, CancellationToken.None);

            await using var stream = File.OpenRead(iso);
            Assert.Equal([0x00], IsoBoot.ReadPlatforms(stream));
            Assert.False(IsoBoot.CatalogMatches(stream, media));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public void HasUefiBootNeedsTheBootImageAndTheLoader()
    {
        var root = TestFolders.Create("isouefi");
        try
        {
            var media = Path.Combine(root, "media");
            Assert.False(IsoBoot.HasUefiBoot(media));
            Put(media, @"efi\microsoft\boot\efisys.bin", Random(Sector, 2));
            Assert.False(IsoBoot.HasUefiBoot(media));
            Put(media, @"efi\boot\bootx64.efi", Random(100, 5));
            Assert.True(IsoBoot.HasUefiBoot(media));
            Put(media, @"efi\microsoft\boot\efisys.bin", []);
            Assert.False(IsoBoot.HasUefiBoot(media));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public void ReadPlatformsIgnoresWhatIsNoBootCatalog()
    {
        Assert.Empty(IsoBoot.ReadPlatforms(new MemoryStream(new byte[100])));
        Assert.Empty(IsoBoot.ReadPlatforms(new MemoryStream(new byte[40 * Sector])));
        // A boot record whose catalog pointer leads past the end of the image.
        var image = new byte[20 * Sector];
        image[17 * Sector] = 0;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, 17 * Sector + 1);
        Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(image, 17 * Sector + 7);
        BitConverter.GetBytes(5000u).CopyTo(image, 17 * Sector + 0x47);
        Assert.Empty(IsoBoot.ReadPlatforms(new MemoryStream(image)));
    }

    [Fact]
    public async Task StopDeletesThePartialIso()
    {
        var root = TestFolders.Create("isostop");
        try
        {
            var media = BootableMedia(root);
            // Big enough for several chunks, so the stop lands between two of them.
            Put(media, @"sources\big.bin", Random(8 << 20, 4));
            var iso = Path.Combine(root, "out.iso");
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress(v => { if (v > 0) cts.Cancel(); });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IsoWriter().WriteAsync(media, iso, "TEST", progress, cts.Token));

            Assert.False(File.Exists(iso));
            Assert.False(File.Exists(iso + ".part"));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public async Task AnExistingIsoStaysUntilTheNewOneIsComplete()
    {
        var root = TestFolders.Create("isokeep");
        try
        {
            var media = BootableMedia(root);
            Put(media, @"sources\big.bin", Random(8 << 20, 4));
            var iso = Path.Combine(root, "out.iso");
            await File.WriteAllTextAsync(iso, "old");

            // Stopped in the middle of writing.
            using (var cts = new CancellationTokenSource())
            {
                var progress = new SyncProgress(v => { if (v > 0) cts.Cancel(); });
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IsoWriter().WriteAsync(media, iso, "TEST", progress, cts.Token));
            }
            Assert.Equal("old", await File.ReadAllTextAsync(iso));
            Assert.False(File.Exists(iso + ".part"));

            // Failed while writing: a media file nobody may read.
            using (File.Open(Path.Combine(media, @"sources\big.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Assert.ThrowsAnyAsync<IOException>(() => new IsoWriter().WriteAsync(media, iso, "TEST", null, CancellationToken.None));
            Assert.Equal("old", await File.ReadAllTextAsync(iso));
            Assert.False(File.Exists(iso + ".part"));

            // Complete: only now the old file is replaced.
            await new IsoWriter().WriteAsync(media, iso, "TEST", null, CancellationToken.None);
            await using var stream = File.OpenRead(iso);
            Assert.True(IsoBoot.CatalogMatches(stream, media));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public async Task CopyTreeCopiesEveryFolderAndNoReadOnlyFlag()
    {
        var root = TestFolders.Create("isocopy");
        try
        {
            var source = Path.Combine(root, "source");
            Put(source, @"sources\install.wim", Random(3 << 20, 6));
            Put(source, "setup.exe", Random(10, 7));
            Directory.CreateDirectory(Path.Combine(source, @"support\empty"));
            File.SetAttributes(Path.Combine(source, "setup.exe"), FileAttributes.ReadOnly);
            var target = Path.Combine(root, "target");
            var reports = new List<double>();

            await IsoReader.CopyTreeAsync(source, target, new SyncProgress(reports.Add), CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(target, @"support\empty")));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, @"sources\install.wim")),
                await File.ReadAllBytesAsync(Path.Combine(target, @"sources\install.wim")));
            Assert.Equal(FileAttributes.None, File.GetAttributes(Path.Combine(target, "setup.exe")) & FileAttributes.ReadOnly);
            Assert.Equal(1.0, reports[^1]);
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Fact]
    public void AttachErrorsSayWhenTheIsoIsInUse()
    {
        var sharing = IsoAttachException.For(32, "attached");
        Assert.Equal(32, sharing.Win32Error);
        Assert.True(sharing.InUse);
        Assert.Contains("in use", sharing.Message);
        var other = IsoAttachException.For(1392, "opened");
        Assert.Equal(1392, other.Win32Error);
        Assert.False(other.InUse);
        Assert.DoesNotContain("in use", other.Message);
    }

    [Fact]
    public async Task AMissingMediaFolderFails()
    {
        var root = TestFolders.Create("isomissing");
        try
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                new IsoWriter().WriteAsync(Path.Combine(root, "nope"), Path.Combine(root, "out.iso"), "TEST", null, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "out.iso.part")));
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    /// <summary>Calls back on the reporting thread (Progress&lt;T&gt; would post to the thread pool and come too late).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
