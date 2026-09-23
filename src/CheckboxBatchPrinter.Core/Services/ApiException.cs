using System.Net;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class ApiException : Exception
{
    public ApiException(string message, HttpStatusCode? statusCode = null, string? responseMessage = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseMessage = responseMessage;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ResponseMessage { get; }

    public string ToUserMessage() => StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AuthenticationMessage(),
        (HttpStatusCode)429 => "Checkbox тимчасово обмежив кількість запитів. Спробуйте ще раз трохи пізніше.",
        HttpStatusCode.RequestTimeout => "Checkbox не відповів вчасно. Перевірте інтернет-з'єднання.",
        _ when (int?)StatusCode >= 500 => "Сервіс Checkbox тимчасово недоступний. Спробуйте пізніше.",
        _ => string.IsNullOrWhiteSpace(ResponseMessage) ? Message : $"{Message} {ResponseMessage}"
    };

    private string AuthenticationMessage()
    {
        // Do not display arbitrary server text here: a gateway could echo credentials.
        var reason = ResponseMessage?.Trim();
        if (reason is not null &&
            (reason.Contains("Невірний логін або пароль", StringComparison.OrdinalIgnoreCase) ||
             reason.Contains("Invalid login or password", StringComparison.OrdinalIgnoreCase)))
            return "Checkbox відхилив логін або пароль касира. Введіть особисті облікові дані касира Checkbox, а не дані входу до кабінету.";

        if (reason is not null &&
            (reason.Contains("Невірний ключ доступу", StringComparison.OrdinalIgnoreCase) ||
             reason.Contains("Invalid access key", StringComparison.OrdinalIgnoreCase)))
            return "Checkbox відхилив ключ доступу інтеграції (HTTP 403). Це не свідчить про помилку пароля касира; зверніться до підтримки Checkbox.";

        if (reason is not null &&
            (reason.Contains("деактив", StringComparison.OrdinalIgnoreCase) ||
             reason.Contains("deactivated", StringComparison.OrdinalIgnoreCase)))
            return "Checkbox відхилив доступ: касира деактивовано. Перевірте його стан у кабінеті Checkbox.";

        return $"Checkbox відхилив авторизацію (HTTP {(int)StatusCode!}). Перевірте облікові дані та стан касира Checkbox.";
    }
}
