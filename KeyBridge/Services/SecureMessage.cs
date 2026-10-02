using System;
using System.Security.Cryptography;
using System.Text;

namespace KeyBridge.Services;

public static class SecureMessage
{
    private static readonly byte[] Magic = "KBS1"u8.ToArray();

    public static string ProtectText(string text, string token) =>
        Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(text), token));

    public static bool TryUnprotectText(string protectedText, string token, out string text)
    {
        text = string.Empty;
        try
        {
            if (!TryUnprotectBytes(Convert.FromBase64String(protectedText), token, out var bytes))
            {
                return false;
            }

            text = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] ProtectBytes(byte[] plaintext, string token)
    {
        var key = DeriveKey(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);

        var result = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
        Buffer.BlockCopy(nonce, 0, result, Magic.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, Magic.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, Magic.Length + nonce.Length + tag.Length, ciphertext.Length);
        return result;
    }

    public static bool TryUnprotectBytes(byte[] protectedBytes, string token, out byte[] plaintext)
    {
        plaintext = [];
        if (protectedBytes.Length < Magic.Length + 12 + 16 ||
            !protectedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return false;
        }

        try
        {
            var key = DeriveKey(token);
            var nonce = protectedBytes.AsSpan(Magic.Length, 12);
            var tag = protectedBytes.AsSpan(Magic.Length + 12, 16);
            var ciphertext = protectedBytes.AsSpan(Magic.Length + 28);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = [];
            return false;
        }
    }

    private static byte[] DeriveKey(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
