using System.Security.Cryptography;

namespace PanelOS.Windows.Development;

public interface IDevelopmentSessionTokenProvider
{
    string? CurrentToken { get; }

    /// <summary>
    /// The credential for this session, minting one if none exists yet.
    /// </summary>
    /// <remarks>
    /// PanelHost refuses to open its default ADB/TCP carrier without this
    /// token, and says so by pointing at "the development launcher or Control
    /// Center". Whoever launches the agent therefore has to be able to supply
    /// one, not merely read the one a previous step happened to leave behind.
    /// </remarks>
    string EnsureToken();
}

/// <summary>Keeps one ADB carrier credential in memory for the active dev session.</summary>
public sealed class DevelopmentSessionToken : IDevelopmentSessionTokenProvider
{
    public const int TokenBytes = 32;
    public const string HostEnvironmentVariable = "PANELOS_ADB_AUTH_TOKEN";
    public const string AndroidIntentExtra = "panelAdbAuthToken";

    private readonly Func<byte[]> _randomBytes;
    private readonly object _sync = new();
    private string? _currentToken;

    public DevelopmentSessionToken(Func<byte[]>? randomBytes = null)
    {
        _randomBytes = randomBytes ?? (() => RandomNumberGenerator.GetBytes(TokenBytes));
    }

    public string? CurrentToken => Volatile.Read(ref _currentToken);

    /// <summary>
    /// Returns the current credential, minting one on first use. Serialized so
    /// two callers racing to start the agent cannot mint two tokens and leave
    /// the running host holding the one nobody else knows.
    /// </summary>
    public string EnsureToken()
    {
        lock (_sync)
        {
            return CurrentToken ?? Rotate();
        }
    }

    public string Rotate()
    {
        var bytes = _randomBytes();
        if (bytes.Length != TokenBytes)
            throw new InvalidOperationException("Development token generator must return 256 bits.");
        try
        {
            var token = Convert.ToHexString(bytes);
            Volatile.Write(ref _currentToken, token);
            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Clear() => Volatile.Write(ref _currentToken, null);

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var token = CurrentToken;
        return string.IsNullOrEmpty(token)
            ? value
            : value.Replace(token, "<adb-session-token>", StringComparison.OrdinalIgnoreCase);
    }
}
