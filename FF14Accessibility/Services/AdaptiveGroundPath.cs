using System.Numerics;

namespace FF14Accessibility.Services;

/// <summary>A bounded A* repair of mesh routes. Native complete/partial routes
/// are long graph edges; nearby mesh samples add walking and explicit jumping
/// edges. No query or graph expansion is allowed to start movement.</summary>
internal sealed class AdaptiveGroundPath : IDisposable
{
    private sealed class Node(Vector3 position, float cost, Node? parent, GroundLeg? leg)
    {
        internal readonly Vector3 Position = position;
        internal readonly float Cost = cost;
        internal readonly Node? Parent = parent;
        internal readonly GroundLeg? Leg = leg;
    }
    private readonly Func<Vector3, float, float, Vector3?> _nearest;
    private readonly Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> _query;
    private readonly Func<Vector3, Vector3, bool> _clear;
    private readonly Func<Vector3, Vector3, bool>? _jumpClear;
    private readonly IReadOnlyList<GroundFailure> _failures;
    private readonly IReadOnlyList<Vector3> _usedApproaches;
    private readonly float _range;
    private readonly PriorityQueue<Node, float> _open = new();
    private readonly Dictionary<(int, int, int), float> _costs = new();
    private readonly List<Vector3> _failedQueryOrigins = [];
    private HeightPath? _native;
    private Node? _current;
    private Node? _expanding;
    private int _probeIndex;
    private bool _checkingPartial, _disposed;
    private List<GroundLeg>? _best;
    private List<GroundLeg>? _approach;
    private float _approachDistance = float.MaxValue;
    private float _bestCost = float.MaxValue;

    internal Vector3 Start { get; }
    internal Vector3 Destination { get; }
    internal bool Done { get; private set; }
    internal List<GroundLeg>? Result { get; private set; }
    internal int Expanded { get; private set; }
    internal int Queries { get; private set; }
    internal string LastFailure { get; private set; } = "search exhausted";
    internal bool ApproachOnly { get; private set; }
    internal const int MaxNodes = 768;

    internal AdaptiveGroundPath(Vector3 start, Vector3 destination, float range,
        Func<Vector3, float, float, Vector3?> nearest,
        Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> query,
        Func<Vector3, Vector3, bool> clear, Func<Vector3, Vector3, bool>? jumpClear = null,
        IReadOnlyList<GroundFailure>? failures = null, IReadOnlyList<Vector3>? usedApproaches = null)
    {
        Start = start; Destination = destination; _range = range;
        _nearest = nearest; _query = query; _clear = clear; _jumpClear = jumpClear;
        _failures = failures?.ToArray() ?? [];
        _usedApproaches = usedApproaches?.ToArray() ?? [];
        if (!HeightPath.Finite(start) || !HeightPath.Finite(destination) || !float.IsFinite(range) || range <= 0)
        { Done = true; LastFailure = "invalid destination"; return; }
        Add(new Node(start, 0, null, null));
    }

