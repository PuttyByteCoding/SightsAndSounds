using SightsAndSounds.Shared.Models;
using System.Net.Http.Json;

namespace SightsAndSounds.Blazor.Services
{
    public class SongService : ISongService
    {
        private readonly HttpClient _httpClient;

        public SongService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri("http://localhost:5192/");
        }
        public async Task<List<Song>> GetSongsAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<Song>>("api/songs") ?? new List<Song>();
        }
        public async Task<Song> GetSongByIdAsync(Guid id)
        {
            return await _httpClient.GetFromJsonAsync<Song>($"api/songs/{id}");
        }
        public async Task CreateSongAsync(Song song)
        {
            var response = await _httpClient.PostAsJsonAsync("api/songs", song);
            response.EnsureSuccessStatusCode();
        }
        public async Task UpdateSongAsync(Song song)
        {
            var response = await _httpClient.PutAsJsonAsync($"api/songs/{song.Id}", song);
            response.EnsureSuccessStatusCode();
        }
        public async Task DeleteSongAsync(Guid id)
        {
            var response = await _httpClient.DeleteAsync($"api/songs/{id}");
            response.EnsureSuccessStatusCode();
        }
    }
}
