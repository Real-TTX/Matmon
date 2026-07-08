namespace Matmon.Host.Services;

/// <summary>Renders an otpauth:// URI to an inline SVG QR (QRCoder; offline, managed - no System.Drawing).</summary>
public static class TotpQr
{
    public static string Svg(string otpauthUri)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(otpauthUri, QRCoder.QRCodeGenerator.ECCLevel.Q);
        return new QRCoder.SvgQRCode(data).GetGraphic(4);
    }
}
