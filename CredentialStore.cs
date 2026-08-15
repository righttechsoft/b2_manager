using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace B2Manager;

public sealed class Credentials
{
    public string KeyId { get; set; } = "";
    public string ApplicationKey { get; set; } = "";
}

public static class CredentialStore
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 600_000;

    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "B2Manager", "credentials.bin");

    public static bool Exists() => File.Exists(StorePath);

    public static void Reset()
    {
        if (File.Exists(StorePath))
            File.Delete(StorePath);
    }

    public static void Save(Credentials credentials, string password)
    {
        string? dir = Path.GetDirectoryName(StorePath);
        if (dir != null)
            Directory.CreateDirectory(dir);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = DeriveKey(password, salt);

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        using var fs = new FileStream(StorePath, FileMode.Create, FileAccess.Write);
        fs.Write(salt, 0, salt.Length);
        fs.Write(nonce, 0, nonce.Length);
        fs.Write(tag, 0, tag.Length);
        fs.Write(ciphertext, 0, ciphertext.Length);
    }

    public static Credentials Load(string password)
    {
        byte[] data = File.ReadAllBytes(StorePath);

        byte[] salt = new byte[SaltSize];
        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        int headerSize = SaltSize + NonceSize + TagSize;
        byte[] ciphertext = new byte[data.Length - headerSize];

        Buffer.BlockCopy(data, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(data, SaltSize, nonce, 0, NonceSize);
        Buffer.BlockCopy(data, SaltSize + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(data, headerSize, ciphertext, 0, ciphertext.Length);

        byte[] key = DeriveKey(password, salt);
        byte[] plaintext = new byte[ciphertext.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return JsonSerializer.Deserialize<Credentials>(plaintext)
            ?? throw new InvalidDataException("Stored credentials could not be parsed.");
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
