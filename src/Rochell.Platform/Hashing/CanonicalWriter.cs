using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rochell.Platform.Hashing;

/// <summary>
/// Canonical serialization v1 (Architecture v2.1 §8.2, Correction 7). Each field is written as
/// 4-byte big-endian length + UTF-8 bytes; NULL is the length marker 0xFFFFFFFF with no bytes.
/// Formats: uuid lower-case "D"; integers invariant decimal; timestamptz "yyyy-MM-ddTHH:mm:ss.ffffffZ" (UTC);
/// date "yyyy-MM-dd"; numeric fixed scale; boolean "t"/"f"; json RFC 8785 subset (<see cref="JsonCanonicalizer"/>).
/// </summary>
public sealed class CanonicalWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly MemoryStream _buffer = new();

    public CanonicalWriter Text(string? value) => value is null ? Null() : Bytes(StrictUtf8.GetBytes(value));

    public CanonicalWriter Uuid(Guid value) => Text(value.ToString("D", CultureInfo.InvariantCulture));

    public CanonicalWriter Uuid(Guid? value) => value is null ? Null() : Uuid(value.Value);

    public CanonicalWriter Int16(short value) => Text(value.ToString(CultureInfo.InvariantCulture));

    public CanonicalWriter Int32(int value) => Text(value.ToString(CultureInfo.InvariantCulture));

    public CanonicalWriter Int64(long value) => Text(value.ToString(CultureInfo.InvariantCulture));

    public CanonicalWriter Boolean(bool value) => Text(value ? "t" : "f");

    public CanonicalWriter Timestamp(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Canonical timestamps must be UTC.", nameof(utc));
        }

        if (utc.Ticks % 10 != 0)
        {
            throw new ArgumentException("Canonical timestamps must have microsecond precision (use Precision.ToMicroseconds).", nameof(utc));
        }

        return Text(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    }

    public CanonicalWriter Date(DateOnly value) => Text(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>numeric(p, scale): fixed number of decimals, no thousands separator, "-" for negatives.</summary>
    public CanonicalWriter Numeric(decimal value, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        var rounded = decimal.Round(value, scale, MidpointRounding.ToEven);
        if (rounded != value)
        {
            throw new ArgumentException($"Value {value} has more than {scale} decimals.", nameof(value));
        }

        return Text(value.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    public CanonicalWriter Json(string json) => Text(JsonCanonicalizer.Canonicalize(json));

    public CanonicalWriter Null()
    {
        Span<byte> marker = [0xFF, 0xFF, 0xFF, 0xFF];
        _buffer.Write(marker);
        return this;
    }

    public byte[] ToArray() => _buffer.ToArray();

    public byte[] Sha256() => SHA256.HashData(_buffer.ToArray());

    private CanonicalWriter Bytes(byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        _buffer.Write(length);
        _buffer.Write(bytes);
        return this;
    }
}
