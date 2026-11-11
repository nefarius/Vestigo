using Microsoft.Dism;

namespace Vestigo.WindowsServicing;

public interface IServicingPackageSource
{
    BoundedList<ServicingPackage> List(int limit);
}

/// <summary>Documented DISM package API; no localized output parsing and no repair entry points.</summary>
public sealed class ServicingPackageSource : IServicingPackageSource
{
    private static readonly object LifecycleLock = new();

    public BoundedList<ServicingPackage> List(int limit)
    {
        SystemDismLibrary.Configure();
        return WithSession(
            () => DismApi.Initialize(DismLogLevel.LogErrors),
            DismApi.OpenOnlineSession,
            session =>
            {
                // Microsoft.Dism copies the native array and frees it before returning.
                DismPackageCollection native = DismApi.GetPackages(session);
                List<ServicingPackage> packages = native.Take(limit).Select(package => new ServicingPackage(
                    package.PackageName, StateName((int)package.PackageState),
                    (int)package.PackageState, (int)package.ReleaseType)).ToList();
                return new BoundedList<ServicingPackage>(packages, native.Count, native.Count > packages.Count);
            },
            DismApi.Shutdown);
    }

    // DISM initialization is process-wide. Keep the whole lifetime serialized, including
    // disposal and shutdown, even when callers use separate source/worker instances.
    internal static TResult WithSession<TSession, TResult>(Action initialize, Func<TSession> open,
        Func<TSession, TResult> read, Action shutdown) where TSession : IDisposable
    {
        lock (LifecycleLock)
        {
            initialize();
            try
            {
                using TSession session = open();
                return read(session);
            }
            finally
            {
                shutdown();
            }
        }
    }

    internal static string StateName(int state) => state switch
    {
        0 => "notPresent", 1 => "uninstallPending", 2 => "staged", 3 => "removed",
        4 => "installed", 5 => "installPending", 6 => "superseded",
        7 => "partiallyInstalled", _ => "unknown"
    };
}
