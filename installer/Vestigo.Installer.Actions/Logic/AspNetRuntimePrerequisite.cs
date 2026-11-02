#nullable enable

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>
    ///     MSI launch-condition pieces for the x64 ASP.NET Core 10 runtime. Native x64 uses
    ///     WiX <c>DotNetCompatibilityCheck</c>. Windows 11 ARM64 also accepts Microsoft's official
    ///     x64 shared-framework registry marker when that hostfxr check misses the side-by-side layout.
    /// </summary>
    public static class AspNetRuntimePrerequisite
    {
        public const string RuntimeVersion = "10.0.0";
        public const string RuntimeType = "aspnet";
        public const string RollForward = "minor";
        public const string Platform = "x64";
        public const string CheckProperty = "DotNetCheckResult";
        public const string RegistryProperty = "VESTIGO_X64_ASPNET10";

        /// <summary><c>IMAGE_FILE_MACHINE_ARM64</c> (<c>0xAA64</c>), as set by <c>util:QueryNativeMachine</c>.</summary>
        public const int NativeArm64Machine = 43620;

        /// <summary><c>IMAGE_FILE_MACHINE_AMD64</c> (<c>0x8664</c>).</summary>
        public const int NativeX64Machine = 34404;

        /// <summary>
        ///     WiX <c>RegistrySearch</c> key. Combined with <c>Bitness="always32"</c> this is the official
        ///     32-bit-view marker at <see cref="RegistryPath" />.
        /// </summary>
        public const string RegistryKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App";

        /// <summary>
        ///     Documented hive path, including <c>WOW6432Node</c>, written by the x64 ASP.NET Core
        ///     Runtime and Hosting Bundle installers.
        /// </summary>
        public const string RegistryPath =
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App";

        public const string RegistryValueName = RuntimeVersion;

        public const string MissingMessage =
            "Vestigo requires the ASP.NET Core Runtime 10.0 (x64). Install the ASP.NET Core 10.0 Hosting Bundle or the x64 ASP.NET Core Runtime 10.0 from https://dotnet.microsoft.com/download/dotnet/10.0, then run this installer again.";

        public static string LaunchCondition =>
            "Installed OR " + CheckProperty + " = 0 OR (WIX_NATIVE_MACHINE = " + NativeArm64Machine + " AND " +
            RegistryProperty + ")";

        /// <summary>Mirrors <see cref="LaunchCondition" /> so the four platform cases stay unit-testable.</summary>
        public static bool IsSatisfied(bool alreadyInstalled, int? checkResult, int? nativeMachine, bool x64AspNet10Present)
        {
            if (alreadyInstalled || checkResult == 0)
            {
                return true;
            }

            return nativeMachine == NativeArm64Machine && x64AspNet10Present;
        }
    }
}
