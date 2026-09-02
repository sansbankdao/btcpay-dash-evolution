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
}
