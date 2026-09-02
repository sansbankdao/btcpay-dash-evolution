// File: Plugins/DashEvolution/DashEvolutionNetwork.cs
//
// A dedicated BTCPayNetworkBase subclass for the DASHE payment method.
//
// WHY NOT BTCPayNetwork: BTCPayNetwork (BTCPayServer.Common/BTCPayNetwork.cs:55)
// models a UTXO chain backed by NBXplorer — it carries NBXplorerNetwork +
// NBitcoinNetwork + CoinType + DefaultSettings, and several core startup
// loops dereference network.NBXplorerNetwork.* without a null guard:
//   - BTCPayServer/Hosting/BTCPayServerServices.cs:201  (NBXplorerOptions)
//   - BTCPayServer/Services/BTCPayNetworkJsonSerializerSettings.cs:36
//   - BTCPayServer/ExplorerClientProvider.cs:45-63     (creates ExplorerClient
//     per NBXplorerConnectionSetting and calls n.NBXplorerNetwork.*)
// Both loops filter GetAll().OfType<BTCPayNetwork>() (runtime-type filter), so
// a network that is NOT a BTCPayNetwork is skipped automatically — no NRE, no
// fake NBXplorer connection, no core change. Registering DASHE as a plain
// BTCPayNetworkBase is the architecturally honest model: shielded notes and
// platform-address credits are not transparent UTXOs and have no NBXplorer.
//
// Fields available on the base (BTCPayServer.Common/BTCPayNetwork.cs:138) and
// used by the handler / sync service: CryptoCode, DisplayName, Divisibility
// (default 8 — Dash), DefaultRateRules, CryptoImagePath. That is the complete
// surface DASHE needs; CoinType/DefaultSettings are never read by any
// DashEvolution code path (grep -rn '\.CoinType|\.DefaultSettings' DashEvolution/
// → empty).

using BTCPayServer;

namespace BTCPayServer.Plugins.DashEvolution;

/// <summary>
/// The Evolution-Dash payment network (crypto code DASHE). Models the union of
/// the shielded pool and Platform Addresses (DIP-17), neither of which is a
/// transparent UTXO chain, so it is a <see cref="BTCPayNetworkBase"/>, not a
/// <see cref="BTCPayNetwork"/>. See file header for the startup-loop rationale.
/// </summary>
public sealed class DashEvolutionNetwork : BTCPayNetworkBase
{
}
