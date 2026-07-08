using System.Runtime.InteropServices;
using Matmon.Host.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matmon.Host.Pages;

[AllowAnonymous]
public sealed class AboutModel : PageModel
{
    private readonly MatmonRuntimeOptions _runtimeOptions;

    public AboutModel(MatmonRuntimeOptions runtimeOptions)
    {
        _runtimeOptions = runtimeOptions;
    }

    public string Version => MatmonVersion.Current;

    public string Channel => MatmonVersion.Channel;

    public string Mode => _runtimeOptions.Mode.ToString();

    public string RuntimeVersion => $".NET {Environment.Version}";

    public string OperatingSystem => RuntimeInformation.OSDescription.Trim();

    public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();
}
