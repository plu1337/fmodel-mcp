using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FModel.Mcp;

internal static class ProtocolJson
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy(false, false) }
    };
    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);
}
