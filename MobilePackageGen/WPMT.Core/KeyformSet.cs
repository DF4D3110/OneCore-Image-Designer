using System.Collections;
using WPMT.Core.Common;
using WPMT.Core.Exceptions;
using WPMT.Core.Interfaces;

namespace WPMT.Core;

public class KeyformSet<T> : IEnumerable<T> where T : IKeyform
{
    private readonly List<T> Keyforms;
    public int Count => Keyforms.Count;
    public int Capacity => Keyforms.Capacity;

    public KeyformSet()
    {
        Keyforms = [];
    }

    public KeyformSet(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be non-negative.");
        Keyforms = [with(capacity)];
    }

    public KeyformSet(IEnumerable<T> keyforms)
    {
        Keyforms = [.. keyforms];
    }

    public int EnsureCapacity(int capacity)
    {
        return Keyforms.EnsureCapacity(capacity);
    }

    public bool ContainsKeyformHash(ulong hash)
    {
        return Keyforms.Any(k => k.Hashes.PseudoKey == hash);
    }

    public bool Contains(IKeyform keyform)
    {
        return Keyforms.Any(k => k.Hashes.PseudoKey == keyform.Hashes.PseudoKey);
    }

    public void Add(T keyform)
    {
        if (!Contains(keyform))
            Keyforms.Add(keyform);
    }

    public void AddRange(IEnumerable<T> keyforms)
    {
        ArgumentNullException.ThrowIfNull(keyforms);

        foreach (T keyform in keyforms)
            Add(keyform);
    }

    public bool Remove(T keyform)
    {
        return Keyforms.Remove(keyform);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return Keyforms.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Clear()
    {
        Keyforms.Clear();
    }

    public KeyformSet<T> GetKeyforms(IKeyform keyform, PseudoKeyType type)
    {
        return type switch
        {
            PseudoKeyType.Standard => [with(Keyforms.Where(k => k.Hashes.PseudoKey == keyform.Hashes.PseudoKey))],
            PseudoKeyType.ServicingIndependent => [with(Keyforms.Where(k => k.Hashes.ServicingIndependentPseudoKey == keyform.Hashes.ServicingIndependentPseudoKey))],
            PseudoKeyType.VersionIndependent => [with(Keyforms.Where(k => k.Hashes.VersionIndependentPseudoKey == keyform.Hashes.VersionIndependentPseudoKey))],
            _ => throw new WPMTException("Unknown PseudoKeyType."),
        };
    }
}
