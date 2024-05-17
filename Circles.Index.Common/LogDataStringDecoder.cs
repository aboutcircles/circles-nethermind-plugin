using System.Text;

namespace Circles.Index.Common;

public static class LogDataStringDecoder
{
    public static string[] ReadStrings(byte[] bytes)
    {
        var strings = new List<string>();
        var offset = 0;
        do
        {
            offset = ReadString(bytes, strings, offset, out var length, out var str);

            // Align the length of 'name' to 32 bytes
            length = (length + 31) / 32 * 32;
            // Take the last offset and the length and determine the next string offset aligned to 32 bytes
            offset = offset + 32 + length;
        } while (offset < bytes.Length);

        return strings.ToArray();
    }

    private static int ReadString(byte[] bytes, List<string> strings, int offset, out int length, out string name)
    {
        // The first 32 bytes contain the offset of the first string
        var val1 = BitConverter.ToInt32(bytes.Skip(offset).Take(32).Reverse().ToArray(), 0);
        // At the beginning of the string data, the length of the string is stored
        if (offset > 0)
        {
            length = val1;
        }
        else
        {
            offset = val1;
            length = BitConverter.ToInt32(bytes.Skip(offset).Take(32).Reverse().ToArray(), 0);
        }

        // The string data is stored after the length
        byte[] stringData = bytes.Skip(offset + 32).Take(length).ToArray();
        name = Encoding.UTF8.GetString(stringData).Split('\0')[0];
        strings.Add(name);

        return offset;
    }
}