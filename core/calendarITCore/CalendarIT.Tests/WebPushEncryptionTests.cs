using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CalendarIT.Infrastructure.Notifications;

namespace CalendarIT.Tests;

/// <summary>Web Push payloads are encrypted as RFC 8291 (aes128gcm), byte for byte its example.</summary>
public sealed class WebPushEncryptionTests
{
    private static byte[] B(string base64Url) => Base64Url.DecodeFromChars(base64Url);

    [Fact]
    public void MatchesTheRfc8291Example()
    {
        var senderPublic = B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });

        var body = WebPushEncryption.Encrypt(
            Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon"),
            B("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            B("BTBZMqHH6r4Tts7J_aSIgg"),
            sender,
            B("DGv6ra1nlYgDCS1FRnbzlw"));

        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.EncodeToString(body));
    }

    [Fact]
    public void AFreshKeyAndSaltEveryTime()
    {
        using var receiver = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = receiver.ExportParameters(false).Q;
        var p256dh = Base64Url.EncodeToString([0x04, .. q.X!, .. q.Y!]);
        var auth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

        var a = WebPushEncryption.Encrypt("""{"type":"sync"}"""u8.ToArray(), p256dh, auth);
        var b = WebPushEncryption.Encrypt("""{"type":"sync"}"""u8.ToArray(), p256dh, auth);

        Assert.NotEqual(a, b);
        Assert.Equal(16 + 4 + 1 + 65 + 15 + 1 + 16, a.Length);
    }
}
