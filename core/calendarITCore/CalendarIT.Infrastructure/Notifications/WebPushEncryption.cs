using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CalendarIT.Infrastructure.Notifications;

/// <summary>
/// Web Push message encryption as RFC 8291 has it: <c>aes128gcm</c> (RFC 8188), one record, the
/// salt and the server's ephemeral key in the body's header. That is the encoding every browser and
/// UnifiedPush (whose distributors forward only the body) can read — the older <c>aesgcm</c> draft
/// keeps them in HTTP headers, which a UnifiedPush distributor never passes on to the app.
/// </summary>
public static class WebPushEncryption
{
    private const int RecordSize = 4096;

    /// <summary>The largest payload that fits one record (RFC 8291 §4: 3993 octets).</summary>
    public const int MaxPayload = 3993;

    /// <summary>Encrypts [payload] for the receiver's [p256dh] public key and [auth] secret (both base64url).</summary>
    public static byte[] Encrypt(byte[] payload, string p256dh, string auth)
    {
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(payload, Base64Url.DecodeFromChars(p256dh.Trim()), Base64Url.DecodeFromChars(auth.Trim()), sender, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The same with the sender key and salt given (for the RFC's test vector).</summary>
    public static byte[] Encrypt(byte[] payload, byte[] receiverPublic, byte[] authSecret, ECDiffieHellman sender, byte[] salt)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException($"A Web Push payload holds at most {MaxPayload} bytes.", nameof(payload));
        }
        if (receiverPublic.Length != 65 || receiverPublic[0] != 0x04)
        {
            throw new ArgumentException("The receiver key isn't an uncompressed P-256 point.", nameof(receiverPublic));
        }

        using var receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = receiverPublic[1..33], Y = receiverPublic[33..65] },
        });
        var senderPoint = sender.ExportParameters(false).Q;
        var senderPublic = new byte[65];
        senderPublic[0] = 0x04;
        senderPoint.X!.CopyTo(senderPublic, 1);
        senderPoint.Y!.CopyTo(senderPublic, 33);

        var ecdhSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);

        // IKM = HKDF(auth, ecdh_secret, "WebPush: info" || 0 || ua_public || as_public, 32)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), receiverPublic, senderPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        // CEK and nonce from the salt (RFC 8188 §2.2, §2.3).
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // One record: the payload, then the last-record delimiter 0x02.
        var plain = new byte[payload.Length + 1];
        payload.CopyTo(plain, 0);
        plain[^1] = 0x02;
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        // Header: salt (16) || rs (4, big-endian) || idlen (1) || keyid (the sender's public key).
        var header = new byte[16 + 4 + 1 + senderPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)senderPublic.Length;
        senderPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(all, at);
            at += p.Length;
        }
        return all;
    }
}
