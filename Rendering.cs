using System.Text;
using KismetAnalyzer.Dot;
using static KismetAnalyzer.Dot.Html;

namespace KismetAnalyzer;

public enum ElementKind
{
    ExpressionType,    // EX_Jump, EX_LocalVariable, etc.
    Address,           // Numeric jump targets, bytecode addresses
    Variable,          // Local, instance, default variables
    Function,          // Function calls, virtual functions
    StringLiteral,     // Text constants
    NumericLiteral,    // Integers, floats, vectors
    TypeName,          // Class names, struct names, property types
    Keyword,           // case, default, operators
    PlainText          // Punctuation, separators, spaces
}

public readonly record struct Element(ElementKind Kind, string Value);

public interface ILinesRenderer
{
    string Render(IEnumerable<Element> elements);
}

public class PlainTextRenderer : ILinesRenderer
{
    public static readonly PlainTextRenderer Instance = new();

    public string Render(IEnumerable<Element> elements)
    {
        return string.Concat(elements.Select(e => e.Value));
    }

    public static void RenderTo(TextWriter writer, SummaryGenerator.Lines lines)
    {
        RenderTo(writer, new[] { lines });
    }

    public static void RenderTo(TextWriter writer, IEnumerable<SummaryGenerator.Lines> linesCollection)
    {
        foreach (var lines in linesCollection)
        {
            foreach (var (address, nest, elements) in lines.GetElements())
            {
                var sb = new StringBuilder();
                for (int i = 0; i < elements.Count; i++)
                {
                    var e = elements[i];

                    // Add indentation after prefix address (or at start if no prefix)
                    if (i == 1 || (i == 0 && e.Kind != ElementKind.Address))
                    {
                        sb.Append("".PadRight(nest * 4));
                    }

                    sb.Append(e.Value);
                }
                writer.WriteLine(sb.ToString());
            }
        }
    }
}

public class AnsiColorRenderer : ILinesRenderer
{
    public static readonly AnsiColorRenderer Instance = new();

    private static readonly Dictionary<ElementKind, string> Colors = new()
    {
        [ElementKind.ExpressionType] = "\x1b[36m",   // Cyan
        [ElementKind.Address] = "\x1b[33m",          // Yellow
        [ElementKind.Variable] = "\x1b[32m",         // Green
        [ElementKind.Function] = "\x1b[35m",         // Magenta
        [ElementKind.StringLiteral] = "\x1b[31m",    // Red
        [ElementKind.NumericLiteral] = "\x1b[34m",   // Blue
        [ElementKind.TypeName] = "\x1b[93m",         // Bright Yellow
        [ElementKind.Keyword] = "\x1b[95m",          // Bright Magenta
        [ElementKind.PlainText] = "",                // No color
    };
    private const string Reset = "\x1b[0m";
    private const string NestedAddressColor = "\x1b[90m";  // Dim gray for nested addresses

    public static string GetColor(ElementKind kind) => Colors.GetValueOrDefault(kind, "");

    public string Render(IEnumerable<Element> elements)
    {
        var sb = new StringBuilder();
        foreach (var e in elements)
        {
            var color = Colors.GetValueOrDefault(e.Kind, "");
            if (!string.IsNullOrEmpty(color))
            {
                sb.Append(color);
                sb.Append(e.Value);
                sb.Append(Reset);
            }
            else
            {
                sb.Append(e.Value);
            }
        }
        return sb.ToString();
    }

    public static void RenderTo(TextWriter writer, SummaryGenerator.Lines lines)
    {
        RenderTo(writer, new[] { lines });
    }

    public static void RenderTo(TextWriter writer, IEnumerable<SummaryGenerator.Lines> linesCollection)
    {
        foreach (var lines in linesCollection)
        {
            foreach (var (address, nest, elements) in lines.GetElements())
            {
                var sb = new StringBuilder();
                for (int i = 0; i < elements.Count; i++)
                {
                    var e = elements[i];
                    bool isPrefix = (i == 0 && e.Kind == ElementKind.Address);

                    // Add indentation after prefix address (or at start if no prefix)
                    if (i == 1 || (i == 0 && e.Kind != ElementKind.Address))
                    {
                        sb.Append("".PadRight(nest * 4));
                    }

                    // Prefix address at nest > 0 gets dimmer color
                    var color = (isPrefix && nest > 0)
                        ? NestedAddressColor
                        : Colors.GetValueOrDefault(e.Kind, "");
                    if (!string.IsNullOrEmpty(color))
                    {
                        sb.Append(color);
                        sb.Append(e.Value);
                        sb.Append(Reset);
                    }
                    else
                    {
                        sb.Append(e.Value);
                    }
                }
                writer.WriteLine(sb.ToString());
            }
        }
    }
}

