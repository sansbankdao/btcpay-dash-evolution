// File: Plugins/DashEvolution/DashEvolutionSyncService.cs
//
// IHostedService that owns the single shielded wallet's native lifecycle and
// marks BTCPay invoices paid when shielded (Orchard) notes are received.
//
// RECEIVE IS UNATTENDED (verified vs dashwallet-ios): the MnemonicResolver
// supplies the configured BIP-39 mnemonic to the Rust derivation pipeline
// synchronously — no PIN, no biometric, no Keychain. Spend is attended on
// iOS; BTCPay is receive-only for the demo, so no spend path here.
//
// PAYMENT MATCHING (demo scope): the FFI sync result carries per-wallet
// balance + NewNotes but NOT per-note nullifiers or recipient diversifiers
// (no FFI to enumerate received notes exists — verified). So matching uses
// the BALANCE-DELTA approach: delta = Balance - priorBalance. The oldest
// unpaid invoice addressed to this wallet's default shielded address is
// marked paid with the delta. LIMITATION: concurrent invoices sharing the
// default address (index 0) cannot be distinguished — fine for a sequential
// demo. Per-invoice diversified addresses need the ~10-line upstream
// `shielded_address_at(index)` addition (noted in HANDOFF.md §7).
//
// BASELINE SEEDING (restart-safe): _priorBalance is in-memory only, so a
// process/container restart would otherwise see a delta equal to the ENTIRE
// wallet balance and book it against an open invoice (the "PaidOver" false
// positive — hit twice in production on routine `docker restart`). To make
// restarts categorically safe, the FIRST completed sync pass after process
// start only seeds the baseline and performs NO invoice matching; matching
// begins from the second pass. Trade-off: a payment received while the
// process was down is not matched after boot; persistence-layer catch-up is
// a later production concern.
//
// AMOUNT UNIT: Platform (shielded) amounts are in CREDITS, where
// 1 DASH = 10^8 duffs and CREDITS_PER_DUFF = 1000 (rs-dpp credits.rs:42),
// so 1 DASH = 10^11 credits. The FFI `ShieldedSyncWalletResultFFI.balance`
// and `shielded_transfer` `amount` are BOTH in credits (platform-wallet-ffi.h:7188).
// We divide by 1e11 for the BTCPay Amount (base unit = DASH). NOTE: do NOT
// confuse with Dash Core L1 duffs (1e8) — the transparent Dash plugin uses
// duffs, this shielded path uses credits.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.DashEvolution.Native;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.DashEvolution;

/// <summary>
/// Singleton background service. Created by DI (registered in
/// DashEvolutionPlugin). One instance owns one native manager + resolver.
/// Also implements IDashEvolutionWalletService so the payment handler fetches
/// the shielded default address from this same singleton (the stub
/// DashEvolutionWalletService is retired — step 4, HANDOFF.md §7).
/// </summary>
public class DashEvolutionSyncService : IHostedService, IDashEvolutionWalletService
{
    // 1 DASH = 1e11 platform credits (1e8 duffs * 1000 CREDITS_PER_DUFF).
    // The FFI shielded balance + transfer amounts are in credits.
    private const decimal CreditsPerDash = 100_000_000_000m;
    private const decimal DuffsPerDash = 100_000_000m; // Dash Core L1 only
    private const string CryptoCode = "DASHE";

    private readonly DashEvolutionSyncOptions _options;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<DashEvolutionSyncService> _logger;

    // PaymentService and PaymentMethodHandlerDictionary are resolved LAZILY via
    // the service provider (not the ctor) to break a DI CIRCULAR DEPENDENCY that
    // would otherwise stack-overflow at host build:
    //   DashEvolutionSyncService (this) ctor → PaymentService /
    //   PaymentMethodHandlerDictionary → enumerates every IPaymentMethodHandler
    //   → DashEvolutionPaymentMethodHandler ctor → IDashEvolutionWalletService
    //   → DashEvolutionSyncService (this) … ∞ recursion (verified live via a
    //   dotnet-dump: clrstack showed VisitCallSiteMain alternating with the two
    //   DashEvolutionPlugin.Init factory lambdas until stack exhaustion).
    // Both are only consumed at RUNTIME in TryMarkInvoicePaid (after the host has
    // started and this singleton already exists), so by the time the Lazy<>
    // values are first resolved every singleton — including this one and the
    // handler it indirectly needs — is already constructed and cached. Thread-
    // safe Lazy (ExecutionAndPublication) because the sync completion callback
    // runs on Rust worker threads (ProcessResults is Task.Run off-thread).
    private readonly IServiceProvider _serviceProvider;
    private readonly Lazy<PaymentService> _paymentService;
    private readonly Lazy<PaymentMethodHandlerDictionary> _handlers;

    // Direct DB context factory (singleton, no DI path back to this service)
    // used in TryMarkInvoicePaid to insert payment rows WITHOUT resolving
    // PaymentService / PaymentMethodHandlerDictionary — which deadlock on a
    // Rust worker thread (see the field comment block above for the cycle).
    private readonly ApplicationDbContextFactory _dbContextFactory;

    // Native handles. _sdkHandle is *mut SDKHandle; _managerHandle is the u64
    // manager handle; _resolver is the DashEvolutionMnemonicResolver (owns its
    // own *mut MnemonicResolverHandle + pinned delegates).
    private IntPtr _sdkHandle;
    private ulong _managerHandle;
    private DashEvolutionMnemonicResolver? _resolver;
    private IntPtr _persistenceCallbacksBuffer;

    // Pinned delegates (held as fields so the GC does not collect them while
    // the native manager is alive). The EventHandlerCallbacks vtable stores
    // IntPtrs built from these via Marshal.GetFunctionPointerForDelegate.
    private readonly ShieldedSyncCompletedCallback _syncCompletedCb;
    private readonly PlatformAddressSyncCompletedCallback _platformAddressCompletedCb;
    private readonly EventHandlerReleaseCallback _releaseCb;
    // Persistence stub callbacks (pinned for the manager's lifetime — the raw
    // PersistenceCallbacks buffer holds fn pointers derived from these; if the
    // GC collects them Rust will call freed memory). All no-ops: load returns
    // zero entries so bind_shielded derives viewing keys from the mnemonic
    // resolver; persist is a no-op (keys re-derived each restart). Required by
    // the 0x9 capability mask (atomic_changesets + shielded_viewing_keys).
    private readonly OnChangesetBeginFn _persistChangesetBeginCb;
    private readonly OnChangesetEndFn _persistChangesetEndCb;
    private readonly OnPersistShieldedViewingKeysFn _persistShieldedVkCb;
    private readonly OnLoadShieldedViewingKeysFn _loadShieldedVkCb;
    private readonly OnLoadShieldedViewingKeysFreeFn _loadShieldedVkFreeCb;
    private GCHandle _selfHandle;

    // Default shielded address (bech32m, index 0) for the configured wallet.
    private string? _defaultShieldedAddress;
    private byte[]? _walletIdBytes;

