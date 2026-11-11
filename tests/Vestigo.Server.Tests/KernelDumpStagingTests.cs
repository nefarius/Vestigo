using System.Security.Cryptography;

using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Diagnostics;
using Vestigo.Files;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Server.Tests;

public sealed class KernelDumpStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vestigo-kdump-" + Guid.NewGuid().ToString("N"));

    public KernelDumpStagingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Copy_HashesTheBytesAndDeletesAnOversizedCopy()
    {
        byte[] bytes = "kernel-dump-fixture"u8.ToArray();
        string source = Path.Combine(_root, "source.dmp");
        File.WriteAllBytes(source, bytes);
        string destination = Path.Combine(_root, "staged.dmp");

        OperationResult<KernelDumpCopied> copied = await KernelDumpCopy.CopyAsync(source, destination, bytes.Length, bytes.Length, CancellationToken.None);

        Assert.True(copied.Success, copied.Error);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), copied.Value!.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));

        string oversized = Path.Combine(_root, "oversized.dmp");
        OperationResult<KernelDumpCopied> rejected = await KernelDumpCopy.CopyAsync(source, oversized, bytes.Length, bytes.Length - 1, CancellationToken.None);
        Assert.Equal("dump_too_large", rejected.ErrorCode);
        Assert.False(File.Exists(oversized));

        byte[] existing = "keep-me"u8.ToArray();
        File.WriteAllBytes(destination, existing);
        OperationResult<KernelDumpCopied> occupied = await KernelDumpCopy.CopyAsync(source, destination, bytes.Length, bytes.Length, CancellationToken.None);
        Assert.Equal("dump_failed", occupied.ErrorCode);
        Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public void InventoryId_RejectsCallerPaths()
    {
        Assert.False(WindowsKernelDumpInventory.IsInventoryId(@"C:\Windows\MEMORY.DMP"));
        Assert.False(WindowsKernelDumpInventory.IsInventoryId(@"minidump\..\MEMORY.DMP"));
        Assert.False(WindowsKernelDumpInventory.IsInventoryId("minidump/../x.dmp"));
        Assert.True(WindowsKernelDumpInventory.IsInventoryId("memory"));
        Assert.True(WindowsKernelDumpInventory.IsInventoryId("minidump/Mini0101.dmp"));
    }

    [Fact]
    public async Task Stage_StaysOffUntilTheGateIsTrue()
    {
        KernelDumpStagingTools tools = Tools(allow: false, free: long.MaxValue, new ThrowingInventory(), link: false);
        OperationResult<KernelDumpStageResult> result = await tools.StageKernelDump("memory", "out:copy.dmp");
        Assert.Equal("kernel_dump_staging_disabled", result.ErrorCode);
    }

    [Fact]
    public async Task Stage_CopiesTheSelectedDumpAndIssuesASingleUseTicket()
    {
        byte[] bytes = "selected-dump"u8.ToArray();
        string source = Path.Combine(_root, "source.dmp");
        File.WriteAllBytes(source, bytes);
        KernelDumpStagingTools tools = Tools(allow: true, free: long.MaxValue, new FixedInventory(source), link: true);

        Assert.Equal("invalid_request", (await tools.StageKernelDump(@"..\MEMORY.DMP", "out:copy.dmp")).ErrorCode);
        OperationResult<KernelDumpStageResult> staged = await tools.StageKernelDump("memory", "out:copy.dmp", wait: 5);

        Assert.True(staged.Success, staged.Error);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), staged.Value!.Sha256);
        Assert.NotNull(staged.Value.Download);
        Assert.True(staged.Value.Download!.SingleUse);
        Assert.Contains("ticket=", staged.Value.Download.Url, StringComparison.Ordinal);
        string serialized = System.Text.Json.JsonSerializer.Serialize(staged.Value);
        string escapedSource = System.Text.Json.JsonSerializer.Serialize(source).Trim('"');
        Assert.DoesNotContain("selected-dump", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("source.dmp", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(escapedSource, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "copy.dmp")));
    }

    [Fact]
    public async Task Stage_RefusesWhenTheVolumeCannotKeepTheReserve()
    {
        byte[] bytes = "selected-dump"u8.ToArray();
        string source = Path.Combine(_root, "source.dmp");
        File.WriteAllBytes(source, bytes);
        KernelDumpStagingTools tools = Tools(allow: true, free: 100, new FixedInventory(source), link: false);

        OperationResult<KernelDumpStageResult> result = await tools.StageKernelDump("memory", "out:copy.dmp", wait: 5);

        Assert.Equal("insufficient_disk_space", result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "copy.dmp")));
    }

    private KernelDumpStagingTools Tools(bool allow, long? free, IKernelDumpInventory inventory, bool link)
    {
        VestigoOptions options = new()
        {
            Roots = [new RootDefinition { Name = "out", Path = _root, Access = RootAccess.Read | RootAccess.Write }],
            ApiTokens = ["0123456789abcdef"],
            Diagnostics =
            {
                AllowKernelDumpStaging = allow,
                MaxDumpBytes = 1024,
                MinDumpFreeBytes = 1024
            }
        };
        RootPolicy policy = new(options);
        FileLinkIssuer? issuer = link ? new FileLinkIssuer(options, policy, new FileTicketStore(options)) : null;
        return new KernelDumpStagingTools(
            inventory,
            new FakeSpace(free),
            policy,
            new JobManager(options, Path.Combine(_root, "jobs")),
            options,
            NullLogger<KernelDumpStagingTools>.Instance,
            issuer);
    }

    private sealed class FixedInventory(string path) : IKernelDumpInventory
    {
        public OperationResult<KernelDumpList> List(int limit) =>
            OperationResult<KernelDumpList>.Ok(new KernelDumpList(null, null, null, null, 0, false, []));

        public OperationResult<KernelDumpLocation> Resolve(string id) =>
            id == "memory"
                ? OperationResult<KernelDumpLocation>.Ok(new KernelDumpLocation("memory", path, new FileInfo(path).Length, null))
                : OperationResult<KernelDumpLocation>.Fail("not_found", "No kernel dump has that id.");
    }

    private sealed class ThrowingInventory : IKernelDumpInventory
    {
        public OperationResult<KernelDumpList> List(int limit) => throw new InvalidOperationException();

        public OperationResult<KernelDumpLocation> Resolve(string id) => throw new InvalidOperationException();
    }

    private sealed class FakeSpace(long? free) : IVolumeSpace
    {
        public long? FreeBytes(string directory) => free;
    }
}
