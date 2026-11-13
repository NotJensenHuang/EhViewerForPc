// Managed replacement for imsearch's Faiss binary HNSW and hnsw_rs adapter.
namespace EhViewerForPc.ImageSearch;

/// <summary>Indexes binary vectors in a bounded-degree hierarchical navigable graph.</summary>
public sealed class BinaryHnsw
{
    private sealed record Node(BinaryDescriptor Vector, List<int>[] Edges);
    private readonly List<Node> _nodes = [];
    private readonly Random _random;
    private int _entry = -1, _maxLevel = -1;

    /// <summary>Initializes a new graph with reproducible level selection.</summary>
    public BinaryHnsw(int connections = 32, int constructionWidth = 128, int seed = 20261112)
    {
        if (connections is < 2 or > 128 || constructionWidth < connections || constructionWidth > 4096)
            throw new ArgumentOutOfRangeException(nameof(connections));
        Connections = connections; ConstructionWidth = constructionWidth; _random = new Random(seed);
    }

    /// <summary>Gets the number of stored vectors.</summary>
    public int Count => _nodes.Count;
    /// <summary>Gets the maximum upper-layer degree.</summary>
    public int Connections { get; }
    /// <summary>Gets the construction search width.</summary>
    public int ConstructionWidth { get; }
    /// <summary>Gets a centroid by insertion order.</summary>
    public BinaryDescriptor this[int index] => _nodes[index].Vector;

    /// <summary>Inserts a vector and returns its stable ordinal.</summary>
    public int Add(BinaryDescriptor vector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int level = Math.Min(24, (int)(-Math.Log(1 - _random.NextDouble()) / Math.Log(Connections)));
        int id = _nodes.Count;
        _nodes.Add(new(vector, Enumerable.Range(0, level + 1).Select(_ => new List<int>()).ToArray()));
        if (_entry < 0) { _entry = id; _maxLevel = level; return id; }
        int nearest = _entry;
        for (int layer = _maxLevel; layer > level; layer--) nearest = Greedy(vector, nearest, layer);
        for (int layer = Math.Min(level, _maxLevel); layer >= 0; layer--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = SearchLayer(vector, nearest, ConstructionWidth, layer);
            int degree = layer == 0 ? Connections * 2 : Connections;
            List<int> selected = SelectNeighbors(vector, candidates, Connections);
            _nodes[id].Edges[layer].AddRange(selected);
            foreach (int neighbor in selected)
            {
                var edges = _nodes[neighbor].Edges[layer];
                edges.Add(id);
                if (edges.Count > degree)
                {
                    List<int> reduced = SelectNeighbors(_nodes[neighbor].Vector, edges, degree);
                    edges.Clear(); edges.AddRange(reduced);
                }
            }
            if (candidates.Count > 0) nearest = candidates[0];
        }
        if (level > _maxLevel) { _entry = id; _maxLevel = level; }
        return id;
    }

    /// <summary>Returns approximate nearest ordinals ordered by distance and ordinal.</summary>
    public int[] Search(BinaryDescriptor query, int count, int searchWidth = 16)
    {
        if (count < 1 || count > Count || searchWidth < 1) throw new ArgumentOutOfRangeException(nameof(count));
        int nearest = _entry;
        for (int layer = _maxLevel; layer > 0; layer--) nearest = Greedy(query, nearest, layer);
        return SearchLayer(query, nearest, Math.Max(count, searchWidth), 0).Take(count).ToArray();
    }

    private int Greedy(BinaryDescriptor query, int entry, int layer)
    {
        int distance = query.Distance(_nodes[entry].Vector);
        bool changed;
        do
        {
            changed = false;
            foreach (int neighbor in _nodes[entry].Edges[layer])
            {
                int next = query.Distance(_nodes[neighbor].Vector);
                if (next < distance) { entry = neighbor; distance = next; changed = true; }
            }
        } while (changed);
        return entry;
    }

