using CouchHud.Core.Config;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Library;

/// <summary>Rules for the user's collections; operates on the config list so it can be tested without a store.</summary>
public static class Collections
{
    public const int MaxNameLength = 40;

    public static ItemCollection Create(List<ItemCollection> all, string name)
    {
        var clean = CleanName(all, name, null);
        var collection = new ItemCollection { Id = Guid.NewGuid().ToString("N")[..8], Name = clean };
        all.Add(collection);
        return collection;
    }

    public static void Rename(List<ItemCollection> all, string id, string name)
    {
        var collection = Get(all, id);
        collection.Name = CleanName(all, name, collection);
    }

    public static void Delete(List<ItemCollection> all, string id) => all.Remove(Get(all, id));

    /// <summary>Adds or removes one item; adding twice keeps a single entry.</summary>
    public static void SetMember(List<ItemCollection> all, string id, string itemId, bool member)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException(Strings.Get("collections.itemMissing"), nameof(itemId));
        var collection = Get(all, id);
        if (member)
        {
            if (!collection.Items.Contains(itemId)) collection.Items.Add(itemId);
        }
        else collection.Items.Remove(itemId);
    }

    public static void SetOnHome(List<ItemCollection> all, string id, bool onHome) => Get(all, id).OnHome = onHome;

    /// <summary>Drops a removed library entry from every collection.</summary>
    public static void Forget(List<ItemCollection> all, string itemId)
    {
        foreach (var c in all) c.Items.Remove(itemId);
    }

    private static ItemCollection Get(List<ItemCollection> all, string id) =>
        all.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException(Strings.Get("collections.notFound"));

    private static string CleanName(List<ItemCollection> all, string name, ItemCollection? self)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException(Strings.Get("collections.needName"));
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        if (all.Any(c => c != self && string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(Strings.Get("collections.exists", clean));
        return clean;
    }
}
