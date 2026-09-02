// File: Plugins/DashEvolution/Native/PlatformAddressFFI.cs
//
// P/Invoke wrapper for the Rust `platform-wallet-ffi` C ABI — the
// platform-address (Evolution / DIP-17) surface. The companion
// PlatformWalletFFI.cs wraps the SHIELDED surface; this file wraps the
// unshielded Platform Address surface (BLAST balance sync, credits,
// transfer/withdraw). Both live in the same native library
// (libplatform_wallet_ffi) and share the PlatformWalletFFIResult / code enum
// and Handle conventions documented in PlatformWalletFFI.cs — this file
// reuses those types rather than redeclaring them.
//
// Every signature here mirrors a `#[no_mangle] pub unsafe extern "C"` entry
// point verified from the built C header (target/release/include/
// platform-wallet-ffi/platform-wallet-ffi.h, cbindgen auto-generated). Line
// numbers cited per function are from that header on the VM build.
//
// CRITICAL CONVENTIONS (same as PlatformWalletFFI.cs):
//   - Every FFI function returns PlatformWalletFFIResult BY VALUE.
//   - The result owns its `message` (char* on the Rust heap); free it via
//     platform_wallet_ffi_result_free or PlatformWalletFFIResultHandle.
//   - Handle is a plain u64 (NULL_HANDLE = 0).
//   - Out-arrays are Rust-allocated; the matching `platform_address_wallet_free_*`
//     function MUST be called to release them (see per-function doc comments).
//
// SCOPE: receive + sync + lifecycle + balance/credit queries are declared
// here. Spend-side entry points (transfer / withdraw / preflight /
// fund_from_asset_lock) are also declared for completeness but are
// UNVERIFIED for the demo — the demo settles shielded only; platform-address
// spend is Phase 2.

using System;
using System.Runtime.InteropServices;

namespace BTCPayServer.Plugins.DashEvolution.Native;

// -----------------------------------------------------------------------
// Shared C-ABI types (reuse from PlatformWalletFFI.cs where identical)
// -----------------------------------------------------------------------

/// <summary>
/// Input selection strategy for platform-address transfers/withdrawals.
/// Mirrors Rust enum InputSelectionType (header line 276).
/// Values are stable ABI — do not renumber.
/// </summary>
public enum InputSelectionType : int
{
    Explicit = 0,
    ExplicitWithNonces = 1,
    Auto = 2,
}

// -----------------------------------------------------------------------
// Platform-address structs (header lines 1330..1380, 3782..3894)
// -----------------------------------------------------------------------

/// <summary>
/// A platform address: address_type + 20-byte hash. Mirrors
/// PlatformAddressFFI (header line 1330).
/// NOTE: the transfer/withdraw surface only honors address_type == 0 (P2pkh)
/// on the way IN; 1 (P2sh) round-trips OUT but is rejected as an input/output.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlatformAddressFFI
{
    public byte AddressType;                 // 0 = P2pkh (in), 1 = P2sh (out only)

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public byte[] Hash;                      // 20-byte address hash
}

/// <summary>
/// An address with its balance entry — used for outputs and balance queries.
/// Mirrors AddressBalanceEntryFFI (header line 1343).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AddressBalanceEntryFFI
{
    public PlatformAddressFFI Address;

    public ulong Balance;                    // credited balance for this address

    public uint Nonce;                       // anti-replay nonce; 0 when unknown/unused

    public uint AccountIndex;                // DIP-17 account index

    public uint AddressIndex;                // derivation index within the account

    public ulong AsOfHeight;                 // platform height `balance` is current as of
}

/// <summary>
/// Configuration for a BLAST balance-sync pass. Mirrors AddressSyncConfigFFI
/// (header line 3782). Pass has_config=false to use engine defaults.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AddressSyncConfigFFI
{
    public ulong MinPrivacyCount;
    public uint MaxConcurrentRequests;
    public uint MaxIterations;
    public ulong FullRescanAfterTimeS;
}

/// <summary>
/// One address discovered (found) during a BLAST sync pass. Mirrors
/// FoundAddressEntryFFI (header line 3812).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FoundAddressEntryFFI
{
    public uint Index;
    public PlatformAddressFFI Address;
    public uint Nonce;
    public ulong Balance;
}