    private List<int> SearchLayer(BinaryDescriptor query, int entry, int width, int layer)
    {
        var visited = new HashSet<int> { entry };
        var candidates = new PriorityQueue<int, (int Distance, int Id)>();
        var best = new PriorityQueue<int, (int Distance, int Id)>();
        int d = query.Distance(_nodes[entry].Vector);
        candidates.Enqueue(entry, (d, entry)); best.Enqueue(entry, (-d, -entry));
        while (candidates.TryDequeue(out int current, out var priority))
        {
            best.TryPeek(out _, out var worst);
            if (best.Count >= width && priority.Distance > -worst.Distance) break;
            foreach (int neighbor in _nodes[current].Edges[layer])
            {
                if (!visited.Add(neighbor)) continue;
                d = query.Distance(_nodes[neighbor].Vector);
                best.TryPeek(out _, out worst);
                if (best.Count < width || (d, neighbor).CompareTo((-worst.Distance, -worst.Id)) < 0)
                {
                    candidates.Enqueue(neighbor, (d, neighbor)); best.Enqueue(neighbor, (-d, -neighbor));
                    if (best.Count > width) best.Dequeue();
                }
            }
        }
        return best.UnorderedItems.Select(x => x.Element).OrderBy(i => query.Distance(_nodes[i].Vector)).ThenBy(i => i).ToList();
    }

    private List<int> SelectNeighbors(BinaryDescriptor query, IEnumerable<int> candidates, int count)
    {
        var ordered = candidates.Distinct().OrderBy(i => query.Distance(_nodes[i].Vector)).ThenBy(i => i).ToArray();
        var chosen = new List<int>(count);
        foreach (int candidate in ordered)
        {
            int distance = query.Distance(_nodes[candidate].Vector);
            if (chosen.All(other => _nodes[candidate].Vector.Distance(_nodes[other].Vector) >= distance)) chosen.Add(candidate);
            if (chosen.Count == count) break;
        }
        // Keep pruned equal-distance points reachable even on uniform training data.
        foreach (int candidate in ordered)
        {
            if (chosen.Count == count) break;
            if (!chosen.Contains(candidate)) chosen.Add(candidate);
        }
        return chosen;
    }

    /// <summary>Saves vectors and graph edges atomically with a checksum.</summary>
    public void Save(string path, string profileId)
    {
        CheckedFile.Write(path, writer =>
        {
            writer.Write(0x3157534843524d49UL); writer.Write(profileId); writer.Write(Connections); writer.Write(ConstructionWidth);
            writer.Write(Count); writer.Write(_entry); writer.Write(_maxLevel);
            foreach (Node node in _nodes)
            {
                CheckedFile.WriteDescriptor(writer, node.Vector); writer.Write(node.Edges.Length);
                foreach (var edges in node.Edges) { writer.Write(edges.Count); foreach (int edge in edges) writer.Write(edge); }
            }
        });
    }

    /// <summary>Loads a graph and validates its configuration and every edge.</summary>
    public static BinaryHnsw Load(string path, string profileId)
    {
        using var reader = CheckedFile.Open(path);
        if (reader.ReadUInt64() != 0x3157534843524d49UL || CheckedFile.ReadShortString(reader) != profileId)
            throw new InvalidDataException("Quantizer format or profile mismatch; rebuild required.");
        var graph = new BinaryHnsw(reader.ReadInt32(), reader.ReadInt32());
        int count = reader.ReadInt32(); graph._entry = reader.ReadInt32(); graph._maxLevel = reader.ReadInt32();
        if (count is < 1 or > 1048576 || graph._entry < 0 || graph._entry >= count || graph._maxLevel is < 0 or > 24)
            throw new InvalidDataException("Invalid graph dimensions.");
        for (int i = 0; i < count; i++)
        {
            BinaryDescriptor descriptor = CheckedFile.ReadDescriptor(reader);
            int levels = reader.ReadInt32();
            if (levels is < 1 or > 25) throw new InvalidDataException("Invalid HNSW level.");
            var edges = new List<int>[levels];
            for (int j = 0; j < levels; j++)
            {
                int n = reader.ReadInt32();
                if (n < 0 || n > graph.Connections * 2) throw new InvalidDataException("Invalid HNSW degree.");
                edges[j] = new List<int>(n);
                for (int k = 0; k < n; k++)
                {
                    int edge = reader.ReadInt32();
                    if (edge < 0 || edge >= count || edge == i || edges[j].Contains(edge)) throw new InvalidDataException("Invalid graph edge.");
                    edges[j].Add(edge);
                }
            }
            graph._nodes.Add(new(descriptor, edges));
        }
        if (graph._nodes[graph._entry].Edges.Length != graph._maxLevel + 1) throw new InvalidDataException("Invalid entry layer.");
        foreach (Node node in graph._nodes)
            for (int j = 0; j < node.Edges.Length; j++)
                if (node.Edges[j].Any(i => graph._nodes[i].Edges.Length <= j)) throw new InvalidDataException("Invalid edge layer.");
        CheckedFile.RequireEnd(reader);
        return graph;
    }
}
