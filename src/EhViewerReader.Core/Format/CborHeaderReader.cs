using System.Text;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Format;

/// <summary>
/// Minimal, bounded CBOR reader for a top-level map containing gid/token/pages.
/// It intentionally does not materialize pTokenMap or other large nested values.
/// Limits mirror EhViewer's restore parser: 32 MB, depth 16, key 64 chars,
/// token 128 chars. The parser returns as soon as it has read the fields it needs.
/// </summary>
public static class CborHeaderReader
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxDepth = 16;
    private const int MaxKeyChars = 64;
    private const int MaxTokenChars = 128;

    public static SpiderHeader? TryRead(ReadOnlySpan<byte> bytes, out string? error)
    {
        error = null;
        try
        {
            if (bytes.Length > MaxBytes)
            {
                error = "CBOR header exceeds 32 MB";
                return null;
            }

            var cursor = new Cursor(bytes.ToArray());
            int head = cursor.ReadByte();
            int major = head >> 5;
            int info = head & 0x1F;
            if (major != 5)
            {
                error = "CBOR header is not a map";
                return null;
            }

            var header = new SpiderHeader { Version = 2, IsCbor = true };
            bool indefinite = info == 31;
            long count = indefinite ? -1 : cursor.ReadArgument(info);
            if (!indefinite && count > bytes.Length)
            {
                error = "CBOR map count is invalid";
                return null;
            }

            long index = 0;
            while (indefinite || index < count)
            {
                if (indefinite)
                {
                    if (!cursor.TryPeekBreak(out bool isBreak)) break;
                    if (isBreak)
                    {
                        cursor.ConsumeBreak();
                        break;
                    }
                }

                string? key = cursor.ReadTextOrSkip(MaxKeyChars);
                if (key is null)
                {
                    cursor.SkipValue(0);
                    index++;
                    continue;
                }

                switch (key)
                {
                    case "gid":
                    {
                        long? value = cursor.ReadIntegerOrSkip();
                        if (value is >= 0)
                        {
                            header.Gid = value.Value;
                            header.HasGid = true;
                        }

                        break;
                    }
                    case "token":
                    {
                        string? value = cursor.ReadTextOrSkip(MaxTokenChars);
                        if (!string.IsNullOrEmpty(value))
                        {
                            header.Token = value;
                        }

                        break;
                    }
                    case "pages":
                    {
                        long? value = cursor.ReadIntegerOrSkip();
                        if (value is > 0 and <= SpiderInfoParser.MaxPages)
                        {
                            header.Pages = (int)value.Value;
                            header.HasPages = true;
                        }

                        break;
                    }
                    case "startPage":
                    {
                        long? value = cursor.ReadIntegerOrSkip();
                        if (value is >= 0)
                        {
                            header.StartPage = (int)Math.Min(value.Value, int.MaxValue);
                        }

                        break;
                    }
                    default:
                        cursor.SkipValue(0);
                        break;
                }

                index++;
                if (header.HasGid && !string.IsNullOrEmpty(header.Token) && header.HasPages)
                {
                    break;
                }
            }

            header.IsValid = header.HasGid || header.HasPages;
            if (!header.HasGid)
            {
                error = "CBOR header has no gid";
            }
            else if (!header.HasPages)
            {
                error = "CBOR header has no pages (this is normal for cache-style headers)";
            }

            header.Error = error;
            return header.HasAnyUsefulData ? header : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private sealed class Cursor(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _position;

        public int ReadByte()
        {
            if (_position >= _data.Length) throw new InvalidDataException("unexpected end of CBOR");
            return _data[_position++];
        }

        public bool TryPeekBreak(out bool result)
        {
            if (_position >= _data.Length)
            {
                result = false;
                return false;
            }

            result = _data[_position] == 0xFF;
            return true;
        }

        public void ConsumeBreak()
        {
            if (ReadByte() != 0xFF) throw new InvalidDataException("expected CBOR break");
        }

        public long ReadArgument(int info)
        {
            if (info < 24) return info;
            int width = info switch
            {
                24 => 1,
                25 => 2,
                26 => 4,
                27 => 8,
                _ => throw new InvalidDataException("invalid CBOR argument"),
            };

            if (_position + width > _data.Length) throw new InvalidDataException("truncated CBOR argument");
            ulong value = 0;
            for (int i = 0; i < width; i++)
            {
                value = (value << 8) | _data[_position++];
            }

            if (width == 8 && value > long.MaxValue)
            {
                throw new OverflowException("CBOR integer overflow");
            }

            return (long)value;
        }

        public string? ReadTextOrSkip(int maxChars)
        {
            int head = ReadByte();
            int major = head >> 5;
            int info = head & 0x1F;
            if (major != 3)
            {
                SkipWithHead(head, 0);
                return null;
            }

            if (info == 31)
            {
                var builder = new StringBuilder();
                while (true)
                {
                    int chunkHead = ReadByte();
                    if (chunkHead == 0xFF) break;
                    if ((chunkHead >> 5) != 3) throw new InvalidDataException("invalid CBOR indefinite text chunk");
                    int chunkInfo = chunkHead & 0x1F;
                    if (chunkInfo == 31) throw new InvalidDataException("nested indefinite CBOR text");
                    long chunkLength = ReadArgument(chunkInfo);
                    if (chunkLength < 0 || chunkLength > _data.Length - _position)
                    {
                        throw new InvalidDataException("invalid CBOR text length");
                    }

                    if (builder.Length + chunkLength <= maxChars)
                    {
                        builder.Append(Encoding.UTF8.GetString(_data, _position, (int)chunkLength));
                    }

                    _position += (int)chunkLength;
                }

                string value = builder.ToString();
                return value.Length <= maxChars ? value : null;
            }

            long length = ReadArgument(info);
            if (length < 0 || length > _data.Length - _position)
            {
                throw new InvalidDataException("invalid CBOR text length");
            }

            if (length > maxChars)
            {
                _position += (int)length;
                return null;
            }

            string text = Encoding.UTF8.GetString(_data, _position, (int)length);
            _position += (int)length;
            return text;
        }

        public long? ReadIntegerOrSkip()
        {
            int head = ReadByte();
            int major = head >> 5;
            int info = head & 0x1F;
            if (major is not (0 or 1))
            {
                SkipWithHead(head, 0);
                return null;
            }

            if (info == 31) throw new InvalidDataException("invalid CBOR integer");
            long value = ReadArgument(info);
            return major == 0 ? value : -1 - value;
        }

        public void SkipValue(int depth)
        {
            SkipWithHead(ReadByte(), depth);
        }

        private void SkipWithHead(int head, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("CBOR nesting too deep");
            int major = head >> 5;
            int info = head & 0x1F;
            switch (major)
            {
                case 0:
                case 1:
                    if (info == 31) throw new InvalidDataException("invalid CBOR integer");
                    _ = ReadArgument(info);
                    break;
                case 2:
                case 3:
                    SkipString(info);
                    break;
                case 4:
                    SkipContainer(info, depth, isMap: false);
                    break;
                case 5:
                    SkipContainer(info, depth, isMap: true);
                    break;
                case 6:
                    if (info == 31) throw new InvalidDataException("invalid CBOR tag");
                    _ = ReadArgument(info);
                    SkipValue(depth + 1);
                    break;
                case 7:
                    SkipSimple(info);
                    break;
                default:
                    throw new InvalidDataException("invalid CBOR major type");
            }
        }

        private void SkipString(int info)
        {
            if (info == 31)
            {
                while (true)
                {
                    int chunkHead = ReadByte();
                    if (chunkHead == 0xFF) return;
                    int chunkMajor = chunkHead >> 5;
                    if (chunkMajor is not (2 or 3)) throw new InvalidDataException("invalid CBOR string chunk");
                    int chunkInfo = chunkHead & 0x1F;
                    if (chunkInfo == 31) throw new InvalidDataException("nested indefinite CBOR string");
                    long length = ReadArgument(chunkInfo);
                    if (length < 0 || length > _data.Length - _position) throw new InvalidDataException("invalid CBOR string length");
                    _position += (int)length;
                }
            }

            long len = ReadArgument(info);
            if (len < 0 || len > _data.Length - _position) throw new InvalidDataException("invalid CBOR string length");
            _position += (int)len;
        }

        private void SkipContainer(int info, int depth, bool isMap)
        {
            if (depth + 1 > MaxDepth) throw new InvalidDataException("CBOR nesting too deep");
            bool indefinite = info == 31;
            long count = indefinite ? -1 : ReadArgument(info);
            if (!indefinite && count > _data.Length) throw new InvalidDataException("invalid CBOR container length");

            long index = 0;
            while (indefinite || index < count)
            {
                if (indefinite)
                {
                    if (_position >= _data.Length) throw new InvalidDataException("unterminated CBOR container");
                    if (_data[_position] == 0xFF)
                    {
                        _position++;
                        return;
                    }
                }

                SkipValue(depth + 1);
                if (isMap) SkipValue(depth + 1);
                index++;
            }
        }

        private void SkipSimple(int info)
        {
            switch (info)
            {
                case 20:
                case 21:
                case 22:
                case 23:
                    return;
                case 24:
                    _ = ReadByte();
                    return;
                case 25:
                case 26:
                case 27:
                    _ = ReadArgument(info);
                    return;
                case 31:
                    return;
                default:
                    return;
            }
        }
    }
}