    // Transparent (DIP-17 platform address) handles: _walletHandle is a clone
    // from platform_wallet_manager_get_wallet (released by WalletDestroy);
    // _platformAddressHandle is a clone from platform_wallet_get_platform
    // (released by PlatformAddressWalletFFI.Destroy). 0 = not acquired.
    private ulong _walletHandle;
    private ulong _platformAddressHandle;

    // Prior balance per wallet (bytes hex → credits) for delta detection.
    private readonly ConcurrentDictionary<string, ulong> _priorBalance = new();

    // Transparent delta detection: prior PER-ADDRESS balances (hash160 hex →
    // credits) + the same first-pass baseline-seed restart-safety rule as the
    // shielded matcher (a restart must not book the whole wallet balance).
    private readonly ConcurrentDictionary<string, ulong> _priorPlatformBalances = new();
    private bool _platformBaselineSeeded;
    // Set when NextUnusedReceiveAddress throws EntryPointNotFound (stale .so
    // without the patched export) — Transparent receive disabled, shielded
    // unaffected.
    private volatile bool _nextUnusedUnavailable;

    // Baseline seeding flag — see the file header. The first completed sync
    // pass after process start only seeds _priorBalance (no invoice marking);
    // matching begins from the second pass.
    private bool _baselineSeeded;

    private readonly CancellationTokenSource _cts = new();
    private int _disposed; // 0 = live, 1 = disposing
    private readonly PaymentMethodId _pmi;

