using SightsAndSounds.Shared.Models;
using System.Net.Http.Json;

namespace SightsAndSounds.Blazor.Services
{
    public class TrackService : ITrackService
    {
        private readonly HttpClient _httpClient;
        public TrackService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri("http://localhost:5192/");
        }

        public async Task<List<Track>> GetTracksAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<Track>>("api/tracks") ?? new List<Track>();
        }
        public async Task<Track> GetTrackByIdAsync(Guid id)
        {
            return await _httpClient.GetFromJsonAsync<Track>($"api/tracks/{id}");
        }
        public async Task CreateTrackAsync(Track track)
        {
            var response = await _httpClient.PostAsJsonAsync("api/tracks", track);
            response.EnsureSuccessStatusCode();
        }
        public async Task UpdateTrackAsync(Track track)
        {
            var response = await _httpClient.PutAsJsonAsync($"api/tracks/{track.Id}", track);
            response.EnsureSuccessStatusCode();
        }
        public async Task DeleteTrackAsync(Guid id)
        {
            var response = await _httpClient.DeleteAsync($"api/tracks/{id}");
            response.EnsureSuccessStatusCode();
        }
    }
}
