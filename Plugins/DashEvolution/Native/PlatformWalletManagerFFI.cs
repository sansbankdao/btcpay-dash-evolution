// File: Plugins/DashEvolution/Native/PlatformWalletManagerFFI.cs
//
// P/Invoke declarations for the platform-wallet MANAGER lifecycle + the
// rs-sdk-ffi entry points it depends on. Distinct from PlatformWalletFFI.cs
// (which is the shielded sync/address surface only) to keep each file
// focused, mirroring the Rust crate split (manager.rs vs shielded_sync.rs).
//
// VERIFIED against source on the VM (packages/rs-platform-wallet-ffi/src/
// manager.rs, event_handler.rs, persistence.rs; packages/rs-sdk-ffi/src/
// sdk.rs, types.rs, mnemonic_resolver.rs; dash-network/src/ffi.rs). All
// symbols confirmed present in the built libplatform_wallet_ffi.so via
// `nm -D` (see docs/HANDOFF.md §8 build-env note).
//
// LIFECYCLE (the DashEvolutionSyncService orchestration):
//   1. dash_sdk_create(config) -> DashSDKResult      (rs-sdk-ffi)
//   2. result.data = *mut SDKHandle
//   3. dash_sdk_get_inner_sdk_ptr(sdkHandle) -> *const c_void   (the raw Sdk)
//   4. platform_wallet_manager_create(sdk_ptr, persistence, event_handler, &out_handle)
//        -> PlatformWalletFFIResult, out_handle = u64 manager handle
//   5. platform_wallet_manager_configure_shielded(handle, sqlitePath)
//   6. dash_sdk_mnemonic_resolver_create(ctx, resolveCb, destroyCb) -> *mut MnemonicResolverHandle
//   7. platform_wallet_manager_bind_shielded(handle, walletId, &resolver, accounts, len)
//   8. platform_wallet_manager_shielded_default_address(handle, walletId, account, out43, &present)
//   9. platform_wallet_manager_shielded_sync_start(handle)
//  10. ... sync results arrive via on_shielded_sync_completed_fn callback ...
//  11. platform_wallet_manager_shielded_sync_stop(handle)
//  12. platform_wallet_manager_destroy(handle)
//  13. dash_sdk_mnemonic_resolver_destroy(resolver)
//  14. dash_sdk_destroy(sdkHandle)
//
// ALL structs here are blittable (LayoutKind.Sequential, no automatic
// marshalling) so they can be passed by-ref to native code and allocated
// via Marshal.AllocHGlobal.

using System;
using System.Runtime.InteropServices;

namespace BTCPayServer.Plugins.DashEvolution.Native;

// -----------------------------------------------------------------------
// rs-sdk-ffi: network + config + result
// -----------------------------------------------------------------------

/// <summary>
/// FFI network enum. Verified: dash-network/src/ffi.rs — Mainnet=0,
/// Testnet=1, Devnet=2, Regtest=3. repr(C).
/// </summary>
public enum DashFFINetwork : int
{
    Mainnet = 0,
    Testnet = 1,
    Devnet = 2,
    Regtest = 3,
}

/// <summary>
/// DashSDKConfig (rs-sdk-ffi/src/types.rs:65). repr(C). The SDK reads
/// dapi_addresses/quorum_url during the create call only (borrows + copies).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DashSDKConfig
{
    public DashFFINetwork Network;
    public IntPtr DapiAddresses;           // *const c_char (NUL-terminated UTF-8, or null for mock)
    [MarshalAs(UnmanagedType.U1)] public bool SkipAssetLockProofVerification;
    public uint RequestRetryCount;
    public ulong RequestTimeoutMs;
    public IntPtr QuorumUrl;               // *const c_char (null/empty = default)
    public uint PlatformVersion;           // 0 = auto-detect
}

