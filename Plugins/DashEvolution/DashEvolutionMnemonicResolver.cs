// File: Plugins/DashEvolution/DashEvolutionMnemonicResolver.cs
//
// C# implementation of the MnemonicResolveCallback vtable. The Rust side
// calls this synchronously to fetch the BIP-39 mnemonic for a wallet_id;
// derivation happens Rust-side (the "no mnemonic round-tripping" rule —
// see mnemonic_resolver.rs module doc). On a headless BTCPay server the
// mnemonic comes from configuration (appsettings / store config), NOT a
// Keychain — so this resolver is a simple dictionary lookup + UTF-8 copy.
//
// LIFETIME: the resolve + destroy delegate instances are held by this
// object (fields _resolve, _destroy) so the GC does not collect them while
// the native resolver handle is live. The sync service keeps this object
// alive for its lifetime, then calls DashSdkFFI.DestroyMnemonicResolver
// and drops the reference. ctx is passed as IntPtr.Zero — the instance-
// method delegate thunk captures `this` directly (no GCHandle needed).
//
// SAFETY: the resolve callback is invoked on a Rust worker thread. It only
// reads the immutable _mnemonics dictionary (populated once at construct
// time, never mutated after). Dictionary<string,string> is safe for
// concurrent reads.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BTCPayServer.Plugins.DashEvolution.Native;

namespace BTCPayServer.Plugins.DashEvolution;

/// <summary>
/// Supplies BIP-39 mnemonics to the Rust shielded-derivation pipeline.
/// Maps wallet_id (32-byte hex) → mnemonic string. Constructed once from
/// config; read-only thereafter.
/// </summary>
internal sealed class DashEvolutionMnemonicResolver : IDisposable
{
    private readonly Dictionary<string, string> _mnemonics;
    // Held to prevent GC of the delegates while the native handle is live.
    private readonly MnemonicResolveCallback _resolve;
    private readonly MnemonicResolverDestroyCallback _destroy;
    private IntPtr _nativeHandle;
    private bool _disposed;

    public DashEvolutionMnemonicResolver(IReadOnlyDictionary<string, string> mnemonicsByWalletIdHex)
    {
        _mnemonics = new Dictionary<string, string>(mnemonicsByWalletIdHex, StringComparer.OrdinalIgnoreCase);
        // Pin the delegates as instance-method thunks. The thunk captures `this`.
        _resolve = OnResolve;
        _destroy = OnDestroy;
    }

    /// <summary>
    /// Build the native resolver handle. Must be called before passing to
    /// BindShielded. Returns the *mut MnemonicResolverHandle pointer.
    /// </summary>
    public IntPtr CreateNativeHandle()
    {
        if (_nativeHandle != IntPtr.Zero)
            return _nativeHandle;
        // ctx = Zero (ignored by our instance-method thunk).
        _nativeHandle = DashSdkFFI.CreateMnemonicResolver(IntPtr.Zero, _resolve, _destroy);
        return _nativeHandle;
    }

    private int OnResolve(IntPtr ctx, IntPtr walletIdBytes, IntPtr outMnemonicUtf8, UIntPtr outCapacity, IntPtr outLen)
    {
        if (walletIdBytes == IntPtr.Zero || outMnemonicUtf8 == IntPtr.Zero || outLen == IntPtr.Zero)
            return 3; // OTHER
        try
        {
            // Read 32-byte wallet_id → lowercase hex (the key in our dictionary).
            var bytes = new byte[32];
            Marshal.Copy(walletIdBytes, bytes, 0, 32);
            var walletIdHex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

            if (!_mnemonics.TryGetValue(walletIdHex, out var mnemonic))
                return 1; // NOT_FOUND — Rust surfaces as ErrorWalletOperation "mnemonic missing"

            var utf8 = Encoding.UTF8.GetBytes(mnemonic);
            // Capacity is 1024 (MNEMONIC_RESOLVER_BUFFER_CAPACITY) incl. trailing NUL.
            var cap = (int)outCapacity.ToUInt32();
            if (utf8.Length + 1 > cap)
                return 2; // BUFFER_TOO_SMALL — should never happen (1024 is plenty)
            Marshal.Copy(utf8, 0, outMnemonicUtf8, utf8.Length);
            // NUL-terminate
            Marshal.WriteByte(outMnemonicUtf8, utf8.Length, 0);
            // Write out_len (excluding NUL) — usize on the Rust side.
            Marshal.WriteInt64(outLen, utf8.Length);
            return 0; // SUCCESS
        }
        catch
        {
            return 3; // OTHER
        }
    }

    private void OnDestroy(IntPtr ctx)
    {
        // No-op: lifetime is managed by the sync service (it holds this C#
        // object + the delegate fields). Rust calls this exactly once when
        // the resolver handle is destroyed, but there's nothing to free —
        // the GCHandle is not used (ctx is Zero).
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_nativeHandle != IntPtr.Zero)
        {
            DashSdkFFI.DestroyMnemonicResolver(_nativeHandle);
            _nativeHandle = IntPtr.Zero;
        }
    }
}
