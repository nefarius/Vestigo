using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.RegistryProbe;

/// <summary>MCP tools that read allow-listed registry keys. Values are not written to the log.</summary>
[McpServerToolType]
public sealed class RegistryTools(VestigoOptions options, IRegistryStore store)
{
    [McpServerTool(Name = "read_registry_value", ReadOnly = true, Destructive = false)]
    [Description("Reads one value from an allow-listed registry key. HKCU is the console user's hive. view is registry64 or registry32 and does not follow the service bitness. REG_BINARY is hashed only when it fits MaxBinaryBytes. The value is not written to the log.")]
    public OperationResult<RegistryValueResult> ReadRegistryValue(
        [Description("Key path: HKLM, HKU, or HKCU, then a subkey. HKLM and HKEY_LOCAL_MACHINE are the same hive.")] string path,
        [Description("Value name. Omit it to read the key's default value.")] string? value = null,
        [Description("Expand a REG_EXPAND_SZ as the service account. Other types ignore this.")] bool expand = false,
        [Description("Include REG_BINARY as hex. This cannot raise MaxBinaryBytes.")] bool includeBinary = false,
        [Description("registry64 or registry32. Default registry64.")] string view = "registry64")
    {
        if (!TryView(view, out RegistryViewKind kind, out OperationResult<RegistryValueResult>? viewError))
        {
            return viewError!;
        }

        OperationResult<RegistryPathDecision> decision = Decide(path);
        if (!decision.Success || decision.Value is null)
        {
            return OperationResult<RegistryValueResult>.Fail(decision.ErrorCode!, decision.Error!);
        }

        RegistryOpen opened = store.Open(kind, decision.Value.Hive, decision.Value.SubKey);
        if (opened.Key is null)
        {
            return OperationResult<RegistryValueResult>.Fail(opened.ErrorCode!, opened.Error!);
        }

        using IRegistryKey key = opened.Key;
        RegistryRawValue raw = key.Read(value, options.Registry.MaxBinaryBytes, StringCapBytes());
        if (!raw.Found)
        {
            return OperationResult<RegistryValueResult>.Fail(raw.ErrorCode!, raw.Error!);
        }

        if (raw.TooLarge)
        {
            return OperationResult<RegistryValueResult>.Fail(
                "value_too_large",
                $"Registry value is {raw.ByteLength} bytes, above the configured limit.");
        }

        return Render(decision.Value.Canonical, string.IsNullOrEmpty(value) ? null : value, ViewName(kind), raw, key.LastWriteUtc, expand, includeBinary);
    }

    [McpServerTool(Name = "list_registry_values", ReadOnly = true, Destructive = false)]
    [Description("Lists values and subkey names under an allow-listed registry key. recurse walks nested keys up to MaxRecursionDepth. maxEntries can only lower MaxEntries. Denied children are omitted. Values are not written to the log.")]
    public OperationResult<RegistryListResult> ListRegistryValues(
        [Description("Key path: HKLM, HKU, or HKCU, then a subkey.")] string path,
        [Description("Nested levels to open. Zero lists only this key. Clamped down to MaxRecursionDepth.")] int recurse = 0,
        [Description("Optional entry cap, clamped down to Vestigo:Registry:MaxEntries.")] int? maxEntries = null,
        [Description("registry64 or registry32. Default registry64.")] string view = "registry64")
    {
        if (recurse < 0)
        {
            return OperationResult<RegistryListResult>.Fail("invalid_request", "recurse must not be negative.");
        }

        if (maxEntries is < 1)
        {
            return OperationResult<RegistryListResult>.Fail("invalid_request", "maxEntries must be positive.");
        }

        if (!TryView(view, out RegistryViewKind kind, out OperationResult<RegistryListResult>? viewError))
        {
            return viewError!;
        }

        OperationResult<RegistryPathDecision> decision = Decide(path);
        if (!decision.Success || decision.Value is null)
        {
            return OperationResult<RegistryListResult>.Fail(decision.ErrorCode!, decision.Error!);
        }

        int entryCap = maxEntries is null
            ? options.Registry.MaxEntries
            : Math.Min(maxEntries.Value, options.Registry.MaxEntries);
        int depthCap = Math.Min(recurse, options.Registry.MaxRecursionDepth);
        List<RegistryKeyListing> keys = [];
        bool truncated = false;
        int budget = entryCap;
        OperationResult<RegistryListResult>? openFailure = Walk(
            kind,
            decision.Value,
            depthCap,
            keys,
            ref budget,
            ref truncated,
            reportOpenFailure: true);
        if (openFailure is not null)
        {
            return openFailure;
        }

        return OperationResult<RegistryListResult>.Ok(new RegistryListResult(keys, keys.Count, truncated));
    }

