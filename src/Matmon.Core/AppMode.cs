using System.ComponentModel;
using System.Globalization;

namespace Matmon.Core;

[TypeConverter(typeof(AppModeTypeConverter))]
public enum AppMode
{
    Primary = 0,
    Secondary = 1
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
                _ => base.ConvertFrom(context, culture, value)
            };
        }

        return base.ConvertFrom(context, culture, value);
    }
}
