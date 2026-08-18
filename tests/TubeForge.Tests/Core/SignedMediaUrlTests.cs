using TubeForge.Core.Media;
using TubeForge.Tests.Framework;

namespace TubeForge.Tests.Core;

public static class SignedMediaUrlTests
{
    [Test]
    public static void ReadsProviderExpiryStamp()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(6);
        var url = new Uri(
            $"https://rr1---sn-test.googlevideo.com/videoplayback?expire={expiry.ToUnixTimeSeconds()}&itag=251");

        Assert.Equal(expiry.ToUnixTimeSeconds(), SignedMediaUrl.GetExpiry(url)?.ToUnixTimeSeconds());
    }

    [Test]
    public static void TreatsUrlsWithoutOrWithImplausibleStampsAsUsable()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(SignedMediaUrl.GetExpiry(new Uri("https://media.test/videoplayback?itag=140")) is null);
        Assert.True(SignedMediaUrl.GetExpiry(new Uri("https://media.test/x?expire=notanumber")) is null);
        Assert.True(SignedMediaUrl.GetExpiry(new Uri("https://media.test/x?expire=42")) is null);
        Assert.True(SignedMediaUrl.IsUsable(new Uri("https://media.test/videoplayback?itag=140"), now));
    }

    [Test]
    public static void RejectsExpiredAndAlmostExpiredLinks()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Stamped(now.AddMinutes(-1));
        var almostExpired = Stamped(now + SignedMediaUrl.SafetyMargin - TimeSpan.FromMinutes(1));
        var usable = Stamped(now + SignedMediaUrl.SafetyMargin + TimeSpan.FromMinutes(1));

        Assert.False(SignedMediaUrl.IsUsable(expired, now));
        // A transfer started inside the safety margin would fail partway through instead of
        // before it begins, which is the case that leaves a half-written file behind.
        Assert.False(SignedMediaUrl.IsUsable(almostExpired, now));
        Assert.True(SignedMediaUrl.IsUsable(usable, now));
    }

    private static Uri Stamped(DateTimeOffset expiry) => new(
        $"https://rr1---sn-test.googlevideo.com/videoplayback?expire={expiry.ToUnixTimeSeconds()}&itag=251");
}
