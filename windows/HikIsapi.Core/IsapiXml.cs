using System.Xml.Linq;

namespace HikIsapi;

public sealed class XmlEditResult
{
    public List<XmlChange> Changes { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed record XmlChange(string Action, string Path, string? Before, string? After);

public static class IsapiXml
{
    public static XElement Parse(string xml)
    {
        var text = xml.TrimStart('\uFEFF').Trim();
        return XElement.Parse(text, LoadOptions.PreserveWhitespace);
    }

    public static string? DirectText(XElement parent, string name)
    {
        var child = parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);
        return child == null ? null : (child.Value ?? "").Trim();
    }

    public static XmlEditResult Edit(XElement root, IReadOnlyList<(string Path, string Value)> setValues, IReadOnlyList<string> removePaths, string onMissing)
    {
        if (onMissing is not ("error" or "skip" or "create"))
            throw new InvalidOperationException("不支援的 on_missing：" + onMissing);
        var result = new XmlEditResult();
        var create = onMissing == "create";
        if (onMissing == "error")
        {
            foreach (var (path, value) in setValues)
                Assign(Find(root, path, create: false), path, value, result.Changes);
        }
        else
        {
            foreach (var (path, value) in setValues)
            {
                XElement node;
                try
                {
                    node = Find(root, path, create);
                }
                catch (InvalidOperationException) when (onMissing == "skip")
                {
                    result.Warnings.Add("找不到節點 " + path + "，已略過");
                    continue;
                }
                Assign(node, path, value, result.Changes);
            }
        }
        foreach (var path in removePaths)
        {
            if (Remove(root, path))
                result.Changes.Add(new XmlChange("remove", path, null, null));
            else
                result.Warnings.Add("找不到要移除的節點 " + path);
        }
        return result;
    }

    public static string Serialize(XElement root)
    {
        var clone = new XElement(root);
        var primary = clone.Name.Namespace;
        if (primary != XNamespace.None)
        {
            clone = Strip(clone, primary);
            clone.SetAttributeValue("xmlns", primary.NamespaceName);
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + clone.ToString(SaveOptions.DisableFormatting);
    }

    public static XElement Find(XElement root, string path, bool create)
    {
        var parts = ParsePath(path);
        if (parts.Count > 0 && parts[0].Name == root.Name.LocalName && parts[0].Index == null)
            parts.RemoveAt(0);
        var node = root;
        foreach (var part in parts)
            node = Descend(node, part, create, path);
        return node;
    }

    private static void Assign(XElement node, string path, string value, List<XmlChange> changes)
    {
        if (node.Elements().Any())
            throw new InvalidOperationException(path + " 底下還有子節點，不能直接指定文字");
        var after = value.Trim();
        var before = (node.Value ?? "").Trim();
        if (before == after)
            return;
        node.Value = after;
        changes.Add(new XmlChange("set", path, before, after));
    }

    private static bool Remove(XElement root, string path)
    {
        var parts = ParsePath(path);
        if (parts.Count > 0 && parts[0].Name == root.Name.LocalName && parts[0].Index == null)
            parts.RemoveAt(0);
        if (parts.Count == 0)
            throw new InvalidOperationException("不能移除根節點");
        var parent = root;
        if (parts.Count > 1)
        {
            var parentPath = string.Join("/", parts.Take(parts.Count - 1).Select(FormatPart));
            try
            {
                parent = Find(root, parentPath, create: false);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        var last = parts[^1];
        var matches = parent.Elements().Where(element => element.Name.LocalName == last.Name).ToList();
        if (last.Index == null)
        {
            if (matches.Count == 0)
                return false;
            matches[0].Remove();
            return true;
        }
        if (last.Index.Value >= matches.Count)
            return false;
        matches[last.Index.Value].Remove();
        return true;
    }

    private static XElement Descend(XElement node, PathPart part, bool create, string fullPath)
    {
        var matches = node.Elements().Where(element => element.Name.LocalName == part.Name).ToList();
        if (part.Index == null)
        {
            if (matches.Count > 0)
                return matches[0];
            if (create)
                return AddChild(node, part.Name);
            throw new InvalidOperationException("找不到節點 " + fullPath);
        }
        if (part.Index.Value < matches.Count)
            return matches[part.Index.Value];
        if (create && part.Index.Value == matches.Count)
            return AddChild(node, part.Name);
        throw new InvalidOperationException("找不到節點 " + fullPath);
    }

    private static XElement AddChild(XElement parent, string name)
    {
        var child = parent.Name.Namespace == XNamespace.None
            ? new XElement(name)
            : new XElement(parent.Name.Namespace + name);
        parent.Add(child);
        return child;
    }

    private static XElement Strip(XElement element, XNamespace primary)
    {
        var name = element.Name.Namespace == primary || element.Name.Namespace == XNamespace.None
            ? element.Name.LocalName
            : (XName)element.Name;
        var copy = new XElement(name);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
                continue;
            copy.SetAttributeValue(attribute.Name.LocalName, attribute.Value);
        }
        if (!element.Elements().Any())
            copy.Value = element.Value;
        else
        {
            foreach (var child in element.Elements())
                copy.Add(Strip(child, primary));
        }
        return copy;
    }

    private static List<PathPart> ParsePath(string path)
    {
        var raw = path.Trim().Trim('/');
        if (raw.Length == 0)
            throw new InvalidOperationException("路徑是空的");
        var parts = new List<PathPart>();
        foreach (var segment in raw.Split('/'))
        {
            var piece = segment.Trim();
            var bracket = piece.IndexOf('[');
            if (bracket < 0)
            {
                parts.Add(new PathPart(piece, null));
                continue;
            }
            if (!piece.EndsWith(']') || !int.TryParse(piece[(bracket + 1)..^1], out var index))
                throw new InvalidOperationException("無法解析路徑「" + path + "」的節點「" + piece + "」");
            parts.Add(new PathPart(piece[..bracket], index));
        }
        return parts;
    }

    private static string FormatPart(PathPart part)
        => part.Index == null ? part.Name : part.Name + "[" + part.Index.Value + "]";

    private readonly record struct PathPart(string Name, int? Index);
}
