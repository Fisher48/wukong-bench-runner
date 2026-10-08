using System.Globalization;
using System.Text;
using WukongBenchRunner.Profiles;
using WukongBenchRunner.Results;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner.Reporting;

/// <summary>
/// Отчёт в виде одной HTML-страницы. Нужен, чтобы результат можно было посмотреть без .NET,
/// без моноширинного терминала и без рендера Markdown: просто открыть файл в браузере.
/// Страница полностью автономна - ни одного внешнего ресурса, ни одного скрипта, только
/// встроенные стили и inline-SVG, - поэтому работает офлайн и ничего не подгружает.
/// </summary>
public static class HtmlReport
{
    /// <summary>Сколько колонок строим на графике. Данных тысячи, а столбцов нужно десятки.</summary>
    private const int ChartColumns = 600;

    private const int ChartWidth = 960;
    private const int ChartHeight = 220;
    private const int ChartPadLeft = 62;
    private const int ChartPadRight = 12;
    private const int ChartPadTop = 14;
    private const int ChartPadBottom = 26;

    public static string Html(RunReport report)
    {
        var page = new StringBuilder();

        page.AppendLine("<!DOCTYPE html>");
        page.AppendLine("<html lang=\"ru\">");
        page.AppendLine("<head>");
        page.AppendLine("<meta charset=\"utf-8\">");
        page.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        page.AppendLine($"<title>Black Myth: Wukong Benchmark Tool - прогон {report.StartedAt:yyyy-MM-dd HH:mm:ss}</title>");
        page.AppendLine("<style>");
        page.AppendLine(Style);
        page.AppendLine("</style>");
        page.AppendLine("</head>");
        page.AppendLine("<body>");
        page.AppendLine("<main>");

        page.AppendLine("<h1>Black Myth: Wukong Benchmark Tool - автоматический прогон</h1>");
        page.AppendLine($"<p class=\"lead\">Дата: {E(report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}<br>"
            + $"Система: {E(report.Installation.ProtonVersion)} (Proton), ввод: {E(report.Installation.InputBackend)}</p>");

        AppendSystem(page, report.System);
        AppendResults(page, report.Passes);

        foreach (var pass in report.Passes)
            AppendPass(page, pass);

        AppendNotes(page);
        page.AppendLine("</main>");
        page.AppendLine("</body>");
        page.AppendLine("</html>");

        return page.ToString();
    }

    private static string E(string? value) => ReportFormat.Enc(value);

    private static void AppendSystem(StringBuilder page, SystemSnapshot system)
    {
        page.AppendLine("<h2>Характеристики компьютера</h2>");
        page.AppendLine("<table class=\"kv\"><tbody>");

        foreach (var (key, value) in system.Rows)
            page.AppendLine($"<tr><th>{E(key)}</th><td>{E(value)}</td></tr>");

        page.AppendLine("</tbody></table>");
    }