public class HtmlRenderer : ILinesRenderer
{
    public static readonly HtmlRenderer Instance = new();

    private static readonly Dictionary<ElementKind, string> CssClasses = new()
    {
        [ElementKind.ExpressionType] = "expr",
        [ElementKind.Address] = "addr",
        [ElementKind.Variable] = "var",
        [ElementKind.Function] = "func",
        [ElementKind.StringLiteral] = "str",
        [ElementKind.NumericLiteral] = "num",
        [ElementKind.TypeName] = "type",
        [ElementKind.Keyword] = "kw",
        [ElementKind.PlainText] = "",
    };

    public string Render(IEnumerable<Element> elements)
    {
        var sb = new StringBuilder();
        foreach (var e in elements)
        {
            var escaped = System.Net.WebUtility.HtmlEncode(e.Value);
            var cssClass = CssClasses.GetValueOrDefault(e.Kind, "");
            if (string.IsNullOrEmpty(cssClass))
                sb.Append(escaped);
            else
                sb.Append($"<span class=\"{cssClass}\">{escaped}</span>");
        }
        return sb.ToString();
    }
}

public class DotHtmlLabelRenderer : ILinesRenderer
{
    public static readonly DotHtmlLabelRenderer Instance = new();

    private static readonly Dictionary<ElementKind, string> Colors = new()
    {
        [ElementKind.ExpressionType] = "#0055AA",  // Bold blue
        [ElementKind.Address] = "#555555",          // Dark gray
        [ElementKind.Variable] = "#006400",         // Dark green
        [ElementKind.Function] = "#6A0080",         // Dark purple
        [ElementKind.StringLiteral] = "#A00000",    // Dark red
        [ElementKind.NumericLiteral] = "#0000CD",   // Medium blue
        [ElementKind.TypeName] = "#996300",         // Dark orange
        [ElementKind.Keyword] = "#800080",          // Purple
        [ElementKind.PlainText] = "",               // No color (default black)
    };

    public static string NestedAddressColor => "#AAAAAA";  // Lighter gray for nested addresses

    public static string GetColor(ElementKind kind) => Colors.GetValueOrDefault(kind, "");

    public static void RenderTo(HtmlElement container, SummaryGenerator.Lines lines)
    {
        RenderTo(container, new[] { lines });
    }

    public static void RenderTo(HtmlElement container, IEnumerable<SummaryGenerator.Lines> linesCollection)
    {
        bool firstLine = true;
        foreach (var lines in linesCollection)
        {
            foreach (var (address, nest, elements) in lines.GetElements())
            {
                if (!firstLine) container.Add(Br());
                firstLine = false;

                for (int i = 0; i < elements.Count; i++)
                {
                    var e = elements[i];

                    // Add indentation after prefix address (or at start if no prefix)
                    if (i == 1 || (i == 0 && e.Kind != ElementKind.Address))
                    {
                        for (int j = 0; j < nest; j++)
                            container.Add(Raw("&nbsp;&nbsp;"));
                    }

                    // Prefix address at nest > 0 gets lighter color
                    var color = (i == 0 && e.Kind == ElementKind.Address && nest > 0)
                        ? NestedAddressColor
                        : GetColor(e.Kind);
                    if (!string.IsNullOrEmpty(color))
                        container.Add(Font(color).Add(e.Value));
                    else
                        container.Add(e.Value);
                }
            }
        }
    }

    public string Render(IEnumerable<Element> elements)
    {
        var sb = new StringBuilder();
        foreach (var e in elements)
        {
            var escaped = SanitizeForHtml(e.Value);
            var color = Colors.GetValueOrDefault(e.Kind, "");
            if (!string.IsNullOrEmpty(color))
                sb.Append($"<FONT COLOR=\"{color}\">{escaped}</FONT>");
            else
                sb.Append(escaped);
        }
        return sb.ToString();
    }

    public static string SanitizeForHtml(string str)
    {
        return str
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
