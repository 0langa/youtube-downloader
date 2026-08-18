namespace TubeForge.YouTube.Player;

internal sealed record YouTubeClientProfile(
    string Name,
    string NumericId,
    string Version,
    string UserAgent,
    int? AndroidSdkVersion = null,
    string? DeviceMake = null,
    string? DeviceModel = null,
    string? OsName = null,
    string? OsVersion = null,
    bool IsEmbedded = false)
{
    public static YouTubeClientProfile VisionOs { get; } = new(
        "VISIONOS",
        "101",
        "1.02",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 " +
        "(KHTML, like Gecko) Version/26.0 Safari/605.1.15",
        DeviceMake: "Apple",
        DeviceModel: "RealityDevice17,1",
        OsName: "visionOS",
        OsVersion: "26.5.23O471");

    public static YouTubeClientProfile AndroidVr { get; } = new(
        "ANDROID_VR",
        "28",
        "1.65.10",
        "com.google.android.apps.youtube.vr.oculus/1.65.10 " +
        "(Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
        AndroidSdkVersion: 32,
        DeviceMake: "Oculus",
        DeviceModel: "Quest 3",
        OsName: "Android",
        OsVersion: "12L");

    public static YouTubeClientProfile WebEmbedded { get; } = new(
        "WEB_EMBEDDED_PLAYER",
        "56",
        "2.20260708.00.00",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36",
        IsEmbedded: true);

    public static YouTubeClientProfile Tv { get; } = new(
        "TVHTML5",
        "7",
        "7.20260707.07.00",
        "Mozilla/5.0 (ChromiumStylePlatform) Cobalt/25.lts.30.1034943-gold " +
        "(unlike Gecko), Unknown_TV_Unknown_0/Unknown (Unknown, Unknown)");

    public static YouTubeClientProfile Ios { get; } = new(
        "IOS",
        "5",
        "20.29.6",
        "com.google.ios.youtube/20.29.6 (iPhone16,2; U; CPU iOS 18_5 like Mac OS X)",
        DeviceMake: "Apple",
        DeviceModel: "iPhone16,2",
        OsName: "iOS",
        OsVersion: "18.5.22F76");

    public static YouTubeClientProfile Android { get; } = new(
        "ANDROID",
        "3",
        "21.26.364",
        "com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip",
        AndroidSdkVersion: 30,
        OsName: "Android",
        OsVersion: "11");
}
