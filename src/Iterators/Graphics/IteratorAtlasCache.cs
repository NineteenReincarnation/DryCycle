using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

// Unity/Futile access is confined to the game thread. Default procedural meshes
// borrow Futile_White and create no textures, atlases or materials of their own.
internal static class IteratorAtlasCache
{
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    internal static IDisposable Acquire(string path)
    {
        if (!Entries.TryGetValue(path, out Entry entry))
        {
            FAtlas atlas = Futile.atlasManager.GetAtlasWithName(path);
            bool owned = atlas == null;
            if (owned) atlas = Futile.atlasManager.LoadAtlas(path);
            entry = new Entry { Atlas = atlas, Owned = owned };
            Entries.Add(path, entry);
        }
        entry.Count++;
        return new Lease(path, entry);
    }
    private sealed class Entry { internal int Count; internal FAtlas Atlas; internal bool Owned; }
    private sealed class Lease : IDisposable
    {
        private string _path;
        private readonly Entry _entry;
        internal Lease(string path, Entry entry) { _path = path; _entry = entry; }
        public void Dispose()
        {
            if (_path == null) return;
            string path = _path; _path = null;
            if (--_entry.Count != 0) return;
            Entries.Remove(path);
            if (_entry.Owned && ReferenceEquals(Futile.atlasManager.GetAtlasWithName(path), _entry.Atlas))
                Futile.atlasManager.UnloadAtlas(path);
        }
    }
}
