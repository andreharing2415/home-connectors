using System.Text;
using HomeConnectors.Adapters;

namespace HomeConnectors.Tests;

/// <summary>
/// HOME-03. The miIO framing, checked against a packet built by an independent
/// Python implementation (pycryptodome) — the same one that first talked to
/// the owner's M40 on 2026-09-27. If this passes, a packet the hub seals is one the
/// device opens.
/// </summary>
[TestClass]
public sealed class MiioClientTests
{
    // Token 00 01 02 … 0f, device id 0x44d57e5b, stamp 17548, and this body.
    private const string Body = """{"id":7,"method":"get_properties","params":[]}""";

    private const string Reference =
        "213100500000000044d57e5b0000448c2952bb655189824687363dd330ff3650b8657c7e35ad4bba388b5df0fd395909d41a1d3ab6f3bacfc75c2804e7a05c791d52d88c6c9848ad716d8c27206446e3";

    private static readonly byte[] Token = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    [TestMethod]
    public void A_sealed_packet_matches_the_reference_byte_for_byte()
    {
        var packet = MiioClient.Seal(Token, 0x44d57e5b, 17548, Encoding.UTF8.GetBytes(Body));

        Assert.AreEqual(Reference, Convert.ToHexStringLower(packet));
    }

    [TestMethod]
    public void The_reference_packet_opens_to_its_body()
    {
        var plain = MiioClient.Open(Token, Convert.FromHexString(Reference));

        Assert.IsNotNull(plain);
        Assert.AreEqual(Body, Encoding.UTF8.GetString(plain));
    }

    [TestMethod]
    public void A_packet_under_another_token_does_not_open()
    {
        var other = Token.Select(b => (byte)(b ^ 0xFF)).ToArray();

        Assert.IsNull(MiioClient.Open(other, Convert.FromHexString(Reference)), "o checksum usa o token: outro token não confere");
    }

    [TestMethod]
    public void A_token_is_32_hex_characters_or_nothing()
    {
        CollectionAssert.AreEqual(Token, MiioClient.ParseToken("000102030405060708090a0b0c0d0e0f"));
        Assert.IsNull(MiioClient.ParseToken("0001"));
        Assert.IsNull(MiioClient.ParseToken("zz0102030405060708090a0b0c0d0e0f"));
        Assert.IsNull(MiioClient.ParseToken(null));
    }
}
