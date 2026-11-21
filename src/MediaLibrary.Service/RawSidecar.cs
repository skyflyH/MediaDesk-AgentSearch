using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MediaLibrary.Service;

internal static class RawSidecar
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "adobe:ns:meta/";
    public static string PathFor(string raw) => Path.ChangeExtension(raw, ".xmp");

    private static XDocument Load(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("XMP 路径不能是符号链接。");
        using var reader = XmlReader.Create(path, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static string? Read(string raw)
    {
        if (!RawPhoto.Supports(System.IO.Path.GetExtension(raw))) return null;
        var path = PathFor(raw);
        if (!File.Exists(path)) return null;
        try {
            var document = Load(path);
            var description = document.Descendants(Dc + "description").FirstOrDefault();
            var values = description?.Descendants(Rdf + "li").ToArray();
            return values?.FirstOrDefault(item => (string?)item.Attribute(XNamespace.Xml + "lang") == "x-default")?.Value
                ?? values?.FirstOrDefault()?.Value ?? description?.Value
                ?? document.Descendants(Rdf + "Description").Attributes(Dc + "description").FirstOrDefault()?.Value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException) { return null; }
    }

    public static void Write(string raw, string value, string? templateRaw = null)
    {
        var path = PathFor(raw);
        var existing = File.Exists(path);
        var template = templateRaw is null ? null : PathFor(templateRaw);
        var document = existing ? Load(path) : template is not null && File.Exists(template) ? Load(template) :
            new XDocument(new XElement(Xmp + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Xmp),
                new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                    new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")))));
        var rdf = document.Descendants(Rdf + "RDF").FirstOrDefault()
            ?? throw new IOException("现有 XMP 格式无效，未覆盖原文件。");
        var node = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (node is null) { node = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", "")); rdf.Add(node); }
        foreach (var property in document.Descendants(Dc + "description").ToArray()) property.Remove();
        foreach (var attribute in document.Descendants(Rdf + "Description").Attributes(Dc + "description").ToArray()) attribute.Remove();
        node.Add(new XElement(Dc + "description", new XElement(Rdf + "Alt",
            new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value))));
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try {
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                document.Save(writer);
            if (existing) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (Read(raw) != value) throw new IOException("XMP 备注写入后读回不一致。");
    }

    public static void CheckRename(string source, string target)
    {
        if (!RawPhoto.Supports(System.IO.Path.GetExtension(source)) || WindowsPaths.SamePath(PathFor(source), PathFor(target))) return;
        LibraryDatabase.CheckDestination(PathFor(target));
    }

    public static void Move(string source, string target)
    {
        if (!RawPhoto.Supports(System.IO.Path.GetExtension(source)) || WindowsPaths.SamePath(PathFor(source), PathFor(target))) return;
        if (File.Exists(PathFor(source))) File.Move(PathFor(source), PathFor(target));
    }
}
