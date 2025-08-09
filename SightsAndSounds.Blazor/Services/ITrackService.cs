using SightsAndSounds.Shared.Models;

namespace SightsAndSounds.Blazor.Services
{
    public interface ITrackService
    {
        Task<List<Track>> GetTracksAsync();
        Task<Track> GetTrackByIdAsync(Guid id);
        Task CreateTrackAsync(Track track);
        Task UpdateTrackAsync(Track track);
        Task DeleteTrackAsync(Guid id);
    }
}
