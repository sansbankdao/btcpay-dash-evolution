// File: Plugins/DashEvolution/DashEvolutionPaymentMethodHandler.cs
//
// IPaymentMethodHandler for the DASHE payment method (PaymentMethodId =
// "DASHE-CHAIN"). This is the core of the integration: it shapes the
// invoice prompt (the destination the customer pays to) and parses the
// payment details the sync service records when a note/credit lands.
//
// ARCHITECTURE vs the transparent Dash handler (BitcoinLikePaymentHandler):
//   - BitcoinLikePaymentHandler depends on NBXplorer (ExplorerClientProvider,
//     NBXplorerDashboard, BTCPayWalletProvider) and reserves a fresh derived
//     address per invoice. We can NOT reuse it: DASHE is a BTCPayNetworkBase
//     (DashEvolutionNetwork), not a BTCPayNetwork — shielded notes / platform
//     credits are not transparent UTXOs, there is no NBXplorerNetwork, and
//     BlobSerializer.CreateSerializer(network.NBXplorerNetwork) in its
//     constructor would NRE.
//   - Instead, DASHE's destination is the shielded DEFAULT address of the
//     bound wallet (platform_wallet_manager_shielded_default_address), surfaced
//     via IDashEvolutionWalletService. Per-invoice diversified addresses are
//     a follow-up (needs ~10 lines new Rust — see docs/HANDOFF.md §8); for
//     the demo each invoice reuses the default address and the sync service
//     correlates by amount + timing.
//   - No fee rate provider, no network-fee reservation: the RECEIVER pays no
//     on-chain fee for a shielded receive; the sender pays. PaymentMethodFee
//     is 0.
//
// REGISTRATION: DashEvolutionPlugin.Init calls AddBTCPayNetwork(network) — the
// BTCPayNetworkBase overload (BTCPayServerServices.cs:776), which only registers
// DefaultRules + the network singleton. It must NOT use the BTCPayNetwork
// overload (line 789): that auto-registers BitcoinLikePaymentHandler. It then
// manually registers this handler as IPaymentMethodHandler for "DASHE-CHAIN".
// This handler does NOT implement IHasNetwork (which requires BTCPayNetwork):
// every ((IHasNetwork)...) cast in the app is guarded (iterates only
// DerivationSchemeSettings/Lightning configs DASHE never has, or a null
// derivationSettings guard, or an `is not IHasNetwork` pattern), so a non-
// IHasNetwork handler is safe for DASHE's on-chain-wallet-less surface.

using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.DashEvolution;

