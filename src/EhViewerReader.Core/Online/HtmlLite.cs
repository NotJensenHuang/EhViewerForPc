using System.Net;
using System.Text;

namespace EhViewerReader.Core.Online;

/// <summary>
/// Minimal, allocation-conscious DOM built for scraping E-Hentai pages.
///
/// The site emits well-formed HTML (tables, divs and anchors), so a tolerant
/// tag tokenizer is enough and keeps the app free of a heavyweight parser
/// dependency (and of the AOT/trimming risk that comes with it).
/// Malformed nesting is recovered by walking up to the matching open element.
/// </summary>
public sealed class HtmlNode
{
    private const string TextNodeName = "#text";

    internal HtmlNode(string name)
    {
        Name = name;
    }

    public static HtmlNode CreateDocument() => new("#document");

    public static HtmlNode CreateText(string text) => new(TextNodeName) { Text = text };

    public string Name { get; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<HtmlNode> Children { get; } = [];
    public HtmlNode? Parent { get; internal set; }
    public string? Text { get; private set; }

    public bool IsText => Name == TextNodeName;
    public bool IsElement => !IsText;

    public string GetAttribute(string name) =>
        Attributes.TryGetValue(name, out string? value) ? value : string.Empty;

    public string? TryGetAttribute(string name) =>
        Attributes.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;

    public bool HasAttribute(string name) => Attributes.ContainsKey(name);

    public bool HasClass(string className)
    {
        string classes = GetAttribute("class");
        if (classes.Length == 0) return false;
        foreach (string part in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, className, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public string Id => GetAttribute("id");

    /// <summary>All descendant elements in document order.</summary>
    public IEnumerable<HtmlNode> Descendants()
    {
        foreach (HtmlNode child in Children)
        {
            if (child.IsText) continue;
            yield return child;
            foreach (HtmlNode nested in child.Descendants()) yield return nested;
        }
    }

    public IEnumerable<HtmlNode> Descendants(string tagName)
    {
        foreach (HtmlNode node in Descendants())
        {
            if (string.Equals(node.Name, tagName, StringComparison.OrdinalIgnoreCase)) yield return node;
        }
    }

    public IEnumerable<HtmlNode> DescendantsByClass(string className)
    {
        foreach (HtmlNode node in Descendants())
        {
            if (node.HasClass(className)) yield return node;
        }
    }

    public HtmlNode? FirstDescendant(string tagName) =>
        Descendants().FirstOrDefault(n => string.Equals(n.Name, tagName, StringComparison.OrdinalIgnoreCase));

    public HtmlNode? FirstDescendantByClass(string className)
    {
        foreach (HtmlNode node in Descendants())
        {
            if (node.HasClass(className)) return node;
        }

        return null;
    }

    public HtmlNode? FirstDescendantById(string id)
    {
        foreach (HtmlNode node in Descendants())
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal)) return node;
        }

        return null;
    }

    /// <summary>Nearest ancestor (or self) matching the predicate.</summary>
    public HtmlNode? AncestorOrSelf(Func<HtmlNode, bool> predicate)
    {
        HtmlNode? node = this;
        while (node is not null)
        {
            if (node.IsElement && predicate(node)) return node;
            node = node.Parent;
        }

        return null;
    }

    /// <summary>Whitespace-collapsed text of this element and its descendants.</summary>
    public string InnerText
    {
        get
        {
            var builder = new StringBuilder();
            AppendText(builder, this);
            return CollapseWhitespace(builder.ToString());
        }
    }

    /// <summary>Raw text without whitespace collapsing (attribute-like content).</summary>
    public string RawText
    {
        get
        {
            var builder = new StringBuilder();
            AppendText(builder, this);
            return builder.ToString();
        }
    }