/// <summary>
/// One address queried but absent (no on-chain state) during a BLAST sync
/// pass. Mirrors AbsentAddressEntryFFI (header line 3820).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AbsentAddressEntryFFI
{
    public uint Index;
    public PlatformAddressFFI Address;
}

/// <summary>
/// Sync metrics for a BLAST pass. Mirrors AddressSyncMetricsFFI (header
/// line 3826). Diagnostic only — do not gate behavior on these.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AddressSyncMetricsFFI
{
    public uint TrunkQueries;
    public uint BranchQueries;
    public uint TotalElementsSeen;
    public uint TotalProofBytes;
    public uint Iterations;
    public uint CompactedQueries;
    public uint RecentQueries;
    public uint RecentEntriesReturned;
    public uint CompactedEntriesReturned;
}

/// <summary>
/// Result of a single-account BLAST balance-sync pass. Mirrors
/// AddressSyncResultFFI (header line 3839). The two arrays (`found`,
/// `absent`) are Rust-allocated; release via
/// PlatformAddressWalletFreeSyncResult.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AddressSyncResultFFI
{
    public IntPtr Found;                     // *mut FoundAddressEntryFFI
    public UIntPtr FoundCount;

    public IntPtr Absent;                     // *mut AbsentAddressEntryFFI
    public UIntPtr AbsentCount;

    public ulong CheckpointHeight;
    public ulong NewSyncHeight;
    public ulong NewSyncTimestamp;
    public ulong LastKnownRecentBlock;

    public AddressSyncMetricsFFI Metrics;
}

/// <summary>
/// Changeset of updated address balances from a transfer/withdraw. Mirrors
/// PlatformAddressChangeSetFFI (header line 3806). The `updated` array is
/// Rust-allocated; release via PlatformAddressWalletFreeChangeset.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlatformAddressChangeSetFFI
{
    public IntPtr Updated;                    // *mut AddressBalanceEntryFFI
    public UIntPtr UpdatedCount;
}

/// <summary>
/// Preflight result for an AUTO withdrawal. Mirrors WithdrawalPreflightFFI
/// (header line 3882). Fields `net_withdrawable` and `estimated_fee` are
/// valid ONLY when `can_withdraw == true`; otherwise 0.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WithdrawalPreflightFFI
{
    [MarshalAs(UnmanagedType.U1)]
    public bool CanWithdraw;

    public ulong NetWithdrawable;             // valid only when can_withdraw
    public ulong EstimatedFee;               // valid only when can_withdraw
}

// -----------------------------------------------------------------------
// Platform-address FFI surface (header lines 6864..7046)
// -----------------------------------------------------------------------

/// <summary>
/// The platform-address (Evolution / DIP-17) surface of platform-wallet-ffi.
/// Receive/sync/lifecycle/balance functions are the demo-relevant ones;
/// spend-side entry points (transfer / withdraw / preflight /
/// fund_from_asset_lock) are declared for completeness but are UNVERIFIED
/// for the demo (Phase 2).
/// </summary>
public static class PlatformAddressWalletFFI
{
    private const string LibName = "platform_wallet_ffi";

    // -------------------------------------------------------------------
    // Balance / address queries (receive-side)
    // -------------------------------------------------------------------

    /// <summary>
    /// Read the total platform credits across the wallet (header 6920).
    /// Writes the credit total (duffs) to out_credits on success.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_total_credits")]
    public static extern PlatformWalletFFIResult TotalCredits(
        ulong handle,
        out ulong outCredits);

    /// <summary>
    /// Enumerate every address with its current balance (header 6950).
    /// out_entries receives a Rust-allocated array of AddressBalanceEntryFFI
    /// of length out_count. The caller MUST release it via
    /// PlatformAddressWalletFreeAddressBalances.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_addresses_with_balances")]
    public static extern PlatformWalletFFIResult AddressesWithBalances(
        ulong handle,
        out IntPtr outEntries,
        out UIntPtr outCount);

    /// <summary>
    /// Free an array returned by AddressesWithBalances (header 6953).
    /// Pass the original pointer and count.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_free_address_balances")]
    public static extern void FreeAddressBalances(
        IntPtr entries,
        UIntPtr count);

