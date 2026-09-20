using System.IO;
using System.Text;

// Minimal stored ZipCrypto fixture. Never used by the application to write archives.
internal static class EncryptedZipFixture
{
    public static void Write(string path, string password, byte[] data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data) crc = UpdateCrc(crc, value);
        crc = ~crc;
        uint key0 = 0x12345678, key1 = 0x23456789, key2 = 0x34567890;
        void Update(byte value)
        {
            key0 = UpdateCrc(key0, value);
            key1 = unchecked((key1 + (byte)key0) * 134775813 + 1);
            key2 = UpdateCrc(key2, (byte)(key1 >> 24));
        }
        foreach (var value in Encoding.UTF8.GetBytes(password)) Update(value);
        var plain = new byte[data.Length + 12];
        plain[11] = (byte)(crc >> 24);
        data.CopyTo(plain, 12);
        var encrypted = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            var temp = key2 | 2;
            encrypted[i] = (byte)(plain[i] ^ (byte)(unchecked(temp * (temp ^ 1)) >> 8));
            Update(plain[i]);
        }
        var name = Encoding.ASCII.GetBytes("1.png");
        using var writer = new BinaryWriter(File.Create(path));
        void U16(int value) => writer.Write((ushort)value);
        writer.Write(0x04034b50u); U16(20); U16(1); U16(0); U16(0); U16(0);
        writer.Write(crc); writer.Write(encrypted.Length); writer.Write(data.Length); U16(name.Length); U16(0);
        writer.Write(name); writer.Write(encrypted);
        var directoryStart = (uint)writer.BaseStream.Position;
        writer.Write(0x02014b50u); U16(20); U16(20); U16(1); U16(0); U16(0); U16(0);
        writer.Write(crc); writer.Write(encrypted.Length); writer.Write(data.Length);
        U16(name.Length); U16(0); U16(0); U16(0); U16(0); writer.Write(0u); writer.Write(0u); writer.Write(name);
        var directorySize = (uint)writer.BaseStream.Position - directoryStart;
        writer.Write(0x06054b50u); U16(0); U16(0); U16(1); U16(1); writer.Write(directorySize); writer.Write(directoryStart); U16(0);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }
}
