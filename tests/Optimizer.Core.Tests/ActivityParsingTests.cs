using Optimizer.Core.Activity;

namespace Optimizer.Core.Tests;

/// <summary>Live readings: the raw formats of the GPU engine counters and the disk trace events.</summary>
public class ActivityParsingTests
{
    [Fact]
    public void GpuInstanceParses() =>
        Assert.Equal(new GpuEngineSample(1234, 0xC2F5, "VideoDecode", 12.5),
            GpuEngineName.Parse("pid_1234_luid_0x00000000_0x0000C2F5_phys_0_eng_3_engtype_VideoDecode", 12.5));

    [Fact]
    public void GpuInstanceHighLuid() =>
        Assert.Equal(0x1_0000_0002L, GpuEngineName.Parse("pid_8_luid_0x00000001_0x00000002_phys_0_eng_0_engtype_3D", 1)?.Luid);

    [Fact]
    public void GpuInstanceEmptyEngine() =>
        Assert.Equal("Other", GpuEngineName.Parse("pid_8_luid_0x00000000_0x00000002_phys_0_eng_5_engtype_", 1)?.EngineType);

    [Theory]
    [InlineData("_Total")]
    [InlineData("pid_x_luid")]
    [InlineData("")]
    [InlineData("pid_12_luid_0xZZ_0x01_phys_0_eng_0_engtype_3D")]
    public void GpuInstanceRejectsGarbage(string instance) => Assert.Null(GpuEngineName.Parse(instance, 5));

    /// <summary>A DiskIo_TypeGroup1 payload: DiskNumber at 0, TransferSize at 8, IssuingThreadId at 48 (64 bit) or 40 (32 bit).</summary>
    private static byte[] DiskPayload(int length, int disk, int bytes, int threadOffset, int thread, long fileKey = 0)
    {
        var data = new byte[length];
        BitConverter.TryWriteBytes(data.AsSpan(0), disk);
        BitConverter.TryWriteBytes(data.AsSpan(4), 0x60043); // IrpFlags, ignored
        BitConverter.TryWriteBytes(data.AsSpan(8), bytes);
        BitConverter.TryWriteBytes(data.AsSpan(16), 123456789L); // ByteOffset, ignored
        if (threadOffset == 48) BitConverter.TryWriteBytes(data.AsSpan(24), fileKey); // FileObject (a file key)
        else BitConverter.TryWriteBytes(data.AsSpan(24), (int)fileKey);
        BitConverter.TryWriteBytes(data.AsSpan(threadOffset), thread);
        return data;
    }

    [Fact]
    public void DiskPayload64() =>
        Assert.Equal(new DiskIoEvent(3, 65536, 4242, true, unchecked((long)0xFFFF_A001_2345_6780)),
            DiskIoPayload.Parse(DiskPayload(52, 3, 65536, 48, 4242, unchecked((long)0xFFFF_A001_2345_6780)), 11, pointer64: true));

    [Fact]
    public void DiskPayload32() =>
        Assert.Equal(new DiskIoEvent(1, 4096, 77, false, 0x8123_4560), DiskIoPayload.Parse(DiskPayload(44, 1, 4096, 40, 77, 0x8123_4560), 10, pointer64: false));

    /// <summary>FileIo_ReadWrite: Offset 0, IrpPtr, FileObject, then FileKey at 24 (64 bit) or 16 (32 bit).</summary>
    [Fact]
    public void FileIoReadWrite()
    {
        var data64 = new byte[44];
        BitConverter.TryWriteBytes(data64.AsSpan(24), unchecked((long)0xFFFF_A001_2345_6780));
        Assert.Equal(new FileIoEvent(unchecked((long)0xFFFF_A001_2345_6780), Write: true), FileIoPayload.Parse(data64, 68, pointer64: true));
        var data32 = new byte[32];
        BitConverter.TryWriteBytes(data32.AsSpan(16), 0x0812_3456);
        Assert.Equal(new FileIoEvent(0x0812_3456L, Write: false), FileIoPayload.Parse(data32, 67, pointer64: false));
        Assert.Null(FileIoPayload.Parse(data64, 64, pointer64: true)); // create: no file key
        Assert.Null(FileIoPayload.Parse(data64, 74, pointer64: true)); // query information: says nothing about writing
        Assert.Null(FileIoPayload.Parse(new byte[20], 68, pointer64: true));
    }

