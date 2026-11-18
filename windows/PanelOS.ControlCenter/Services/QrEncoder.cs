using System.Text;
using QRCoder;

namespace PanelOS.ControlCenter.Services;

/// <summary>Turns the pairing text into QR modules. Rows of '1' (dark) and '0' (light), WITHOUT a quiet zone.</summary>
public interface IQrEncoder
{
    IReadOnlyList<string> Encode(string text);
}

/// <summary>
/// QRCoder only produces the module matrix (byte mode, error correction M); the drawing is QrCodeControl. Quiet
/// zone handling lives in the control, so whatever QRCoder adds is trimmed here and the matrix is exactly the symbol.
/// </summary>
public sealed class QrCoderEncoder : IQrEncoder
{
    public IReadOnlyList<string> Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Encoding.UTF8.GetBytes(text), QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix
            .Select(row => new string(row.Cast<bool>().Select(dark => dark ? '1' : '0').ToArray()))
            .ToArray();
        // A version-V symbol is 17 + 4V modules wide and starts with the 7-module finder pattern in its top-left corner.
        var quiet = Array.FindIndex(rows, row => row.Contains('1'));
        if (quiet < 0) throw new InvalidOperationException("Empty QR matrix.");
        return quiet == 0 ? rows : rows[quiet..^quiet].Select(row => row[quiet..^quiet]).ToArray();
    }
}