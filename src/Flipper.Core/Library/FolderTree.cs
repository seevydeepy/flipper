namespace Flipper.Core.Library;

public sealed record FolderItem(string Name, string Key, IReadOnlyList<FolderItem> Children);

public static class FolderTree
{
    public static IReadOnlyList<FolderItem> FromRelativeFolders(IEnumerable<string> folders)
    {
        var root = new Node(string.Empty, string.Empty);
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder) || folder == ".")
            {
                continue;
            }

            var parts = folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var path = new List<string>();
            foreach (var part in parts)
            {
                path.Add(part);
                current = current.Ensure(part, string.Join("\\", path));
            }
        }

        return root.Children.Values
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Select(node => node.ToItem())
            .ToArray();
    }

    private sealed class Node
    {
        public Node(string name, string key)
        {
            Name = name;
            Key = key;
        }

        public string Name { get; }
        public string Key { get; }
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Node Ensure(string name, string key)
        {
            if (!Children.TryGetValue(name, out var child))
            {
                child = new Node(name, key);
                Children[name] = child;
            }

            return child;
        }

        public FolderItem ToItem()
        {
            var children = Children.Values
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(node => node.ToItem())
                .ToArray();
            return new FolderItem(Name, Key, children);
        }
    }
}
