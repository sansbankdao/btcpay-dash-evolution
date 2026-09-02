// File: Plugins/DashEvolution/Native/PlatformWalletFFI.cs
//
// P/Invoke wrapper for the Rust `platform-wallet-ffi` C ABI
// (dashpay/platform, packages/rs-platform-wallet-ffi, branch v4.2-dev).
//
// Every signature here mirrors a `#[no_mangle] pub unsafe extern "C"` entry
// point verified from the Rust source. The C header is auto-generated at
// build time via `cbindgen` (build.rs emits target/<PROFILE>/include/
// platform-wallet-ffi/platform-wallet-ffi.h); this file is the hand-written
// C# projection of that header for the shielded SYNC (receive) surface only.
//
// Spend (send) entry points live in shielded_send.rs and are deliberately
// omitted from this file — the receive/sync demo path does not need them.
// They will be added in a follow-up when unattended spend is scoped.
//
// CRITICAL CONVENTIONS (verified from error.rs):
//   - Every FFI function returns PlatformWalletFFIResult BY VALUE.
//   - PlatformWalletFFIResult owns its `message` (char* allocated on the
//     Rust heap via CString::from_raw). The caller MUST free it by passing
//     a pointer to the struct to platform_wallet_ffi_result_free. Forgetting
//     to free leaks the string. The JNI consumer (rs-unified-sdk-jni/
//     support.rs) follows this exact pattern.
//   - Handle is a plain u64 (handle.rs). NULL_HANDLE = 0.
//   - wallet_id is always 32 raw bytes (not a hex string).
//   - The shielded default address is 43 raw bytes (recipient + diversifier);
//     the host applies its own bech32m encoding.
//
// LOADING: [DllImport("platform_wallet_ffi")] — no extension, no "lib" prefix.
//   The BTCPay ManagedLoadContext.LoadUnmanagedDll override (see
//   Plugins/Dotnet/Loader/ManagedLoadContext.cs) resolves the bare name to
//   platform_wallet_ffi.dll (Windows) / libplatform_wallet_ffi.dylib (macOS)
//   / libplatform_wallet_ffi.so (Linux) using PlatformInformation's
//   prefix/suffix tables, provided the plugin registers the native asset
//   via AssemblyLoadContextBuilder.AddNativeLibrary. No existing coin
//   plugin does this today — DashEvolution is the first.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BTCPayServer.Plugins.DashEvolution.Native;

/// <summary>
/// Result codes returned by every platform-wallet-ffi function.
/// Mirrors the Rust enum PlatformWalletFFIResultCode (error.rs).
/// Values are stable ABI — do not renumber.
/// </summary>
public enum PlatformWalletFFIResultCode : int
{
    Success = 0,
    ErrorInvalidHandle = 1,
    ErrorInvalidParameter = 2,
    ErrorNullPointer = 3,
    ErrorSerialization = 4,
    ErrorDeserialization = 5,
    ErrorWalletOperation = 6,
    ErrorIdentityNotFound = 7,
    ErrorContactNotFound = 8,
    ErrorInvalidNetwork = 9,
    ErrorInvalidIdentifier = 10,
    ErrorMemoryAllocation = 11,
    ErrorUtf8Conversion = 12,
    ErrorArithmeticOverflow = 13,
    ErrorNoSelectableInputs = 14,
    ErrorWalletAlreadyExists = 15,
    ErrorShieldedBroadcastFailed = 16,
    ErrorShieldedBroadcastUnconfirmed = 17,
    ErrorShieldedSpendUnconfirmed = 18,
    ErrorShieldedNoRecordedAnchor = 19,
    ErrorTransactionBroadcastUnconfirmed = 20,
    ErrorAddressNonceMismatch = 21,
    ErrorCoreInsufficientFunds = 22,
    ErrorAssetLockNotTracked = 23,
    ErrorAssetLockAlreadyConsumed = 24,
    ErrorAssetLockFundingMismatch = 25,
    ErrorTransactionBroadcastRejected = 26,
    ErrorShutdownIncomplete = 27,
    ErrorAssetLockInsufficientFunds = 29,
    ErrorSigningKeyUnavailable = 31,
    ErrorStaleReservationToken = 34,
    ErrorReservationTokenConsumed = 35,
}

/// <summary>
/// By-value return type of every platform-wallet-ffi function.
/// The message pointer is owned by Rust and MUST be freed via
/// PlatformWalletFFIResultFree after reading. See PlatformWalletFFIResultCode
/// for the success/error interpretation. (error.rs)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlatformWalletFFIResult
{
    public PlatformWalletFFIResultCode Code;
    public IntPtr Message; // char* — NUL-terminated UTF-8, or IntPtr.Zero on success

    public bool IsSuccess => Code == PlatformWalletFFIResultCode.Success;

    /// <summary>
    /// Reads the message as a UTF-8 string, or returns null if the pointer is null.
    /// Does NOT free — the caller must dispose the result.
    /// </summary>
    public string? ReadMessage()
    {
        if (Message == IntPtr.Zero)
        {
            return null;
        }

        // Rust CString is NUL-terminated UTF-8 without a length prefix.
        // Marshal.PtrToStringUTF8 reads up to the NUL.
        return Marshal.PtrToStringUTF8(Message);
    }
}

