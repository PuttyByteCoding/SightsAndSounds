using SightsAndSounds.Shared.Models;

namespace SightsAndSounds.Blazor.Services
{
    public interface ISongService
    {
        Task<List<Song>> GetSongsAsync();
        Task<Song> GetSongByIdAsync(Guid id);
        Task CreateSongAsync(Song song);
        Task UpdateSongAsync(Song song);
        Task DeleteSongAsync(Guid id);
    }
}