    /// <summary>
    /// Minimum selectable input amount for a platform-address transfer
    /// (header 6932). Writes duffs to out_min_input_amount.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_min_input_amount")]
    public static extern PlatformWalletFFIResult MinInputAmount(
        ulong handle,
        out ulong outMinInputAmount);

    /// <summary>
    /// Minimum output amount for a platform-address transfer (header 6944).
    /// Writes duffs to out_min_output_amount.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_min_output_amount")]
    public static extern PlatformWalletFFIResult MinOutputAmount(
        ulong handle,
        out ulong outMinOutputAmount);

    // -------------------------------------------------------------------
    // Sync (BLAST) — receive-side
    // -------------------------------------------------------------------

    /// <summary>
    /// Run a BLAST balance-sync pass for the wallet (header 6893). Pass
    /// has_config=false (config pointer null) to use engine defaults.
    /// The result's found/absent arrays MUST be released via
    /// FreeSyncResult.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_sync_balances")]
    public static extern PlatformWalletFFIResult SyncBalances(
        ulong handle,
        [MarshalAs(UnmanagedType.U1)] bool hasConfig,
        ref AddressSyncConfigFFI config,
        out AddressSyncResultFFI outResult);

    /// <summary>
    /// Restore a persisted sync checkpoint before the next sync pass
    /// (header 6917). Lets the engine resume from (sync_height,
    /// sync_timestamp, last_known_recent_block) instead of rescanning
    /// from genesis.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_restore_sync_state")]
    public static extern PlatformWalletFFIResult RestoreSyncState(
        ulong handle,
        ulong syncHeight,
        ulong syncTimestamp,
        ulong lastKnownRecentBlock);

    /// <summary>
    /// Add a provider key for a DIP-17 account (header 6910). Must run
    /// before the first sync pass for a new account so BLAST knows which
    /// derivation path to poll.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_add_provider")]
    public static extern PlatformWalletFFIResult AddProvider(
        ulong handle,
        uint accountIndex);

    /// <summary>
    /// Free an AddressSyncResultFFI's found/absent arrays (header 6959).
    /// Pass a pointer to the result struct.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_free_sync_result")]
    public static extern void FreeSyncResult(
        ref AddressSyncResultFFI result);

    // -------------------------------------------------------------------
    // Lifecycle (receive-side)
    // -------------------------------------------------------------------

    /// <summary>
    /// Destroy the platform-address wallet bound to this manager handle
    /// (header 6907). Releases Rust-side state for the wallet. Call on
    /// shutdown before the manager itself is dropped.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_destroy")]
    public static extern PlatformWalletFFIResult Destroy(ulong handle);

    /// <summary>
    /// Free a PlatformAddressChangeSetFFI's updated array (header 6956).
    /// Pass a pointer to the changeset.
    /// </summary>
    [DllImport(LibName, EntryPoint = "platform_address_wallet_free_changeset")]
    public static extern void FreeChangeset(
        ref PlatformAddressChangeSetFFI changeset);

    // -------------------------------------------------------------------
    // Spend-side (transfer / withdraw / preflight / fund_from_asset_lock)
    // -------------------------------------------------------------------
    // These are declared for completeness but are UNVERIFIED for the demo.
    // The demo settles shielded only; platform-address spend is Phase 2.
    // The complex struct pointer parameters (ExplicitInputFFI,
    // ExplicitInputWithNonceFFI, FeeStrategyStepFFI, SignerHandle,
    // MnemonicResolverHandle, OutPointFFI, FundingAddressEntryFFI) are
    // passed as opaque IntPtr until dedicated C# structs are written in
    // the Phase 2 spend wrapper.

    /// <summary>PHASE 2 / UNVERIFIED. Transfer credits between platform
    /// addresses (header 6904). Outputs is an array of AddressBalanceEntryFFI.
    /// explicit_inputs / nonce_inputs / fee_strategy are opaque arrays.
    /// signer_address_handle is in/out. out_changeset receives the
    /// resulting balance changes (free via FreeChangeset).
    [DllImport(LibName, EntryPoint = "platform_address_wallet_transfer")]
    public static extern PlatformWalletFFIResult Transfer(
        ulong handle,
        uint accountIndex,
        InputSelectionType inputType,
        IntPtr explicitInputs, UIntPtr explicitInputsCount,
        IntPtr nonceInputs, UIntPtr nonceInputsCount,
        IntPtr outputs, UIntPtr outputsCount,
        IntPtr feeStrategy, UIntPtr feeStrategyCount,
        ref IntPtr signerAddressHandle,
        out PlatformAddressChangeSetFFI outChangeset);

