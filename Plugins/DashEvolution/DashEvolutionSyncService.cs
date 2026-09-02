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

    // Prior balance per wallet (bytes hex → credits) for delta detection.
    private readonly ConcurrentDictionary<string, ulong> _priorBalance = new();

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
            OnPlatformAddressSyncCompletedFn = IntPtr.Zero,
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
        // registers it AND fills the 32-byte id. account_options=0 (None):
        // no default accounts, matches the derivation/test path.
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

        var amountDash = deltaCredits / CreditsPerDash;
        var details = new DashEvolutionPaymentData
        {
            Address = address,
            AmountDuffs = (long)(deltaCredits / 1000m), // credits → duffs for the detail record (1 duff = 1000 credits)
            NullifierHex = "",           // not available from the FFI (no note enum)
            ConfirmedHeight = 0,         // shielded notes are not block-confirmed via this path
            Shielded = true,
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
        _logger.LogInformation("Marked invoice {Id} paid {Amt} DASH (shielded) to {A}",
            invoice.Id, amountDash, address);
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
    /// createDefaultAccounts). account_options=0 (None) for the receive-only
    /// demo (no default accounts need pre-creating for shielded receive).
    /// The wallet handle returned is owned by the manager (released on
    /// Destroy); we ignore it and key everything off the 32-byte id.
    /// </summary>
    private byte[] CreateWalletFromMnemonic()
    {
        var network = _options.Mainnet ? (int)DashFFINetwork.Mainnet : (int)DashFFINetwork.Testnet;
        var id = new byte[32];
        using var create = new PlatformWalletFFIResultHandle(
            PlatformWalletFFI.CreateWalletFromMnemonic(
                _managerHandle, _options.Mnemonic, network,
                accountOptions: 0,                 // None — no default accounts
                out var walletHandle, id));        // walletHandle owned by manager
        create.EnsureSuccess();
        return id;
    }
}
