using Vestigo.Diagnostics;

namespace Vestigo.Server.Tests;

public sealed class KernelDumpTests
{
    [Fact]
    public void Header_ReadsTheSameFieldsFromAmd64Arm64AndI386Fixtures()
    {
        KernelDumpHeader amd64 = KernelDumpHeaderReader.Read(Prefix("PAGEDU64", 15, 22621, machineOffset: 48, machine: 0x8664, processors: 8));
        KernelDumpHeader arm64 = KernelDumpHeaderReader.Read(Prefix("PAGEDU64", 15, 22621, machineOffset: 48, machine: 0xAA64, processors: 8));
        KernelDumpHeader i386 = KernelDumpHeaderReader.Read(Prefix("PAGEDUMP", 15, 19041, machineOffset: 32, machine: 0x014C, processors: 1));

        Assert.Equal("kernel64", KernelDumpHeaderReader.FileKind(amd64));
        Assert.Equal("amd64", amd64.MachineImageTypeName);
        Assert.Equal(8, amd64.NumberOfProcessors);
        Assert.Equal("arm64", arm64.MachineImageTypeName);
        Assert.Equal(0xAA64, arm64.MachineImageType);
        Assert.Equal("kernel32", KernelDumpHeaderReader.FileKind(i386));
        Assert.Equal("i386", i386.MachineImageTypeName);
        Assert.Equal(15, i386.MajorVersion);
        Assert.Equal(19041, i386.MinorVersion);
        Assert.All(new[] { amd64, arm64, i386 }, header => Assert.Null(header.Note));
    }

    [Fact]
    public void Header_LeavesBugcheckBytesUnreadAndUnknownMachinesUnnamed()
    {
        byte[] prefix = Prefix("PAGEDU64", 10, 0, 48, 0x9999, 2);
        prefix[56] = 0x11;
        KernelDumpHeader header = KernelDumpHeaderReader.Read(prefix);

        Assert.Null(header.MachineImageTypeName);
        Assert.Equal(0x9999, header.MachineImageType);
        Assert.DoesNotContain("11", System.Text.Json.JsonSerializer.Serialize(header), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("minidump", KernelDumpHeaderReader.FileKind(KernelDumpHeaderReader.Read("MDMP...."u8)));
        Assert.Equal("unknown", KernelDumpHeaderReader.FileKind(KernelDumpHeaderReader.Read("NOTADUMP"u8)));
        Assert.Equal("truncated", KernelDumpHeaderReader.Read("PAGEDU"u8).Note);
        Assert.Null(KernelDumpHeaderReader.CrashDumpEnabledName(9));
        Assert.Equal("automatic", KernelDumpHeaderReader.CrashDumpEnabledName(7));
    }

    [Fact]
    public void Windows_ListsFixedLocationsWithoutDumpBytes()
    {
        WindowsKernelDumpInventory inventory = new(new EmptyReports());
        OperationResult<KernelDumpList> result = inventory.List(5);

        Assert.True(result.Success, result.Error);
        Assert.InRange(result.Value!.Returned, 0, 5);
        Assert.All(result.Value.Dumps, dump =>
        {
            Assert.Contains(dump.Location, new[] { "memory", "dedicated", "minidump" });
            Assert.DoesNotContain("BugCheck", System.Text.Json.JsonSerializer.Serialize(dump), StringComparison.OrdinalIgnoreCase);
            Assert.True(dump.Bytes is null || dump.Bytes >= 0);
        });
    }

    private static byte[] Prefix(string signature, int major, int minor, int machineOffset, int machine, int processors)
    {
        byte[] bytes = new byte[64];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BitConverter.GetBytes(major).CopyTo(bytes, 8);
        BitConverter.GetBytes(minor).CopyTo(bytes, 12);
        BitConverter.GetBytes(machine).CopyTo(bytes, machineOffset);
        BitConverter.GetBytes(processors).CopyTo(bytes, machineOffset + 4);
        bytes[56] = 0xAB;
        bytes[57] = 0xCD;
        return bytes;
    }

    private sealed class EmptyReports : ICrashReportSource
    {
        public OperationResult<CrashReportList> List(string scope, int limit, DateTimeOffset? since) =>
            OperationResult<CrashReportList>.Ok(new CrashReportList(0, 0, false, false, 0, []));
    }
}
