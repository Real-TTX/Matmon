namespace Matmon.Host.Ui;

/// <summary>
/// A compact status block at the top of the menu (<c>_NavInfo.cshtml</c>, styled as <c>.mm-navinfo</c> in app.css): a
/// titled link with one big number and its state word, and under it a row of small linked counts. Alerts use it today;
/// it is written generically so it can carry any "how does it stand" summary, and so it can move into MatUi one day
/// (MatUi's menu only has count badges - SHELL-14 - and a badge cannot say Error / Warning / Ack at a glance). The same
/// record and markup exist in Matmon.Cloud - keep the two in step.
/// </summary>
/// <param name="Key">Names the block for the live update (<c>data-navinfo</c>).</param>
/// <param name="Tone">error | warn | info | ok | plain - the colour of the number, the state word and the frame.</param>
/// <param name="Active">The block is the menu entry of the current page (it replaces the plain menu item).</param>
/// <param name="MenuPath">Inside the Matmon.Cloud console: the instance page the block opens in the frame, so the
/// console marks it like a menu item while the frame shows that page (<c>data-instance-menu</c>).</param>
public sealed record NavInfo(
    string Key,
    string Title,
    string Icon,
    string Href,
    int Value,
    string Tone,
    string State,
    bool Active,
    IReadOnlyList<NavInfoStat> Stats,
    string? Target = null,
    string? Description = null,
    string? MenuPath = null);

/// <summary>One small count under the big number: a link to the list it counts. A zero is drawn quietly, whatever its tone.</summary>
public sealed record NavInfoStat(string Key, string Label, int Value, string Href, string Tone, string? Title = null, string? Target = null);
