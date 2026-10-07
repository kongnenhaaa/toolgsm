using gsm.Models;

namespace gsm.Services;

public static class TelegramPhoneWhitelist
{
    public static IReadOnlySet<string> Parse(string? value)
    {
        return (value ?? string.Empty)
            .Split(
                [',', ';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
            .Select(MyVnptService.NormalizePhone)
            .Where(phone => phone.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string NormalizeList(string? value) =>
        string.Join(",", Parse(value).OrderBy(phone => phone));

    public static bool IsAllowed(AppSettings? settings, string? phone)
    {
        if (settings?.EnableTelegramPhoneWhitelist != true) return true;

        string normalizedPhone = MyVnptService.NormalizePhone(phone);
        return normalizedPhone.Length > 0
            && Parse(settings.TelegramPhoneWhitelist)
                .Contains(normalizedPhone);
    }

    public static bool IsAllowed(
        TelegramRouteSettings route,
        string? phone)
    {
        if (!route.EnablePhoneWhitelist) return true;

        string normalizedPhone = MyVnptService.NormalizePhone(phone);
        return normalizedPhone.Length > 0
            && Parse(route.PhoneWhitelist).Contains(normalizedPhone);
    }

    public static IReadOnlyList<TelegramRouteSettings> GetEligibleRoutes(
        AppSettings? settings,
        string? phone)
    {
        if (settings == null) return Array.Empty<TelegramRouteSettings>();

        IEnumerable<TelegramRouteSettings> routes =
            settings.TelegramRoutes ?? [];
        if (!routes.Any()
            && (!string.IsNullOrWhiteSpace(settings.TelegramBotToken)
                || !string.IsNullOrWhiteSpace(settings.TelegramChatIds)
                || !string.IsNullOrWhiteSpace(settings.TelegramChatId)))
        {
            routes =
            [
                new TelegramRouteSettings
                {
                    Id = "legacy-primary",
                    BotToken = settings.TelegramBotToken,
                    ChatIds = !string.IsNullOrWhiteSpace(
                            settings.TelegramChatIds)
                        ? settings.TelegramChatIds
                        : settings.TelegramChatId,
                    EnablePhoneWhitelist =
                        settings.EnableTelegramPhoneWhitelist,
                    PhoneWhitelist = settings.TelegramPhoneWhitelist
                }
            ];
        }

        return routes
            .Where(route =>
                !string.IsNullOrWhiteSpace(route.BotToken)
                && !string.IsNullOrWhiteSpace(route.ChatIds)
                && IsAllowed(route, phone))
            .GroupBy(
                route =>
                    $"{route.BotToken.Trim()}\n{route.ChatIds.Trim()}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }
}