/// <summary>
/// Per-wallet outcome of one shielded sync pass. Mirrors
/// ShieldedSyncWalletResultFFI (shielded_types.rs). Delivered via the
/// event-handler callback (on_shielded_sync_completed), not returned by a
/// function — the host receives it during the sync loop.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ShieldedSyncWalletResultFFI
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] WalletId;            // [u8; 32]

    [MarshalAs(UnmanagedType.U1)]
    public bool Success;               // true only on a successful sync

    [MarshalAs(UnmanagedType.U1)]
    public bool Skipped;               // wallet had no bound shielded sub-wallet (excludes success)

    [MarshalAs(UnmanagedType.U1)]
    public bool CooldownSkip;          // caught-up cooldown skipped the pass — keep prior cached balance

    public uint NewNotes;              // new decrypted notes this pass
    public ulong TotalScanned;         // total encrypted notes scanned
    public uint NewlySpent;            // notes newly detected as spent this pass
    public ulong Balance;              // unspent shielded balance after the pass

    public IntPtr ErrorMessage;        // const char* — valid until the callback returns; null on success/skip
}

/// <summary>
/// Disposable wrapper around PlatformWalletFFIResult that frees the Rust-allocated
/// message string on dispose. Use with `using`:
///     using var r = PlatformWalletFFI.ConfigureShielded(...);
///     if (!r.IsSuccess) { ... r.ReadMessage() ... }
/// The Dispose call invokes platform_wallet_ffi_result_free, which is null-safe
/// and matches the Drop impl in error.rs.
/// </summary>
public readonly struct PlatformWalletFFIResultHandle : IDisposable
{
    private readonly PlatformWalletFFIResult _result;

    internal PlatformWalletFFIResultHandle(PlatformWalletFFIResult result)
    {
        _result = result;
    }

    public PlatformWalletFFIResultCode Code => _result.Code;
    public bool IsSuccess => _result.IsSuccess;
    public string? Message => _result.ReadMessage();

    /// <summary>
    /// Throws a PlatformWalletFFIException if the result is an error.
    /// Call after the native call returns so the string is still live.
    /// </summary>
    public void EnsureSuccess()
    {
        if (!_result.IsSuccess)
        {
            var msg = _result.ReadMessage() ?? _result.Code.ToString();
            throw new PlatformWalletFFIException(_result.Code, msg);
        }
    }

    public void Dispose()
    {
        // platform_wallet_ffi_result_free takes *mut PlatformWalletFFIResult.
        // We alloc a native copy of the by-value struct, pass its pointer to
        // the free function (which is null-safe and only touches .message),
        // then free the native copy. Avoids `unsafe`/`fixed` so the file
        // compiles without <AllowUnsafeBlocks>.
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PlatformWalletFFIResult>());
        try
        {
            Marshal.StructureToPtr(_result, ptr, fDeleteOld: false);
            PlatformWalletFFI.PlatformWalletFFIResultFree(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}

public class PlatformWalletFFIException : Exception
{
    public PlatformWalletFFIResultCode Code { get; }
    public PlatformWalletFFIException(PlatformWalletFFIResultCode code, string message)
        : base($"[{code}] {message}")
    {
        Code = code;
    }
}

/// <summary>
/// Opaque handle to a platform-wallet manager. Equals u64 on the Rust side
/// (handle.rs). NULL_HANDLE = 0 means "no manager".
/// </summary>
public static class Handle
{
    public const ulong NULL_HANDLE = 0;
}

/// <summary>
/// The shielded SYNC (receive) surface of platform-wallet-ffi.
/// Spend entry points (shielded_send.rs) are intentionally not declared here.
/// </summary>
public static class PlatformWalletFFI
{
    private const string LibName = "platform_wallet_ffi";

    // -----------------------------------------------------------------------
    // Result lifecycle (error.rs)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Free a PlatformWalletFFIResult's message string. Null-safe. Matches the
    /// Drop impl in error.rs — call this after reading every result returned
    /// by value, or use PlatformWalletFFIResultHandle (which calls it on Dispose).
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_ffi_result_free")]
    public static extern void PlatformWalletFFIResultFree(IntPtr result);

    // -----------------------------------------------------------------------
    // Shielded setup (shielded_sync.rs)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Install the shielded coordinator on this manager with a SQLite path for
    /// the commitment-tree store. Must run before bind_shielded.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_configure_shielded")]
    public static extern PlatformWalletFFIResult ConfigureShielded(
        ulong handle,
        [MarshalAs(UnmanagedType.LPStr)] string dbPathCstr);

    /// <summary>
    /// Bind the shielded sub-wallet for one or more accounts. Tries the seedless
    /// path first (rebind from persisted viewing keys); only if an account has no
    /// persisted row does it resolve the mnemonic via the resolver handle. After
    /// the first successful bind, subsequent binds are seedless and need no
    /// mnemonic — the resolver handle may be null on the seedless path.
    /// accountsLen must be in 1..=64.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_bind_shielded")]
    public static extern PlatformWalletFFIResult BindShielded(
        ulong handle,
        byte[] walletIdBytes,            // *const u8, 32 bytes
        IntPtr mnemonicResolverHandle,   // *mut MnemonicResolverHandle — BY VALUE. dash_sdk_mnemonic_resolver_create
                                         //   returns this pointer; passing `ref` adds one indirection
                                         //   (*mut *mut MnemonicResolverHandle) and Rust dereferences 8 bytes
                                         //   of stack as the 16-byte vtable struct → segfault (error 15).
                                         //   Verified live on mainnet DAPI.
        uint[] accountsPtr,              // *const u32, accountsLen entries
        UIntPtr accountsLen);

    // -----------------------------------------------------------------------
    // Shielded address (shielded_sync.rs)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Read the default Orchard payment address (diversifier index 0, external
    /// scope) for the given account on the bound shielded sub-wallet. Writes 43
    /// raw bytes (recipient + diversifier) to outBytes43 when present. The host
    /// applies its own bech32m encoding. *outPresent is false when the account
    /// is known but unbound.
    /// NOTE: the FFI currently exposes only index 0. A per-invoice diversified
    /// address requires a small upstream addition wrapping address_at(index).
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_default_address")]
    public static extern PlatformWalletFFIResult ShieldedDefaultAddress(
        ulong handle,
        byte[] walletIdBytes,            // *const u8, 32 bytes
        uint account,
        byte[] outBytes43,               // *mut u8, 43 writable bytes
        [MarshalAs(UnmanagedType.U1)] out bool outPresent);

    // -----------------------------------------------------------------------
    // Shielded sync lifecycle (shielded_sync.rs)
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_start")]
    public static extern PlatformWalletFFIResult ShieldedSyncStart(ulong handle);

    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_stop")]
    public static extern PlatformWalletFFIResult ShieldedSyncStop(ulong handle);

    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_is_running")]
    public static extern PlatformWalletFFIResult ShieldedSyncIsRunning(
        ulong handle,
        [MarshalAs(UnmanagedType.U1)] out bool outRunning);

    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_is_syncing")]
    public static extern PlatformWalletFFIResult ShieldedSyncIsSyncing(
        ulong handle,
        [MarshalAs(UnmanagedType.U1)] out bool outSyncing);

    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_last_sync_unix_seconds")]
    public static extern PlatformWalletFFIResult ShieldedSyncLastSyncUnixSeconds(
        ulong handle,
        out ulong outLastSyncUnix);

    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_set_interval")]
    public static extern PlatformWalletFFIResult ShieldedSyncSetInterval(
        ulong handle,
        ulong intervalSeconds);

    /// <summary>
    /// Force an immediate sync pass across all bound wallets. Dispatches onto
    /// the runtime's 8 MB-stack worker threads (NOT the calling thread) — safe
    /// to call from a .NET thread-pool thread.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_sync_now")]
    public static extern PlatformWalletFFIResult ShieldedSyncSyncNow(ulong handle);

    /// <summary>
    /// Force an immediate sync pass on a single wallet. Does not set the
    /// manager's global is_syncing flag.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_sync_wallet")]
    public static extern PlatformWalletFFIResult ShieldedSyncWallet(
        ulong handle,
        byte[] walletIdBytes);          // *const u8, 32 bytes

    /// <summary>
    /// Reset Rust-side shielded state: stop the sync loop, drop wallet
    /// registrations, reset the shared tree to empty. The SQLite file stays
    /// on disk. The host MUST wipe its own per-wallet persistence AFTER this
    /// returns ok, then the next bind_shielded repopulates and the next sync
    /// pass re-saves notes. Cold-resyncs from index 0 on the shared tree.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_shielded_clear")]
    public static extern PlatformWalletFFIResult ShieldedClear(ulong handle);

    // -----------------------------------------------------------------------
    // Wallet creation + id derivation (manager-create path)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Create a PlatformWallet in the manager from a BIP-39 mnemonic + network
    /// and read its 32-byte wallet id in one call. On success out_wallet_handle
    /// is a handle into PLATFORM_WALLET_STORAGE and out_wallet_id is filled.
    /// account_options: 0 = None (no default accounts), 1 = Default (matches
    /// iOS `createDefaultAccounts ? 1 : 0`; see SwiftDashSDK
    // PlatformWalletManager.swift:940). Use 0 for the receive-only demo.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_create_wallet_from_mnemonic")]
    public static extern PlatformWalletFFIResult CreateWalletFromMnemonic(
        ulong managerHandle,
        [MarshalAs(UnmanagedType.LPStr)] string mnemonic,
        int network,                              // FFINetwork
        uint accountOptions,                     // 0=None, 1=Default
        out ulong outWalletHandle,               // Handle* — owned by manager
        byte[] outWalletId);                      // uint8_t (*)[32] — 32 bytes
}