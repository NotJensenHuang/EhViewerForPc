using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EhViewerForPc.Services;

/// <summary>固定 JManga v1 的日语 Unigram 编码与简体中文 SentencePiece 解码。</summary>
internal sealed class JMangaTokenizer
{
    private const char SpaceMarker = '\u2581';
    private readonly Dictionary<string, Piece> _source = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _added = new(StringComparer.Ordinal);
    private readonly int[] _sourceBytes = new int[256];
    private readonly int[] _targetBytes = new int[256];
    private readonly int[] _targetIds;
    private readonly string[] _targetPieces;
    private readonly int _maxPieceLength;
    private readonly float _unknownScore;
    private readonly record struct Piece(int Id, float Score);

    internal JMangaTokenizer(string sourcePath, string targetVocabulary, string sentencePiece)
    {
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(sourcePath)))
        {
            JsonElement root = doc.RootElement;
            JsonElement model = root.GetProperty("model");
            JsonElement preTokenizer = root.GetProperty("pre_tokenizer");
            if (!model.GetProperty("type").ValueEquals("Unigram") ||
                !model.GetProperty("byte_fallback").GetBoolean() ||
                model.GetProperty("unk_id").GetInt32() != 0 ||
                root.GetProperty("normalizer").ValueKind != JsonValueKind.Null ||
                !preTokenizer.GetProperty("type").ValueEquals("Metaspace") ||
                !preTokenizer.GetProperty("replacement").ValueEquals("\u2581") ||
                !preTokenizer.GetProperty("prepend_scheme").ValueEquals("never") ||
                preTokenizer.GetProperty("split").GetBoolean())
                throw new InvalidDataException("JManga 编码器分词配置不符。");

            int id = 0;
            float minScore = float.PositiveInfinity;
            foreach (JsonElement row in model.GetProperty("vocab").EnumerateArray())
            {
                string text = row[0].GetString()!;
                float score = row[1].GetSingle();
                _source[text] = new Piece(id, score);
                _maxPieceLength = Math.Max(text.Length, _maxPieceLength);
                minScore = Math.Min(score, minScore);
                if (TryByte(text, out int value)) _sourceBytes[value] = id;
                id++;
            }
            _unknownScore = minScore - 10;
            foreach (JsonElement row in root.GetProperty("added_tokens").EnumerateArray())
            {
                if (row.GetProperty("single_word").GetBoolean() || row.GetProperty("lstrip").GetBoolean() ||
                    row.GetProperty("rstrip").GetBoolean() || row.GetProperty("normalized").GetBoolean())
                    throw new InvalidDataException("JManga 附加分词标记配置不符。");
                _added.Add(row.GetProperty("content").GetString()!, row.GetProperty("id").GetInt32());
            }
        }
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(targetVocabulary)))
        {
            JsonElement data = doc.RootElement;
            _targetIds = data.GetProperty("dat_ids").EnumerateArray().Select(static row => row.GetInt32()).ToArray();
            var compact = new Dictionary<int, int>();
            for (int i = 0; i < _targetIds.Length; i++)
                if (_targetIds[i] >= 0) compact.Add(_targetIds[i], i);
            int index = 0;
            foreach (JsonElement row in data.GetProperty("byte_piece_ids").EnumerateArray())
                _targetBytes[index++] = compact[row.GetInt32()];
            if (index != _targetBytes.Length || _targetIds.Length != 22935)
                throw new InvalidDataException("JManga 紧凑词表尺寸不符。");
        }
        _targetPieces = ReadSentencePieces(File.ReadAllBytes(sentencePiece));
        if (_targetIds.Any(id => id >= _targetPieces.Length))
            throw new InvalidDataException("JManga 目标词表索引无效。");
    }

    internal long[] Encode(string text, int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 2);
        if (text.Length > (long)maxTokens * _maxPieceLength) throw new MangaTranslationRegionLimitException();
        var ids = new List<long>(Math.Min(maxTokens, text.Length + 2)) { 1 };
        int start = 0;
        while (start < text.Length)
        {
            int next = text.Length, markerLength = 0, markerId = -1;
            foreach (var (marker, id) in _added)
            {
                int index = text.IndexOf(marker, start, StringComparison.Ordinal);
                if (index < 0 || index > next || (index == next && marker.Length <= markerLength)) continue;
                next = index;
                markerLength = marker.Length;
                markerId = id;
            }
            if (next > start)
                EncodeSegment(text[start..next].Replace(' ', SpaceMarker), ids, maxTokens);
            if (markerId >= 0) ids.Add(markerId);
            if (ids.Count >= maxTokens) throw new MangaTranslationRegionLimitException();
            start = next + markerLength;
        }
        ids.Add(2);
        return ids.ToArray();
    }

    private void EncodeSegment(string segment, List<long> ids, int maxTokens)
    {
        var lookup = _source.GetAlternateLookup<ReadOnlySpan<char>>();
        var scores = new float[segment.Length + 1];
        var starts = new int[scores.Length];
        var tokens = new int[scores.Length];
        scores.AsSpan(1).Fill(float.NegativeInfinity);
        for (int start = 0; start < segment.Length;)
        {
            int runeLength = char.IsSurrogatePair(segment, start) ? 2 : 1;
            bool hasSingle = false;
            int endLimit = Math.Min(segment.Length, start + _maxPieceLength);
            for (int end = start + runeLength; end <= endLimit; end++)
            {
                if (end < segment.Length && char.IsSurrogatePair(segment, end - 1)) continue;
                if (!lookup.TryGetValue(segment.AsSpan(start, end - start), out Piece piece)) continue;
                if (end == start + runeLength) hasSingle = true;
                Update(end, piece.Id, piece.Score);
            }
            if (!hasSingle) Update(start + runeLength, 0, _unknownScore);
            start += runeLength;

            void Update(int end, int id, float score)
            {
                float total = scores[start] + score;
                if (total <= scores[end]) return;
                scores[end] = total;
                starts[end] = start;
                tokens[end] = id;
            }
        }

        int reverseStart = ids.Count;
        Span<byte> utf8 = stackalloc byte[4];
        for (int end = segment.Length; end > 0; end = starts[end])
        {
            int start = starts[end];
            int id = tokens[end];
            if (id == 0)
            {
                int bytes = Encoding.UTF8.GetBytes(segment.AsSpan(start, end - start), utf8);
                for (int i = bytes - 1; i >= 0; i--) ids.Add(_sourceBytes[utf8[i]]);
            }
            else ids.Add(id);
            if (ids.Count >= maxTokens) throw new MangaTranslationRegionLimitException();
        }
        ids.Reverse(reverseStart, ids.Count - reverseStart);
    }

    internal int SelectToken(ReadOnlySpan<float> logits, int previous, int previous2)
    {
        // 作者的 compact vocab 已移除含假名的词片，此处补足 UTF-8 字节回退限制。
        int first = 0, last = -1;
        if (previous == _targetBytes[0xe3]) { first = 0x81; last = 0x83; }
        else if (previous2 == _targetBytes[0xe3] && previous == _targetBytes[0x87]) { first = 0xb0; last = 0xbf; }
        else if (previous2 == _targetBytes[0xef] && previous == _targetBytes[0xbd]) { first = 0xa6; last = 0xbf; }
        else if (previous2 == _targetBytes[0xef] && previous == _targetBytes[0xbe]) { first = 0x80; last = 0x9d; }
        int best = -1;
        float max = float.NegativeInfinity;
        for (int id = 0; id < logits.Length; id++)
        {
            if (!(logits[id] > max)) continue;
            if (last >= first && _targetBytes.AsSpan(first, last - first + 1).Contains(id)) continue;
            max = logits[id];
            best = id;
        }
        return best >= 0 ? best : throw new InvalidDataException("JManga 未返回有效的词片分数。");
    }

    internal string Decode(ReadOnlySpan<int> tokens)
    {
        var result = new StringBuilder(tokens.Length);
        Span<byte> bytes = stackalloc byte[tokens.Length];
        int byteCount = 0;
        foreach (int token in tokens)
        {
            int pieceId = _targetIds[token];
            if (pieceId < 4) continue;
            string piece = _targetPieces[pieceId];
            if (TryByte(piece, out int value))
            {
                bytes[byteCount++] = (byte)value;
                continue;
            }
            if (byteCount > 0)
            {
                AppendBytes(result, bytes[..byteCount]);
                byteCount = 0;
            }
            // 发布的 joint.model 使用 identity，不删除开头的空格标记。
            foreach (char character in piece)
                result.Append(character == SpaceMarker ? ' ' : character);
        }
        if (byteCount > 0) AppendBytes(result, bytes[..byteCount]);
        return result.ToString();
    }

    private static void AppendBytes(StringBuilder result, ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[2];
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out Rune rune, out int consumed) == OperationStatus.Done)
            {
                result.Append(chars[..rune.EncodeToUtf16(chars)]);
                bytes = bytes[consumed..];
            }
            else
            {
                // SentencePiece 对无效或未完成的 UTF-8 保留逐字节替换行为。
                result.Append('�');
                bytes = bytes[1..];
            }
        }
    }


    private static bool TryByte(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        return text.Length == 6 && text.StartsWith("<0x", StringComparison.Ordinal) && text[^1] == '>' &&
            int.TryParse(text[3..5], System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static string[] ReadSentencePieces(ReadOnlySpan<byte> file)
    {
        // SentencePiece ModelProto 的第 1 个字段是重复 SentencePiece 子消息，子消息第 1 字段是词片。
        var pieces = new List<string>();
        while (!file.IsEmpty)
        {
            uint tag = ReadVarint(ref file);
            ReadOnlySpan<byte> field = ReadField(ref file, tag);
            if (tag != 10) continue;
            string? piece = null;
            while (!field.IsEmpty)
            {
                uint inner = ReadVarint(ref field);
                ReadOnlySpan<byte> value = ReadField(ref field, inner);
                if (inner == 10) piece = Encoding.UTF8.GetString(value);
            }
            pieces.Add(piece ?? throw new InvalidDataException("JManga SentencePiece 词片缺失。"));
        }
        return pieces.ToArray();
    }

    private static uint ReadVarint(ref ReadOnlySpan<byte> data)
    {
        uint result = 0;
        for (int shift = 0; shift < 32 && !data.IsEmpty; shift += 7)
        {
            byte current = data[0];
            data = data[1..];
            result |= (uint)(current & 0x7f) << shift;
            if (current < 0x80) return result;
        }
        throw new InvalidDataException("JManga SentencePiece 字段格式无效。");
    }

    private static ReadOnlySpan<byte> ReadField(ref ReadOnlySpan<byte> data, uint tag)
    {
        int size = (tag & 7) switch
        {
            0 => 0,
            1 => 8,
            2 => checked((int)ReadVarint(ref data)),
            5 => 4,
            _ => throw new InvalidDataException("JManga SentencePiece 字段类型无效。"),
        };
        if ((tag & 7) == 0) _ = ReadVarint(ref data);
        if (size > data.Length) throw new InvalidDataException("JManga SentencePiece 字段越界。");
        ReadOnlySpan<byte> field = data[..size];
        data = data[size..];
        return field;
    }
}
