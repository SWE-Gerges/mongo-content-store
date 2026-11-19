using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Content.Store;

/// <summary>Opaque keyset cursor: the last returned id, wrapped so clients do not depend on its shape.</summary>
public static class HotelCursor
{
    private const string Prefix = "h1.";

    public static string Encode(long lastId) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Prefix + lastId.ToString(CultureInfo.InvariantCulture)));

    public static long Decode(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            if (text.StartsWith(Prefix, StringComparison.Ordinal)
                && long.TryParse(text.AsSpan(Prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }
        catch (FormatException)
        {
            // Falls through to the single failure below.
        }

        throw new InvalidCursorException();
    }
}