    [McpServerTool(Name = "get_registry_metadata", ReadOnly = true, Destructive = false)]
    [Description("Returns the last write time, subkey count, and value count of an allow-listed registry key. It does not read values or security descriptors.")]
    public OperationResult<RegistryMetadata> GetRegistryMetadata(
        [Description("Key path: HKLM, HKU, or HKCU, then a subkey.")] string path,
        [Description("registry64 or registry32. Default registry64.")] string view = "registry64")
    {
        if (!TryView(view, out RegistryViewKind kind, out OperationResult<RegistryMetadata>? viewError))
        {
            return viewError!;
        }

        OperationResult<RegistryPathDecision> decision = Decide(path);
        if (!decision.Success || decision.Value is null)
        {
            return OperationResult<RegistryMetadata>.Fail(decision.ErrorCode!, decision.Error!);
        }

        RegistryOpen opened = store.Open(kind, decision.Value.Hive, decision.Value.SubKey);
        if (opened.Key is null)
        {
            return OperationResult<RegistryMetadata>.Fail(opened.ErrorCode!, opened.Error!);
        }

        using IRegistryKey key = opened.Key;
        return OperationResult<RegistryMetadata>.Ok(new RegistryMetadata(
            decision.Value.Canonical,
            ViewName(kind),
            key.LastWriteUtc,
            key.SubKeyCount,
            key.ValueCount));
    }

    private OperationResult<RegistryListResult>? Walk(
        RegistryViewKind view,
        RegistryPathDecision decision,
        int depthLeft,
        List<RegistryKeyListing> keys,
        ref int budget,
        ref bool truncated,
        bool reportOpenFailure)
    {
        if (budget <= 0)
        {
            truncated = true;
            return null;
        }

        RegistryOpen opened = store.Open(view, decision.Hive, decision.SubKey);
        if (opened.Key is null)
        {
            return reportOpenFailure
                ? OperationResult<RegistryListResult>.Fail(opened.ErrorCode!, opened.Error!)
                : null;
        }

        using IRegistryKey key = opened.Key;
        budget--;
        RegistryNamePage valueNames = key.ValueNames(Math.Max(budget, 0));
        if (valueNames.Truncated || key.ValueCount > valueNames.Names.Count)
        {
            truncated = true;
        }

        List<RegistryListedValue> values = [];
        foreach (string name in valueNames.Names)
        {
            if (budget <= 0)
            {
                truncated = true;
                break;
            }

            budget--;
            RegistryRawValue raw = key.Read(name, options.Registry.MaxBinaryBytes, StringCapBytes());
            values.Add(new RegistryListedValue(name, raw.Type, Display(raw), raw.ByteLength));
        }

        List<RegistryListedSubKey> subKeys = [];
        List<RegistryPathDecision> children = [];
        int examined = 0;
        int request = budget;
        while (budget > 0 && examined < key.SubKeyCount)
        {
            if (request <= examined)
            {
                int doubled = request > (int.MaxValue / 2) ? int.MaxValue : request * 2;
                request = Math.Min(key.SubKeyCount, Math.Max(examined + 1, doubled));
            }

            RegistryNamePage page = key.SubKeyNames(request);
            if (page.Names.Count <= examined)
            {
                break;
            }

            for (int index = examined; index < page.Names.Count; index++)
            {
                if (budget <= 0)
                {
                    break;
                }

                string name = page.Names[index];
                examined++;
                string childPath = decision.Canonical + "\\" + name;
                OperationResult<RegistryPathDecision> child = RegistryPathPolicy.Check(childPath, consoleSid: null, options.Registry);
                if (!child.Success || child.Value is null)
                {
                    continue;
                }

                budget--;
                subKeys.Add(new RegistryListedSubKey(name, key.SubKeyValueCount(name)));
                children.Add(child.Value);
            }

            if (budget <= 0 || !page.Truncated)
            {
                break;
            }
        }

        if (examined < key.SubKeyCount)
        {
            truncated = true;
        }

        keys.Add(new RegistryKeyListing(decision.Canonical, key.LastWriteUtc, values, subKeys));
        if (depthLeft <= 0)
        {
            return null;
        }

        foreach (RegistryPathDecision child in children)
        {
            Walk(view, child, depthLeft - 1, keys, ref budget, ref truncated, reportOpenFailure: false);
        }

        return null;
    }

