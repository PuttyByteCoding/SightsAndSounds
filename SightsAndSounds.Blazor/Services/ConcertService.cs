using SightsAndSounds.Shared.Models;
using System.Net.Http.Json;

namespace SightsAndSounds.Blazor.Services
{
    public class ConcertService : IConcertService
    {
        private readonly HttpClient _httpClient;
        
        public ConcertService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri("http://localhost:5192/");
        }
        
        public async Task<List<Concert>> GetConcertsAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<Concert>>("api/concerts") ?? new List<Concert>();
        }
        public async Task<Concert> GetConcertByIdAsync(Guid id)
        {
            return await _httpClient.GetFromJsonAsync<Concert>($"api/concerts/{id}");
        }
        public async Task CreateConcertAsync(Concert concert)
        {
            var response = await _httpClient.PostAsJsonAsync("api/concerts", concert);
            response.EnsureSuccessStatusCode();
        }
        public async Task UpdateConcertAsync(Concert concert)
        {
            var response = await _httpClient.PutAsJsonAsync($"api/concerts/{concert.Id}", concert);
            response.EnsureSuccessStatusCode();
        }
        public async Task DeleteConcertAsync(Guid id)
        {
            var response = await _httpClient.DeleteAsync($"api/concerts/{id}");
            response.EnsureSuccessStatusCode();
        }
    }
}