/// <summary>
/// DashSDKResultDataType (rs-sdk-ffi/src/types.rs:107). repr(C).
/// Only NoData/Handle variants matter for dash_sdk_create (success() sets
/// NoData but carries the SDKHandle* in .data — see sdk.rs:182).
/// </summary>
public enum DashSDKResultDataType : int
{
    NoData = 0,
    String = 1,
    BinaryData = 2,
    ResultIdentityHandle = 3,
    ResultDocumentHandle = 4,
    ResultDataContractHandle = 5,
    IdentityBalanceMap = 6,
    ResultPublicKeyHandle = 7,
    AddressInfo = 8,
    AddressInfoMap = 9,
}

/// <summary>
/// DashSDKResult (rs-sdk-ffi/src/types.rs:413). repr(C). On success, .error
/// is null and .data holds the returned handle/value. On error, .data is null
/// and .error points to a DashSDKError (caller must free via
/// dash_sdk_free_error — UNVERIFIED, not needed for the demo path which
/// treats any null .data as failure).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DashSDKResult
{
    public DashSDKResultDataType DataType;
    public IntPtr Data;        // *mut c_void — SDKHandle* on create success
    public IntPtr Error;       // *mut DashSDKError — null on success
}

// -----------------------------------------------------------------------
// rs-sdk-ffi: SDK lifecycle
// -----------------------------------------------------------------------

public static partial class DashSdkFFI
{
    private const string LibName = "platform_wallet_ffi";

    /// <summary>
    /// Create an SDK instance. On success result.Data is a *mut SDKHandle
    /// (cast). dapi_addresses null/empty → mock SDK. (sdk.rs:106)
    /// </summary>
    [DllImport(LibName, EntryPoint = "dash_sdk_create")]
    public static extern DashSDKResult Create(in DashSDKConfig config);

    /// <summary>
    /// Create a REAL (non-mock) SDK for mainnet/testnet. Sets up the trusted
    /// context provider automatically — the plain dash_sdk_create only works
    /// for the mock path (null/empty DAPI); with real DAPI addresses it fails
    /// with code 99 "context provider is not set". dash_sdk_create_trusted
    /// (sdk.rs:307) builds the TrustedHttpContextProvider using the
    /// network-derived default quorum lookup endpoints when quorum_url is
    /// null (mainnet/testnet). VERIFIED live on the VPS against
    /// https://45.135.180.70:443 — returns a working mainnet SDK.
    /// </summary>
    [DllImport(LibName, EntryPoint = "dash_sdk_create_trusted")]
    public static extern DashSDKResult CreateTrusted(in DashSDKConfig config);

    /// <summary>
    /// Extract the raw inner Sdk pointer from an SDKHandle. This is the
    /// sdk_ptr platform_wallet_manager_create wants. Valid as long as the
    /// SDKHandle is alive. (sdk.rs:547)
    /// </summary>
    [DllImport(LibName, EntryPoint = "dash_sdk_get_inner_sdk_ptr")]
    public static extern IntPtr GetInnerSdkPtr(IntPtr sdkHandle);

    /// <summary>
    /// Free an SDKHandle. Null-safe. (sdk.rs:533)
    /// </summary>
    [DllImport(LibName, EntryPoint = "dash_sdk_destroy")]
    public static extern void DestroySdk(IntPtr sdkHandle);
}

// -----------------------------------------------------------------------
// rs-sdk-ffi: mnemonic resolver (the vtable for "fetch mnemonic for wallet_id")
// -----------------------------------------------------------------------

/// <summary>
/// Function pointer: fetch the BIP-39 mnemonic for wallet_id into the buffer.
/// Returns 0=SUCCESS, 1=NOT_FOUND, 2=BUFFER_TOO_SMALL, 3=OTHER.
/// (mnemonic_resolver.rs:96 — MnemonicResolveCallback)
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int MnemonicResolveCallback(
    IntPtr ctx,
    IntPtr walletIdBytes,    // *const u8, 32 bytes
    IntPtr outMnemonicUtf8,  // *mut c_char, capacity 1024
    UIntPtr outCapacity,     // 1024
    IntPtr outLen);          // *mut usize

