using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace Content.Migration;

/// <summary>One row as the source returned it: its key and the values of the mapped columns (null for SQL NULL).</summary>
public sealed record SourceRow(object Key, IReadOnlyDictionary<string, object?> Values);

/// <summary>The outcome of converting one row: a document, or the reasons it is invalid.</summary>
/// <param name="KeyText">The key as text, used to name the dead letter.</param>
/// <param name="PayloadHash">SHA-256 (hex) of the source values, to recognise a changed row on replay.</param>
public sealed record MappedRow(string KeyText, BsonDocument? Document, IReadOnlyList<string> Problems, string PayloadHash)
{
    public bool IsValid => Document is not null;
}

/// <summary>
/// Turns source rows into documents according to the column mapping. Conversions are tolerant between text and numbers
/// (a rating of "4.5", 4 or 4.5 all become 4.5) but never guess: a value that cannot be read as the mapped type, or a
/// missing required value, makes the whole row invalid so it can be dead-lettered and looked at, instead of leaving a
/// silently incomplete document behind.
/// </summary>
public static class RowMapper
{
    public static MappedRow Map(MigrationDefinition definition, SourceRow row)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(row);

        var keyText = Convert.ToString(row.Key, CultureInfo.InvariantCulture) ?? string.Empty;
        var hash = Hash(definition, row);
        var document = new BsonDocument();
        var problems = new List<string>();

        foreach (var mapping in definition.Columns)
        {
            row.Values.TryGetValue(mapping.Column, out var raw);
            if (IsMissing(raw))
            {
                if (mapping.Required)
                {
                    problems.Add($"column '{mapping.Column}' is required but empty");
                }

                continue;
            }

            if (TryConvert(raw!, mapping.Type, out var converted))
            {
                document[mapping.Field] = converted;
            }
            else
            {
                problems.Add($"column '{mapping.Column}' value {Describe(raw!)} cannot be read as {mapping.Type}");
            }
        }

        if (!document.Contains("_id") && problems.Count == 0)
        {
            problems.Add("the key column produced no _id");
        }

        return problems.Count == 0
            ? new MappedRow(keyText, document, [], hash)
            : new MappedRow(keyText, null, problems, hash);
    }

    private static bool IsMissing(object? value) => value switch
    {
        null or DBNull => true,
        string text => string.IsNullOrWhiteSpace(text),
        _ => false,
    };

    private static bool TryConvert(object value, FieldType type, out BsonValue result)
    {
        result = BsonNull.Value;
        switch (type)
        {
            case FieldType.String:
            case FieldType.StringOrNumber:
                if (value is byte[] or bool)
                {
                    return false;
                }

                result = value is string text ? text : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;

            case FieldType.Integer:
                return TryInteger(value, out var whole) && Set(out result, new BsonInt64(whole));

            case FieldType.Double:
                return TryDouble(value, out var number) && Set(out result, new BsonDouble(number));

            case FieldType.Boolean:
                return TryBoolean(value, out var flag) && Set(out result, flag);

            case FieldType.DateTime:
                return TryDateTime(value, out var moment) && Set(out result, new BsonDateTime(moment));

            default:
                return false;
        }
    }

    private static bool Set(out BsonValue target, BsonValue value)
    {
        target = value;
        return true;
    }

    private static bool TryInteger(object value, out long result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case int or short or byte or sbyte or ushort:
                result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            case uint or ulong:
                return TryUnsigned(value, out result);
            case double or float or decimal:
                var asDecimal = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                result = (long)asDecimal;
                return asDecimal == result;
            case string text:
                return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                    || TryIntegralDecimal(text, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryUnsigned(object value, out long result)
    {
        var unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        result = unchecked((long)unsigned);
        return unsigned <= long.MaxValue;
    }

    private static bool TryIntegralDecimal(string text, out long result)
    {
        result = 0;
        if (!decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed != decimal.Truncate(parsed))
        {
            return false;
        }

        if (parsed is < long.MinValue or > long.MaxValue)
        {
            return false;
        }

        result = (long)parsed;
        return true;
    }

    private static bool TryDouble(object value, out double result)
    {
        switch (value)
        {
            case double or float or decimal or long or int or short or byte or sbyte or ushort or uint or ulong:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(result);
            case string text:
                return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryBoolean(object value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case long or int or short or byte:
                var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                result = number == 1;
                return number is 0 or 1;
            case string text:
                switch (text.Trim().ToLowerInvariant())
                {
                    case "true" or "1":
                        result = true;
                        return true;
                    case "false" or "0":
                        result = false;
                        return true;
                }

                result = false;
                return false;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryDateTime(object value, out DateTime result)
    {
        switch (value)
        {
            case DateTime moment:
                result = moment.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(moment, DateTimeKind.Utc) : moment.ToUniversalTime();
                return true;
            case DateTimeOffset offset:
                result = offset.UtcDateTime;
                return true;
            case string text when DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed):
                result = parsed.UtcDateTime;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static string Describe(object value) => value switch
    {
        string text => $"'{(text.Length > 40 ? text[..40] + "..." : text)}'",
        byte[] bytes => $"<{bytes.Length} bytes>",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
    };

    private static string Hash(MigrationDefinition definition, SourceRow row)
    {
        var text = new StringBuilder();
        foreach (var mapping in definition.Columns)
        {
            row.Values.TryGetValue(mapping.Column, out var raw);
            text.Append(mapping.Column).Append('=').Append(raw switch
            {
                null or DBNull => "\u0000",
                byte[] bytes => Convert.ToHexStringLower(bytes),
                _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
            }).Append('\u001f');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
