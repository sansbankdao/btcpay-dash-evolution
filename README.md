# Dash Evolution BTCPay Server Plugin

[![Build & Test](https://github.com/sansbankdao/btcpay-dash-evolution/actions/workflows/build.yml/badge.svg)](https://github.com/sansbankdao/btcpay-dash-evolution/actions/workflows/build.yml)
[![codecov](https://codecov.io/gh/sansbankdao/btcpay-dash-evolution/branch/master/graph/badge.svg)](https://codecov.io/gh/sansbankdao/btcpay-dash-evolution)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

This plugin extends BTCPay Server to enable users to receive payments via **Dash on the Evolution Platform** — both the **shielded pool (Orchard / ZK)** and **unshielded Platform Addresses (DIP-17)**.

> [!WARNING]
> This plugin shares a single Dash Evolution wallet across all the stores in the BTCPay Server instance. Use this plugin only if you are not sharing your instance.

## Features

- **Shielded payments** (Orchard / ZK protocol) — privacy-preserving Dash receives
- **Platform Addresses** (DIP-17) — unshielded Evolution Platform credits
- **Single payment method code** (`DASHE`) — the handler multiplexes internally between shielded and Platform Address transports
- **Native FFI integration** — uses the `platform-wallet-ffi` Rust library for wallet operations
- **Automatic invoice settlement** — background sync service detects incoming payments and marks invoices paid

## Architecture

Unlike transparent Dash (which uses NBXplorer), Evolution Dash uses:
- **Rust FFI** (`libplatform_wallet_ffi`) for shielded note scanning and Platform Address sync
- **DAPI** (Dash Platform API) for blockchain state — no local node required
- **Bech32m addresses** (BIP-350) for shielded receives

## Installation

### Prerequisites

1. BTCPay Server 2.3.7 or later
2. The native library `libplatform_wallet_ffi.so` (Linux) or `platform_wallet_ffi.dll` (Windows)

### Native Library Setup

The plugin requires the `platform-wallet-ffi` native library. You can either:

**Option A: Build from source** (recommended for development)
```bash
git clone https://github.com/dashpay/platform
cd platform
cargo build --release --features shielded -p platform-wallet-ffi
# Set the environment variable:
export DASHE_NATIVE_LIB=/path/to/platform/target/release/libplatform_wallet_ffi.so
```

**Option B: Download pre-built binary** (when available)
```bash
# Place in the plugin's runtimes/<rid>/native/ directory
mkdir -p Plugins/DashEvolution/runtimes/linux-x64/native/
# Download libplatform_wallet_ffi.so to that directory
```

### Plugin Installation

1. Build the plugin:
   ```bash
   dotnet build Plugins/DashEvolution/BTCPayServer.Plugins.DashEvolution.csproj -c Release
   ```

2. Copy the output to your BTCPay Server plugins directory

3. Configure the plugin via environment variables:
   ```bash
   export DashEvolution__WalletIdHex="your-64-char-hex-wallet-id"
   export DashEvolution__Mnemonic="your 24 word mnemonic phrase"
   export DashEvolution__DapiAddresses="https://45.135.180.70:443"
   export DashEvolution__Mainnet="true"
   export DashEvolution__SyncIntervalSeconds="15"
   ```

4. Restart BTCPay Server

## Deploy on a fresh VPS with Docker (verified)

This is a complete, step-by-step guide to a production deployment on a brand-new VPS (Ubuntu/Docker), verified end-to-end. Bring your own reverse proxy (Cloudflare Tunnel, nginx, …).

### 1. Provision the VPS

- A small VPS is fine (2 vCPU / 4 GB RAM / 30 GB disk is sufficient — BTCPay + the plugin add ~2.8 GB RSS over Docker).
- Install **Docker** and the **.NET 10 SDK** (only needed to build the plugin and `.btcpay`).
- Create a dedicated Docker network and a Postgres container:

```bash
docker network create btcpay-network
docker run -d --name btcpay-pg --network btcpay-network --restart unless-stopped \
  -e POSTGRES_PASSWORD='<postgres-password>' -p 127.0.0.1:5432:5432 postgres:16
docker exec btcpay-pg psql -U postgres -c "CREATE DATABASE btcpayserver;"
```

### 2. Build the plugin

```bash
git clone https://github.com/sansbankdao/btcpay-dash-evolution.git && cd btcpay-dash-evolution
dotnet build Plugins/DashEvolution/BTCPayServer.Plugins.DashEvolution.csproj -c Release
OUT=Plugins/DashEvolution/bin/Release/net10.0

mkdir -p ~/btcpay-docker/plugins/BTCPayServer.Plugins.DashEvolution
cp $OUT/BTCPayServer.Plugins.DashEvolution.dll ~/btcpay-docker/plugins/BTCPayServer.Plugins.DashEvolution/
cp $OUT/BTCPayServer.Plugins.DashEvolution.deps.json ~/btcpay-docker/plugins/BTCPayServer.Plugins.DashEvolution/
cp Plugins/DashEvolution/BTCPayServer.Plugins.DashEvolution.json ~/btcpay-docker/plugins/BTCPayServer.Plugins.DashEvolution/
# Package the distributable .btcpay archive too (manifest + dll + deps.json at the zip root):
STAGE=$(mktemp -d)
cp $OUT/BTCPayServer.Plugins.DashEvolution.dll $OUT/BTCPayServer.Plugins.DashEvolution.deps.json $STAGE/
cp Plugins/DashEvolution/BTCPayServer.Plugins.DashEvolution.json $STAGE/
(cd $STAGE && zip ~/btcpay-docker/plugins/BTCPayServer.Plugins.DashEvolution.btcpay ./*) && rm -rf $STAGE
```

> **The plugin must be present as an EXTRACTED directory** named by its `Identifier`
> (`BTCPayServer.Plugins.DashEvolution/`, containing `BTCPayServer.Plugins.DashEvolution.json`
> + the dll + deps.json). A `.btcpay` file alone is NOT loaded by the packaged
> base image. Place both in `~/btcpay-docker/plugins/`.

### 3. Place the native library

```bash
mkdir -p ~/Workspace/native
cp libplatform_wallet_ffi.so ~/Workspace/native/   # see "Native Library Setup" above
```

### 4. docker-compose.yml

Create `~/btcpay-docker/docker-compose.yml`:

```yaml
version: "3.7"
services:
  btcpayserver:
    image: btcpayserver/btcpayserver:2.3.7     # check https://hub.docker.com/r/btcpayserver/btcpayserver/tags for the current release
    restart: unless-stopped
    ports:
      - "127.0.0.1:14142:23000"
    environment:
      BTCPAY_HOST: your-domain.example
      BTCPAY_CHAINS: btc                       # see note below — do NOT put dashe here
      BTCPAY_POSTGRES: Host=btcpay-pg;Port=5432;Database=btcpayserver;Username=postgres;Password=<postgres-password>
      BTCPAY_CHEATMODE: "false"
      BTCPAY_BIND: 0.0.0.0                     # required: the packaged image otherwise binds to localhost only
      DASHE_NATIVE_LIB: /native/libplatform_wallet_ffi.so
      DashEvolution__DapiAddresses: https://<mainnet-dapi-node>:443,https://<another>:443   # see note below
      DashEvolution__Mainnet: "true"
      DashEvolution__Mnemonic: <your 24-word BIP-39 mnemonic>
      DashEvolution__AccountIndex: "0"
      DashEvolution__ShieldedDbPath: /data/dash_shielded.sqlite
      DashEvolution__SyncIntervalSeconds: "15"
    volumes:
      - btcpay_data:/data
      - ./plugins:/root/.btcpayserver/Plugins
      - ~/Workspace/native:/native:ro
    networks:
      - btcpay-network

networks:
  btcpay-network:
    external: true

volumes:
  btcpay_data:
```

**Critical gotchas (all verified on a fresh host):**

- **`BTCPAY_CHAINS: btc`** — the packaged base image does not know `dashe` and exits with
  `Invalid chains "dashe"` if you set it. The plugin registers `DASHE` at startup; the log
  then shows `Supported chains: BTC,DASHE`.
- **`BTCPAY_BIND: 0.0.0.0`** — without it the container only listens on localhost from its own
  perspective and nothing reaches port 14142 (connection refused).
- **Do NOT set `DashEvolution__WalletIdHex`.** The wallet id is derived from the mnemonic and
  auto-created (`Auto-created DashEvolution wallet id …` in the log). Setting it manually with a
  mismatched value throws `WalletIdHex mismatch` at startup.
- **Secrets**: keep the mnemonic out of git — either put this file in a private, gitignored
  directory, or split secrets into an un-tracked `docker-compose.override.yml` / `.env` file.
- **`DapiAddresses`** — the IPs above are placeholders. Fetch the current enabled-node list from
  `https://quorums.mainnet.networks.dash.org/masternodes` and pick ~10-20 enabled mainnet nodes
  exposing their Platform DAPI on port 443, formatted as `https://<ip>:443`, comma-separated.
- **`8333 connection refused` / NBXplorer errors in the log are harmless** — the packaged image
  tries to SPV-sync BTC headers and there is no Bitcoin Core on this box. `DASHE` is unaffected.

### 5. First start & verification

```bash
cd ~/btcpay-docker && docker compose up -d
docker logs -f <project>-btcpayserver-1 2>&1 | grep -aE "Running plugin|Supported chains|sync started|Baseline seeded|Auto-created"
```

Expected lines (in order): `Running plugin BTCPayServer.Plugins.DashEvolution - 1.0.0.0` →
`Supported chains:  BTC,DASHE` → `Auto-created DashEvolution wallet id …` →
`DashEvolution sync started for wallet …` → `Baseline seeded on first sync pass: balance=…
(no invoice matching on first pass)`. A `New` invoice created now survives restarts (see
"Restart behavior" in Limitations).

### 6. Debugging first boot

If the app never listens on 14142, read the **full** log, not only ERR entries — a
`wallet-not-configured` FATAL means the mnemonic env was missing/misparsed.

### 7. Bootstrap the first admin (API + one-time SQL fixes)

The initial admin **cannot** log in via Greenfield basic auth out of the box when created
through the API — three documented gaps require one-time SQL fixes:

```bash
# 1) Create the user (allowed while the instance has no admin):
curl -s -X POST http://127.0.0.1:14142/api/v1/users \
  -H "Content-Type: application/json" \
  -d '{"email":"you@example.org","password":"<strong-password>"}'

# 2) Fix the three API-creation gaps (EmailConfirmed, basic-auth flag, ServerAdmin role)
#    — substitute the user id returned above:
cat > /tmp/fix_admin.sql <<EOF
UPDATE "AspNetUsers" SET "Blob2" = jsonb_set(COALESCE("Blob2",'{}'::jsonb), '{allowGreenfieldBasicAuth}', 'true'::jsonb) WHERE "Email" = 'you@example.org';
UPDATE "AspNetUsers" SET "EmailConfirmed" = true WHERE "Email" = 'you@example.org';
UPDATE "AspNetUsers" SET "Blob2" = jsonb_set("Blob2", '{showInvoiceStatusChangeWarning}', 'true'::jsonb) WHERE "Email" = 'you@example.org';
INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","ConcurrencyStamp")
  SELECT 'ServerAdmin','ServerAdmin','SERVERADMIN', gen_random_uuid()::text
  WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "Name"='ServerAdmin');
INSERT INTO "AspNetUserRoles" ("UserId","RoleId")
  SELECT '<user-id>', 'ServerAdmin'
  WHERE NOT EXISTS (SELECT 1 FROM "AspNetUserRoles" WHERE "UserId"='<user-id>' AND "RoleId"='ServerAdmin');
-- If login was attempted before these fixes, "FirstRun" may be stuck true:
DELETE FROM "Settings" WHERE "Id" = 'BTCPayServer.Services.PoliciesSettings' AND "Value"::jsonb ->> 'FirstRun' = 'true';
EOF
docker cp /tmp/fix_admin.sql btcpay-pg:/tmp/ && docker exec btcpay-pg psql -U postgres -d btcpayserver -f /tmp/fix_admin.sql
```

> This is a **BTCPay Server limitation, not a plugin issue**: the Greenfield
> `POST /api/v1/users` endpoint creates the user with `EmailConfirmed=false`,
> `allowGreenfieldBasicAuth` unset, and NO roles — the first admin must be
> registered through the **MVC UI** to avoid this, or fixed with the SQL above.

### 8. Create the store (note the units)

```bash
SID=$(curl -s -X POST http://127.0.0.1:14142/api/v1/stores \
  -u 'you@example.org:<strong-password>' -H "Content-Type: application/json" \
  -d '{"name":"My Store","defaultCurrency":"USD","invoiceExpiration":54000,"paymentTolerance":2.0}' \
  | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])")
```

- **`invoiceExpiration` is in SECONDS** over the API (54000 = 15 hours). `monitoringExpiration` likewise.
- **`paymentTolerance` percent**: shielded receives arrive slightly under the invoice amount
  (the sender-side Orchard fee varies), so use `2.0` for the demo. Must be set BEFORE the
  invoices you intend to settle — existing invoices keep the tolerance baked in at creation.

### 9. Enable the DASHE-CHAIN payment method

The Greenfield endpoint `PUT /api/v1/stores/{storeId}/payment-methods/DASHE-CHAIN` currently
rejects new payment-method ids for a store that has never had one (verified: `paymentmethod-not-found`). The only working method on a fresh store is a **direct DB write** (no BTCPay restart needed — the next invoice picks it up):

```bash
docker exec btcpay-pg psql -U postgres -d btcpayserver -c \
"UPDATE \"Stores\" SET \"DerivationStrategies\" = '{\"DASHE-CHAIN\": {\"walletIdHex\": \"<64-hex wallet id from the startup log>\"}}' WHERE \"Id\" = '$SID';"
```

Confirm with a test invoice (`POST /api/v1/stores/$SID/invoices {"amount":0.25,"currency":"USD"}`)
and read back `paymentPrompts` — `DASHE-CHAIN` should appear with the shielded address and a
quoted rate.

### 10. Point the domain

Any reverse proxy works. With Cloudflare Tunnel (dashboard-created token tunnel):
`cloudflared service install <token>` with the public hostname routed to `http://localhost:14142`.
Then browse `https://your-domain.example/i/<invoiceId>` — the checkout shows the shielded
address as a QR code and the wallet identity string.

---

## Configuration

| Environment Variable | Description | Default |
|---|---|---|
| `DashEvolution__WalletIdHex` | 64-character hex wallet ID. **Omit it** — the wallet id is derived from the mnemonic and auto-created; setting a mismatched value fails startup | *auto* |
| `DashEvolution__Mnemonic` | 24-word BIP-39 mnemonic phrase | *required* |
| `DashEvolution__DapiAddresses` | Comma-separated DAPI endpoint URLs | `https://45.135.180.70:443` |
| `DashEvolution__Mainnet` | `true` for mainnet, `false` for testnet | `true` |
| `DashEvolution__AccountIndex` | Account index for HD derivation | `0` |
| `DashEvolution__ShieldedDbPath` | Path to shielded SQLite database | `dash_shielded.sqlite` |
| `DashEvolution__SyncIntervalSeconds` | How often to sync (seconds) | `15` |
| `DASHE_NATIVE_LIB` | Absolute path to native library | auto-detect |

## Usage

Once deployed (see "Deploy on a fresh VPS with Docker" above):

1. Create one unpaid invoice at a time (see Limitations)
2. Present the checkout (`/i/<invoiceId>`) — it shows the shielded address + QR code
3. The customer pays from any Dash Evolution wallet with shielded support; the invoice settles automatically within ~15-30 s of the note reaching the pool

## Limitations (current, demo scope)

- **Shared address, balance-delta matching** — every DASHE invoice shows the wallet's default shielded address; the matcher attributes the balance delta to the **single outstanding unpaid** DASHE invoice. Keep only ONE unpaid DASHE invoice at a time. Per-invoice diversified addresses are planned (needs a small upstream FFI addition).
- **Restart behavior** — the wallet baseline is seeded on the FIRST sync pass after process start and **no invoice is marked on that pass**, so `docker restart` is safe. A payment received **while the process is down** is NOT matched after boot (full catch-up needs persisted baselines — planned).
- **Authorized viewing key not yet implemented** — outgoing spends are not reflected in BTCPay's view of the wallet balance (receive-only view).
- **`dash:` payment-link UX** — the `dash:` URI works for copy-paste, but mobile wallet deep-links for bech32m shielded addresses are pending a wallet-side fix. Use the QR / copy button today.
- **For plugin developers**: a plugin-shipped checkout partial MUST declare its own `_ViewImports.cshtml` with `@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers`, `@addTagHelper *, BTCPayServer` and `@addTagHelper *, BTCPayServer.Abstractions` — BTCPay's Content-Security-Policy blocks any inline `<script>` that was not nonced by the TagHelpers carried in via `_ViewImports`. (The host app's `_ViewImports.cshtml` does NOT flow into plugin projects at compile time — this is exactly the bug that produced a seemingly-empty payment box in 1.0.0.)

## Development

### Building

```bash
# Clone with submodules
git clone --recurse-submodules https://github.com/sansbankdao/btcpay-dash-evolution
cd btcpay-dash-evolution

# Build
dotnet build Plugins/DashEvolution/BTCPayServer.Plugins.DashEvolution.csproj
```

### Project Structure

```
Plugins/DashEvolution/
├── DashEvolutionPlugin.cs              # Plugin entry point
├── DashEvolutionNetwork.cs             # Network definition (BTCPayNetworkBase)
├── DashEvolutionPaymentMethodHandler.cs # Payment method handler
├── DashEvolutionSyncService.cs         # Background sync service
├── DashEvolutionSyncOptions.cs         # Configuration options
├── DashEvolutionMnemonicResolver.cs    # Native mnemonic resolver
├── DashEvolutionNativeRegistration.cs  # Native library loader
├── Bech32m.cs                          # Bech32m address encoding
├── Native/
│   ├── PlatformWalletFFI.cs            # Shielded wallet P/Invoke
│   ├── PlatformWalletManagerFFI.cs     # SDK/manager P/Invoke
│   └── PlatformAddressFFI.cs           # Platform Address P/Invoke
└── Views/
    └── DashEvolutionMethodCheckout.cshtml  # Checkout UI
```

## License

MIT

## Acknowledgments

- Built on the [Dash Platform](https://github.com/dashpay/platform) Rust SDK
- Follows the BTCPay Server plugin architecture established by the [Monero](https://github.com/btcpay-monero/btcpayserver-monero-plugin) and [Zcash](https://github.com/btcpay-zcash/btcpayserver-zcash-plugin) plugins
