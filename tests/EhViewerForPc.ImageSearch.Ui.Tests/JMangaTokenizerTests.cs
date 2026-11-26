using System.Text;
using System.Text.Json;
using EhViewerForPc.Services;

namespace EhViewerForPc.ImageSearch.Ui.Tests;

public sealed class JMangaTokenizerTests
{
    [Fact]
    public void EncodesUnigramPathAndHandlesMetaspaceAddedTokensAndBytes()
    {
        WithTokenizer(tokenizer =>
        {
            Assert.Equal([1L, 7, 6, 3, 2], tokenizer.Encode("日本 日", 8));
            Assert.Equal([1L, 8, 3, 2], tokenizer.Encode("<mask>日", 8));
            Assert.Equal(new long[] { 1, 9 + 0xf0, 9 + 0x9f, 9 + 0x98, 9 + 0x80, 2 },
                tokenizer.Encode("😀", 8));
            Assert.Equal([1L, 7, 2], tokenizer.Encode("日本", 3));
            Assert.Throws<MangaTranslationRegionLimitException>(() => tokenizer.Encode("日本", 2));
        });
    }

    [Fact]
    public void DecodesSentencePieceAndBlocksKanaByteFallback()
    {
        WithTokenizer(tokenizer =>
        {
            Assert.Equal(" 中文 中文😀", tokenizer.Decode([260, 260, 4 + 0xf0, 4 + 0x9f, 4 + 0x98, 4 + 0x80]));
            Assert.Equal("��", tokenizer.Decode([4 + 0xe3, 4 + 0x81]));
            var logits = new float[22935];
            logits[4 + 0x82] = 5;
            logits[260] = 4;
            Assert.Equal(4 + 0x82, tokenizer.SelectToken(logits, -1, -1));
            Assert.Equal(260, tokenizer.SelectToken(logits, 4 + 0xe3, -1));
        });
    }

    private static void WithTokenizer(Action<JMangaTokenizer> verify)
    {
        // 每次测试使用独占的临时目录，不依赖用户安装的权重。
        DirectoryInfo directory = Directory.CreateTempSubdirectory("eh-jmanga-tokenizer-");
        string source = Path.Combine(directory.FullName, "tokenizer.json");
        string vocabulary = Path.Combine(directory.FullName, "vocab.json");
        string sentencePiece = Path.Combine(directory.FullName, "joint.model");
        try
        {
            var bytes = Enumerable.Range(0, 256).Select(static value => $"<0x{value:X2}>").ToArray();
            var sourcePieces = new[] { "<unk>", "<s>", "</s>", "日", "本", "日▁", "▁", "日本", "<mask>" }
                .Concat(bytes).ToArray();
            var weighted = sourcePieces.Select(static (piece, id) => new object[] { piece, id == 7 ? -1f : -2f });
            File.WriteAllText(source, JsonSerializer.Serialize(new
            {
                normalizer = (string?)null,
                pre_tokenizer = new { type = "Metaspace", replacement = "▁", prepend_scheme = "never", split = false },
                model = new { type = "Unigram", unk_id = 0, byte_fallback = true, vocab = weighted },
                added_tokens = new[] { new { id = 8, content = "<mask>", single_word = false, lstrip = false,
                    rstrip = false, normalized = false } },
            }));
            int[] ids = Enumerable.Repeat(-1, 22935).ToArray();
            for (int i = 0; i <= 260; i++) ids[i] = i;
            File.WriteAllText(vocabulary, JsonSerializer.Serialize(new { dat_ids = ids, byte_piece_ids = Enumerable.Range(4, 256) }));
            using (var file = File.Create(sentencePiece))
            {
                IEnumerable<string> pieces = new[] { "<unk>", "<s>", "</s>", "<pad>" }.Concat(bytes).Append("▁中文");
                foreach (string piece in pieces)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(piece);
                    file.WriteByte(10);
                    file.WriteByte((byte)(2 + utf8.Length));
                    file.WriteByte(10);
                    file.WriteByte((byte)utf8.Length);
                    file.Write(utf8);
                }
            }
            verify(new JMangaTokenizer(source, vocabulary, sentencePiece));
        }
        finally
        {
            File.Delete(sentencePiece);
            File.Delete(vocabulary);
            File.Delete(source);
            directory.Delete();
        }
    }
}
