using System;
using System.Linq;

using JetBrains.Annotations;

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    const string PublishRuntime = "win-x64";

    static AbsolutePath PublishRoot => RootDirectory / "publish-x64";
    static AbsolutePath PublishServerDir => PublishRoot / "server";
    static AbsolutePath InstallerOutputDir => PublishRoot / "installer";
    static AbsolutePath InstallerProjectFile => RootDirectory / "installer" / "Vestigo.Installer.csproj";

    [Solution] readonly Solution Solution;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetClean(settings => settings
                .SetProject(Solution.Path)
                .SetConfiguration(Configuration));
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetTasks.DotNetRestore(settings => settings
                .SetProjectFile(Solution.Path));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(settings => settings
                .SetProjectFile(Solution.Path)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    [UsedImplicitly]
    public Target PublishLocal => _ => _
        .Description("Publish Vestigo locally using the Release configuration and win-x64 runtime")
        .Executes(() =>
        {
            PublishProject(Solution.GetAllProjects("Vestigo.Server").Single(), PublishServerDir);
        });

    [UsedImplicitly]
    public Target BuildInstaller => _ => _
        .Description("Publish Vestigo and build the x64 MSI. Requires Windows and WiX 5.0.2.")
        .DependsOn(PublishLocal)
        .Executes(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "BuildInstaller must run on Windows with the WiX 5.0.2 CLI available.");
            }

            const string envServer = "VESTIGO_INSTALLER_SERVER";
            const string envOut = "VESTIGO_INSTALLER_OUT";
            try
            {
                Environment.SetEnvironmentVariable(envServer, PublishServerDir);
                Environment.SetEnvironmentVariable(envOut, InstallerOutputDir);
                DotNetTasks.DotNetRun(settings => settings
                    .SetProjectFile(InstallerProjectFile)
                    .SetConfiguration(Configuration.Release));
            }
            finally
            {
                Environment.SetEnvironmentVariable(envServer, null);
                Environment.SetEnvironmentVariable(envOut, null);
            }
        });

    void PublishProject(Project project, AbsolutePath publishDirectory)
    {
        DotNetTasks.DotNetPublish(settings => settings
            .SetProject(project)
            .SetConfiguration(Configuration.Release)
            .SetRuntime(PublishRuntime)
            .SetOutput(publishDirectory)
            .AddProperty("PublishReadyToRun", "true")
            .AddProperty("PublishSingleFile", "false")
            .AddProperty("SelfContained", "false")
            .AddProperty("DebugType", "none")
            .AddProperty("GenerateDocumentationFile", "false")
            .AddProperty("AllowedReferenceRelatedFileExtensions", "none"));
    }
}
