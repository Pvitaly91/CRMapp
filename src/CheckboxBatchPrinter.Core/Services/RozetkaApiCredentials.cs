using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class RozetkaApiCredentials
{
    public const string SetupMessage = "Збережіть API-токен Rozetka з кабінету продавця: Налаштування → Безпека API. Логін і пароль кабінету більше не використовуються.";

    public static bool ValidToken(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) &&
        value.Trim().All(c => c is > ' ' and <= '~');

    public static string RequireToken(MarketplaceCredentials? credentials) => ValidToken(credentials?.Token)
        ? credentials!.Token.Trim() : throw new InvalidOperationException(SetupMessage);
}
