namespace Pipedl.Domain.Entities;

public class Track
{
    public string SpotifyId { get; set; } = string.Empty;
    public string PlaylistId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int Duration { get; set; }
    public int Downloaded { get; set; } = 0;
}
