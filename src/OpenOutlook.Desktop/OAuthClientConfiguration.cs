using System.Text.Json;
using OpenOutlook.Auth;

namespace OpenOutlook.Desktop;

/// <summary>Public OAuth application IDs supplied by the OpenOutlook build owner.</summary>
public sealed class OAuthClientConfiguration
{
    public const string FileName = "openoutlook-oauth.json";

    public string? MicrosoftClientId { get; }
    public string? GoogleClientId { get; }

    private OAuthClientConfiguration(string? microsoftClientId, string? googleClientId)
    {
        MicrosoftClientId = microsoftClientId;
        GoogleClientId = googleClientId;
    }

    public static OAuthClientConfiguration Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) return new OAuthClientConfiguration(null, null);
        if (new FileInfo(path).Length > 4096)
            throw new InvalidDataException("OAuth application configuration is too large.");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OAuth application configuration is invalid.");
            return new OAuthClientConfiguration(
                ReadId(json.RootElement, "microsoftClientId"),
                ReadId(json.RootElement, "googleClientId"));
        }
        catch (JsonException)
        { throw new InvalidDataException("OAuth application configuration is invalid."); }
    }

    public string? GetClientId(OAuthProvider provider) => provider switch
    {
        OAuthProvider.MicrosoftConsumers => MicrosoftClientId,
        OAuthProvider.Google => GoogleClientId,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string? ReadId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("OAuth application configuration is invalid.");
        var id = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        if (id.Length > 1024 || id.Any(char.IsControl))
            throw new InvalidDataException("OAuth application configuration is invalid.");
        return id;
    }
}
