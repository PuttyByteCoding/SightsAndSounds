using SightsAndSounds.Shared.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SightsAndSounds.Blazor.Services
{
    public class VenueService : IVenueService
    {
        private readonly HttpClient _httpClient;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        static VenueService()
        {
            JsonOptions.Converters.Add(new JsonStringEnumConverter());
        }
        public VenueService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }
        public async Task<List<Venue>> GetVenuesAsync()
        {
            var resp = await _httpClient.GetFromJsonAsync<List<Venue>>("api/venues", JsonOptions);
            return resp ?? new List<Venue>();
        }
        public async Task<Venue> GetVenueByIdAsync(Guid id)
        {
            return await _httpClient.GetFromJsonAsync<Venue>($"api/venues/{id}", JsonOptions);
        }
        public async Task CreateVenueAsync(Venue venue)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync("api/venues", venue, JsonOptions);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
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

