using System.ComponentModel;
using System.Globalization;

namespace Matmon.Core;

[TypeConverter(typeof(AppModeTypeConverter))]
public enum AppMode
{
    Primary = 0,
    Secondary = 1,

    /// <summary>Stateless sensor-executor service: no workspace/persistence/UI, just an authenticated
    /// HTTP API to run one sensor on demand. Matmon.Cloud drives it to run cloud sensors.</summary>
    Executor = 2
}

public sealed class AppModeTypeConverter : EnumConverter
{
    public AppModeTypeConverter()
        : base(typeof(AppMode))
    {
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "master" or "primary" => AppMode.Primary,
                "slave" or "secondary" => AppMode.Secondary,
                "executor" => AppMode.Executor,
                _ => base.ConvertFrom(context, culture, value)
            };
        }

        return base.ConvertFrom(context, culture, value);
    }
}
