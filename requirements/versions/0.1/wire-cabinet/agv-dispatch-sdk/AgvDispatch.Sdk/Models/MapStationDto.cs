using System.Text.Json.Serialization;

namespace AgvDispatch.Sdk.Models;

/// <summary>RCS 地图上的站点（只取发车需要的 id / name）。</summary>
public sealed class MapStationDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
