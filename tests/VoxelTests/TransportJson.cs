namespace SE2GameAPI.Redis;
// Compile the real voxel transport without the unrelated device gateway/engine DTOs.
internal static class RedisGateway
{
    internal static readonly System.Text.Json.JsonSerializerOptions Json=new()
    { PropertyNamingPolicy=System.Text.Json.JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true };
}
