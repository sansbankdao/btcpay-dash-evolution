// File: Plugins/DashEvolution/DashEvolutionPlugin.cs
//
// Payment-method plugin for Dash on the Evolution Platform — the umbrella
// crypto code "DASHE" covers BOTH the shielded pool (Orchard / ZK, protocol
// v12+) and unshielded Platform Addresses (DIP-17 credits). Distinct from the
// transparent-L1 DASH network in AltcoinsPlugin.Dash.cs.
//
// Rationale for a single code: shielded notes and platform-address credits
// are the SAME asset (DASH). The iOS wallet (dashwallet-ios) models them as
// one wallet's accounts — SwiftDashSDKWalletState.swift:128 (accountType == 14
// = PlatformPayment) and DWIdentityRegistrationBridge.swift:64-65
// (core=0, platformPayment=1, shielded=2 are funding sources of one identity),
// not separate currencies. The payment method handler (IPaymentMethodHandler)
// multiplexes internally between the shielded sync engine and the BLAST
// (platform-address) sync engine — exactly as a Bitcoin handler routes
// on-chain vs Lightning. The payment-method IDENTITY models the asset; the
// handler models the transport.
//
// This plugin does NOT use NBXplorer. The transparent Dash plugin relies on
// NBXplorerNetworkProvider.GetFromCryptoCode("DASH") + NBitcoin for UTXO
// tracking. Evolution Dash (shielded notes AND platform credits) has no
// transparent UTXOs visible to NBXplorer — shielded notes are encrypted and
// discovered by trial-decrypting against the wallet's incoming viewing key
// (IVK) via the Rust sync engine; platform credits are polled over DAPI via
// BLAST. So we register a DashEvolutionNetwork (a BTCPayNetworkBase subclass,
// NOT a BTCPayNetwork) — see DashEvolutionNetwork.cs for why this type choice
// avoids NREs in the core OfType<BTCPayNetwork>() startup loops — and drive
// balance/invoice settlement through a custom payment method handler backed
// by the Rust FFI (PlatformWalletFFI.cs + PlatformAddressFFI.cs), not the
// NBXplorer listener.
//
// The native library (libplatform_wallet_ffi.{so,dylib,dll}) is loaded by
// BTCPay's existing plugin loader infrastructure (Plugins/Dotnet/Loader/
// ManagedLoadContext.cs → LoadUnmanagedDll override). No existing coin plugin
// registers a native library today — this is the first. See R1 in the risk
// register (docs/PROPOSAL.md §6) for the schedule implication.
//
// RATE RULES: 1 Evolution Dash = 1 transparent Dash (DASHE_DASH = 1).
// Both layers are the same asset, not separate tokens.

using System;
using System.Collections.Generic;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins;
using BTCPayServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.DashEvolution;

public class DashEvolutionPlugin : BaseBTCPayServerPlugin
{
    // DASHE — 5 letters, fits WalletId regex [a-zA-Z]{2,5} (WalletId.cs:11).
    // Umbrella for the whole Evolution Platform: shielded + platform addresses.
    public const string CryptoCode = "DASHE";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        // Wire the native-library DllImportResolver FIRST, before any code
        // path can trigger [DllImport("platform_wallet_ffi")] (the resolver
        // itself is lazy — DllImports resolve on first call, which happens
        // in DashEvolutionSyncService.StartAsync at runtime). Idempotent.
        // See DashEvolutionNativeRegistration.TryRegisterDllImportResolver for
        // why the default-ALC resolver (not AddNativeLibrary) is the active path.
        DashEvolutionNativeRegistration.TryRegisterDllImportResolver();

        // No NBXplorerNetwork — shielded notes and platform credits are not
        // transparent UTXOs. DashEvolutionNetwork is a BTCPayNetworkBase (NOT
        // a BTCPayNetwork): the core startup loops filter
        // GetAll().OfType<BTCPayNetwork>() by runtime type, so a base-only
        // network is skipped — no null-NBXplorerNetwork NRE in
        // BTCPayServerServices.cs:201 / BTCPayNetworkJsonSerializerSettings.cs:36,
        // and no fake NBXplorer connection in ExplorerClientProvider.cs:45.
        // See DashEvolutionNetwork.cs for the full rationale. The custom payment
        // method handler (DashEvolutionPaymentMethodHandler) drives sync +
        // note/credit detection via the Rust FFI and internally multiplexes
        // shielded vs BLAST.
        var network = new DashEvolutionNetwork()
        {
            CryptoCode = CryptoCode,
            DisplayName = "Dash (Evolution)",
            // Divisibility defaults to 8 (Dash) on BTCPayNetworkBase.
            DefaultRateRules = new[]
            {
                "DASHE_DASH = 1",
                "DASHE_X = DASHE_DASH * DASH_X"
            },
            CryptoImagePath = "imlegacy/dash.png"
        };

