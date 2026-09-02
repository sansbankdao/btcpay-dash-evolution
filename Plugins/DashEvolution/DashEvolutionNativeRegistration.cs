// File: Plugins/DashEvolution/DashEvolutionNativeRegistration.cs
//
// Registers the Rust native library (libplatform_wallet_ffi) with BTCPay's
// plugin loader so [DllImport("platform_wallet_ffi")] in PlatformWalletFFI.cs
// and PlatformAddressFFI.cs resolves at runtime. This is the call site for
// the dormant AddNativeLibrary hook
// (Plugins/Dotnet/Loader/AssemblyLoadContextBuilder.cs:251) that no existing
// coin plugin exercises — DashEvolution is the first.
//
// The native assets are packaged as a NuGet (DashEvolution.Native) with the
// standard .NET runtimes/native layout that NativeLibrary.AppLocalPath
// expects (see NativeLibrary.cs doc comment: "runtimes/linux-x64/native/
// libsqlite.so"). The loader's LoadUnmanagedDll override
// (ManagedLoadContext.cs:220) walks prefix+name+suffix per OS using
// PlatformInformation's tables:
//   Windows: "" prefix,  ".dll"
//   macOS:   "" / "lib", ".dylib"
//   Linux:   "" / "lib", ".so" / ".so.1"
//
// RIDs and the lib filenames below are fixed by the Rust crate's
// `cargo build --release --target <rid>` output (crate-type = ["cdylib"]).
// The bare DllImport name "platform_wallet_ffi" (no extension, no lib prefix)
// is resolved by the loader to the platform-specific filename.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BTCPayServer.Plugins.Dotnet;
using BTCPayServer.Plugins.Dotnet.Loader;
// Alias the LibraryModel NativeLibrary (the BTCPay plugin-loader model) so
// that the unqualified `NativeLibrary` below unambiguously means the BCL's
// System.Runtime.InteropServices.NativeLibrary (SetDllImportResolver / TryLoad).
using LibraryModelNativeLibrary = BTCPayServer.Plugins.Dotnet.LibraryModel.NativeLibrary;

namespace BTCPayServer.Plugins.DashEvolution;

/// <summary>
/// Wires the native library into a plugin's AssemblyLoadContextBuilder.
/// Called from the plugin bootstrap path (where the builder is constructed)
/// before the load context is built — AddNativeLibrary must run while the
/// builder is still mutable.
/// </summary>
public static class DashEvolutionNativeRegistration
{
    public const string NativePackageId = "DashEvolution.Native";
    public const string NativePackageVersion = "1.0.0";

    // The bare DllImport name used across all FFI wrapper files
    // (PlatformWalletFFI.cs / PlatformAddressFFI.cs / PlatformWalletManagerFFI.cs).
    private const string DllName = "platform_wallet_ffi";
    // Env var carrying an absolute path override to the built native lib.
    private const string EnvLibPath = "DASHE_NATIVE_LIB";
    private static int _resolverRegistered; // 0 = pending, 1 = done

