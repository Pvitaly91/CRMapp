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
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "Checkbox відхилив авторизацію. Перевірте логін і пароль у Налаштуваннях.",
        (HttpStatusCode)429 => "Checkbox тимчасово обмежив кількість запитів. Спробуйте ще раз трохи пізніше.",
        HttpStatusCode.RequestTimeout => "Checkbox не відповів вчасно. Перевірте інтернет-з'єднання.",
        _ when (int?)StatusCode >= 500 => "Сервіс Checkbox тимчасово недоступний. Спробуйте пізніше.",
        _ => string.IsNullOrWhiteSpace(ResponseMessage) ? Message : $"{Message} {ResponseMessage}"
    };
}
