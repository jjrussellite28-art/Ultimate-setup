using CouchHud.Core.Notifications;

namespace CouchHud.Core.Tests;

public class NotificationTests
{
    private static Notification N(string id, long time = 0) => new(id, "windows", "WhatsApp", "Mama", "Essen ist fertig", time);

    [Fact]
    public void Newest_first_without_duplicates_and_capped()
    {
        var center = new NotificationCenter();
        var raised = 0;
        center.Added += _ => raised++;

        Assert.True(center.Add(N("a")));
        Assert.True(center.Add(N("b")));
        Assert.False(center.Add(N("a")));
        for (var i = 0; i < NotificationCenter.Capacity + 5; i++) center.Add(N($"x{i}"));

        Assert.Equal(NotificationCenter.Capacity, center.Items.Count);
        Assert.Equal($"x{NotificationCenter.Capacity + 4}", center.Items[0].Id);
        Assert.Equal(NotificationCenter.Capacity + 7, raised);
    }

    [Fact]
    public void Unread_counts_until_marked_read()
    {
        var center = new NotificationCenter();
        center.Add(N("a"));
        center.Add(N("b"));
        Assert.Equal(2, center.Unread);
        center.MarkAllRead();
        Assert.Equal(0, center.Unread);
        Assert.All(center.Items, n => Assert.True(n.Read));
    }
}
