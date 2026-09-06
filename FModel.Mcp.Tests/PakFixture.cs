using System.Security.Cryptography;
using System.Text;

namespace FModel.Mcp.Tests;

internal static class PakFixture
{
    // Small independently written PAK v7 fixture: uncompressed payload, AES-ECB encrypted index.
    public static void WriteEncryptedIndex(string path, byte[] key)
    {
        var payload = Encoding.UTF8.GetBytes("[Fixture]\nValue=42\n");
        using var output = new BinaryWriter(File.Create(path));
        WriteEntry(output, payload);
        output.Write(payload);
        var indexOffset = output.BaseStream.Position;
        using var indexStream = new MemoryStream();
        using (var index = new BinaryWriter(indexStream, Encoding.UTF8, true))
        {
            WriteString(index, "../../../SyntheticGame/"); index.Write(1);
            WriteString(index, "Config/Test.ini"); WriteEntry(index, payload);
        }
        var plainIndex = indexStream.ToArray();
        var padded = new byte[(plainIndex.Length + 15) / 16 * 16]; plainIndex.CopyTo(padded, 0);
        using var aes = Aes.Create(); aes.Key = key;
        var encrypted = aes.EncryptEcb(padded, PaddingMode.None);
        output.Write(encrypted);
        output.Write(new byte[16]); // main-key GUID
        output.Write((byte)1); // encrypted index
        output.Write(0x5A6F12E1u); output.Write(7);
        output.Write(indexOffset); output.Write((long)encrypted.Length); output.Write(SHA1.HashData(plainIndex));
    }
    private static void WriteEntry(BinaryWriter writer, byte[] payload)
    {
        writer.Write(0L); writer.Write((long)payload.Length); writer.Write((long)payload.Length);
        writer.Write(0); writer.Write(SHA1.HashData(payload)); writer.Write((byte)0); writer.Write(0u);
    }
    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length + 1); writer.Write(bytes); writer.Write((byte)0);
    }
}