    public DashEvolutionSyncService(
        IOptions<DashEvolutionSyncOptions> options,
        InvoiceRepository invoiceRepository,
        ApplicationDbContextFactory dbContextFactory,
        IServiceProvider serviceProvider,
        EventAggregator eventAggregator,
        ILogger<DashEvolutionSyncService> logger)
    {
        _options = options.Value;
        _invoiceRepository = invoiceRepository;
        _dbContextFactory = dbContextFactory;
        _serviceProvider = serviceProvider;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _pmi = PaymentTypes.CHAIN.GetPaymentMethodId(CryptoCode);
        // Defer the two cycle-inducing deps (see field comment above) until
        // first runtime use — they resolve against the fully-built provider.
        _paymentService = new Lazy<PaymentService>(
            () => _serviceProvider.GetRequiredService<PaymentService>(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _handlers = new Lazy<PaymentMethodHandlerDictionary>(
            () => _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        // Pin the trampolines now; they capture `this`.
        _syncCompletedCb = OnSyncCompleted;
        _platformAddressCompletedCb = OnPlatformAddressSyncCompleted;
        _releaseCb = OnRelease;
        // Persistence stubs are static no-ops but still pinned as fields so the
        // GC never moves/collects them while native code holds their fn pointers.
        _persistChangesetBeginCb = OnChangesetBegin;
        _persistChangesetEndCb = OnChangesetEnd;
        _persistShieldedVkCb = OnPersistShieldedViewingKeys;
        _loadShieldedVkCb = OnLoadShieldedViewingKeys;
        _loadShieldedVkFreeCb = OnLoadShieldedViewingKeysFree;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Mnemonic))
        {
            _logger.LogWarning("DashEvolution sync disabled: Mnemonic not configured");
            return;
        }
        // WalletIdHex is optional: if blank, derive it deterministically from
        // the mnemonic via the FFI (platform_wallet_manager_create_wallet_from_mnemonic,
        // header:6534). This also CREATES+registers the wallet in the manager,
        // which bind_shielded requires. Verified deterministic per (mnemonic,
        // network): wallet_lifecycle.rs:877 test comment.
        if (string.IsNullOrWhiteSpace(_options.WalletIdHex))
            _logger.LogInformation("DashEvolution will derive+create wallet from mnemonic at startup");
        else if (_options.WalletIdHex.Length != 64)
        {
            _logger.LogError("DashEvolution WalletIdHex must be 64 hex chars, got {Len}", _options.WalletIdHex.Length);
            return;
        }

        try
        {
            BuildSdk();
            BuildManager();
            ConfigureAndBind();
            await EnsureDefaultAddressAsync();
            StartSyncLoop();
            StartTransparentPipeline();
            _logger.LogInformation("DashEvolution sync started for wallet {W} address {A}",
                _options.WalletIdHex, _defaultShieldedAddress);
        }
        catch (PlatformWalletFFIException ex)
        {
            _logger.LogError(ex, "DashEvolution FFI init failed [{Code}] {Msg}", ex.Code, ex.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginDispose();
        try
        {
            // Transparent (BLAST) shutdown BEFORE the manager destroy: stop
            // the loop, then release the platform-address + wallet clones.
            try
            {
                if (_managerHandle != 0)
                {
                    using var pstop = new PlatformWalletFFIResultHandle(
                        PlatformAddressWalletFFI.PlatformAddressSyncStop(_managerHandle));
                }
                if (_platformAddressHandle != 0)
                {
                    using var pd = new PlatformWalletFFIResultHandle(
                        PlatformAddressWalletFFI.Destroy(_platformAddressHandle));
                }
                if (_walletHandle != 0)
                {
                    using var wd = new PlatformWalletFFIResultHandle(
                        PlatformAddressWalletFFI.WalletDestroy(_walletHandle));
                }
                _platformAddressHandle = 0;
                _walletHandle = 0;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping transparent sync"); }

            if (_managerHandle != 0)
            {
                using var stop = new PlatformWalletFFIResultHandle(PlatformWalletFFI.ShieldedSyncStop(_managerHandle));
                using var destroy = new PlatformWalletFFIResultHandle(PlatformWalletManagerFFI.Destroy(_managerHandle));
                _managerHandle = 0;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Error stopping shielded sync"); }

        _resolver?.Dispose();
        _resolver = null;

        if (_sdkHandle != IntPtr.Zero)
        {
            DashSdkFFI.DestroySdk(_sdkHandle);
            _sdkHandle = IntPtr.Zero;
        }
        if (_persistenceCallbacksBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_persistenceCallbacksBuffer);
            _persistenceCallbacksBuffer = IntPtr.Zero;
        }
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        await Task.CompletedTask;
    }

    // -------------------------------------------------------------------
    // IDashEvolutionWalletService — serves the payment handler's address
    // fetch (DashEvolutionPaymentMethodHandler.ConfigurePrompt). Returns the
    // shielded default address derived + cached during StartAsync
    // (EnsureDefaultAddressAsync). The handler wraps any exception in
    // PaymentMethodUnavailableException, so an unbound or mismatched wallet
    // yields a soft skip (method off for that invoice).
    // -------------------------------------------------------------------

    public Task<string> GetShieldedDefaultAddressAsync(
        DashEvolutionPaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        // The sync service owns exactly ONE wallet (DashEvolutionSyncOptions).
        // A store configured with a different WalletIdHex cannot be served —
        // its notes would never be matched by this singleton's sync loop.
        // Reject it so the handler marks the method unavailable rather than
        // showing an address the sync loop isn't watching. Hex compare is
        // case-insensitive (config may be any case; ours is lowercase).
        if (!string.IsNullOrWhiteSpace(config.WalletIdHex)
            && !string.Equals(config.WalletIdHex, _options.WalletIdHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Store WalletIdHex {config.WalletIdHex} does not match the synced wallet {_options.WalletIdHex}");
        }

        if (string.IsNullOrWhiteSpace(_defaultShieldedAddress))
        {
            // StartAsync hasn't completed or the wallet is unbound. The
            // handler treats this as PaymentMethodUnavailable (soft skip).
            throw new InvalidOperationException(
                "DashEvolution shielded default address not available (sync service not started or wallet unbound)");
        }

        return Task.FromResult(_defaultShieldedAddress);
    }

    // -------------------------------------------------------------------
    // Per-invoice diversified address allocation (IDashEvolutionWalletService)
    //
    // shielded_address_at(index) yields a unique Orchard address per invoice
    // (FFI added in sansbankdao/platform branch shielded-address-at). All
    // diversified indices share the account IVK, so the normal sync loop
    // detects receipts to ANY index with no extra scanning state. The index
    // counter persists to <ShieldedDbPath>.diversifier (tmp+rename) so a
    // restart never re-issues an index. Index 0 (the default address) is
    // NEVER allocated per-invoice — it stays the store-level known address.
    //
    // Fallback: a stale libplatform_wallet_ffi.so without the export throws
    // EntryPointNotFoundException on the first P/Invoke — we log once, flip
    // _addressAtUnavailable, and return the shared default address
    // (IsDiversified=false → the handler will NOT add it to
    // TrackedDestinations, preserving the shared-address PK-collision guard).
    // -------------------------------------------------------------------

    private readonly object _diversifierLock = new();
    private uint _nextDiversifierIndex = 1;
    private bool _diversifierIndexLoaded;
    private volatile bool _addressAtUnavailable;

    private string DiversifierIndexFilePath => _options.ShieldedDbPath + ".diversifier";

    public Task<DashEvolutionAddressResult> GetShieldedAddressAsync(
        DashEvolutionPaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        // Same single-wallet guard as GetShieldedDefaultAddressAsync: this
        // service owns exactly ONE wallet; a mismatched store config must be
        // a soft skip, not a silently-unwatched address.
        if (!string.IsNullOrWhiteSpace(config.WalletIdHex)
            && !string.Equals(config.WalletIdHex, _options.WalletIdHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Store WalletIdHex {config.WalletIdHex} does not match the synced wallet {_options.WalletIdHex}");
        }

        if (_addressAtUnavailable)
            return Task.FromResult(FallbackResult());

        // Allocate (and persist) the index BEFORE the FFI call. A burned
        // index on failure is harmless — diversified-address gaps cost
        // nothing; reuse would.
        var index = AllocateDiversifierIndex();
        try
        {
            var out43 = new byte[43];
            using var addr = new PlatformWalletFFIResultHandle(PlatformWalletFFI.ShieldedAddressAt(
                _managerHandle,
                _walletIdBytes ?? throw new InvalidOperationException(
                    "DashEvolution wallet not bound (sync service not started?)"),
                _options.AccountIndex, index, out43, out var present));
            addr.EnsureSuccess();
            if (!present)
                throw new PlatformWalletFFIException(PlatformWalletFFIResultCode.ErrorWalletOperation,
                    $"shielded address at diversifier index {index} not present (wallet unbound?)");
            return Task.FromResult(new DashEvolutionAddressResult
            {
                Address = Bech32m.EncodeShieldedAddress(_options.Mainnet, out43),
                DiversifierIndex = index,
                IsDiversified = true
            });
        }
        catch (EntryPointNotFoundException ex)
        {
            _addressAtUnavailable = true;
            _logger.LogWarning(ex,
                "libplatform_wallet_ffi has no platform_wallet_manager_shielded_address_at export " +
                "(stale .so). Falling back to the SHARED default shielded address for all invoices — " +
                "deploy the .so built from sansbankdao/platform branch shielded-address-at.");
            return Task.FromResult(FallbackResult());
        }
    }

    private DashEvolutionAddressResult FallbackResult()
    {
        if (string.IsNullOrWhiteSpace(_defaultShieldedAddress))
            throw new InvalidOperationException(
                "DashEvolution shielded default address not available (sync service not started or wallet unbound)");
        return new DashEvolutionAddressResult
        {
            Address = _defaultShieldedAddress,
            DiversifierIndex = 0,
            IsDiversified = false
        };
    }

    /// <summary>
    /// Allocate the next unused Transparent (DIP-17 platform) receive address
    /// via platform_address_wallet_next_unused_receive_address and return it
    /// base58check-encoded (Dash P2PKH: 'X…' mainnet / 'y…' testnet). Each
    /// call yields a FRESH gap-limit-aware address (the pool marks the
    /// returned index used) → per-invoice unique Transparent addresses with
    /// exact attribution. Returns IsUnique=false with a null Address when
    /// unavailable (stale .so, provider not started) — the checkout then
    /// hides the Transparent tab and the invoice is shielded-only.
    /// </summary>
    public Task<DashEvolutionTransparentAddressResult> GetTransparentAddressAsync(
        DashEvolutionPaymentMethodConfig config, CancellationToken ct = default)
    {
        // Wallet-id guard (same rule as GetShieldedAddressAsync): never hand
        // out an address for a store config bound to a different wallet id.
        if (!string.IsNullOrWhiteSpace(config.WalletIdHex) &&
            !string.IsNullOrWhiteSpace(_options.WalletIdHex) &&
            !string.Equals(config.WalletIdHex, _options.WalletIdHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"DashEvolution wallet mismatch: store configured {_options.WalletIdHex} but this host serves {config.WalletIdHex}.");
        if (_nextUnusedUnavailable || _platformAddressHandle == 0)
            return Task.FromResult(DashEvolutionTransparentAddressResult.Unavailable());
        try
        {
            using var res = new PlatformWalletFFIResultHandle(
                PlatformAddressWalletFFI.NextUnusedReceiveAddress(
                    _platformAddressHandle, (uint)_options.AccountIndex, keyClass: 0, out var addr));
            res.EnsureSuccess();
            if (addr.AddressType != 0)   // P2SH is out-only; never a receive address
                return Task.FromResult(DashEvolutionTransparentAddressResult.Unavailable());
            // DIP-0018 user-facing form: bech32m dash1… (type byte 0xb0 || hash160).
            // Deliberately NOT the X… base58 alias: that encoding is also a valid
            // L1 P2PKH address, and wallets send those on-chain (L1) where the
            // BLAST sync cannot see them (a customer payment was lost to L1 this way).
            return Task.FromResult(new DashEvolutionTransparentAddressResult
            {
                Address = Bech32m.EncodePlatformAddress(_options.Mainnet, 0xb0, addr.Hash.ToArray()),
                IsUnique = true,
            });
        }
        catch (EntryPointNotFoundException)
        {
            _nextUnusedUnavailable = true;
            _logger.LogWarning("platform_address_wallet_next_unused_receive_address missing (stale .so) — Transparent receive disabled");
            return Task.FromResult(DashEvolutionTransparentAddressResult.Unavailable());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to allocate Transparent receive address — Transparent tab hidden for this invoice");
            return Task.FromResult(DashEvolutionTransparentAddressResult.Unavailable());
        }
    }

    private uint AllocateDiversifierIndex()
    {
        lock (_diversifierLock)
        {
            if (!_diversifierIndexLoaded)
            {
                _diversifierIndexLoaded = true;
                try
                {
                    if (File.Exists(DiversifierIndexFilePath)
                        && uint.TryParse(File.ReadAllText(DiversifierIndexFilePath).Trim(), out var persisted)
                        && persisted >= 1)
                    {
                        _nextDiversifierIndex = persisted;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not read diversifier index file {Path}; starting at {Index}",
                        DiversifierIndexFilePath, _nextDiversifierIndex);
                }
            }

            var index = _nextDiversifierIndex++;
            try
            {
                // tmp+rename so a crash mid-write can't leave a truncated
                // (and later re-issued) counter behind.
                var tmp = DiversifierIndexFilePath + ".tmp";
                File.WriteAllText(tmp, _nextDiversifierIndex.ToString());
                File.Move(tmp, DiversifierIndexFilePath, true);
            }
            catch (Exception ex)
            {
                // Persistence failure must not break invoice creation. The
                // in-memory counter already advanced (unique for this
                // process); a restart would resume from the last persisted
                // value and could re-issue indices allocated since.
                _logger.LogWarning(ex,
                    "Could not persist diversifier index to {Path}", DiversifierIndexFilePath);
            }
            return index;
        }
    }

    // -------------------------------------------------------------------
    // Native lifecycle
    // -------------------------------------------------------------------

    private void BuildSdk()
    {
        // DapiAddresses is REQUIRED for a real (non-mock) SDK. Null/empty →
        // dash_sdk_create_trusted has no DAPI nodes to talk to. Verified live:
        // https://45.135.180.70:443 is a mainnet DAPI node.
        if (string.IsNullOrWhiteSpace(_options.DapiAddresses))
            throw new PlatformWalletFFIException(PlatformWalletFFIResultCode.ErrorWalletOperation,
                "DashEvolution DapiAddresses is required for a real (non-mock) SDK");
        var config = new DashSDKConfig
        {
            Network = _options.Mainnet ? DashFFINetwork.Mainnet : DashFFINetwork.Testnet,
            DapiAddresses = Marshal.StringToHGlobalAnsi(_options.DapiAddresses),
            SkipAssetLockProofVerification = false,
            RequestRetryCount = 3,
            RequestTimeoutMs = 30_000,
            QuorumUrl = IntPtr.Zero,            // mainnet default quorum endpoints
            PlatformVersion = 0,
        };
        try
        {
            // dash_sdk_create_trusted (sdk.rs:307) builds the trusted context
            // provider automatically — plain dash_sdk_create only works for the
            // mock path and fails (code 99) with real DAPI addresses.
            var result = DashSdkFFI.CreateTrusted(in config);
            if (result.Data == IntPtr.Zero || result.Error != IntPtr.Zero)
                throw new PlatformWalletFFIException(PlatformWalletFFIResultCode.ErrorWalletOperation,
                    "dash_sdk_create_trusted returned no handle");
            _sdkHandle = result.Data;
        }
        finally
        {
            if (config.DapiAddresses != IntPtr.Zero)
                Marshal.FreeHGlobal(config.DapiAddresses);
        }
    }

    private void BuildManager()
    {
        var sdkPtr = DashSdkFFI.GetInnerSdkPtr(_sdkHandle);
        if (sdkPtr == IntPtr.Zero)
            throw new PlatformWalletFFIException(PlatformWalletFFIResultCode.ErrorWalletOperation, "inner sdk ptr null");

        // PersistenceCallbacks: zeroed native buffer, then write the fn pointers
        // the 0x9 capability mask (atomic_changesets + shielded_viewing_keys)
        // demands at their exact byte offsets (header:2930, 38 fields × 8). The
        // remaining slots stay null (None). bind_shielded rejects a manager
        // created with the plain Create entry point ("missing mask 0x9") — only
        // CreateWithPersistenceCapabilities declares the caps bind needs. The
        // stubs are in-memory: load returns zero entries so bind falls back to
        // the mnemonic resolver to derive the Orchard viewing keys; persist is a
        // no-op (keys are re-derived every restart). See
        // PlatformWalletManagerFFI for the offset constants.
        _persistenceCallbacksBuffer = Marshal.AllocHGlobal(PlatformWalletManagerFFI.PersistenceCallbacksBufferSize);
        for (var i = 0; i < PlatformWalletManagerFFI.PersistenceCallbacksBufferSize; i++)
            Marshal.WriteByte(_persistenceCallbacksBuffer, i, 0);
        WritePersistFnPtr(PlatformWalletManagerFFI.OffsetOnChangesetBeginFn, _persistChangesetBeginCb);
        WritePersistFnPtr(PlatformWalletManagerFFI.OffsetOnChangesetEndFn, _persistChangesetEndCb);
        WritePersistFnPtr(PlatformWalletManagerFFI.OffsetOnPersistShieldedViewingKeysFn, _persistShieldedVkCb);
        WritePersistFnPtr(PlatformWalletManagerFFI.OffsetOnLoadShieldedViewingKeysFn, _loadShieldedVkCb);
        WritePersistFnPtr(PlatformWalletManagerFFI.OffsetOnLoadShieldedViewingKeysFreeFn, _loadShieldedVkFreeCb);

        // Pin `this` so the release callback can free the GCHandle when the
        // manager's last worker drops (on destroy), even if StopAsync races.
        _selfHandle = GCHandle.Alloc(this);

        var eventHandler = new EventHandlerCallbacks
        {
            Context = GCHandle.ToIntPtr(_selfHandle),
            OnWalletEventFn = IntPtr.Zero,
            OnErrorFn = IntPtr.Zero,
            OnPlatformAddressSyncCompletedFn = Marshal.GetFunctionPointerForDelegate(_platformAddressCompletedCb),
            OnShieldedSyncCompletedFn = Marshal.GetFunctionPointerForDelegate(_syncCompletedCb),
            OnShieldedSyncProgressFn = IntPtr.Zero,
            OnShieldedTreeProgressFn = IntPtr.Zero,
            ReleaseFn = Marshal.GetFunctionPointerForDelegate(_releaseCb),
        };

        var capabilities = new PersistenceCapabilitiesFFI
        {
            Version = 1,                                  // PLATFORM_WALLET_PERSISTENCE_CAPABILITIES_VERSION
            Reserved = 0,
            Bits = PersistenceCapability.RequiredMask,    // 0x9
        };
        using var createResult = new PlatformWalletFFIResultHandle(PlatformWalletManagerFFI.CreateWithPersistenceCapabilities(
            sdkPtr, _persistenceCallbacksBuffer, ref eventHandler, ref capabilities, out _managerHandle));
        createResult.EnsureSuccess();
    }

    private void WritePersistFnPtr(int offset, Delegate d)
        => Marshal.WriteIntPtr(_persistenceCallbacksBuffer, offset, Marshal.GetFunctionPointerForDelegate(d));

    private void ConfigureAndBind()
    {
        var dbPath = string.IsNullOrWhiteSpace(_options.ShieldedDbPath)
            ? "dash_shielded.sqlite"
            : _options.ShieldedDbPath;
        using (var cfg = new PlatformWalletFFIResultHandle(PlatformWalletFFI.ConfigureShielded(_managerHandle, dbPath)))
            cfg.EnsureSuccess();

        // If WalletIdHex was supplied, just parse it. If blank, create the
        // wallet from the mnemonic via the manager (header:6534) — this both
        // registers it AND fills the 32-byte id. account_options=1 (Default):
        // creates the DIP-17 platform-payment accounts Transparent receive
        // requires (shielded binds via the resolver regardless).
        if (!string.IsNullOrWhiteSpace(_options.WalletIdHex))
        {
            _walletIdBytes = HexToBytes(_options.WalletIdHex);
        }
        else
        {
            _walletIdBytes = CreateWalletFromMnemonic();
            _options.WalletIdHex = BitConverter.ToString(_walletIdBytes).Replace("-", "").ToLowerInvariant();
            _logger.LogInformation("DashEvolution derived WalletIdHex={W}", _options.WalletIdHex);
        }

        // Resolver: one wallet → one mnemonic for the demo.
        var mnemonics = new Dictionary<string, string>
        {
            { _options.WalletIdHex, _options.Mnemonic },
        };
        _resolver = new DashEvolutionMnemonicResolver(mnemonics);
        var resolverPtr = _resolver.CreateNativeHandle();
        if (resolverPtr == IntPtr.Zero)
            throw new PlatformWalletFFIException(PlatformWalletFFIResultCode.ErrorWalletOperation, "resolver create null");

        var accounts = new uint[] { _options.AccountIndex };
        // bind_shielded takes *mut MnemonicResolverHandle BY VALUE —
        // dash_sdk_mnemonic_resolver_create returns that pointer; passing `ref`
        // adds one indirection (*mut *mut MnemonicResolverHandle) and Rust
        // dereferences 8 bytes of stack as the 16-byte vtable struct → segfault.
        using var bind = new PlatformWalletFFIResultHandle(PlatformWalletFFI.BindShielded(
            _managerHandle, _walletIdBytes!, resolverPtr, accounts, (UIntPtr)accounts.Length));
        bind.EnsureSuccess();

        if (_options.SyncIntervalSeconds > 0)
        {
            using var iv = new PlatformWalletFFIResultHandle(PlatformWalletFFI.ShieldedSyncSetInterval(_managerHandle, _options.SyncIntervalSeconds));
            iv.EnsureSuccess();
        }
    }

    private async Task EnsureDefaultAddressAsync()
    {
        var out43 = new byte[43];
        using var addr = new PlatformWalletFFIResultHandle(PlatformWalletFFI.ShieldedDefaultAddress(
            _managerHandle, _walletIdBytes!, _options.AccountIndex, out43, out var present));
        addr.EnsureSuccess();
        if (!present)
            throw new PlatformWalletFFIException(PlatformWalletFFIResultCode.ErrorWalletOperation,
                "shielded default address not present (wallet unbound?)");
        _defaultShieldedAddress = Bech32m.EncodeShieldedAddress(_options.Mainnet, out43);
        await Task.CompletedTask;
    }

    private void StartSyncLoop()
    {
        using var start = new PlatformWalletFFIResultHandle(PlatformWalletFFI.ShieldedSyncStart(_managerHandle));
        start.EnsureSuccess();
    }

    // -------------------------------------------------------------------
    // Native callbacks (run on Rust worker threads — keep them short)
    // -------------------------------------------------------------------

    private void OnSyncCompleted(IntPtr ctx, IntPtr results, UIntPtr count, ulong syncUnixSeconds)
    {
        if (_disposed == 1)
            return;
        var n = (int)count.ToUInt32();
        _logger.LogInformation(
            "OnSyncCompleted fired: count={Count} syncUnixSeconds={SyncTs} resultsPtr={ResultsPtr}",
            n, syncUnixSeconds, results.ToInt64());
        if (count == UIntPtr.Zero)
            return;
        // Snapshot the array on the worker thread, then process off-thread.
        var snapshot = new ShieldedSyncWalletResultFFI[n];
        var size = Marshal.SizeOf<ShieldedSyncWalletResultFFI>();
        for (var i = 0; i < n; i++)
        {
            var ptr = (IntPtr)(results.ToInt64() + i * size);
            snapshot[i] = Marshal.PtrToStructure<ShieldedSyncWalletResultFFI>(ptr);
        }
        // Log each wallet result for diagnostics before processing.
        foreach (var r in snapshot)
        {
            var walletHex = BitConverter.ToString(r.WalletId).Replace("-", "").ToLowerInvariant();
            var errMsg = r.ErrorMessage != IntPtr.Zero ? Marshal.PtrToStringAnsi(r.ErrorMessage) : null;
            _logger.LogInformation(
                "SyncResult wallet={Wallet} success={Success} skipped={Skipped} cooldown={Cooldown} " +
                "balance={Balance} newNotes={NewNotes} totalScanned={TotalScanned} newlySpent={NewlySpent} err={Err}",
                walletHex, r.Success, r.Skipped, r.CooldownSkip,
                r.Balance, r.NewNotes, r.TotalScanned, r.NewlySpent, errMsg);
        }
        // Fire-and-forget off the Rust worker. _cts guards shutdown.
        _ = Task.Run(() => ProcessResults(snapshot), _cts.Token);
    }

    private void OnRelease(IntPtr ctx)
    {
        // Called by Rust exactly once when the manager's last worker drops.
        // The GCHandle is also freed in StopAsync (idempotent: Free is a no-op
        // if already freed / not allocated). We do NOT free here to avoid a
        // race with StopAsync's Marshal reads; StopAsync frees it last.
    }

    // -------------------------------------------------------------------
    // Persistence stub callbacks (run on Rust worker threads — keep short)
    // -------------------------------------------------------------------

    private static int OnChangesetBegin(IntPtr context, IntPtr walletId) => 0;

    private static int OnChangesetEnd(IntPtr context, IntPtr walletId, bool success) => 0;

    private static int OnPersistShieldedViewingKeys(IntPtr context, IntPtr walletId, IntPtr entries, UIntPtr count) => 0;

    private static int OnLoadShieldedViewingKeys(IntPtr context, IntPtr outEntries, IntPtr outCount)
    {
        // No persisted viewing keys → tell Rust there are zero entries so
        // bind_shielded falls back to the mnemonic resolver to derive them.
        Marshal.WriteIntPtr(outEntries, IntPtr.Zero);
        Marshal.WriteInt64(outCount, 0);
        return 0;
    }

    private static void OnLoadShieldedViewingKeysFree(IntPtr context, IntPtr entries, UIntPtr count) { }

    // -------------------------------------------------------------------
    // Payment matching
    // -------------------------------------------------------------------

    private async Task ProcessResults(ShieldedSyncWalletResultFFI[] results)
    {
        try
        {
        foreach (var r in results)
        {
            if (!r.Success || r.CooldownSkip || r.Skipped)
                continue;
            var walletHex = BitConverter.ToString(r.WalletId).Replace("-", "").ToLowerInvariant();
            var prev = _priorBalance.GetOrAdd(walletHex, _ => 0);
            _logger.LogInformation(
                "ProcessResults wallet={Wallet} balance={Balance} prev={Prev} newNotes={NewNotes} totalScanned={TotalScanned}",
                walletHex, r.Balance, prev, r.NewNotes, r.TotalScanned);
            if (!_baselineSeeded)
            {
                // First completed sync pass after process start: seed ONLY.
                // The wallet's whole pre-existing balance must never be
                // booked as a payment after a container/process restart.
                _baselineSeeded = true;
                _priorBalance[walletHex] = r.Balance;
                _logger.LogInformation(
                    "Baseline seeded on first sync pass: balance={Balance} credits wallet={Wallet} — no invoice matching on first pass (restart-safe)",
                    r.Balance, walletHex);
                continue;
            }
            if (r.Balance <= prev)
            {
                // No increase — either no new funds or a spend we don't track
                // (demo is receive-only). Refresh baseline anyway.
                _priorBalance[walletHex] = r.Balance;
                continue;
            }
            var deltaCredits = r.Balance - prev;
            _priorBalance[walletHex] = r.Balance;
            _logger.LogInformation("Balance DELTA detected: {Delta} credits ({Dash} DASH) wallet={Wallet} newNotes={NewNotes}",
                deltaCredits, deltaCredits / CreditsPerDash, walletHex, r.NewNotes);
            if (_defaultShieldedAddress == null)
                continue;
            await TryMarkInvoicePaid(_defaultShieldedAddress, deltaCredits, r.NewNotes);
        }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessResults threw an unobserved exception (Task.Run)");
        }
    }

    private async Task TryMarkInvoicePaid(string address, ulong deltaCredits, uint newNotes)
    {
        // The shielded DEFAULT address (Orchard diversifier index 0) is SHARED
        // across every invoice, so the AddressInvoices lookup
        // (GetInvoiceFromAddress) is ambiguous — only ONE row can exist per
        // (address, PaymentMethodId) and we deliberately do NOT track it (see
        // DashEvolutionPaymentMethodHandler.ConfigurePrompt). Instead, find
        // the most recent UNPAID invoice that has a DASHE-CHAIN payment
        // prompt and attribute the balance-delta to it. This is the
        // balance-delta correlation model documented in the file header.
        //
        // We bypass PaymentService + PaymentMethodHandlerDictionary here and
        // insert the payment row directly via ApplicationDbContextFactory.
        // Resolving those two from a Task.Run on a Rust worker thread
        // deadlocks: the Lazy<> first-touch enters the DI provider, which
        // enumerates every IPaymentMethodHandler → DashEvolutionPayment-
        // MethodHandler → IDashEvolutionWalletService → this singleton, and
        // the provider lock stalls (see the field comment block above for
        // the full cycle). ApplicationDbContextFactory is a singleton with
        // no path back to this service, so it resolves cleanly.
        _logger.LogInformation("TryMarkInvoicePaid: delta={D} credits newNotes={N} address={A}", deltaCredits, newNotes, address);

        var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
        {
            Status = new[] { InvoiceStatus.New.ToString() },
            Take = 50,
            OrderByDesc = true,
        });
        _logger.LogInformation("TryMarkInvoicePaid: GetInvoices returned {C} invoices", invoices.Length);
        InvoiceEntity invoice = null;
        foreach (var inv in invoices)
        {
            if (inv.GetPaymentPrompt(_pmi) is not null)
            {
                invoice = inv;
                break;
            }
        }
        if (invoice == null)
        {
            _logger.LogInformation("Shielded delta {D} credits but no unpaid DASHE invoice found", deltaCredits);
            return;
        }

        _logger.LogInformation("TryMarkInvoicePaid: matched invoice {Id} for payment", invoice.Id);

        // Dedup id: address + sync balance snapshot. Stable within a process so
        // a retried pass won't double-credit (the FFI does not give nullifiers).
        var paymentId = $"{address}:{deltaCredits}:{newNotes}";
        if (invoice.GetPayments(false).Any(p => p.Id == paymentId && p.PaymentMethodId == _pmi))
            return;

        await InsertPayment(invoice, address, deltaCredits, paymentId, confirmedHeight: 0, shielded: true);
    }

    /// <summary>
    /// Shared payment-insert body for both shielded and transparent matches:
    /// builds the DashEvolutionPaymentData + PaymentBlob + PaymentData row and
    /// inserts it directly (replicating PaymentService.AddPayment without the
    /// DI-deadlocking handler dictionary), then publishes ReceivedPayment.
    /// Idempotent via the deterministic paymentId (duplicate insert suppressed).
    /// </summary>
    private async Task InsertPayment(InvoiceEntity invoice, string address, ulong deltaCredits, string paymentId, ulong confirmedHeight, bool shielded)
    {
        var amountDash = deltaCredits / CreditsPerDash;
        var details = new DashEvolutionPaymentData
        {
            Address = address,
            AmountDuffs = (long)(deltaCredits / 1000m), // credits → duffs for the detail record (1 duff = 1000 credits)
            NullifierHex = "",           // not available from the FFI (no note enum)
            ConfirmedHeight = confirmedHeight, // shielded: 0 (notes not block-confirmed via this path); transparent: BLAST checkpoint height
            Shielded = shielded,
        };

        // Build the PaymentBlob + PaymentData manually, replicating
        // PaymentDataExtensions.Set + PaymentBlob.SetDetails but using the
        // static DB-layer serializer (InvoiceDataExtensions.DefaultSerializer,
        // which is BlobSerializer.CreateSerializer(null as Network)) instead
        // of handler.Serializer. For DashEvolutionPaymentData (only primitive
        // properties) both serializers emit identical JSON, so the handler's
        // ParsePaymentDetails round-trips it correctly later.
        var prompt = invoice.GetPaymentPrompt(_pmi);
        if (prompt is null)
        {
            _logger.LogWarning("No DASHE payment prompt on invoice {Id}", invoice.Id);
            return;
        }
        var paymentBlob = new PaymentBlob
        {
            Destination = prompt.Destination,
            PaymentMethodFee = prompt.PaymentMethodFee,
            Divisibility = prompt.Divisibility,
            Details = JToken.FromObject(details, InvoiceDataExtensions.DefaultSerializer),
        };
        var paymentData = new PaymentData
        {
            Id = paymentId,
            Created = DateTimeOffset.UtcNow,
            Status = PaymentStatus.Settled, // shielded: code=0 = broadcast + confirmed on Platform (L2); unconfirmed (Processing) leaves minimumDue>0 → invoice Invalid after MonitoringExpiration
            Amount = amountDash,
            Currency = prompt.Currency,  // "DASH" — matches the prompt + RateBook fast lane; CryptoCode ("DASHE") would miss the rate → PreprocessError
            InvoiceDataId = invoice.Id,
            PaymentMethodId = _pmi.ToString(),
            Blob2 = JToken.FromObject(paymentBlob, InvoiceDataExtensions.DefaultSerializer).ToString(Formatting.None),
        };

        // Replicate PaymentService.AddPayment: open a context, confirm the
        // invoice row still exists, add the payment + address text-search
        // term, save (catching DbUpdateException for the duplicate-id
        // idempotency guard). Then reload the invoice to get the deserialized
        // PaymentEntity and publish the ReceivedPayment event. The handler
        // existence check AddPayment does via _handlers.TryGetValue is
        // skipped — we KNOW the DASHE-CHAIN handler is registered (this
        // service is its wallet backend).
        bool alreadyExists = false;
        await using (var context = _dbContextFactory.CreateContext())
        {
            var invoiceRow = await context.Invoices.FindAsync(invoice.Id);
            if (invoiceRow == null)
            {
                _logger.LogWarning("Invoice {Id} disappeared before payment insert", invoice.Id);
                return;
            }
            InvoiceRepository.AddToTextSearch(context, invoiceRow, address);
            await context.Payments.AddAsync(paymentData);
            try
            {
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                alreadyExists = true;
            }
        }
        if (alreadyExists)
        {
            _logger.LogDebug("Payment {Id} already exists (duplicate insert suppressed)", paymentId);
            return;
        }

        var updatedInvoice = await _invoiceRepository.GetInvoice(invoice.Id);
        var paymentEntity = updatedInvoice.GetPayments(false).Single(p => p.Id == paymentId);
        _eventAggregator.Publish(new InvoiceEvent(updatedInvoice, InvoiceEvent.ReceivedPayment) { Payment = paymentEntity });
        _logger.LogInformation("Marked invoice {Id} paid {Amt} DASH ({Kind}) to {A}",
            invoice.Id, amountDash, shielded ? "shielded" : "transparent", address);
    }

    // -------------------------------------------------------------------
    // Transparent (DIP-17 platform address) receive pipeline
    // -------------------------------------------------------------------

    /// <summary>
    /// Acquire the Transparent handle chain (manager → PlatformWallet →
    /// PlatformAddressWallet), register the platform-address provider, and
    /// start the manager-level BLAST background sync. Self-contained: any
    /// failure is logged and the service continues shielded-only.
    /// </summary>
    private void StartTransparentPipeline()
    {
        try
        {
            using var getW = new PlatformWalletFFIResultHandle(
                PlatformAddressWalletFFI.ManagerGetWallet(_managerHandle, _walletIdBytes!, out var walletHandle));
            getW.EnsureSuccess();
            _walletHandle = walletHandle;

            using var getP = new PlatformWalletFFIResultHandle(
                PlatformAddressWalletFFI.WalletGetPlatform(_walletHandle, out var platformHandle));
            getP.EnsureSuccess();
            _platformAddressHandle = platformHandle;

            // Register the unified provider for the configured account index
            // (internally initialize()s from the wallet's public key material).
            using var addP = new PlatformWalletFFIResultHandle(
                PlatformAddressWalletFFI.AddProvider(_platformAddressHandle, (uint)_options.AccountIndex));
            addP.EnsureSuccess();

            // Same 15s cadence as shielded for fast demo detection.
            if (_options.SyncIntervalSeconds > 0)
            {
                using var si = new PlatformWalletFFIResultHandle(
                    PlatformAddressWalletFFI.PlatformAddressSyncSetInterval(_managerHandle, (ulong)_options.SyncIntervalSeconds));
                si.EnsureSuccess();
            }

            using var start = new PlatformWalletFFIResultHandle(
                PlatformAddressWalletFFI.PlatformAddressSyncStart(_managerHandle));
            start.EnsureSuccess();
            _logger.LogInformation("DashEvolution transparent (platform-address) BLAST sync started for wallet {W}", _options.WalletIdHex);
        }
        catch (EntryPointNotFoundException ex)
        {
            _nextUnusedUnavailable = true;
            _logger.LogWarning(ex, "DashEvolution transparent receive unavailable: patched .so (next_unused_receive_address) not loaded — continuing shielded-only");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DashEvolution transparent pipeline failed to start — continuing shielded-only");
        }
    }

    /// <summary>
    /// Native event callback for a completed BLAST (platform-address) sync
    /// pass. Runs on a Rust worker thread — snapshot the results, then hand
    /// the delta matching off to a .NET thread (mirrors OnSyncCompleted).
    /// </summary>
    private void OnPlatformAddressSyncCompleted(IntPtr ctx, IntPtr results, UIntPtr count, ulong syncUnixSeconds)
    {
        var n = (int)count;
        if (n <= 0) return;
        var snapshot = new PlatformAddressSyncWalletResultFFI[n];
        for (var i = 0; i < n; i++)
            snapshot[i] = Marshal.PtrToStructure<PlatformAddressSyncWalletResultFFI>(
                IntPtr.Add(results, i * Marshal.SizeOf<PlatformAddressSyncWalletResultFFI>()));

        foreach (var r in snapshot)
        {
            if (_walletIdBytes is null || !r.WalletId.SequenceEqual(_walletIdBytes)) continue;
            var err = r.ErrorMessage != IntPtr.Zero ? Marshal.PtrToStringAnsi(r.ErrorMessage) : null;
            _logger.LogInformation("PlatformAddressSyncResult: wallet={W} success={S} found={F} absent={A} checkpoint={C} err={E}",
                Convert.ToHexString(r.WalletId), r.Success, (ulong)r.FoundCount, (ulong)r.AbsentCount, r.CheckpointHeight, err ?? "");
        }

        _ = Task.Run(async () =>
        {
            try { await ProcessPlatformAddressResults(snapshot); }
            catch (Exception ex) { _logger.LogWarning(ex, "ProcessPlatformAddressResults threw an unobserved exception"); }
        });
    }

    /// <summary>
    /// Per-address delta matching for Transparent payments. After each BLAST
    /// pass, re-read AddressesWithBalances and diff against the prior
    /// per-address snapshot. The FIRST completed pass only seeds the baseline
    /// (no invoice matching) — the same restart-safety rule as the shielded
    /// matcher; matching begins from the second pass.
    /// </summary>
    private async Task ProcessPlatformAddressResults(PlatformAddressSyncWalletResultFFI[] results)
    {
        foreach (var r in results)
        {
            if (_walletIdBytes is null || !r.WalletId.SequenceEqual(_walletIdBytes)) continue;
            if (!r.Success) continue;

            var balances = ReadPlatformAddressBalances();

            if (!_platformBaselineSeeded)
            {
                foreach (var e in balances)
                    _priorPlatformBalances[e.HashHex] = e.Balance;
                _platformBaselineSeeded = true;
                _logger.LogInformation("Transparent baseline seeded on first BLAST pass: {N} addresses tracked — no invoice matching on first pass (restart-safe)", balances.Count);
                return;
            }

            foreach (var e in balances)
            {
                var prev = _priorPlatformBalances.GetValueOrDefault(e.HashHex);
                if (e.Balance < prev)
                {
                    _priorPlatformBalances[e.HashHex] = e.Balance;
                    continue;
                }
                if (e.Balance == prev) continue;

                var delta = e.Balance - prev;
                _priorPlatformBalances[e.HashHex] = e.Balance;
                _logger.LogInformation("TRANSPARENT DELTA {D} credits ({Dash} DASH) on address {A} (index {I})",
                    delta, delta / CreditsPerDash, e.Address, e.AddressIndex);
                await TryMarkTransparentInvoicePaid(e.Hash, e.Address, delta, r.CheckpointHeight);
            }
        }
    }

    /// <summary>Snapshot of one address's balance row from AddressesWithBalances.</summary>
    private readonly record struct PlatformBalanceEntry(string HashHex, byte[] Hash, string Address, ulong Balance, uint AddressIndex);

    /// <summary>
    /// Read every platform address with its current balance via
    /// platform_address_wallet_addresses_with_balances; the Rust-allocated
    /// array is freed in a finally (free_address_balances).
    /// </summary>
    private List<PlatformBalanceEntry> ReadPlatformAddressBalances()
    {
        var list = new List<PlatformBalanceEntry>();
        if (_platformAddressHandle == 0) return list;
        using var res = new PlatformWalletFFIResultHandle(
            PlatformAddressWalletFFI.AddressesWithBalances(_platformAddressHandle, out var entries, out var count));
        res.EnsureSuccess();
        try
        {
            var n = (int)count;
            for (var i = 0; i < n; i++)
            {
                var e = Marshal.PtrToStructure<AddressBalanceEntryFFI>(
                    IntPtr.Add(entries, i * Marshal.SizeOf<AddressBalanceEntryFFI>()));
                var hash = e.Address.Hash;
                var hashHex = Convert.ToHexString(hash).ToLowerInvariant();
                // P2SH (type 1) is out-only; receive attribution is P2PKH-only.
                // Must produce the SAME string the handler tracked in AddressInvoices
                // (DIP-0018 bech32m, type byte 0xb0 — see GetTransparentAddressAsync).
                var address = e.Address.AddressType == 0
                    ? Bech32m.EncodePlatformAddress(_options.Mainnet, 0xb0, hash.ToArray())
                    : $"p2sh:{hashHex}";
                list.Add(new PlatformBalanceEntry(hashHex, hash, address, e.Balance, e.AddressIndex));
            }
        }
        finally
        {
            if (entries != IntPtr.Zero)
                PlatformAddressWalletFFI.FreeAddressBalances(entries, count);
        }
        return list;
    }

    /// <summary>
    /// Transparent matcher: a per-address delta maps to EXACTLY one invoice —
    /// the handler registers every allocated Transparent address in
    /// AddressInvoices at invoice creation (unique per invoice, so the
    /// (Address, PaymentMethodId) PK is collision-free).
    /// </summary>
    private async Task TryMarkTransparentInvoicePaid(byte[] hash20, string address, ulong deltaCredits, ulong checkpointHeight)
    {
        string? invoiceId;
        await using (var context = _dbContextFactory.CreateContext())
        {
            invoiceId = await context.AddressInvoices
                .Where(ai => ai.Address == address && ai.PaymentMethodId == _pmi.ToString())
                .Select(ai => ai.InvoiceDataId)
                .FirstOrDefaultAsync();
        }
        if (invoiceId is null)
        {
            _logger.LogInformation("Transparent delta {D} credits on {A} but no invoice is watching that address", deltaCredits, address);
            return;
        }

        var invoice = await _invoiceRepository.GetInvoice(invoiceId);
        var prompt = invoice.GetPaymentPrompt(_pmi);
        if (prompt is null || prompt.Destination != address)
        {
            _logger.LogDebug("Transparent delta on {A}: invoice {Id} no longer prompts for that address", address, invoiceId);
            return;
        }

        // Dedup id: address + delta + BLAST checkpoint height. Deterministic
        // per observation so a retried pass won't double-credit.
        var paymentId = $"{address}:{deltaCredits}:{checkpointHeight}";
        if (invoice.GetPayments(false).Any(p => p.Id == paymentId && p.PaymentMethodId == _pmi))
            return;

        // Skip invoices that already settled (paid > 0): a second incoming
        // payment to a watched address after settlement is not auto-booked.
        if (invoice.GetPayments(false).Any(p => p.PaymentMethodId == _pmi))
        {
            _logger.LogInformation("Transparent delta on {A}: invoice {Id} already has a DASHE payment — skipping", address, invoiceId);
            return;
        }

        await InsertPayment(invoice, address, deltaCredits, paymentId, checkpointHeight, shielded: false);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private void BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts.Cancel(); } catch { }
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return bytes;
    }

    /// <summary>
    /// Create the demo wallet in the manager from the BIP-39 mnemonic +
    /// configured network, and return its 32-byte wallet id. Uses
    /// platform_wallet_manager_create_wallet_from_mnemonic (header:6534),
    /// which both registers the wallet in PLATFORM_WALLET_STORAGE and fills
    /// out_wallet_id — the same call the iOS app makes at first launch
    /// (SwiftDashSDK PlatformWalletManager.swift:940, accountOptions=1 when
    /// createDefaultAccounts).
    /// The wallet handle returned is owned by the manager (released on
    /// Destroy); we ignore it and key everything off the 32-byte id.
    /// account_options=1 (WalletAccountCreationOptions::Default) — creates
    /// the standard account set INCLUDING the DIP-17 platform-payment
    /// accounts; Transparent receive (next_unused_receive_address) requires
    /// them. Shielded receive is unaffected (it binds via the resolver).
    /// </summary>
    private byte[] CreateWalletFromMnemonic()
    {
        var network = _options.Mainnet ? (int)DashFFINetwork.Mainnet : (int)DashFFINetwork.Testnet;
        var id = new byte[32];
        using var create = new PlatformWalletFFIResultHandle(
            PlatformWalletFFI.CreateWalletFromMnemonic(
                _managerHandle, _options.Mnemonic, network,
                accountOptions: 1,                 // Default — incl. platform-payment accounts
                out var walletHandle, id));        // walletHandle owned by manager
        create.EnsureSuccess();
        return id;
    }
}
