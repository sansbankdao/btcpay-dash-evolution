using System;
using BTCPayServer.Plugins.DashEvolution;
using Xunit;

namespace BTCPayServer.Plugins.DashEvolution.Tests;

public class Bech32mTests
{
    [Fact]
    public void EncodeShieldedAddress_Mainnet_ReturnsDashPrefix()
    {
        // Arrange
        var payload = new byte[43]; // 43-byte Orchard payload
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        // Act
        var result = Bech32m.EncodeShieldedAddress(mainnet: true, payload);

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("dash1", result);
        Assert.True(result.Length > 70); // bech32m encoded 44 bytes should be ~80+ chars
    }

    [Fact]
    public void EncodeShieldedAddress_Testnet_ReturnsTdashPrefix()
    {
        // Arrange
        var payload = new byte[43];

        // Act
        var result = Bech32m.EncodeShieldedAddress(mainnet: false, payload);

        // Assert
        Assert.StartsWith("tdash1", result);
    }

    [Fact]
    public void EncodeShieldedAddress_NullPayload_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Bech32m.EncodeShieldedAddress(true, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(44)]
    public void EncodeShieldedAddress_WrongLength_Throws(int length)
    {
        // Arrange
        var payload = new byte[length];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Bech32m.EncodeShieldedAddress(true, payload));
    }

    [Fact]
    public void Decode_ValidAddress_ReturnsHrpAndData()
    {
        // Arrange
        var originalPayload = new byte[43];
        for (int i = 0; i < originalPayload.Length; i++)
            originalPayload[i] = (byte)(i * 2);
        var encoded = Bech32m.EncodeShieldedAddress(true, originalPayload);

        // Act
        var (hrp, data) = Bech32m.Decode(encoded);

        // Assert
        Assert.Equal("dash", hrp);
        Assert.NotNull(data);
        Assert.Equal(44, data.Length); // 0x10 type byte + 43 payload bytes
        Assert.Equal(0x10, data[0]); // type byte
        Assert.Equal(originalPayload, data[1..]); // payload matches
    }

    [Fact]
    public void Decode_Null_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => Bech32m.Decode(null!));
    }

    [Fact]
    public void RoundTrip_MainnetAddress_PreservesData()
    {
        // Arrange
        var payload = new byte[43];
        new Random(42).NextBytes(payload);

        // Act
        var encoded = Bech32m.EncodeShieldedAddress(true, payload);
        var (hrp, data) = Bech32m.Decode(encoded);

        // Assert
        Assert.Equal("dash", hrp);
        Assert.Equal(payload, data[1..]);
    }

    [Fact]
    public void RoundTrip_TestnetAddress_PreservesData()
    {
        // Arrange
        var payload = new byte[43];
        new Random(42).NextBytes(payload);

        // Act
        var encoded = Bech32m.EncodeShieldedAddress(false, payload);
        var (hrp, data) = Bech32m.Decode(encoded);

        // Assert
        Assert.Equal("tdash", hrp);
        Assert.Equal(payload, data[1..]);
    }

    [Fact]
    public void EncodePlatformAddress_P2pkhMainnet_MatchesDip18Vector()
    {
        // DIP-0018 test vector: rs-dpp address_funds/platform_address.rs
        // test_bech32m_p2pkh_mainnet_roundtrip
        var hash = new byte[]
        {
            0xf7, 0xda, 0x0a, 0x2b, 0x5c, 0xbd, 0x4f, 0xf6, 0xbb, 0x2c, 0x4d, 0x89, 0xb6, 0x7d,
            0x2f, 0x3f, 0xfe, 0xec, 0x05, 0x25,
        };

        var encoded = Bech32m.EncodePlatformAddress(mainnet: true, 0xb0, hash);

        Assert.Equal("dash1krma5z3ttj75la4m93xcndna9ullamq9y5e9n5rs", encoded);
    }

    [Fact]
    public void EncodePlatformAddress_P2shTestnet_MatchesDip18Vector()
    {
        // DIP-0018 test vector: rs-dpp address_funds/platform_address.rs
        // test_bech32m_p2sh_testnet_roundtrip
        var hash = new byte[]
        {
            0x43, 0xfa, 0x18, 0x3c, 0xf3, 0xfb, 0x6e, 0x9e, 0x7d, 0xc6, 0x2b, 0x69, 0x2a, 0xeb,
            0x4f, 0xc8, 0xd8, 0x04, 0x56, 0x36,
        };

        var encoded = Bech32m.EncodePlatformAddress(mainnet: false, 0x80, hash);

        Assert.Equal("tdash1sppl5xpu70aka8nacc4kj2htflydspzkxc8jtru5", encoded);
    }
}