        // The base AddBTCPayNetwork overload (BTCPayServerServices.cs:776)
        // only registers DefaultRules + the network singleton. We must NOT use
        // the BTCPayNetwork overload (line 789): it auto-registers a
        // BitcoinLikePaymentHandler whose ctor calls
        // BlobSerializer.CreateSerializer(network.NBXplorerNetwork) and would
        // NRE. We then manually register our custom handler below.
        services.AddBTCPayNetwork(network);

        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(CryptoCode); // "DASHE-CHAIN"
        services.AddDefaultPrettyName(pmi, network.DisplayName);

        // Custom payment method handler — drives invoice prompt creation
        // (shielded default address) and payment-detail parsing. Mirrors how
        // BTCPayServerServices registers BitcoinLikePaymentHandler per network
        // (line 797) but with our handler + a wallet-service dependency.
        //
        // The sync service is the single source of truth for the bound wallet:
        // register ONE concrete singleton, then forward it as BOTH the
        // IDashEvolutionWalletService (serves the handler's shielded default
        // address fetch) and — further below — the IHostedService (owns the
        // native manager + sync loop). Forwarding via GetRequiredService keeps
        // the handler and the host on the same instance; the placeholder stub
        // DashEvolutionWalletService is retired (step 4, HANDOFF.md §7).
        services.AddSingleton<DashEvolutionSyncService>();
        services.AddSingleton<IDashEvolutionWalletService>(provider =>
            provider.GetRequiredService<DashEvolutionSyncService>());
        services.AddSingleton<IPaymentMethodHandler>(provider =>
            (DashEvolutionPaymentMethodHandler)ActivatorUtilities.CreateInstance(
                provider, typeof(DashEvolutionPaymentMethodHandler),
                new object[] { pmi, network }));

        // -------------------------------------------------------------------
        // Checkout UI: ICheckoutModelExtension + IPaymentLinkExtension + the
        // view partial. WITHOUT these the checkout page renders no address /
        // QR code — <component :is="paymentMethodComponent"> resolves to null
        // because CheckoutBodyComponentName is never set (see
        // DashEvolutionCheckoutModelExtension.cs header). Mirrors the Bitcoin
        // registration at BTCPayServerServices.cs:801-806 (handler + link +
        // checkout extension) + :362 (checkout-end UI extension).
        // -------------------------------------------------------------------
        services.AddSingleton<IPaymentLinkExtension>(provider =>
            (DashEvolutionPaymentLinkExtension)ActivatorUtilities.CreateInstance(
                provider, typeof(DashEvolutionPaymentLinkExtension),
                new object[] { pmi }));
        services.AddSingleton<ICheckoutModelExtension>(provider =>
            (DashEvolutionCheckoutModelExtension)ActivatorUtilities.CreateInstance(
                provider, typeof(DashEvolutionCheckoutModelExtension),
                new object[] { pmi, network }));
        services.AddUIExtension("checkout-end",
            "/Views/DashEvolutionMethodCheckout.cshtml");

        // -------------------------------------------------------------------
        // Shielded sync engine (receive-side). One singleton owns the native
        // manager + resolver and marks invoices paid on shielded note arrival.
        // Bound to the "DashEvolution" config section (appsettings.json or env
        // vars DashEvolution__*). Registered as IHostedService so the generic
        // host starts/stops it with the app. See DashEvolutionSyncService.cs
        // for the unattended-receive rationale.
        // -------------------------------------------------------------------
        var configuration = ((PluginServiceCollection)services).BootstrapServices
            .GetRequiredService<IConfiguration>();
        services.Configure<DashEvolutionSyncOptions>(
            configuration.GetSection(DashEvolutionSyncOptions.SectionName));
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<DashEvolutionSyncService>());

        // The FFI wrappers are in:
        //   - Native/PlatformWalletFFI.cs           (shielded P/Invoke)
        //   - Native/PlatformAddressFFI.cs           (platform-address P/Invoke)
        //   - Native/PlatformWalletManagerFFI.cs     (SDK/manager P/Invoke)
    }
}