    /// <summary>PHASE 2 / UNVERIFIED. Withdraw credits to a raw Core script
    /// (header 6962). output_script is a byte array of length output_script_len.
    /// core_fee_per_byte is the L1 fee rate. Other params opaque as above.
    [DllImport(LibName, EntryPoint = "platform_address_wallet_withdraw")]
    public static extern PlatformWalletFFIResult Withdraw(
        ulong handle,
        uint accountIndex,
        InputSelectionType inputType,
        IntPtr explicitInputs, UIntPtr explicitInputsCount,
        IntPtr nonceInputs, UIntPtr nonceInputsCount,
        byte[] outputScript, UIntPtr outputScriptLen,
        uint coreFeePerByte,
        IntPtr feeStrategy, UIntPtr feeStrategyCount,
        ref IntPtr signerAddressHandle,
        out PlatformAddressChangeSetFFI outChangeset);

    /// <summary>PHASE 2 / UNVERIFIED. Withdraw credits to a Core P2PKH
    /// address string (header 6989). core_address is a NUL-terminated C
    /// string. Other params opaque as above.
    [DllImport(LibName, EntryPoint = "platform_address_wallet_withdraw_to_address")]
    public static extern PlatformWalletFFIResult WithdrawToAddress(
        ulong handle,
        uint accountIndex,
        InputSelectionType inputType,
        IntPtr explicitInputs, UIntPtr explicitInputsCount,
        IntPtr nonceInputs, UIntPtr nonceInputsCount,
        [MarshalAs(UnmanagedType.LPStr)] string coreAddress,
        uint coreFeePerByte,
        IntPtr feeStrategy, UIntPtr feeStrategyCount,
        ref IntPtr signerAddressHandle,
        out PlatformAddressChangeSetFFI outChangeset);

    /// <summary>PHASE 2 / UNVERIFIED. Preflight an AUTO withdrawal
    /// (header 7046). Writes a WithdrawalPreflightFFI to out.
    [DllImport(LibName, EntryPoint = "platform_address_wallet_preflight_withdrawal")]
    public static extern PlatformWalletFFIResult PreflightWithdrawal(
        ulong handle,
        uint accountIndex,
        uint coreFeePerByte,
        out WithdrawalPreflightFFI outPreflight);

    /// <summary>PHASE 2 / UNVERIFIED. Fund from an asset-lock signer
    /// (header 6864). amount_duffs is the funding amount. account_index
    /// and platform_account_index select the accounts. addresses is an
    /// array of FundingAddressEntryFFI (opaque). core_signer_handle is
    /// out. out_changeset receives balance changes (free via FreeChangeset).
    [DllImport(LibName, EntryPoint = "platform_address_wallet_fund_from_asset_lock_signer")]
    public static extern PlatformWalletFFIResult FundFromAssetLockSigner(
        ulong handle,
        ulong amountDuffs,
        uint accountIndex,
        uint platformAccountIndex,
        IntPtr addresses, UIntPtr addressesCount,
        IntPtr feeStrategy, UIntPtr feeStrategyCount,
        ref IntPtr signerAddressHandle,
        ref IntPtr coreSignerHandle,
        out PlatformAddressChangeSetFFI outChangeset);

    /// <summary>PHASE 2 / UNVERIFIED. Resume a fund-from-asset-lock signer
    /// session after an out-point (header 6884). out_point is an
    /// OutPointFFI (opaque). Other params opaque as above.
    [DllImport(LibName, EntryPoint = "platform_address_wallet_resume_fund_from_asset_lock_signer")]
    public static extern PlatformWalletFFIResult ResumeFundFromAssetLockSigner(
        ulong handle,
        IntPtr outPoint,
        uint platformAccountIndex,
        IntPtr addresses, UIntPtr addressesCount,
        IntPtr feeStrategy, UIntPtr feeStrategyCount,
        ref IntPtr signerAddressHandle,
        ref IntPtr coreSignerHandle,
        out PlatformAddressChangeSetFFI outChangeset);
}