/// <summary>
/// Function pointer: destructor for the resolver ctx. Called exactly once.
/// (mnemonic_resolver.rs — destroy slot of MnemonicResolverVTable)
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void MnemonicResolverDestroyCallback(IntPtr ctx);

/// <summary>
/// MnemonicResolverVTable (mnemonic_resolver.rs:120). repr(C).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MnemonicResolverVTable
{
    public MnemonicResolveCallback Resolve;
    public MnemonicResolverDestroyCallback Destroy;
}

/// <summary>
/// MnemonicResolverHandle (mnemonic_resolver.rs:127). repr(C). Opaque —
/// we only pass the pointer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MnemonicResolverHandle
{
    public IntPtr Ctx;
    public IntPtr VTable;   // *const MnemonicResolverVTable
}

public static partial class DashSdkFFI
{
    /// <summary>
    /// Create a mnemonic resolver wrapping a host-owned ctx + callback pair.
    /// Returns *mut MnemonicResolverHandle (null on bad args). (mnemonic_resolver.rs)
    /// </summary>
    [DllImport(LibName, EntryPoint = "dash_sdk_mnemonic_resolver_create")]
    public static extern IntPtr CreateMnemonicResolver(
        IntPtr ctx,
        MnemonicResolveCallback resolveCallback,
        MnemonicResolverDestroyCallback destroyCallback);

    /// <summary>
    /// Destroy a resolver handle (calls the destroy callback once). Null-safe.
    /// </summary>
    [DllImport(LibName, EntryPoint = "dash_sdk_mnemonic_resolver_destroy")]
    static extern void DestroyMnemonicResolverInternal(IntPtr resolver);

    public static void DestroyMnemonicResolver(IntPtr resolver)
    {
        if (resolver != IntPtr.Zero) DestroyMnemonicResolverInternal(resolver);
    }
}

// -----------------------------------------------------------------------
// platform-wallet-ffi: event handler callbacks vtable
// -----------------------------------------------------------------------

/// <summary>
/// Shielded-sync-completed callback. Receives an array of
/// ShieldedSyncWalletResultFFI (one per bound wallet) + the pass's Unix
/// timestamp. (event_handler.rs:130 — on_shielded_sync_completed_fn)
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ShieldedSyncCompletedCallback(
    IntPtr ctx,
    IntPtr results,           // *const ShieldedSyncWalletResultFFI
    UIntPtr count,
    ulong syncUnixSeconds);

/// <summary>
/// Wallet-event callback (balance update, tx received, etc.). event_json is
/// UTF-8, length-prefixed by event_json_len. UNVERIFIED what the JSON schema
/// is — may carry per-note details for proper invoice matching (Phase 2).
/// (event_handler.rs:112 — on_wallet_event_fn)
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void WalletEventCallback(
    IntPtr ctx,
    IntPtr eventJson,         // *const u8
    UIntPtr eventJsonLen);

/// <summary>
/// Error callback. (event_handler.rs:115 — on_error_fn)
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ErrorCallback(IntPtr ctx, IntPtr errorMsg); // *const c_char

/// <summary>
/// Vtable destructor — called by Rust exactly once when the manager's last
/// worker drops. Frees the GCHandle pinned in ctx. (event_handler.rs:180)
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void EventHandlerReleaseCallback(IntPtr ctx);

