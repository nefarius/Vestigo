using Microsoft.Dism;

namespace Vestigo.WindowsServicing;

public interface IServicingPackageSource
{
    BoundedList<ServicingPackage> List(int limit);
}

/// <summary>Documented DISM package API; no localized output parsing and no repair entry points.</summary>
public sealed class ServicingPackageSource(ILogger<ServicingPackageSource>? logger = null) : IServicingPackageSource
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
            DismApi.Shutdown,
            error => logger?.LogWarning("DISM shutdown failed after an earlier error: {ErrorType}: 0x{HResult:X8}",
                error.GetType().Name, error.HResult));
    }

    // DISM initialization is process-wide. Keep the whole lifetime serialized, including
    // disposal and shutdown, even when callers use separate source/worker instances.
    internal static TResult WithSession<TSession, TResult>(Action initialize, Func<TSession> open,
        Func<TSession, TResult> read, Action shutdown, Action<Exception>? reportShutdownFailure = null) where TSession : IDisposable
    {
        lock (LifecycleLock)
        {
            initialize();
            bool failed = false;
            try
            {
                using TSession session = open();
                return read(session);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                try
                {
                    shutdown();
                }
                catch (Exception error) when (failed)
                {
                    // Preserve the original HRESULT/stack; report secondary cleanup failure
                    // without exposing native messages or replacing the operation's error.
                    reportShutdownFailure?.Invoke(error);
                }
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
