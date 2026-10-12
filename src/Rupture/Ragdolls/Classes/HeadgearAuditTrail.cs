using System;
using System.Collections.Generic;

namespace Rupture.Ragdolls.Classes;

// Managed-only bounded history; successful operations never write to disk.
internal sealed class HeadgearAuditTrail
{
    internal const int Capacity = 256;
    private readonly Dictionary<string, Queue<string>> _items = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
    private readonly Queue<string> _order = new Queue<string>();
    private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);
    internal int Count => _items.Count;
    internal bool Contains(string id) => id != null && _items.ContainsKey(id);
    internal string Add(string id, string message)
    {
        if (string.IsNullOrEmpty(id)) return null;
        string evicted = null;
        if (!_items.TryGetValue(id, out var history))
        {
            if (_items.Count == Capacity)
            {
                evicted = _order.Dequeue(); _items.Remove(evicted); _reported.Remove(evicted);
            }
            _items.Add(id, history = new Queue<string>(8)); _order.Enqueue(id);
        }
        if (history.Count == 8) history.Dequeue();
        history.Enqueue(message == null ? "" : message.Length <= 512 ? message : message.Substring(0, 512));
        return evicted;
    }
    internal string Report(string id)
    {
        if (!Contains(id) || !_reported.Add(id)) return null;
        return string.Join(" | ", _items[id]);
    }
    internal void Clear() { _items.Clear(); _order.Clear(); _reported.Clear(); }
}

internal sealed class HeadgearThrowEvidence
{
    internal bool OperationStarted, Finished, Successful, WorldCreated;
    internal string Error;
    internal bool Complete => Finished && Successful && WorldCreated;
    internal string Failure => Finished && !Successful ? Error ?? "operation failed" : null;
}
