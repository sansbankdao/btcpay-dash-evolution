// File: Plugins/DashEvolution/Bech32m.cs
//
// Self-contained bech32m (BIP-350) encoder for DIP-0018 Dash addresses.
// No external dependency — avoids needing to verify NBitcoin's Bech32
// supports the bech32m variant constant. The algorithm is the well-known
// BIP-173/BIP-350 polymorphic encoder: bech32m uses checksum constant
// 0x2bc830a3 (vs bech32's 0x00000001).
//
// WIRE FORMAT (verified from dashwallet-ios sources, see HANDOFF.md §6):
//   - Shielded (Orchard) display address:
//       HRP = "dash" (mainnet) / "tdash" (testnet)
//       data = 0x10 (type byte) || 43 raw Orchard bytes = 44 bytes
//       encoding = bech32m
//       (DWParsedPaymentURI.swift:34-37, PaymentsLandingViewModel.swift:305-307)
//   - Platform (DIP-0018) address:
//       HRP = "dash" / "tdash", data = 0xb0|0x80 || 20 bytes = 21 bytes
//   This encoder handles the shielded case for the demo; platform addresses
//   are Phase 2.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.DashEvolution;

public static class Bech32m
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32mConstant = 0x2bc830a3;

    private static uint Polymod(IReadOnlyList<byte> values)
    {
        var gen = new uint[] { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            // BIP-350 reference: mask BEFORE the left shift, not after.
            // The prior form ((chk << 5) ^ v) & 0x1ffffff truncated bits
            // 25-29 that the shift produces from bits 20-24, corrupting the
            // generator feedback. The iOS wallet validates with the standard
            // BIP-350 constant 0x2bc830a3; the buggy mask-after encoding
            // yielded polymod 0x01c830a3 and the wallet rejected the
            // address with "This is not a valid Dash address for this
            // network".
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0)
                    chk ^= gen[i];
            }
        }
        return chk;
    }

    private static IReadOnlyList<byte> HrpExpand(string hrp)
    {
        var ret = new List<byte>(hrp.Length * 2 + 1);
        foreach (var c in hrp)
            ret.Add((byte)(c >> 5));
        ret.Add(0);
        foreach (var c in hrp)
            ret.Add((byte)(c & 0x1f));
        return ret;
    }

    private static IReadOnlyList<byte> CreateChecksum(string hrp, IReadOnlyList<byte> data)
    {
        var values = new List<byte>(hrp.Length * 2 + 1 + data.Count + 6);
        values.AddRange(HrpExpand(hrp));
        values.AddRange(data);
        values.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });
        var mod = Polymod(values) ^ Bech32mConstant;
        var ret = new byte[6];
        for (var i = 0; i < 6; i++)
            ret[i] = (byte)((mod >> (5 * (5 - i))) & 0x1f);
        return ret;
    }

    /// <summary>
    /// Convert from 8-bit bytes to 5-bit groups (bech32 data values).
    /// </summary>
    private static IReadOnlyList<byte> ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        var ret = new List<byte>();
        int acc = 0, bits = 0;
        var maxv = (1 << toBits) - 1;
        var maxAcc = (1 << (fromBits + toBits - 1)) - 1;
        foreach (var value in data)
        {
            if (value >> fromBits != 0)
                throw new ArgumentException($"Invalid byte 0x{value:x2} for {fromBits}-bit group");
            acc = ((acc << fromBits) | value) & maxAcc;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                ret.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad && bits > 0)
            ret.Add((byte)((acc << (toBits - bits)) & maxv));
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
            throw new ArgumentException("Non-zero padding bits in input");
        return ret;
    }

    /// <summary>
    /// Encode a DIP-0018 shielded (Orchard) display address: bech32m with
    /// HRP "dash"/"tdash" and data = 0x10 || raw43.
    /// </summary>
    /// <param name="mainnet">true for HRP "dash", false for "tdash".</param>
    /// <param name="rawOrchardBytes">43 raw Orchard address bytes (recipient + diversifier),
    /// exactly as returned by platform_wallet_manager_shielded_default_address.</param>
    public static string EncodeShieldedAddress(bool mainnet, byte[] rawOrchardBytes)
    {
        if (rawOrchardBytes == null || rawOrchardBytes.Length != 43)
            throw new ArgumentException("Orchard address must be exactly 43 bytes", nameof(rawOrchardBytes));
        var hrp = mainnet ? "dash" : "tdash";
        // 0x10 type byte + 43 raw bytes = 44 bytes payload
        var payload = new byte[44];
        payload[0] = 0x10;
        Buffer.BlockCopy(rawOrchardBytes, 0, payload, 1, 43);
        // Convert 8-bit payload to 5-bit groups (bech32 data values)
        var data5 = ConvertBits(payload, 8, 5, pad: true);
        var checksum = CreateChecksum(hrp, data5);
        var sb = new System.Text.StringBuilder(hrp.Length + 1 + (data5.Count + 6) * 1);
        sb.Append(hrp).Append('1');
        foreach (var v in data5)
            sb.Append(Charset[v]);
        foreach (var v in checksum)
            sb.Append(Charset[v]);
        return sb.ToString();
    }

    /// <summary>
    /// Decode a bech32m string into (hrp, 8-bit data payload). Throws on
    /// invalid checksum / wrong variant. Used for validating user-supplied
    /// addresses (Phase 2 send-side); the demo receive path only encodes.
    /// </summary>
    public static (string hrp, byte[] data) Decode(string s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        s = s.ToLowerInvariant();
        var pos = s.LastIndexOf('1');
        if (pos < 1 || pos + 7 > s.Length)
            throw new FormatException("Invalid bech32m string: missing or misplaced separator");
        var hrp = s.Substring(0, pos);
        var dataPart = s.Substring(pos + 1);
        var data5 = new List<byte>(dataPart.Length);
        foreach (var c in dataPart)
        {
            var idx = Charset.IndexOf(c);
            if (idx < 0) throw new FormatException($"Invalid bech32m character '{c}'");
            data5.Add((byte)idx);
        }
        // Verify checksum
        var values = new List<byte>(hrp.Length * 2 + 1 + data5.Count);
        values.AddRange(HrpExpand(hrp));
        values.AddRange(data5);
        var mod = Polymod(values);
        if (mod != Bech32mConstant)
            throw new FormatException($"Invalid bech32m checksum (got polymod 0x{mod:x8})");
        // Strip the 6 checksum values, convert 5-bit → 8-bit
        var dataNoChecksum = data5.GetRange(0, data5.Count - 6);
        var data8 = ConvertBits(dataNoChecksum.ToArray(), 5, 8, pad: false);
        return (hrp, data8.ToArray());
    }
}