/// <summary>
/// Minimal contract between the payment handler and the wallet/sync backend.
/// The real implementation is DashEvolutionSyncService (step 4); a stub
/// (DashEvolutionWalletService) stands in until then so the handler can be
/// constructed and invoices can be created against a placeholder address.
/// </summary>
public interface IDashEvolutionWalletService
{
    /// <summary>
    /// Return the shielded default Orchard payment address (diversifier index 0)
    /// for the bound wallet, bech32m-encoded. Mirrors
    /// platform_wallet_manager_shielded_default_address (43 raw bytes → host
    /// bech32m). UNVERIFIED for per-invoice uniqueness until the diversified-
    /// address FFI lands.
    /// </summary>
    Task<string> GetShieldedDefaultAddressAsync(
        DashEvolutionPaymentMethodConfig config,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-store config for the DASHE payment method. Stored as the payment-method
/// config blob on the store. Identifies WHICH wallet this store receives to
/// (the server may host several DASHE wallets).
/// </summary>
public class DashEvolutionPaymentMethodConfig
{
    /// <summary>32-byte wallet id as lowercase hex (no 0x). Matches the
    /// FFI's `wallet_id` (*const u8, 32 bytes).</summary>
    public string WalletIdHex { get; set; }

    /// <summary>BIP44 account index within the wallet (0 for the first
    /// shielded sub-wallet). Defaults to 0.</summary>
    public uint AccountIndex { get; set; } = 0;

    /// <summary>
    /// Optional SQLite path for the shielded commitment-tree store. If null,
    /// the wallet service uses a server-default path. Forwarded to
    /// platform_wallet_manager_configure_shielded.
    /// </summary>
    public string ShieldedDbPath { get; set; }
}

/// <summary>
/// Prompt details written to the invoice for the DASHE-CHAIN payment method.
/// Surfaces the destination address and the account it derives from.
/// </summary>
public class DashEvolutionPaymentPromptDetails
{
    /// <summary>The bech32m-encoded shielded address the customer pays to.</summary>
    public string Address { get; set; }

    /// <summary>BIP44 account index the address belongs to.</summary>
    public uint AccountIndex { get; set; }

    /// <summary>True when this prompt is settled via the shielded pool
    /// (Orchard). False for platform-address (BLAST) settlement — the handler
    /// currently emits shielded only; BLAST is Phase 2.</summary>
    public bool Shielded { get; set; } = true;
}

/// <summary>
/// Payment data recorded when the sync service detects a shielded note (or,
/// later, a platform-credit delta) for this invoice's destination. Populated
/// by DashEvolutionSyncService, not by this handler.
/// </summary>
public class DashEvolutionPaymentData
{
    /// <summary>The destination address the note/credit landed on.</summary>
    public string Address { get; set; }

    /// <summary>Received amount in duffs (1 DASH = 100,000,000 duffs; the
    /// FFI reports credits where 1 duff = 1000 credits, so we divide by
    /// 1000 on insert). Display/metadata only — BTCPay accounting uses the
    /// PaymentData.Amount (in DASH base units), not this field.</summary>
    public long AmountDuffs { get; set; }

    /// <summary>Shielded note nullifier (hex) when Shielded == true; null
    /// otherwise. Used for idempotency / replay protection.</summary>
    public string NullifierHex { get; set; }

    /// <summary>Platform block height the note/credit was confirmed at.</summary>
    public ulong ConfirmedHeight { get; set; }

    /// <summary>True for shielded (Orchard) settlement; false for platform-
    /// address (BLAST) credit settlement.</summary>
    public bool Shielded { get; set; } = true;
}

public class DashEvolutionPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly BTCPayNetworkBase _network;
    private readonly IDashEvolutionWalletService _walletService;

    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId { get; }

    public DashEvolutionPaymentMethodHandler(
        PaymentMethodId paymentMethodId,
        BTCPayNetworkBase network,
        IDashEvolutionWalletService walletService)
    {
        PaymentMethodId = paymentMethodId;
        _network = network;
        _walletService = walletService;
        // Paramless overload: no NBitcoin/NBXplorer converters needed — we
        // serialize only plain CLR types (strings/ints/bools). Avoids the NRE
        // that BitcoinLikePaymentHandler's BlobSerializer.CreateSerializer(
        // network.NBXplorerNetwork) would hit (DASHE has no NBXplorerNetwork;
        // the network is a BTCPayNetworkBase, not a BTCPayNetwork).
        Serializer = BlobSerializer.CreateSerializer().Serializer;
    }

    private DashEvolutionPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
        => config?.ToObject<DashEvolutionPaymentMethodConfig>(Serializer)
           ?? new DashEvolutionPaymentMethodConfig();

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
        => ParsePaymentMethodConfig(config);

    public DashEvolutionPaymentPromptDetails ParsePaymentPromptDetails(JToken details)
        => details.ToObject<DashEvolutionPaymentPromptDetails>(Serializer);
    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
        => ParsePaymentPromptDetails(details);

    public DashEvolutionPaymentData ParsePaymentDetails(JToken details)
        => details.ToObject<DashEvolutionPaymentData>(Serializer);
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
        => ParsePaymentDetails(details);

    // -------------------------------------------------------------------
    // Invoice creation flow
    // -------------------------------------------------------------------

    private class Prepare
    {
        public Task<string> AddressTask;
        public DashEvolutionPaymentMethodConfig Config;
    }

    public Task BeforeFetchingRates(PaymentMethodContext paymentMethodContext)
    {
        paymentMethodContext.Prompt.Currency = "DASH";   // display label (internal CryptoCode stays "DASHE")
        paymentMethodContext.Prompt.Divisibility = _network.Divisibility; // 8 for Dash
        if (paymentMethodContext.Prompt.Activated)
        {
            var config = ParsePaymentMethodConfig(paymentMethodContext.PaymentMethodConfig);
            // Kick off the (potentially slow) address fetch now so it runs
            // concurrently with rate fetching — same pattern as
            // BitcoinLikePaymentHandler.BeforeFetchingRates reserving an address.
            paymentMethodContext.State = new Prepare
            {
                AddressTask = _walletService.GetShieldedDefaultAddressAsync(config,
                    CancellationToken.None),
                Config = config
            };
        }
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext paymentContext)
    {
        var prepare = (Prepare)paymentContext.State;
        var config = prepare.Config;

        // No wallet configured for this store → this payment method is
        // unavailable for the invoice. BTCPay treats this as a soft failure
        // (the method is skipped, other methods proceed).
        if (string.IsNullOrWhiteSpace(config.WalletIdHex))
            throw new PaymentMethodUnavailableException(
                "No Dash Evolution wallet configured for this store. " +
                "Set the DASHE payment method's wallet id in the store settings.");

        string address;
        try
        {
            address = await prepare.AddressTask;
        }
        catch (Exception ex)
        {
            throw new PaymentMethodUnavailableException(
                $"Dash Evolution wallet service unavailable: {ex.Message}", ex);
        }
        if (string.IsNullOrWhiteSpace(address))
            throw new PaymentMethodUnavailableException(
                "Dash Evolution wallet service returned no shielded address.");

        var paymentMethod = paymentContext.Prompt;
        // The receiver pays no on-chain fee; the sender pays the shielded
        // tx fee. Keep PaymentMethodFee at 0 for the demo.
        paymentMethod.PaymentMethodFee = 0m;
        paymentMethod.Destination = address;
        // We deliberately do NOT add `address` to TrackedDestinations.
        // BitcoinLikePaymentHandler reserves a UNIQUE address per invoice, so
        // its AddressInvoices row (PK = (Address, PaymentMethodId)) never
        // collides. DASHE reuses the single shielded DEFAULT address (index 0)
        // for every invoice — a second invoice would hit
        // `duplicate key value violates unique constraint "PK_AddressInvoices"`
        // in InvoiceRepository.CreateInvoiceAsync (raw EF AddAsync, no
        // ON CONFLICT). Address->invoice lookup is also AMBIGUOUS with a shared
        // address, so the index is useless here anyway. The sync service
        // correlates payments by balance-delta (see DashEvolutionSyncService
        // header), NOT by AddressInvoices. Per-invoice diversified addresses
        // (FFI `shielded_address_at(index)`, ~10 lines Rust -- HANDOFF §8)
        // will restore unique tracking later.
        // paymentContext.TrackedDestinations.Add(address);  // disabled — see above

        paymentMethod.Details = JObject.FromObject(new DashEvolutionPaymentPromptDetails
        {
            Address = address,
            AccountIndex = config.AccountIndex,
            Shielded = true
        }, Serializer);
    }

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var config = ParsePaymentMethodConfig(validationContext.Config);
        if (string.IsNullOrWhiteSpace(config.WalletIdHex))
        {
            validationContext.ModelState.AddModelError(
                nameof(config.WalletIdHex),
                "A Dash Evolution wallet id is required.");
            return Task.CompletedTask;
        }
        // WalletIdHex must be 64 hex chars (32 bytes).
        var hex = config.WalletIdHex.Trim();
        if (hex.Length != 64 || !IsLowerHex(hex))
        {
            validationContext.ModelState.AddModelError(
                nameof(config.WalletIdHex),
                "Wallet id must be 32 bytes as 64 lowercase hex characters.");
        }
        return Task.CompletedTask;
    }

    private static bool IsLowerHex(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }

    public void StripDetailsForNonOwner(object details)
    {
        // Nothing sensitive to strip for the demo (the address is the
        // destination the customer already sees). Placeholder for Phase 2
        // where BLAST changesets may carry internal account info.
    }
}