    internal void Update()
    {
        if (Done) return;
        if (_expanding != null)
        {
            // Spread geometry probes over frames. A graph node has sixteen
            // neighbours, but only two are inspected in one framework tick.
            for (var i = 0; i < 2 && _expanding != null; ++i)
            {
                if (_probeIndex >= 16) { _expanding = null; break; }
                var radius = _probeIndex < 8 ? 1.25f : 2.5f;
                var angle = (_probeIndex++ % 8) * MathF.PI / 4;
                ExpandNeighbour(_expanding, _expanding.Position + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius));
            }
            return;
        }
        if (_native != null)
        {
            _native.Update();
            if (!_native.Done) return;
            var result = _native.Result;
            var partial = _native.NativePartialEnd;
            var failure = _native.LastFailure;
            Queries += _native.Queries;
            _native.Dispose(); _native = null;
            if (failure == "path query IPC unavailable")
            { LastFailure = failure; Complete(); return; }
            if (result is { Count: > 0 })
            {
                var leg = new GroundLeg(_current!.Position, new List<Vector3>(result));
                if (_checkingPartial)
                {
                    Add(new Node(leg.End, _current.Cost + leg.Cost, _current, leg));
                    var remaining = Vector3.Distance(leg.End, Destination);
                    var score = remaining + _current.Cost + leg.Cost;
                    if (score < _approachDistance && Vector3.Distance(Start, leg.End) > 5
                        && !_usedApproaches.Any(p => Vector3.Distance(p, leg.End) < 2))
                    { _approachDistance = score; _approach = Route(_current, leg); }
                }
                else
                {
                    var cost = _current.Cost + leg.Cost;
                    if (cost < _bestCost) { _bestCost = cost; _best = Route(_current, leg); }
                }
            }
            if (!_checkingPartial && result == null) _failedQueryOrigins.Add(_current!.Position);
            if (!_checkingPartial && result == null && partial is { } endpoint
                && Vector3.Distance(endpoint, _current!.Position) > 2)
            {
                _checkingPartial = true;
                SearchNative(_current.Position, endpoint, 0.5f);
                return;
            }
            _checkingPartial = false;
            Expand(_current!); _current = null;
            return;
        }
        // Each tick consumes only one graph node. All native/geometry calls
        // remain on the framework thread, with asynchronous native path queries.
        while (_open.TryDequeue(out var node, out var priority))
        {
            if (priority >= _bestCost) { Complete(); return; }
            if (node.Cost > _costs[Key(node.Position)] + 0.01f) continue;
            if (++Expanded > MaxNodes) { LastFailure = "node budget exhausted"; Complete(); return; }
            if (Vector3.Distance(node.Position, Destination) <= _range
                && _clear(node.Position, Destination))
            {
                if (node.Cost < _bestCost)
                { _bestCost = node.Cost; _best = Route(node, new GroundLeg(node.Position, [node.Position])); }
                continue;
            }
            _current = node;
            if (node.Leg is not { Jump: true }
                && _failedQueryOrigins.Any(p => MathF.Abs(p.Y - node.Position.Y) < 0.3f
                    && GroundDetour.FlatDistance(p, node.Position) < 1.5f))
            {
                Expand(node); _current = null;
                return;
            }
            SearchNative(node.Position, Destination, _range);
            return;
        }
        Complete();
    }

    private void SearchNative(Vector3 from, Vector3 to, float range)
        => _native = new HeightPath(from, to, range, _nearest, _query,
            (a, b) => Allowed(a, b, false) && _clear(a, b), directOnly: true);

    private void Expand(Node node)
    {
        _expanding = node; _probeIndex = 0;
    }

    private void ExpandNeighbour(Node node, Vector3 probe)
    {
        // Repair close to an actual graph origin. Far-away jumps cannot be
        // certified from a streamed scene; a partial approach can be walked and
        // the same planner invoked again when the player reaches the gap.
        var floor = _nearest(probe, 0.35f, 1.1f);
        if (floor is not { } p || !HeightPath.Finite(p) || GroundDetour.FlatDistance(probe, p) > 0.35f
            || Vector3.Distance(p, node.Position) < 0.6f) return;
        if (Allowed(node.Position, p, false) && GroundTraversal.Walk(node.Position, p, _nearest, _clear) is { } points)
        {
            var leg = new GroundLeg(node.Position, points);
            Add(new Node(p, node.Cost + leg.Cost, node, leg));
        }
        else if (_jumpClear != null && Allowed(node.Position, p, true) && _jumpClear(node.Position, p))
        {
            var leg = new GroundLeg(node.Position, [p], true);
            Add(new Node(p, node.Cost + leg.Cost, node, leg));
        }
    }

    private bool Allowed(Vector3 from, Vector3 to, bool jump) => !_failures.Any(f => f.Blocks(from, to, jump));
    private float Heuristic(Vector3 p) => MathF.Max(0, Vector3.Distance(p, Destination) - _range);
    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 2), (int)MathF.Round(p.Y * 2), (int)MathF.Round(p.Z * 2));
    private void Add(Node node)
    {
        var key = Key(node.Position);
        if (_costs.TryGetValue(key, out var previous) && previous <= node.Cost + 0.01f) return;
        // Bound the queue as well as expanded nodes to avoid a huge search on
        // an open but disconnected surface.
        if (_costs.Count >= MaxNodes * 4 && !_costs.ContainsKey(key)) return;
        _costs[key] = node.Cost;
        _open.Enqueue(node, node.Cost + Heuristic(node.Position));
    }

    private static List<GroundLeg> Route(Node node, GroundLeg tail)
    {
        var route = new List<GroundLeg> { tail };
        for (var current = node; current.Leg != null; current = current.Parent!) route.Add(current.Leg);
        route.Reverse();
        return route;
    }

    private void Complete()
    {
        Done = true; Result = _best ?? _approach;
        ApproachOnly = _best == null && _approach != null;
        if (Result != null) LastFailure = "none";
    }

    internal void FinishWithBestAvailable() => Complete();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Done = true; Result = null;
        _native?.Dispose(); _native = null; _open.Clear(); _expanding = null;
    }
}
