namespace Matmon.Host.Services;

/// <summary>Renders a string to an inline SVG QR code (QRCoder; offline and managed - no System.Drawing, and
/// nothing leaves the box, which matters for both a TOTP secret and a wallboard link).</summary>
public static class TotpQr
{
    /// <summary>A TOTP enrolment URI. Error correction Q, because the code is photographed off a screen.</summary>
    public static string Svg(string otpauthUri) => Render(otpauthUri, 4);

    /// <summary>Any text - used for the wallboard public link, so a TV can be pointed at a phone camera.
    /// Error correction M is enough for a URL shown on a clean background and keeps the code smaller.</summary>
    public static string Render(string text, int pixelsPerModule = 4,
        QRCoder.QRCodeGenerator.ECCLevel level = QRCoder.QRCodeGenerator.ECCLevel.Q)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, level);
        return new QRCoder.SvgQRCode(data).GetGraphic(pixelsPerModule);
    }
}