    private static void AppendResults(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        page.AppendLine("<h2>Результаты</h2>");
        page.AppendLine("<table><thead><tr>"
            + "<th>Тест</th><th>FPS (средний)</th><th>FPS 95%</th><th>FPS 1% low</th>"
            + "<th>FPS мин.</th><th>FPS макс.</th><th>Кадров</th><th>Ср. кадр, мс</th>"
            + "</tr></thead><tbody>");

        foreach (var pass in passes)
        {
            var stats = pass.Result.FrameStats;
            page.AppendLine(
                $"<tr><th scope=\"row\">{E(pass.Title)}</th>"
                + $"<td>{N(pass.Result.FpsAverage)}</td><td>{N(pass.Result.Fps95)}</td><td>{N(stats.OnePercentLowFps)}</td>"
                + $"<td>{N(pass.Result.FpsMinimum)}</td><td>{N(pass.Result.FpsMaximum)}</td>"
                + $"<td>{stats.Frames}</td><td>{N(stats.AverageFrameMs)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");

        AppendSeries(page, passes);
        AppendFrameTimeTable(page, passes);
        AppendCharts(page, passes);
        AppendBenchmarkJson(page, passes);
        AppendLoad(page, passes);
        AppendHardware(page, passes);
    }

    private static void AppendSeries(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        var withRepeats = passes.Where(pass => pass.AllRuns.Count > 1).ToList();
        if (withRepeats.Count == 0)
            return;

        page.AppendLine("<h3>Серия прогонов</h3>");
        page.AppendLine("<p class=\"note\">Одиночный замер неустойчив, поэтому их несколько.</p>");
        page.AppendLine("<table><thead><tr>"
            + "<th>Тест</th><th>Прогонов</th><th>FPS каждого прогона</th><th>Медиана</th>"
            + "<th>Минимум</th><th>Максимум</th><th>Разброс</th>"
            + "</tr></thead><tbody>");

        foreach (var pass in withRepeats)
        {
            var values = pass.AllRuns.Select(r => r.FpsAverage).OrderBy(v => v).ToList();
            var listed = string.Join(", ", values.Select(N));
            page.AppendLine(
                $"<tr><th scope=\"row\">{E(pass.Title)}</th><td>{values.Count}</td><td>{E(listed)}</td>"
                + $"<td>{N(ResultParser.Median(values))}</td><td>{N(values[0])}</td>"
                + $"<td>{N(values[^1])}</td><td>{N(values[^1] - values[0])}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    private static void AppendFrameTimeTable(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        var withFrameTimes = passes.Where(pass => pass.Result.FrameStats.MedianCpuFrameMs is not null).ToList();
        if (withFrameTimes.Count == 0)
            return;

        page.AppendLine("<h3>Время кадра по данным бенчмарка (медиана)</h3>");
        page.AppendLine("<table><thead><tr>"
            + "<th>Тест</th><th>CPUFrameTime, мс</th><th>GPUFrameTime, мс</th><th>Длина кадра, мс</th>"
            + "</tr></thead><tbody>");

        foreach (var pass in withFrameTimes)
        {
            var stats = pass.Result.FrameStats;
            page.AppendLine(
                $"<tr><th scope=\"row\">{E(pass.Title)}</th><td>{N(stats.MedianCpuFrameMs ?? 0)}</td>"
                + $"<td>{N(stats.MedianGpuFrameMs ?? 0)}</td><td>{N(stats.AverageFrameMs)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
        page.AppendLine("<p class=\"note\"><code>CPUFrameTime</code> под Proton настоящий, а <code>GPUFrameTime</code> "
            + "повторяет обратную величину FPS, то есть это длина кадра, а не работа видеокарты. "
            + "Определить узкое место по нему нельзя.</p>");
    }

    /// <summary>
    /// График длительности кадра по номеру кадра. Данных тысячи, поэтому сначала свожу их
    /// в колонки и беру в каждой максимум: среднее по колонке съело бы просадки, а именно
    /// просадки и есть то, ради чего график смотрят.
    /// </summary>
    private static void AppendCharts(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        foreach (var pass in passes)
        {
            var times = pass.Result.FrameStats.FrameTimes;
            if (times is not { Count: > 1 })
                continue;

            page.AppendLine($"<h3>Длительность кадра по ходу прогона: {E(pass.Title)}</h3>");

            var sorted = times.OrderBy(v => v).ToList();
            page.AppendLine("<table class=\"stats\"><tbody>");
            page.AppendLine($"<tr><th>Кадров</th><td>{times.Count}</td>"
                + $"<th>Медиана, мс</th><td>{N(ResultParser.Median(sorted))}</td>"
                + $"<th>Минимум, мс</th><td>{N(sorted[0])}</td>"
                + $"<th>1% low, мс</th><td>{N(1000.0 / pass.Result.FrameStats.OnePercentLowFps)}</td>"
                + $"<th>Максимум, мс</th><td>{N(sorted[^1])}</td></tr>");
            page.AppendLine("</tbody></table>");

            page.AppendLine(Chart(pass.Title, times, ResultParser.Median(sorted)));
            page.AppendLine("<p class=\"note\">По вертикали - длительность кадра в миллисекундах, по горизонтали - "
                + "порядковый номер кадра. Пунктир - медиана. Пики означают просадки, ровные участки - стабильный кадр.</p>");
        }
    }

    private static string Chart(string title, IReadOnlyList<double> times, double medianMs)
    {
        var columns = ColumnMaxima(times, ChartColumns);

        // Масштаб берём по исходным кадрам, а не по свёрнутым колонкам: иначе ось начиналась бы
        // не с настоящего минимума и не совпадала бы с числами в таблице рядом с графиком.
        var maxValue = times.Max();
        var minValue = times.Min();
        var span = Math.Max(0.001, maxValue - minValue);
        var plotWidth = ChartWidth - ChartPadLeft - ChartPadRight;
        var plotHeight = ChartHeight - ChartPadTop - ChartPadBottom;

        double X(int index) => ChartPadLeft + (columns.Count == 1 ? 0 : plotWidth * index / (double)(columns.Count - 1));
        double Y(double value) => ChartPadTop + plotHeight - (value - minValue) / span * plotHeight;

        var points = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
            points.Append($"{X(i).ToString("0.#", CultureInfo.InvariantCulture)},{Y(columns[i]).ToString("0.#", CultureInfo.InvariantCulture)} ");

        var grid = new StringBuilder();
        for (var step = 0; step <= 4; step++)
        {
            var value = minValue + span * step / 4;
            var y = Y(value);
            grid.Append($"<line class=\"grid\" x1=\"{ChartPadLeft}\" y1=\"{Fmt(y)}\" x2=\"{ChartWidth - ChartPadRight}\" y2=\"{Fmt(y)}\" />");
            grid.Append($"<text class=\"axis\" x=\"{ChartPadLeft - 6}\" y=\"{Fmt(y + 4)}\" text-anchor=\"end\">{Fmt(value)}</text>");
        }

        return "<figure class=\"chart\">"
            + $"<svg viewBox=\"0 0 {ChartWidth} {ChartHeight}\" width=\"100%\" role=\"img\" preserveAspectRatio=\"xMidYMid meet\">"
            + $"<title>{E(title)}: длительность кадра по ходу прогона</title>"
            + grid
            + $"<line class=\"median\" x1=\"{ChartPadLeft}\" y1=\"{Fmt(Y(medianMs))}\" x2=\"{ChartWidth - ChartPadRight}\" y2=\"{Fmt(Y(medianMs))}\" />"
            + $"<polyline class=\"line\" points=\"{points.ToString().TrimEnd()}\" />"
            + $"<text class=\"axis\" x=\"{ChartPadLeft}\" y=\"{ChartHeight - 8}\">кадр 0</text>"
            + $"<text class=\"axis\" x=\"{ChartWidth - ChartPadRight}\" y=\"{ChartHeight - 8}\" text-anchor=\"end\">кадр {times.Count - 1}</text>"
            + "</svg></figure>";
    }

    /// <summary>Сводит тысячи кадров в колонки, оставляя худший кадр каждой колонки.</summary>
    public static IReadOnlyList<double> ColumnMaxima(IReadOnlyList<double> values, int columns)
    {
        if (values.Count <= columns)
            return values.ToList();

        var result = new List<double>(columns);
        var bucket = values.Count / (double)columns;

        for (var i = 0; i < columns; i++)
        {
            var from = (int)Math.Floor(i * bucket);
            var to = (int)Math.Ceiling((i + 1) * bucket);
            to = Math.Clamp(to, from + 1, values.Count);

            var worst = 0.0;
            for (var j = from; j < to; j++)
                worst = Math.Max(worst, values[j]);

            result.Add(worst);
        }

        return result;
    }

    private static void AppendBenchmarkJson(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        page.AppendLine("<h3>Дополнительно из JSON бенчмарка</h3>");
        page.AppendLine("<table><thead><tr>"
            + "<th>Тест</th><th>Разрешение</th><th>Масштаб рендера, %</th><th>Качество</th>"
            + "<th>Трассировка</th><th>Апскейлер</th><th>Генерация кадров</th><th>Версия игры</th>"
            + "</tr></thead><tbody>");

        foreach (var pass in passes)
        {
            var applied = pass.Result.Applied;
            page.AppendLine(
                $"<tr><th scope=\"row\">{E(pass.Title)}</th><td>{E(applied.ScreenResolution)}</td>"
                + $"<td>{applied.RenderScalePercent}</td><td>{applied.QualityLevel}</td>"
                + $"<td>{ReportFormat.OnOff(applied.RayTracing)}</td><td>{ReportFormat.OnOff(applied.Upscaler)}</td>"
                + $"<td>{ReportFormat.OnOff(applied.FrameGeneration)}</td><td>{E(pass.Result.GameVersion)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    private static void AppendLoad(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        if (!passes.Any(pass => pass.Load is { Available: true }))
            return;

        page.AppendLine("<h3>Загрузка CPU/GPU во время прогона</h3>");
        page.AppendLine("<p class=\"note\">Важно: <code>gpu_busy_percent</code> у AMD показывает занятость любого движка GPU, "
            + "а не его загрузку, поэтому 90+% в CPU-тесте не означает, что видеокарта ограничивает кадр.</p>");
        page.AppendLine("<table><thead><tr>"
            + "<th>Тест</th><th>CPU средняя</th><th>CPU пик</th><th>GPU средняя</th>"
            + "<th>GPU пик</th><th>CPU окно 60 с</th><th>GPU окно 60 с</th>"
            + "</tr></thead><tbody>");

        foreach (var pass in passes)
        {
            var load = pass.Load;
            page.AppendLine(
                $"<tr><th scope=\"row\">{E(pass.Title)}</th>"
                + $"<td>{P(load?.AverageCpuBusyPercent)}</td><td>{P(load?.PeakCpuBusyPercent)}</td>"
                + $"<td>{P(load?.AverageGpuBusyPercent)}</td><td>{P(load?.PeakGpuBusyPercent)}</td>"
                + $"<td>{P(load?.MeasureWindowCpuBusyPercent)}</td><td>{P(load?.MeasureWindowGpuBusyPercent)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
        page.AppendLine("<p class=\"note\">Измерено инструментом на Linux: <code>/proc/stat</code> и <code>gpu_busy_percent</code>. "
            + "«Окно 60 с» - самое нагруженное минутное окно прогона, то есть фаза самого измерения.</p>");
    }

    private static void AppendHardware(StringBuilder page, IReadOnlyList<PassReport> passes)
    {
        page.AppendLine("<h3>Данные о железе по версии самого бенчмарка</h3>");
        page.AppendLine("<table><thead><tr>"
            + "<th>Тест</th><th>CPU</th><th>GPU</th><th>Драйвер</th><th>RAM</th><th>VRAM</th>"
            + "</tr></thead><tbody>");

        foreach (var pass in passes)
        {
            var result = pass.Result;
            page.AppendLine(
                $"<tr><th scope=\"row\">{E(pass.Title)}</th><td>{E(result.CpuModel)}</td><td>{E(result.GpuModel)}</td>"
                + $"<td>{E(result.GpuDriverVersion)}</td><td>{E(result.SystemMemory)}</td><td>{E(result.VideoMemory)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    private static void AppendPass(StringBuilder page, PassReport pass)
    {
        page.AppendLine($"<h2>{E(pass.Title)}: применённые настройки</h2>");
        page.AppendLine("<table class=\"kv\"><tbody>");

        foreach (var (parameter, value) in pass.Settings)
            page.AppendLine($"<tr><th>{E(parameter)}</th><td>{E(value)}</td></tr>");

        page.AppendLine("</tbody></table>");
        page.AppendLine("<h3>Почему именно так</h3>");
        page.AppendLine("<ul class=\"why\">");

        foreach (var reason in pass.Rationale)
            page.AppendLine($"<li>{E(reason)}</li>");

        page.AppendLine("</ul>");

        if (!string.IsNullOrEmpty(pass.RawCopyPath))
            page.AppendLine($"<p class=\"note\">Сырой файл результата рядом с отчётом: <code>{E(Path.GetFileName(pass.RawCopyPath))}</code></p>");
    }

    private static void AppendNotes(StringBuilder page)
    {
        page.AppendLine("<h2>Примечания</h2>");
        page.AppendLine("<ul class=\"notes\">");
        page.AppendLine("<li><code>CPUAvg</code>/<code>GPUAvg</code> из JSON бенчмарка под Proton всегда равны 1%: счётчики "
            + "Windows Performance Counters недоступны из wine, поэтому загрузка CPU/GPU из отчёта бенчмарка не приводится.</li>");
        page.AppendLine("<li><code>CPUFrameTime</code> в JSON настоящий, а <code>GPUFrameTime</code> повторяет обратную величину FPS, "
            + "то есть это длина кадра, а не работа видеокарты.</li>");
        page.AppendLine("<li>Инструмент после получения результата принудительно закрывает процесс бенчмарка, чтобы игра не "
            + "переписала <code>GameUserSettings.ini</code>; исходный конфиг восстанавливается из резервной копии и сверяется побайтно.</li>");
        page.AppendLine("<li>Проверка изоляции процессора сделана контрольным прогоном: при падении масштаба рендера с 50% до 25% "
            + "результат упал внутрь разброса серии, то есть работа видеокарты не держит кадр. При этом <code>CPUFrameTime</code> "
            + "заметно меньше длины кадра, поэтому эти FPS измеряют конвейер Linux + Proton целиком, а не процессор в одиночку.</li>");
        page.AppendLine("</ul>");
    }

    private static string N(double value) => ReportFormat.Num(value);

    private static string P(double? value) => ReportFormat.Pct(value);

    private static string Fmt(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private const string Style = """
        :root { color-scheme: light dark; }
        * { box-sizing: border-box; }
        body {
          margin: 0;
          padding: 2rem 1.25rem 4rem;
          font: 15px/1.55 -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
          color: #1b1f24;
          background: #ffffff;
        }
        main { max-width: 1080px; margin: 0 auto; }
        h1 { font-size: 1.6rem; margin: 0 0 .35rem; }
        h2 { font-size: 1.2rem; margin: 2.25rem 0 .6rem; padding-bottom: .3rem; border-bottom: 1px solid #d8dee4; }
        h3 { font-size: 1rem; margin: 1.5rem 0 .5rem; color: #37414d; }
        .lead { color: #57606a; margin: 0 0 1.5rem; }
        .note { color: #57606a; font-size: .875rem; margin: .4rem 0 .9rem; }
        table { border-collapse: collapse; width: 100%; margin: .35rem 0 1rem; font-size: .9rem; }
        th, td { border: 1px solid #d8dee4; padding: .4rem .6rem; text-align: left; }
        thead th { background: #f6f8fa; white-space: nowrap; }
        tbody th[scope="row"] { font-weight: 600; white-space: nowrap; }
        table.kv th { width: 18rem; background: #f6f8fa; font-weight: 500; }
        table.stats { width: auto; }
        table.stats th { background: #f6f8fa; font-weight: 500; }
        ul.why, ul.notes { margin: .3rem 0 1rem; padding-left: 1.2rem; }
        ul.why li, ul.notes li { margin: .3rem 0; }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: .875em; }
        figure.chart { margin: .5rem 0 0; }
        svg { display: block; background: #fbfcfd; border: 1px solid #d8dee4; border-radius: 4px; }
        polyline.line { fill: none; stroke: #1f6feb; stroke-width: 1; stroke-linejoin: round; }
        line.grid { stroke: #e6ebf1; stroke-width: 1; }
        line.median { stroke: #b0471f; stroke-width: 1; stroke-dasharray: 5 4; }
        text.axis { fill: #57606a; font-size: 11px; }
        @media (prefers-color-scheme: dark) {
          body { color: #d8dee4; background: #0f1319; }
          h2 { border-bottom-color: #2c333c; }
          .lead, .note, text.axis { color: #9aa5b1; }
          th, td { border-color: #2c333c; }
          thead th, table.kv th, table.stats th { background: #171c23; }
          svg { background: #141920; border-color: #2c333c; }
          line.grid { stroke: #232a33; }
        }
        """;
}