    /// <summary>
    /// One-time registration of a DllImportResolver on the DEFAULT
    /// AssemblyLoadContext for the BTCPayServer assembly. REQUIRED because
    /// DashEvolution is a BUILT-IN plugin (compiled into BTCPayServer.dll —
    /// see DefaultConfiguration.cs:166 where AltcoinsPlugin is `new`-ed and
    /// Execute-d directly, NOT loaded via PluginLoader). Its [DllImport]s
    /// therefore resolve in the default ALC, which never invokes the
    /// AssemblyLoadContextBuilder.AddNativeLibrary hook below (that hook
    /// only fires for plugins loaded into a collectible context by
    /// PluginLoader.CreateLoadContextBuilder). AddNativeLibrary is kept
    /// for if DashEvolution is ever extracted to a dynamically-loaded plugin.
    ///
    /// This resolver maps DllName to the built native lib via, in priority:
    ///   1. env DASHE_NATIVE_LIB (absolute path override)
    ///   2. the Rust release build output (~/Workspace/platform/target/release)
    ///   3. runtimes/&lt;rid&gt;/native/ next to the app (NuGet-packaged layout)
    ///   4. the app base dir (app-local)
    /// Returns IntPtr.Zero (fall through to default probing) for any other
    /// library name so unrelated [DllImport]s still resolve normally.
    /// Idempotent + thread-safe (Interlocked.Exchange).
    /// </summary>
    public static void TryRegisterDllImportResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 1)
            return; // already wired — SetDllImportResolver throws on double-set
        NativeLibrary.SetDllImportResolver(
            typeof(DashEvolutionNativeRegistration).Assembly,
            new DllImportResolver(ResolvePlatformWalletFfi));
    }

    private static IntPtr ResolvePlatformWalletFfi(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.Ordinal))
            return IntPtr.Zero; // not ours — defer to default resolution
        var path = ResolveLibPath();
        if (path != null && NativeLibrary.TryLoad(path, out var handle))
            return handle;
        // Last resort: let the runtime's own probing run (LD_LIBRARY_PATH etc.).
        return IntPtr.Zero;
    }

    private static string? ResolveLibPath()
    {
        try
        {
            // 1. explicit env override (production / CI)
            var env = Environment.GetEnvironmentVariable(EnvLibPath);
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                return env;

            var fileName = NativeFileName();

            // 2. Rust release build output (VM dev path)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rustPath = Path.Combine(home, "Workspace", "platform", "target", "release", fileName);
            if (File.Exists(rustPath))
                return rustPath;

            // 3. runtimes/<rid>/native/ next to the app (NuGet-packaged layout)
            var appDir = AppContext.BaseDirectory;
            var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx"
                    : "linux-x64";
            var packaged = Path.Combine(appDir, "runtimes", rid, "native", fileName);
            if (File.Exists(packaged))
                return packaged;

            // 4. app-local (app base dir)
            var local = Path.Combine(appDir, fileName);
            if (File.Exists(local))
                return local;
        }
        catch
        {
            // Resolver must never throw — default resolution stays intact.
        }
        return null;
    }

    private static string NativeFileName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "platform_wallet_ffi.dll"
         : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "libplatform_wallet_ffi.dylib"
         : "libplatform_wallet_ffi.so";

    // -----------------------------------------------------------------------
    // Dormant-dynamic-plugin path. Active ONLY if DashEvolution is moved out
    // of the built-in AltcoinsPlugin and loaded via PluginLoader into a
    // collectible AssemblyLoadContext (where this builder hook IS invoked).
    // Kept for future extraction; not exercised today. See
    // TryRegisterDllImportResolver above for the active path.
    // -----------------------------------------------------------------------
    /// <summary>
    /// Register all three desktop RID native assets. The loader only probes
    /// the one matching the current OS/arch at runtime; the others are inert.
    /// </summary>
    public static AssemblyLoadContextBuilder AddDashEvolutionNative(
        this AssemblyLoadContextBuilder builder)
    {
        // Linux x64
        builder.AddNativeLibrary(LibraryModelNativeLibrary.CreateFromPackage(
            NativePackageId, NativePackageVersion,
            "runtimes/linux-x64/native/libplatform_wallet_ffi.so"));

        // Windows x64
        builder.AddNativeLibrary(LibraryModelNativeLibrary.CreateFromPackage(
            NativePackageId, NativePackageVersion,
            "runtimes/win-x64/native/platform_wallet_ffi.dll"));

        // macOS — both arches; the .dylib is the same fat/universal binary
        // produced by `cargo build` for aarch64-apple-darwin + x86_64-apple-darwin
        // then lipo-merged, OR two separate RID entries (kept as two for clarity).
        builder.AddNativeLibrary(LibraryModelNativeLibrary.CreateFromPackage(
            NativePackageId, NativePackageVersion,
            "runtimes/osx/native/libplatform_wallet_ffi.dylib"));

        return builder;
    }
}
