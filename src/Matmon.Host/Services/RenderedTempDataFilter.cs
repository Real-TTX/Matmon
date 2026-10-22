using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Matmon.Host.Services;

/// <summary>
/// A message shown NOW must not be shown again on the next page. A <c>[TempData]</c> property ("Saved.", an error) is
/// meant to survive a redirect - but ASP.NET saves it just the same when the handler renders the page directly
/// (<c>return Page()</c>, the usual way back to a form with a validation error or a test result), so the same message
/// turned up a second time on whatever page was opened next. When a handler renders its page, this filter clears the
/// page's TempData properties once the page has been written and before TempData is saved: the message is shown
/// exactly once either way. A redirect is left alone - that is what TempData is for.
/// </summary>
/// <remarks>
/// Order matters and is guaranteed: TempData is saved from a <c>Response.OnStarting</c> callback registered by a
/// resource filter (earlier), callbacks run last-registered-first, and a Razor page is buffered, so the response starts
/// only after the whole page - message included - has been rendered.
/// </remarks>
public sealed class RenderedTempDataFilter : IAsyncPageFilter
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertiesByType = new();

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        // null = a void handler (OnGet), which renders the page too.
        if (executed.Result is not (null or PageResult) || context.HandlerInstance is not { } handler)
        {
            return;
        }

        var properties = TempDataProperties(handler.GetType());
        if (properties.Length == 0)
        {
            return;
        }

        context.HttpContext.Response.OnStarting(() =>
        {
            foreach (var property in properties)
            {
                property.SetValue(handler, null);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>The handler's <c>[TempData]</c> properties that can be cleared (reference or nullable types).</summary>
    public static PropertyInfo[] TempDataProperties(Type type) => PropertiesByType.GetOrAdd(type, static candidate => candidate
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.IsDefined(typeof(TempDataAttribute), inherit: true)
            && property.CanWrite
            && (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null))
        .ToArray());
}
