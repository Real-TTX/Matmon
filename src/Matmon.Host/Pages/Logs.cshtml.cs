using System.Globalization;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

/// <summary>Admin-only in-app log viewer (Admin-gated in Program.cs). Reads the process-local
/// <see cref="InMemoryLogStore"/> ring buffer; rows are rendered client-side from <see cref="OnGetData"/> so the
/// initial load and the live auto-refresh share one render path.</summary>
public class LogsModel : PageModel
{
    private readonly InMemoryLogStore _logs;

    public LogsModel(InMemoryLogStore logs) => _logs = logs;

    [BindProperty(SupportsGet = true)] public string? Level { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public int Capacity => _logs.Capacity;

    public void OnGet() { }

    /// <summary>Live feed: newest-first entries filtered by level + text, as JSON for the client to render.</summary>
    public IActionResult OnGetData(string? level, string? q)
    {
        var items = _logs.Snapshot(ParseLevel(level), q, 500)
            .Select(e => new
            {
                ts = e.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                level = e.Level.ToString(),
                category = e.Category,
                message = e.Message,
                exception = e.Exception
            });
        return new JsonResult(new { items });
    }

    public IActionResult OnPostClear()
    {
        _logs.Clear();
        return RedirectToPage(new { Level, Q });
    }

    private static LogLevel ParseLevel(string? level) =>
        Enum.TryParse<LogLevel>(level, ignoreCase: true, out var l) ? l : LogLevel.Trace;
}