    private OperationResult<RegistryValueResult> Render(
        string path,
        string? name,
        string view,
        RegistryRawValue raw,
        DateTimeOffset? lastWrite,
        bool expand,
        bool includeBinary)
    {
        byte[] data = raw.Data ?? [];
        string? text = null;
        IReadOnlyList<string>? multi = null;
        ulong? number = null;
        string? hex = null;
        string? sha = null;
        if (raw.Type is "REG_SZ" or "REG_EXPAND_SZ")
        {
            text = DecodeString(data);
            if (expand && raw.Type == "REG_EXPAND_SZ")
            {
                text = Environment.ExpandEnvironmentVariables(text);
            }

            if (text.Length > options.Registry.MaxValueStringLength)
            {
                return OperationResult<RegistryValueResult>.Fail(
                    "value_too_large",
                    $"Registry value is {text.Length} characters, above the {options.Registry.MaxValueStringLength} character limit.");
            }
        }
        else if (raw.Type == "REG_MULTI_SZ")
        {
            multi = DecodeMulti(data);
            if (multi.Sum(item => item.Length) > options.Registry.MaxValueStringLength)
            {
                return OperationResult<RegistryValueResult>.Fail(
                    "value_too_large",
                    $"Registry value is above the {options.Registry.MaxValueStringLength} character limit.");
            }
        }
        else if (raw.Type == "REG_DWORD" && data.Length >= 4)
        {
            number = BitConverter.ToUInt32(data, 0);
        }
        else if (raw.Type == "REG_DWORD_BIG_ENDIAN" && data.Length >= 4)
        {
            number = BinaryPrimitives.ReadUInt32BigEndian(data);
        }
        else if (raw.Type == "REG_QWORD" && data.Length >= 8)
        {
            number = BitConverter.ToUInt64(data, 0);
        }
        else if (raw.Type == "REG_BINARY")
        {
            sha = Convert.ToHexString(SHA256.HashData(data));
            if (includeBinary)
            {
                hex = Convert.ToHexString(data);
            }
        }

        return OperationResult<RegistryValueResult>.Ok(new RegistryValueResult(
            path, name, view, raw.Type, raw.ByteLength, text, multi, number, hex, sha, lastWrite));
    }

    private string? Display(RegistryRawValue raw)
    {
        if (!raw.Found || raw.TooLarge || raw.Data is null || raw.Type == "REG_BINARY")
        {
            return null;
        }

        if (raw.Type is "REG_SZ" or "REG_EXPAND_SZ")
        {
            string text = DecodeString(raw.Data);
            return text.Length > options.Registry.MaxValueStringLength ? null : text;
        }

        if (raw.Type == "REG_MULTI_SZ")
        {
            IReadOnlyList<string> items = DecodeMulti(raw.Data);
            return items.Sum(item => item.Length) > options.Registry.MaxValueStringLength ? null : string.Join(" | ", items);
        }

        if (raw.Type == "REG_DWORD" && raw.Data.Length >= 4)
        {
            return BitConverter.ToUInt32(raw.Data, 0).ToString(CultureInfo.InvariantCulture);
        }

        if (raw.Type == "REG_QWORD" && raw.Data.Length >= 8)
        {
            return BitConverter.ToUInt64(raw.Data, 0).ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private int StringCapBytes() => checked((options.Registry.MaxValueStringLength + 1) * 2);

    private OperationResult<RegistryPathDecision> Decide(string path)
    {
        string? sid = null;
        if (RegistryPathPolicy.NeedsConsoleUser(path))
        {
            ConsoleUserLookup user = store.LookupConsoleUser();
            if (user.Sid is null)
            {
                return OperationResult<RegistryPathDecision>.Fail(user.ErrorCode!, user.Error!);
            }

            sid = user.Sid;
        }

        return RegistryPathPolicy.Check(path, sid, options.Registry);
    }

    private static bool TryView<T>(string view, out RegistryViewKind kind, out OperationResult<T>? error)
    {
        if (view.Equals("registry64", StringComparison.OrdinalIgnoreCase))
        {
            kind = RegistryViewKind.Registry64;
            error = null;
            return true;
        }

        if (view.Equals("registry32", StringComparison.OrdinalIgnoreCase))
        {
            kind = RegistryViewKind.Registry32;
            error = null;
            return true;
        }

        kind = default;
        error = OperationResult<T>.Fail("invalid_request", "view must be registry64 or registry32.");
        return false;
    }

    private static string ViewName(RegistryViewKind view) =>
        view == RegistryViewKind.Registry32 ? "registry32" : "registry64";

    private static string DecodeString(byte[] data)
    {
        int length = data.Length;
        if (length >= 2 && data[^1] == 0 && data[^2] == 0)
        {
            length -= 2;
        }

        return Encoding.Unicode.GetString(data, 0, length);
    }

    private static IReadOnlyList<string> DecodeMulti(byte[] data)
    {
        string text = Encoding.Unicode.GetString(data);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
