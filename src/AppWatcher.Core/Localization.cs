using System.Globalization;
using System.Resources;

namespace AppWatcher.Core;

public static class Localization
{
    private static readonly ResourceManager ResourceManager =
        new("AppWatcher.Core.Resources.Strings", typeof(Localization).Assembly);
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    public static UiLanguage CurrentLanguage { get; private set; } = UiLanguage.Auto;

    public static CultureInfo EffectiveUiCulture => CultureInfo.CurrentUICulture;

    public static void Apply(UiLanguage language)
    {
        CurrentLanguage = language;
        var culture = ResolveCulture(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static UiLanguage DetectSystemLanguage() =>
        SystemUiCulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Japanese
            : UiLanguage.English;

    public static string T(string key)
    {
        try
        {
            return ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static string StateText(AppRuntimeState value) => T($"State_{value}");
    public static string PrivilegeText(PrivilegeLevel value) => T($"Privilege_{value}");
    public static string RestartPolicyText(RestartPolicy value) => T($"RestartPolicy_{value}");
    public static string ChildProcessPolicyText(ChildProcessPolicy value) => T($"ChildProcessPolicy_{value}");
    public static string LogLevelText(AppLogLevel value) => T($"LogLevel_{value}");

    public static string EventCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var translated = T($"Event_{code}");
        return translated.StartsWith("Event_", StringComparison.Ordinal) ? code : translated;
    }

    public static string ReasonCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var translated = T($"Reason_{code}");
        return translated.StartsWith("Reason_", StringComparison.Ordinal) ? code : translated;
    }

    private static CultureInfo ResolveCulture(UiLanguage language) => language switch
    {
        UiLanguage.Japanese => CultureInfo.GetCultureInfo("ja-JP"),
        UiLanguage.English => CultureInfo.GetCultureInfo("en-US"),
        _ => DetectSystemLanguage() == UiLanguage.Japanese
            ? CultureInfo.GetCultureInfo("ja-JP")
            : CultureInfo.GetCultureInfo("en-US")
    };
}

public sealed record LocalizedOption<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
