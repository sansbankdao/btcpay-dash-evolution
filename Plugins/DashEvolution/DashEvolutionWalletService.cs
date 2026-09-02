// File: Plugins/DashEvolution/DashEvolutionWalletService.cs
//
// STUB implementation of IDashEvolutionWalletService. Lets the payment
// handler be constructed and invoices created against a PLACEHOLDER address
// while the real backend (DashEvolutionSyncService, docs/HANDOFF.md §7 step 4)
// is written. Step 4 replaces this registration with a service that calls the
// real FFI (platform_wallet_manager_shielded_default_address) against a bound
// manager handle.
//
// TODO(step 4): replace AddSingleton<IDashEvolutionWalletService, DashEvolutionWalletService>()
// with AddSingleton<IDashEvolutionWalletService, DashEvolutionSyncService>()
// (or DashEvolutionSyncService implements the interface directly).
//
// The placeholder address below is a syntactically-valid bech32m-like string
// so invoice creation and checkout rendering do not crash. It is NOT a real
// Orchard address and will NOT receive real funds. Real addresses come only
// from the FFI once the manager handle is bound.

using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.DashEvolution;

/// <summary>
/// Placeholder backend. Returns a constant non-null address so the handler's
// ConfigurePrompt does not throw PaymentMethodUnavailableException during the
// build-up phase. Replace with the FFI-backed implementation in step 4.
/// </summary>
public class DashEvolutionWalletService : IDashEvolutionWalletService
{
    // PLACEHOLDER ONLY — do NOT send funds here. 43-char bech32m-shaped string
    // (real Orchard addresses are 43 chars, charset qpzry9sx...). Step 4 swaps
    // this for a real address from platform_wallet_manager_shielded_default_address.
    private const string PlaceholderAddress = "d1asheplaceholderplaceholderplaceholderplaceholderplacehold";

    public Task<string> GetShieldedDefaultAddressAsync(
        DashEvolutionPaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        // Return the placeholder synchronously. The real implementation will:
        //   1. ensure the manager is configured + bound for config.WalletIdHex
        //      (platform_wallet_manager_configure_shielded + bind_shielded),
        //   2. call platform_wallet_manager_shielded_default_address for the
        //      account, receiving 43 raw bytes,
        //   3. bech32m-encode them with the "dashe" HRP (or the Dash
        //      Orchard HRP the iOS app uses) and return the string.
        return Task.FromResult(PlaceholderAddress);
    }
}
