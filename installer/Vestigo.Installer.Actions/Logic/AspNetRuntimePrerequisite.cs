#nullable enable
using System;
using System.Collections.Generic;

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
        public const string DetectAction = "VestigoDetectX64AspNet10";

        /// <summary><c>IMAGE_FILE_MACHINE_ARM64</c> (<c>0xAA64</c>), as set by <c>util:QueryNativeMachine</c>.</summary>
        public const int NativeArm64Machine = 43620;

        /// <summary><c>IMAGE_FILE_MACHINE_AMD64</c> (<c>0x8664</c>).</summary>
        public const int NativeX64Machine = 34404;

        /// <summary>
        ///     32-bit-view key whose value names are installed x64 ASP.NET versions. Combined with
        ///     <c>RegistryView.Registry32</c> this is the official marker at <see cref="RegistryPath" />.
        /// </summary>
        public const string RegistryKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App";

        /// <summary>
        ///     Documented hive path, including <c>WOW6432Node</c>, written by the x64 ASP.NET Core
        ///     Runtime and Hosting Bundle installers.
        /// </summary>
        public const string RegistryPath =
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App";

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

        /// <summary>
        ///     True when any value name is <c>{major}.{minor}.{patch}</c> for <see cref="RuntimeVersion" />
        ///     (for example <c>10.0.0</c> or <c>10.0.14</c>).
        /// </summary>
        public static bool HasCompatibleRuntime(IEnumerable<string>? valueNames)
        {
            if (valueNames is null)
            {
                return false;
            }

            foreach (string name in valueNames)
            {
                if (IsCompatibleValueName(name))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsCompatibleValueName(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string[] expected = RuntimeVersion.Split('.');
            string[] actual = name.Split('.');
            if (expected.Length < 2 || actual.Length != 3 || actual[2].Length == 0)
            {
                return false;
            }

            if (actual[0] != expected[0] || actual[1] != expected[1])
            {
                return false;
            }

            foreach (char digit in actual[2])
            {
                if (!char.IsDigit(digit))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
