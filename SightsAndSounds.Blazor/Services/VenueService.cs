using SightsAndSounds.Shared.Models;
using System.Net.Http.Json;

namespace SightsAndSounds.Blazor.Services
{
    public class VenueService : IVenueService
    {
        private readonly HttpClient _httpClient;
        public VenueService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri("http://localhost:5192/");
        }
        public async Task<List<Venue>> GetVenuesAsync()
        {
            var resp = await _httpClient.GetFromJsonAsync<List<Venue>>("api/venues");
            return resp ?? new List<Venue>();
        }
        public async Task<Venue> GetVenueByIdAsync(Guid id)
        {
            return await _httpClient.GetFromJsonAsync<Venue>($"api/venues/{id}");
        }
        public async Task CreateVenueAsync(Venue venue)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync("api/venues", venue);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                // Handle exception (e.g., log it, rethrow, etc.)
                throw new Exception("Error creating venue", ex);
            }
            
        }
        public async Task UpdateVenueAsync(Venue venue)
        {
            var response = await _httpClient.PutAsJsonAsync($"api/venues/{venue.Id}", venue);
            response.EnsureSuccessStatusCode();
        }
        public async Task DeleteVenueAsync(Guid id)
        {
            var response = await _httpClient.DeleteAsync($"api/venues/{id}");
            response.EnsureSuccessStatusCode();
        }
    }
}

