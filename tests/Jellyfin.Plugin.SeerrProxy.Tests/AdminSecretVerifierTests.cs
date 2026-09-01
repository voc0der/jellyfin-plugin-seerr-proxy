using Jellyfin.Plugin.SeerrProxy.Security;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class AdminSecretVerifierTests
{
    private const string Secret = "T8jJcVQ0y-9wKzXqLmR2nPbA4sDfGhJkLmNoPqRsTuV";

    [Fact]
    public void Verify_MatchingSecret_Succeeds()
    {
        var hash = AdminSecretVerifier.ComputeHashHex(Secret);

        Assert.True(AdminSecretVerifier.Verify(hash, Secret));
    }

    [Fact]
    public void Verify_MatchingSecret_IgnoresSurroundingWhitespaceOnTheHash()
    {
        var hash = "  " + AdminSecretVerifier.ComputeHashHex(Secret) + "\n";

        Assert.True(AdminSecretVerifier.Verify(hash, Secret));
    }

    [Fact]
    public void Verify_UppercaseHash_Succeeds()
    {
        var hash = AdminSecretVerifier.ComputeHashHex(Secret).ToUpperInvariant();

        Assert.True(AdminSecretVerifier.Verify(hash, Secret));
    }

    [Fact]
    public void Verify_WrongSecret_Fails()
    {
        var hash = AdminSecretVerifier.ComputeHashHex(Secret);

        Assert.False(AdminSecretVerifier.Verify(hash, Secret + "x"));
    }

    [Fact]
    public void Verify_SecretWithTrailingWhitespace_Fails()
    {
        var hash = AdminSecretVerifier.ComputeHashHex(Secret);

        // The presented value is compared exactly. Only the configured hash is trimmed.
        Assert.False(AdminSecretVerifier.Verify(hash, Secret + " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-hex")]
    [InlineData("abcd")]
    public void Verify_UnusableConfiguredHash_FailsClosed(string? configuredHash)
    {
        Assert.False(AdminSecretVerifier.Verify(configuredHash, Secret));
        Assert.False(AdminSecretVerifier.IsConfigured(configuredHash));
    }

    [Fact]
    public void Verify_HashOfCorrectLengthButNotHex_FailsClosed()
    {
        var notHex = new string('z', 64);

        Assert.False(AdminSecretVerifier.IsConfigured(notHex));
        Assert.False(AdminSecretVerifier.Verify(notHex, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_MissingPresentedSecret_Fails(string? presented)
    {
        var hash = AdminSecretVerifier.ComputeHashHex(Secret);

        Assert.False(AdminSecretVerifier.Verify(hash, presented));
    }

    [Fact]
    public void ComputeHashHex_IsLowercaseSha256()
    {
        var hash = AdminSecretVerifier.ComputeHashHex("abc");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void IsConfigured_WellFormedHash_IsTrue()
    {
        Assert.True(AdminSecretVerifier.IsConfigured(AdminSecretVerifier.ComputeHashHex(Secret)));
    }

    [Fact]
    public void HeaderName_IsStable()
    {
        // Renaming this breaks every deployed caller. Pin it.
        Assert.Equal("X-Seerr-Proxy-Secret", AdminSecretVerifier.HeaderName);
    }
}