    /// <summary>
    /// FileIo_Info SetInfo (69): IrpPtr, FileObject, FileKey at 16 (64 bit) or 8 (32 bit). Copies inside the kernel issue
    /// no write events of the program, but it sets the new file's size, which counts as writing it.
    /// </summary>
    [Fact]
    public void FileIoSetInfoCountsAsWrite()
    {
        var data64 = new byte[40];
        BitConverter.TryWriteBytes(data64.AsSpan(16), 0x7777L);
        Assert.Equal(new FileIoEvent(0x7777, Write: true), FileIoPayload.Parse(data64, 69, pointer64: true));
        var data32 = new byte[24];
        BitConverter.TryWriteBytes(data32.AsSpan(8), 0x6666);
        Assert.Equal(new FileIoEvent(0x6666, Write: true), FileIoPayload.Parse(data32, 69, pointer64: false));
    }

    [Fact]
    public void DiskPayloadOtherOpcodeOrShort()
    {
        Assert.Null(DiskIoPayload.Parse(DiskPayload(52, 3, 512, 48, 1), 14, pointer64: true));
        Assert.Null(DiskIoPayload.Parse(new byte[20], 10, pointer64: true));
        Assert.Null(DiskIoPayload.Parse(new byte[50], 10, pointer64: true));
    }

    /// <summary>The trace reads its events through EVENT_TRACE_LOGFILEW; a wrong layout would crash the trace thread.</summary>
    [Fact]
    public void TraceLogfileLayoutIs64Bit() =>
        Assert.Equal(448, System.Runtime.CompilerServices.Unsafe.SizeOf<Optimizer.Core.Interop.Etw.EVENT_TRACE_LOGFILEW>());

    [Fact]
    public void AccumulatorSumsAndDrains()
    {
        var acc = new DiskIoAccumulator();
        acc.Add(new DiskIoEvent(3, 1000, 4242, true, 0));
        acc.Add(new DiskIoEvent(3, 500, 4242, true, 0));
        acc.Add(new DiskIoEvent(3, 200, 4242, false, 0));
        acc.Add(new DiskIoEvent(0, 4096, 7, false, 0));
        var totals = acc.Drain().OrderBy(t => t.DiskNumber).ToList();
        Assert.Equal([new DiskIoTotal(0, 7, 0, 4096, 0), new DiskIoTotal(3, 4242, 0, 200, 1500)], totals);
        Assert.Empty(acc.Drain());
    }

    /// <summary>Windows writes cached data later from its own threads: the disk write names the file the program wrote.</summary>
    [Fact]
    public void AccumulatorNamesTheProgramThatLastUsedTheFile()
    {
        var acc = new DiskIoAccumulator();
        acc.NoteFile(new FileIoEvent(0x55, Write: true), pid: 1234);
        acc.NoteFile(new FileIoEvent(0x55, Write: false), pid: 4321); // a virus scanner reads the new file
        acc.Add(new DiskIoEvent(3, 8192, 99, true, 0x55));  // lazy writer thread 99 writes it: the writer's bytes
        acc.Add(new DiskIoEvent(3, 4096, 99, true, 0x66));  // a file nobody was seen using
        acc.Add(new DiskIoEvent(3, 100, 99, false, 0x55));  // read-ahead: the reader's bytes
        acc.NoteFile(new FileIoEvent(0x77, Write: true), pid: 4); // Windows' own file work names no program
        acc.Add(new DiskIoEvent(3, 1, 99, true, 0x77));
        var totals = acc.Drain().OrderBy(t => t.FilePid).ThenBy(t => t.WriteBytes).ToList();
        Assert.Equal([new DiskIoTotal(3, 99, 0, 0, 4097), new DiskIoTotal(3, 99, 1234, 0, 8192), new DiskIoTotal(3, 99, 4321, 100, 0)], totals);
    }

    [Fact]
    public async Task AccumulatorManyEvents()
    {
        var acc = new DiskIoAccumulator();
        var drained = new List<DiskIoTotal>();
        var workers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 25_000; i++) acc.Add(new DiskIoEvent(i % 3, 512, i % 50, (i & 1) == 0, i % 7));
        })).ToList();
        while (!workers.All(t => t.IsCompleted)) drained.AddRange(acc.Drain());
        await Task.WhenAll(workers);
        drained.AddRange(acc.Drain());
        Assert.Equal(100_000L * 512, drained.Sum(t => t.ReadBytes + t.WriteBytes));
    }
}
