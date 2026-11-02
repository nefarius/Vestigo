using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Updates;

/// <summary>
///     MCP tools that check the signed Buildbot feed and, when allowed, launch the packaged Vicius updater.
/// </summary>
[McpServerToolType]
public sealed class UpdateTools(
    IUpdateFeedClient feed,
    IInstalledVersionSource versions,
    ISelfUpdateRunner runner,
    VestigoOptions options)
{
    [McpServerTool(Name = "check_for_updates", ReadOnly = true)]
    [Description("Fetches the signed Buildbot updates.json, verifies updates.json.minisig with the Nefarius minisign public key, and compares the newest release to the installed Vestigo version (HKLM Version when the updater feature is installed, otherwise the entry assembly). Also reports whether the packaged updater is present, whether self-update is allowed, and the last start_self_update attempt.")]
    public async Task<OperationResult<UpdateCheck>> CheckForUpdates(CancellationToken cancellationToken = default)
    {
        InstalledVersion installed = versions.Read();
        OperationResult<UpdateFeedRelease> latest = await feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (!latest.Success)
        {
            return OperationResult<UpdateCheck>.Fail(latest.ErrorCode!, latest.Error!);
        }

        UpdateFeedRelease release = latest.Value!;
        return OperationResult<UpdateCheck>.Ok(new UpdateCheck(
            installed,
            release,
            UpdateFeedClient.IsNewer(release.Version, installed.Version),
            runner.IsInstalled,
            options.Updates.AllowSelfUpdate,
            runner.ReadLastAttempt(installed.Version)));
    }

    [McpServerTool(Name = "start_self_update", ReadOnly = false, Destructive = true)]
    [Description("Launches the packaged Vicius updater with --silent-update so it downloads and installs the signed Buildbot MSI without UI. Requires Vestigo:Updates:AllowSelfUpdate and the Automatic updates feature. The updater copies itself to TEMP and exits 207 before the MSI stops this service; the MCP session will drop. dryRun returns the planned command only.")]
    public async Task<OperationResult<SelfUpdateResult>> StartSelfUpdate(
        [Description("Return the planned updater command without running it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Updates.AllowSelfUpdate)
        {
            return OperationResult<SelfUpdateResult>.Fail(
                "self_update_disabled",
                "Self-update is disabled. Set Vestigo:Updates:AllowSelfUpdate to true.");
        }

        if (!runner.IsInstalled)
        {
            return OperationResult<SelfUpdateResult>.Fail(
                "updater_not_installed",
                "The packaged updater is not installed. Add the Automatic updates feature, or reinstall without REMOVE=Updater.");
        }

        if (runner.TrustFailure is { } untrusted)
        {
            return OperationResult<SelfUpdateResult>.Fail(
                "untrusted_executable",
                $"Refusing to run the packaged updater as the service account: {untrusted}.");
        }

        if (!dryRun && runner.IsInProgress)
        {
            return OperationResult<SelfUpdateResult>.Fail(
                "update_in_progress",
                "A Vestigo self-update is already running.");
        }

        InstalledVersion installed = versions.Read();
        OperationResult<UpdateFeedRelease> latest = await feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (!latest.Success)
        {
            return OperationResult<SelfUpdateResult>.Fail(latest.ErrorCode!, latest.Error!);
        }

        UpdateFeedRelease release = latest.Value!;
        if (!UpdateFeedClient.IsNewer(release.Version, installed.Version))
        {
            return OperationResult<SelfUpdateResult>.Fail(
                "up_to_date",
                $"Vestigo {installed.Version} is already the newest signed release.");
        }

        return await runner.StartAsync(installed.Version, release.Version, dryRun, cancellationToken).ConfigureAwait(false);
    }
}
