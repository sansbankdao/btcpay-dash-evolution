// File: Plugins/DashEvolution/Base58Check.cs
//
// Minimal base58check encoding for Dash Transparent (DIP-17 platform)
// addresses. The FFI (platform_address_wallet_next_unused_receive_address)
// returns an address as (address_type, 20-byte hash); the host applies the
// network version byte and base58check encoding for display — the same
// split the FFI uses for shielded bech32m (host encodes, Rust derives).
//
// No external dependency: the algorithm is a few lines.

using System;
using System.Security.Cryptography;

namespace BTCPayServer.Plugins.DashEvolution;

public static class Base58Check
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    // Dash address version bytes.
    public const byte P2pkhVersionMainnet = 0x4C;   // 76  — 'X…' addresses
    public const byte P2pkhVersionTestnet = 0x8C;   // 140 — 'y…' addresses

    /// <summary>
    /// Base58check-encode a 20-byte pubkey hash with the Dash P2PKH version
    /// byte for the network (mainnet → 'X…', testnet → 'y…').
    /// </summary>
    public static string EncodeP2pkh(bool mainnet, byte[] hash20)
    {
        if (hash20 is null || hash20.Length != 20)
            throw new ArgumentException("hash20 must be exactly 20 bytes", nameof(hash20));
        return Encode(mainnet ? P2pkhVersionMainnet : P2pkhVersionTestnet, hash20);
    }

    /// <summary>
    /// Base58check-encode version byte + payload + 4-byte double-SHA256
    /// checksum.
    /// </summary>
    public static string Encode(byte version, byte[] payload)
    {
        var data = new byte[1 + payload.Length + 4];
        data[0] = version;
        Array.Copy(payload, 0, data, 1, payload.Length);
        var checksum = DoubleSha256(data.AsSpan(0, 1 + payload.Length));
        Array.Copy(checksum, 0, data, 1 + payload.Length, 4);
        return Base58Encode(data);
    }

    private static byte[] DoubleSha256(ReadOnlySpan<byte> data)
    {
        var first = SHA256.HashData(data);
        return SHA256.HashData(first);
    }

    private static string Base58Encode(byte[] input)
    {
        var leadingZeros = 0;
        while (leadingZeros < input.Length && input[leadingZeros] == 0)
            leadingZeros++;

        // Big-number division: repeatedly divide the buffer by 58 in place,
        // emitting each remainder as a base58 digit.
        var digits = new char[input.Length * 2];
        var digitCount = 0;
        var startAt = leadingZeros;
        while (startAt < input.Length)
        {
            var remainder = 0;
            for (var i = startAt; i < input.Length; i++)
            {
                var acc = remainder * 256 + input[i];
                input[i] = (byte)(acc / 58);
                remainder = acc % 58;
            }
            digits[digitCount++] = Alphabet[remainder];
            if (input[startAt] == 0)
                startAt++;
        }

        var result = new char[leadingZeros + digitCount];
        for (var i = 0; i < leadingZeros; i++)
            result[i] = '1';
        for (var i = 0; i < digitCount; i++)
            result[leadingZeros + i] = digits[digitCount - 1 - i];
        return new string(result);
    }
}
