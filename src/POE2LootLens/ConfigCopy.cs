using Newtonsoft.Json;

namespace Poe2LootLens;

internal static class ConfigCopy
{
    public static AppConfig Clone(AppConfig source)
    {
        string json = JsonConvert.SerializeObject(source);
        return JsonConvert.DeserializeObject<AppConfig>(
                   json,
                   new JsonSerializerSettings
                   {
                       // AppConfig contains lists with non-empty defaults. Newtonsoft's default
                       // Auto behavior appends deserialized values to those defaults, which silently
                       // reset a user-selected rumor category order while opening a settings window.
                       ObjectCreationHandling = ObjectCreationHandling.Replace,
                   })
               ?? new AppConfig();
    }
}
