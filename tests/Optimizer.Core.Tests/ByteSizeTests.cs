using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>Sizes as the Storage page, the cleanup rows and the install media show them.</summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(511L, "1 KB")] // a Recycle Bin with one small file read "0 KB in 1 file"
    [InlineData(1023L, "1 KB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(12L * 1024, "12 KB")]
    [InlineData(512L << 20, "512 MB")]
    [InlineData(2L << 30, "2 GB")]
    public void ASizeIsNeverZeroForSomething(long bytes, string text) => Assert.Equal(text, ByteSize.Format(bytes));
}
