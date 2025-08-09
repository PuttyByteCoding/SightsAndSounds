using SightsAndSounds.Shared.Models;

namespace SightsAndSounds.Blazor.Services
{
    public interface IConcertService
    {
        Task<List<Concert>> GetConcertsAsync();
        Task<Concert> GetConcertByIdAsync(Guid id);
        Task CreateConcertAsync(Concert concert);
        Task UpdateConcertAsync(Concert concert);
        Task DeleteConcertAsync(Guid id);
    }
}