/// <summary>
/// EventHandlerCallbacks (event_handler.rs:106). repr(C). Layout verified:
/// 8 fields × 8 bytes = 64 bytes. All Option&lt;fn&gt; map to IntPtr (Zero = None).
/// We set context + on_shielded_sync_completed_fn + release_fn; rest Zero.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct EventHandlerCallbacks
{
    public IntPtr Context;                                   // *mut c_void
    public IntPtr OnWalletEventFn;                           // Option<fn> → IntPtr
    public IntPtr OnErrorFn;                                 // Option<fn> → IntPtr
    public IntPtr OnPlatformAddressSyncCompletedFn;          // Option<fn> → IntPtr
    public IntPtr OnShieldedSyncCompletedFn;                 // Option<fn> → IntPtr
    public IntPtr OnShieldedSyncProgressFn;                  // Option<fn> → IntPtr
    public IntPtr OnShieldedTreeProgressFn;                  // Option<fn> → IntPtr
    public IntPtr ReleaseFn;                                 // Option<fn> → IntPtr
}

// -----------------------------------------------------------------------
// platform-wallet-ffi: persistence capabilities (manager-create path)
// -----------------------------------------------------------------------

// Stable version-1 C bit values (platform-wallet-ffi.h:71-91). Mirrors the
// upstream [`PersistenceCapabilities`] enum. The manager intersects a declared
// bit with the matching callback slot in PersistenceCallbacks — declaring a
// bit whose callback is null is a hard error ("missing mask 0x9" at
// bind_shielded time). Verified live: bind_shielded requires
// ATOMIC_CHANGESETS (1<<0 = 0x1) + SHIELDED_VIEWING_KEYS (1<<3 = 0x8) = 0x9.
public static class PersistenceCapability
{
    public const ulong AtomicChangesets = 1UL << 0;       // 0x1 — needs on_changeset_begin_fn + on_changeset_end_fn
    public const ulong ShieldedViewingKeys = 1UL << 3;    // 0x8 — needs on_persist_shielded_viewing_keys_fn + on_load_shielded_viewing_keys_fn + on_load_shielded_viewing_keys_free_fn
    public const ulong RequiredMask = AtomicChangesets | ShieldedViewingKeys; // 0x9
}

/// <summary>
/// PersistenceCapabilitiesFFI (platform-wallet-ffi.h:3434). repr(C).
/// `version` MUST be PLATFORM_WALLET_PERSISTENCE_CAPABILITIES_VERSION (= 1);
/// unknown versions fail closed to no capabilities. `reserved` is ignored.
/// `bits` is the OR of declared PersistenceCapability flags.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PersistenceCapabilitiesFFI
{
    public uint Version;
    public uint Reserved;
    public ulong Bits;
}

// PersistenceCallbacks stub callback delegates (cdecl). These are the MINIMUM
// set required by RequiredMask (0x9). All are no-ops in-memory: load returns
// zero entries so bind_shielded falls back to the mnemonic resolver to derive
// viewing keys; persist is a no-op (keys are re-derived on every restart).
// PersistenceCallbacks is built as a raw native buffer with these fn pointers
// written at the exact byte offsets verified from the header (38 fields × 8
// bytes; field N at offset (N-1)*8). See PlatformWalletManagerFFI offsets.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int OnChangesetBeginFn(IntPtr context, IntPtr walletId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int OnChangesetEndFn(IntPtr context, IntPtr walletId, [MarshalAs(UnmanagedType.U1)] bool success);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int OnPersistShieldedViewingKeysFn(IntPtr context, IntPtr walletId, IntPtr entries, UIntPtr count);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int OnLoadShieldedViewingKeysFn(IntPtr context, IntPtr outEntries, IntPtr outCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void OnLoadShieldedViewingKeysFreeFn(IntPtr context, IntPtr entries, UIntPtr count);

// -----------------------------------------------------------------------
// platform-wallet-ffi: manager lifecycle
// -----------------------------------------------------------------------

public static partial class PlatformWalletManagerFFI
{
    private const string LibName = "platform_wallet_ffi";