    private static void AppendText(StringBuilder builder, HtmlNode node)
    {
        if (node.IsText)
        {
            if (node.Text is { Length: > 0 }) builder.Append(node.Text);
            return;
        }

        if (string.Equals(node.Name, "script", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Name, "style", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Block-level boundaries read as spaces so "Posted:" and the value do
        // not fuse into one token when the markup nests them in divs.
        bool block = IsBlockLevel(node.Name);
        if (block && builder.Length > 0 && builder[^1] is not ' ') builder.Append(' ');
        foreach (HtmlNode child in node.Children) AppendText(builder, child);
        if (block && builder.Length > 0 && builder[^1] is not ' ') builder.Append(' ');
    }

    private static bool IsBlockLevel(string name) => name.ToLowerInvariant() is
        "div" or "p" or "tr" or "td" or "th" or "table" or "li" or "ul" or "ol" or "br" or "h1" or "h2" or "h3" or "h4" or "section";

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool space = false;
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    public override string ToString() => IsText ? Text ?? string.Empty : $"<{Name}>";
}

public static class HtmlLite
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link",
        "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    public static HtmlNode Parse(string? html)
    {
        HtmlNode document = HtmlNode.CreateDocument();
        if (string.IsNullOrEmpty(html)) return document;

        HtmlNode current = document;
        int index = 0;
        int length = html.Length;

        while (index < length)
        {
            int next = html.IndexOf('<', index);
            if (next < 0)
            {
                AppendText(current, html[index..]);
                break;
            }

            if (next > index)
            {
                AppendText(current, html[index..next]);
            }

            index = next;

            if (Matches(html, index, "<!--"))
            {
                int end = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = end < 0 ? length : end + 3;
                continue;
            }

            if (Matches(html, index, "<!"))
            {
                int end = html.IndexOf('>', index + 2);
                index = end < 0 ? length : end + 1;
                continue;
            }

            if (Matches(html, index, "</"))
            {
                int end = html.IndexOf('>', index + 2);
                if (end < 0)
                {
                    index = length;
                    break;
                }

                string name = html[(index + 2)..end].Trim();
                CloseTag(document, ref current, name);
                index = end + 1;
                continue;
            }

            // Open tag: name, attributes, optional self-closing slash.
            int cursor = index + 1;
            int nameStart = cursor;
            while (cursor < length && (char.IsLetterOrDigit(html[cursor]) || html[cursor] is '-' or ':' or '_'))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                // A stray '<' in text.
                AppendText(current, "<");
                index++;
                continue;
            }

            string tagName = html[nameStart..cursor];
            var element = new HtmlNode(tagName);
            bool selfClosing = false;
            cursor = ParseAttributes(html, cursor, element, out selfClosing);
            index = cursor;

            element.Parent = current;
            current.Children.Add(element);

            if (selfClosing || VoidElements.Contains(tagName))
            {
                continue;
            }

            if (RawTextElements.Contains(tagName))
            {
                string closeTag = "</" + tagName;
                int close = html.IndexOf(closeTag, index, StringComparison.OrdinalIgnoreCase);
                string raw = close < 0 ? html[index..] : html[index..close];
                if (raw.Length > 0) element.Children.Add(HtmlNode.CreateText(raw));
                if (close < 0)
                {
                    index = length;
                }
                else
                {
                    int end = html.IndexOf('>', close);
                    index = end < 0 ? length : end + 1;
                }

                continue;
            }

            current = element;
        }

        return document;
    }

    private static void AppendText(HtmlNode current, string raw)
    {
        if (raw.Length == 0) return;
        string decoded = WebUtility.HtmlDecode(raw);
        if (decoded.Length == 0) return;
        current.Children.Add(HtmlNode.CreateText(decoded));
    }

    private static void CloseTag(HtmlNode document, ref HtmlNode current, string name)
    {
        if (name.Length == 0) return;
        int space = name.IndexOfAny([' ', '\t', '\r', '\n']);
        if (space > 0) name = name[..space];

        HtmlNode? node = current;
        while (node is not null && node != document)
        {
            if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                current = node.Parent ?? document;
                return;
            }

            node = node.Parent;
        }
    }

    /// <summary>Reads attributes starting at <paramref name="cursor"/>; returns the index after '>'.</summary>
    private static int ParseAttributes(string html, int cursor, HtmlNode element, out bool selfClosing)
    {
        selfClosing = false;
        int length = html.Length;
        while (cursor < length)
        {
            while (cursor < length && char.IsWhiteSpace(html[cursor])) cursor++;
            if (cursor >= length) return cursor;

            char ch = html[cursor];
            if (ch == '>')
            {
                return cursor + 1;
            }

            if (ch == '/')
            {
                selfClosing = true;
                cursor++;
                continue;
            }

            int nameStart = cursor;
            while (cursor < length && !char.IsWhiteSpace(html[cursor]) && html[cursor] is not ('=' or '>' or '/'))
            {
                cursor++;
            }

            string name = html[nameStart..cursor];
            if (name.Length == 0)
            {
                cursor++;
                continue;
            }

            while (cursor < length && char.IsWhiteSpace(html[cursor])) cursor++;
            if (cursor >= length || html[cursor] != '=')
            {
                element.Attributes[name] = string.Empty;
                continue;
            }

            cursor++; // '='
            while (cursor < length && char.IsWhiteSpace(html[cursor])) cursor++;
            if (cursor >= length) break;

            string value;
            if (html[cursor] is '"' or '\'')
            {
                char quote = html[cursor];
                int start = ++cursor;
                int end = html.IndexOf(quote, start);
                if (end < 0)
                {
                    value = html[start..];
                    cursor = length;
                }
                else
                {
                    value = html[start..end];
                    cursor = end + 1;
                }
            }
            else
            {
                int start = cursor;
                while (cursor < length && !char.IsWhiteSpace(html[cursor]) && html[cursor] != '>') cursor++;
                value = html[start..cursor];
            }

            element.Attributes[name] = WebUtility.HtmlDecode(value);
        }

        return cursor;
    }

    private static bool Matches(string text, int index, string token) =>
        index + token.Length <= text.Length &&
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
