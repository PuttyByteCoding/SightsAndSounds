using SightsAndSounds.Shared.Models;

namespace SightsAndSounds.Blazor.Services
{
    public interface IVenueService
    {
        Task<List<Venue>> GetVenuesAsync();
        Task<Venue> GetVenueByIdAsync(Guid id);
        Task CreateVenueAsync(Venue venue);
        Task UpdateVenueAsync(Venue venue);
        Task DeleteVenueAsync(Guid id);
    }
}
