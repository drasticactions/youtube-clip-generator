using System.Text.Json.Serialization;
using YoutubeExplode.Videos;

namespace YouTubeClipGenerator;


[JsonSourceGenerationOptions(
WriteIndented = true,
PropertyNameCaseInsensitive = true,
PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull | JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(ChapterDescription[]))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}