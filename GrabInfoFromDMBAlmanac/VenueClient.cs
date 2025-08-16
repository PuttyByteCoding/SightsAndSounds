using System.Text.Json;
using System.Net.Http.Json;     // <- for PostAsJsonAsync
using System.Text.Json.Serialization;
using SightsAndSounds.Shared.Models;


namespace GrabInfoFromDMBAlmanac
{
    public class VenueClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        public VenueClient(string baseUrl, bool acceptAnyCert = true)
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl)
            };

            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters =
                {
                    new JsonStringEnumConverter()
                },
            };


        }

        public async Task PostVenueAsync(Venue venue, string path="api/venues", CancellationToken cancellationToken = default)
        {
            if (venue == null) throw new ArgumentNullException(nameof(venue));
            using var response = await _httpClient.PostAsJsonAsync(path, venue, _jsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
