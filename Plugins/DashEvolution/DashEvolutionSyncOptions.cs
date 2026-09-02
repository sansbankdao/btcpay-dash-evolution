// File: Plugins/DashEvolution/DashEvolutionSyncOptions.cs
//
// Configuration for the DashEvolutionSyncService (singleton IHostedService).
// Bound from the "DashEvolution" section of appsettings.json (or env vars
// DashEvolution__DapiAddresses etc.). For the demo a SINGLE wallet is
// synced; multi-wallet support is Phase 2 (per-store walletId+mnemonic in
// DashEvolutionPaymentMethodConfig, with the sync service multiplexing).
//
// SECURITY: Mnemonic is a wallet recovery phrase. In production this MUST
// be sourced from a secrets store (Azure Key Vault / env / BTCPay's
// encrypted store settings), NEVER committed to appsettings.json. The
// demo uses env-var injection on the VM.

namespace BTCPayServer.Plugins.DashEvolution;

public class DashEvolutionSyncOptions
{
    public const string SectionName = "DashEvolution";

    /// <summary>Comma-separated DAPI endpoint URLs. If empty, the SDK uses a mock
    /// (useless for real syncs). For mainnet: the public DAPI hosts discovered
    /// via https://quorums.mainnet.networks.dash.org/masternodes (see
    /// docs/HANDOFF.md §9 — e.g. "https://45.135.180.70:443").</summary>
    public string DapiAddresses { get; set; } = "";

    /// <summary>true for mainnet (HRP "dash"), false for testnet ("tdash").
    /// The live test MUST be mainnet (the funder's requirement).</summary>
    public bool Mainnet { get; set; } = true;

    /// <summary>32-byte wallet id as 64 lowercase hex chars. Identifies which
    /// wallet the sync service binds + syncs. Must match a walletId derived
    /// from Mnemonic (the iOS app's wallet).</summary>
    public string WalletIdHex { get; set; } = "";

    /// <summary>BIP-39 mnemonic for WalletIdHex. Supplied to the Rust derivation
    /// pipeline via the MnemonicResolver vtable. RECEIVE (sync) is unattended
    /// — this mnemonic gives Rust the spending key to derive viewing keys +
    /// decrypt notes. (Spend is attended/PIN on iOS; not needed for receive.)
    /// WARNING: treat as a production secret.</summary>
    public string Mnemonic { get; set; } = "";

    /// <summary>BIP44 account index within the wallet (0 for the first shielded
    /// sub-wallet). Defaults to 0.</summary>
    public uint AccountIndex { get; set; } = 0;

    /// <summary>SQLite path for the shielded commitment-tree store. If empty,
    /// uses a default under the BTCPay data dir. Forwarded to
    /// platform_wallet_manager_configure_shielded.</summary>
    public string ShieldedDbPath { get; set; } = "";

    /// <summary>Sync interval in seconds (0 = use the Rust default). The Rust
    /// loop auto-polls; this tunes the cadence.</summary>
    public ulong SyncIntervalSeconds { get; set; } = 0;
}
