namespace Matmon.Host.Services;

public sealed class MatmonAuthOptions
{
    // Empty by default so a bare install (no Matmon__Auth__* and no appsettings override) has no
    // pre-provisioned admin and falls through to the first-run setup wizard. Setting both via env
    // pre-provisions an admin headlessly and skips setup.
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
