using Microsoft.JSInterop;
using System.Text.Json;

namespace HexTailSharp.Persistence;

public sealed class BrowserLocalStoragePersistence(IJSRuntime jsRuntime) : IAppPersistence
{
    public const string StorageKey = "hextail.session.v1";

    public async ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", cancellationToken, StorageKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return AppConfigJson.Deserialize(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ValueTask SaveAsync(AppConfig config, CancellationToken cancellationToken = default) =>
        new(jsRuntime.InvokeVoidAsync("localStorage.setItem", cancellationToken, StorageKey, AppConfigJson.Serialize(config)).AsTask());
}