    // PersistenceCallbacks (platform-wallet-ffi.h:2930) is a 38-field vtable of
    // fn pointers (field N at byte offset (N-1)*8 → 38*8 = 304 bytes). We build
    // it as a zeroed native buffer and write only the fn pointers the
    // RequiredMask (0x9) capability declaration demands; the rest stay null
    // (None). Buffer 512 bytes exceeds the 304-byte struct so Rust reads in
    // bounds. PersistenceCallbacks is NOT declared as a C# struct because its
    // full field list is large and only these slots matter to the shielded demo:
    //   field 2  on_changeset_begin_fn                  offset   8  (bit 0x1)
    //   field 3  on_changeset_end_fn                   offset  16  (bit 0x1)
    //   field 23 on_persist_shielded_viewing_keys_fn   offset 176  (bit 0x8)
    //   field 32 on_load_shielded_viewing_keys_fn      offset 248  (bit 0x8)
    //   field 33 on_load_shielded_viewing_keys_free_fn offset 256  (bit 0x8)
    //   field 38 release_fn                           offset 296  (context null → None valid)
    internal const int PersistenceCallbacksBufferSize = 512;
    internal const int OffsetOnChangesetBeginFn = 8;
    internal const int OffsetOnChangesetEndFn = 16;
    internal const int OffsetOnPersistShieldedViewingKeysFn = 176;
    internal const int OffsetOnLoadShieldedViewingKeysFn = 248;
    internal const int OffsetOnLoadShieldedViewingKeysFreeFn = 256;

    /// <summary>
    /// Create a platform-wallet manager. (manager.rs:68) Returns Success and
    /// writes the u64 handle to outHandle. Both vtable pointers MUST be
    /// non-null (check_ptr! rejects null). ctx may be null iff release_fn is
    /// also null (None).
    /// NOTE: this plain entry point declares NO persistence capabilities, so
    /// bind_shielded rejects it with "missing mask 0x9". Use
    /// CreateWithPersistenceCapabilities for the shielded path. Kept for
    /// non-shielded / mock paths that do not bind a shielded sub-wallet.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_create")]
    public static extern PlatformWalletFFIResult Create(
        IntPtr sdkPtr,                                   // *const c_void (from GetInnerSdkPtr)
        IntPtr persistenceCallbacks,                     // *const PersistenceCallbacks (zeroed buffer OK)
        ref EventHandlerCallbacks eventHandlerCallbacks, // by-ref (blittable struct)
        out ulong outHandle);

    /// <summary>
    /// Create a platform-wallet manager with an explicit, versioned persistence
    /// capability declaration (manager.rs; header:6470). This is the ONLY create
    /// entry point that satisfies bind_shielded: the plain Create declares no
    /// capabilities, so bind_shielded rejects with "missing mask 0x9" (requires
    /// atomic_changesets + shielded_viewing_keys). `persistenceCapabilities`
    /// points to a PersistenceCapabilitiesFFI with Version=1 and
    /// Bits=RequiredMask (0x9); the matching callback slots in
    /// `persistenceCallbacks` MUST be non-null (Rust intersects declared bits
    /// with the callback structure). Unknown versions fail closed to no caps.
    /// Verified live on mainnet DAPI (https://45.135.180.70:443).
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_create_with_persistence_capabilities")]
    public static extern PlatformWalletFFIResult CreateWithPersistenceCapabilities(
        IntPtr sdkPtr,                                   // *const c_void (from GetInnerSdkPtr)
        IntPtr persistenceCallbacks,                     // *const PersistenceCallbacks (raw buffer w/ fn ptrs at offsets)
        ref EventHandlerCallbacks eventHandlerCallbacks, // by-ref (blittable struct)
        ref PersistenceCapabilitiesFFI persistenceCapabilities, // by-ref (blittable struct)
        out ulong outHandle);

    /// <summary>
    /// Destroy a manager. Runs the full lifecycle shutdown. (manager.rs:654)
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_wallet_manager_destroy")]
    public static extern PlatformWalletFFIResult Destroy(ulong handle);
}
