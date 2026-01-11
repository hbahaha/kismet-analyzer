namespace KismetAnalyzer.Dot;

interface IStatement
{
    void Write(TextWriter writer);
}

public class Graph : AbstractGraph, IStatement
{
    public string Type { get; set; }

    public Graph(string type)
    {
        Type = type;
    }

    public void Write(TextWriter writer)
    {
        writer.WriteLine(Type);
        writer.WriteLine("{");
        WriteStatements(writer);
        writer.WriteLine("}");
    }
}

public class Subgraph : AbstractGraph, IStatement
{
    public string? Id { get; set; }

    public Subgraph(string? id = null)
    {
        Id = id;
    }

    public void Write(TextWriter writer)
    {
        if (Id != null) writer.WriteLine(AbstractGraph.EscapeId(Id));
        writer.WriteLine("{");
        WriteStatements(writer);
        writer.WriteLine("}");
    }
}

public abstract class AbstractGraph
{
    public Attributes Attributes = new Attributes();
    public List<Node> Nodes { get; } = new List<Node>();
    public List<Edge> Edges { get; } = new List<Edge>();
    public List<Subgraph> Subgraphs { get; } = new List<Subgraph>();
    public Attributes GraphAttributes { get; } = new Attributes();
    public Attributes NodeAttributes { get; } = new Attributes();
    public Attributes EdgeAttributes { get; } = new Attributes();

    public virtual void WriteStatements(TextWriter writer)
    {
        Attributes.WriteStatements(writer);
        WriteAttributes(writer, "graph", GraphAttributes);
        WriteAttributes(writer, "node", NodeAttributes);
        WriteAttributes(writer, "edge", EdgeAttributes);
        foreach (var node in Nodes)
        {
            node.Write(writer);
        }
        foreach (var edge in Edges)
        {
            edge.Write(writer);
        }
        foreach (var subgraph in Subgraphs)
        {
            subgraph.Write(writer);
        }
    }

    static void WriteAttributes(TextWriter writer, string label, Attributes attributes)
    {
        if (attributes.Count <= 0) return;

        writer.Write($"{label} ");
        attributes.Write(writer);
    }

    public static string EscapeId(string id)
    {
        return $"\"{id.Replace("\"", "\\\"")}\"";
    }
}

public class Attributes : Dictionary<string, string>
{
    public void Write(TextWriter writer)
    {
        writer.Write("[");
        writer.Write(String.Join("; ", this.Select(attr => $"{attr.Key} = \"{attr.Value}\"").ToList()));
        writer.WriteLine("]");
    }
    public void WriteStatements(TextWriter writer)
    {
        foreach (var attr in this)
        {
            writer.WriteLine($"{attr.Key} = \"{attr.Value}\";");
        }
    }
}

public class Node : IStatement
{
    public string Id { get; set; }
    public Attributes Attributes { get; } = new Attributes();
    public IHtmlElement? HtmlLabel { get; set; }

    public Node(string id)
    {
        Id = id;
    }
    public void Write(TextWriter writer)
    {
        writer.Write(AbstractGraph.EscapeId(Id));
        if (HtmlLabel != null || Attributes.Count > 0)
        {
            writer.Write(" [");
            var parts = new List<string>();

            if (HtmlLabel != null)
            {
                var sw = new StringWriter();
                sw.Write("<");
                HtmlLabel.Write(sw);
                sw.Write(">");
                parts.Add($"label = {sw}");
            }

            parts.AddRange(Attributes.Select(attr => $"{attr.Key} = \"{attr.Value}\""));
            writer.Write(string.Join("; ", parts));
            writer.WriteLine("]");
        }
        else
        {
            writer.WriteLine();
        }
    }
}
public class Edge : IStatement
{
    public string A { get; set; }
    public string? ACompass { get; set; }
    public string B { get; set; }
    public string? BCompass { get; set; }
    public Attributes Attributes { get; } = new Attributes();
    public Edge(string a, string b)
    {
        A = a;
        ACompass = null;
        B = b;
        BCompass = null;
    }
    public Edge(string a, string aCompass, string b, string bCompass)
    {
        A = a;
        ACompass = aCompass;
        B = b;
        BCompass = bCompass;
    }
    public void Write(TextWriter writer)
    {
        writer.Write($"{AbstractGraph.EscapeId(A)}{(ACompass == null ? "" : ":" + ACompass)} -> {AbstractGraph.EscapeId(B)}{(BCompass == null ? "" : ":" + BCompass)}");
        if (Attributes.Count > 0)
        {
            writer.Write(" ");
            Attributes.Write(writer);
        }
        else
        {
            writer.WriteLine();
        }
    }
}

// HTML-like label support for GraphViz

public interface IHtmlElement
{
    void Write(TextWriter writer);
}

public class HtmlText : IHtmlElement
{
    public string Value { get; }
    public HtmlText(string value) => Value = value;

    public void Write(TextWriter writer)
    {
        writer.Write(Value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;"));
    }
}

public class HtmlRaw : IHtmlElement
{
    public string Value { get; }
    public HtmlRaw(string value) => Value = value;
    public void Write(TextWriter writer) => writer.Write(Value);
}

public class HtmlElement : IHtmlElement
{
    public string Tag { get; }
    public Dictionary<string, string> Attributes { get; } = new();
    public List<IHtmlElement> Children { get; } = new();
    public bool SelfClosing { get; set; }

    public HtmlElement(string tag) => Tag = tag;

    public HtmlElement Attr(string key, string value) { Attributes[key] = value; return this; }
    public HtmlElement Add(IHtmlElement child) { Children.Add(child); return this; }
    public HtmlElement Add(string text) { Children.Add(new HtmlText(text)); return this; }

    public void Write(TextWriter writer)
    {
        writer.Write($"<{Tag}");
        foreach (var attr in Attributes)
            writer.Write($" {attr.Key}=\"{attr.Value}\"");

        if (SelfClosing)
        {
            writer.Write("/>");
        }
        else
        {
            writer.Write(">");
            foreach (var child in Children)
                child.Write(writer);
            writer.Write($"</{Tag}>");
        }
    }
}

public static class Html
{
    public static HtmlElement Table(string? bgcolor = null)
    {
        var table = new HtmlElement("TABLE")
            .Attr("BORDER", "0").Attr("CELLBORDER", "1").Attr("CELLSPACING", "0");
        if (bgcolor != null) table.Attr("BGCOLOR", bgcolor);
        return table;
    }
    public static HtmlElement Tr() => new("TR");
    public static HtmlElement Td() => new("TD");
    public static HtmlElement Font(string color) => new HtmlElement("FONT").Attr("COLOR", color);
    public static HtmlElement Br() => new HtmlElement("BR") { SelfClosing = true };
    public static HtmlText Text(string value) => new(value);
    public static HtmlRaw Raw(string value) => new(value);
}
