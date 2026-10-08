using System.Globalization;
using System.Net;

namespace WukongBenchRunner.Reporting;

/// <summary>
/// Форматирование значений, общее для всех выводов отчёта: консоли, markdown и HTML.
/// Общее здесь специально: когда число печатается тремя способами, разойтись им проще всего,
/// а расхождение в цифрах читатель видит как ошибку данных.
/// </summary>
public static class ReportFormat
{
    public static string Num(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static string Pct(double? value) => value is { } v ? $"{v.ToString("0", CultureInfo.InvariantCulture)}%" : "н/д";

    public static string OnOff(int value) => value == 0 ? "выкл." : "вкл.";

    /// <summary>Экранирует вертикальную черту, иначе Markdown-таблица развалится.</summary>
    public static string Cell(string? value) =>
        (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>Экранирует значение для HTML: данные приходят из системных команд и могут содержать спецсимволы.</summary>
    public static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}