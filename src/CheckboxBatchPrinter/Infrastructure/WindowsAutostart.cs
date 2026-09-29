using Microsoft.Win32;
using System.IO;

namespace CheckboxBatchPrinter.Infrastructure;

internal sealed class WindowsAutostart(string channel)
{
    private readonly string _valueName = channel == "Production" ? "CRMapp-Production" : "CRMapp-Development";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string ExpectedCommand
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(Path.GetFileNameWithoutExtension(path), "CheckboxBatchPrinter", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Автозапуск можна налаштувати лише із зібраного CheckboxBatchPrinter.exe.");
            return $"\"{Path.GetFullPath(path)}\" --background";
        }
    }
    public string? RegisteredCommand
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(_valueName) as string; }
    }
    public bool IsMismatched => !string.Equals(RegisteredCommand, ExpectedCommand, StringComparison.OrdinalIgnoreCase);

    public void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(_valueName, ExpectedCommand, RegistryValueKind.String);
        else key.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
