using HtmlAgilityPack;
using SightsAndSounds.Shared.Models;
using System.Text.RegularExpressions;
using Serilog;
using GrabInfoFromDMBAlmanac;

namespace AlmanacScraper
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();


            if (args.Length == 0)
            {
                Console.WriteLine("Usage: AlmanacScraper <url>");
                return 1;
            }

            var url = args[0];
            try
            {
                Log.Information("Fetching data from {Url}", url);
                var html = await FetchAsync(url);
                var baseUri = new Uri(url);
                var pageNumber = 0;
                Log.Information("Parsing HTML content from {Url}", url);

                while (true)
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var sections = ParseSections(html, baseUri);

                    // Process sections on this page
                    foreach (var sec in sections)
                    {
                        pageNumber++;
                        Log.Information("Processing section: {Title} from page number: {PageNumber}", sec.Title ?? "", pageNumber);
                        var venue = new Venue();
                        if (sec.Title != null)
                        {
                            var cityAndState = sec.Title.Split(",");
                            if (cityAndState.Length != 2)
                            {
                                Console.Error.WriteLine($"Invalid section title format: '{sec.Title}'");
                                continue;
                            }
                            venue.City = cityAndState[0].Trim();
                            venue.State = cityAndState[1].Trim();
                        }
                        foreach (var item in sec.Items)
                        {
                            venue.DmbAlmanacUrl = item.Href;
                            venue.DmbAlmanacId = TryExtractVid(item.Href) ?? -1;
                            venue.Name = item.FirstText;
                            venue.SetVenueTypeFromString(item.SecondAText);
                        }
                        var client = new VenueClient("http://localhost:5192/");
                        await client.PostVenueAsync(venue /*, "api/venues" if you need a path */);
                        Log.Information("Extracted Venue: {Name}, {City}, {State}, Type: {Type}, DMB ID: {DmbId}, URL: {Url}",
                            venue.Name, venue.City, venue.State, venue.Type, venue.DmbAlmanacId, venue.DmbAlmanacUrl);
                    }

                    // wait before fetching next page
                    await Task.Delay(TimeSpan.FromSeconds(10));

                    // try to get next page
                    var nextHtml = await FetchNextPageAsync(baseUri, doc);
                    if (nextHtml == null)
                        break; // no more pages
                    html = nextHtml;
                }


                return 0;
            }
            catch (Exception ex)
            {
                Log.Warning(ex.Message);
                return 2;
            }
        }

        private static async Task<string> FetchAsync(string url)
        {
            using var http = new HttpClient();
            // Some of these pages are old ASP.NET and can be sensitive to headers
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AlmanacScraper/1.0)");
            return await http.GetStringAsync(url);
        }

        private static List<Section> ParseSections(string html, Uri baseUri)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Pick the 50%-width data table with class 'threedeetable' (the big list)
            var dataTable = doc.DocumentNode
                .SelectSingleNode("//table[@class='threedeetable' and @width='50%']");

            if (dataTable == null)
                throw new InvalidOperationException("Could not find the data table.");

            var rows = dataTable.SelectNodes("./tr") ?? new HtmlNodeCollection(null);

            var sections = new List<Section>();
            var currentSection = new Section(); // start empty until we hit first header

            foreach (var tr in rows)
            {
                // Header row? looks like: <tr><td colspan=...><span class='smallreportlink'>...</span></td></tr>
                var headerSpan = tr.SelectSingleNode(".//td/span[contains(concat(' ', normalize-space(@class), ' '), ' smallreportlink ')]");
                if (headerSpan != null)
                {
                    // Commit the previous section if it has items
                    if (currentSection.Items.Count > 0)
                        sections.Add(currentSection);

                    currentSection = new Section
                    {
                        Title = Normalize(headerSpan.InnerText)
                    };

                    continue;
                }

                // Data row? exactly two <td> cells
                var tds = tr.SelectNodes("./td");
                if (tds == null || tds.Count != 2) continue;

                // First <td>: get the first <a>, read href + text
                var a1 = tds[0].SelectSingleNode(".//a");
                if (a1 == null) continue; // skip malformed rows

                var href1 = a1.GetAttributeValue("href", "").Trim();
                var firstText = Normalize(a1.InnerText);

                // make absolute URL if it's relative
                var hrefAbs = MakeAbsolute(baseUri, href1);

                // Second <td>: prefer <a> text if present, otherwise cell text
                var a2 = tds[1].SelectSingleNode(".//a");
                var second = Normalize(a2?.InnerText ?? tds[1].InnerText);

                // Only add items once we've seen a header
                if (!string.IsNullOrEmpty(currentSection.Title))
                {
                    currentSection.Items.Add(new RowItem
                    {
                        Href = hrefAbs,
                        FirstText = firstText,
                        SecondAText = second
                    });
                }
            }

            // Commit the final section
            if (currentSection.Items.Count > 0)
                sections.Add(currentSection);

            return sections;
        }

        private static string MakeAbsolute(Uri baseUri, string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return href;
            if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
            return new Uri(baseUri, href).ToString();
        }

        private static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var decoded = HtmlEntity.DeEntitize(s).Replace('\u00A0', ' '); // &nbsp; => space
            // collapse whitespace
            return string.Join(" ", decoded.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static int? TryExtractVid(string urlOrPath)
        {
            var m = Regex.Match(urlOrPath, @"[?&]vid=(\d+)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
        }

        private static Dictionary<string, string> ExtractFormFields(HtmlDocument doc)
        {
            var fields = new Dictionary<string, string>();
            foreach (var input in doc.DocumentNode.SelectNodes("//input"))
            {
                var name = input.GetAttributeValue("name", null);
                var value = input.GetAttributeValue("value", "");
                if (!string.IsNullOrEmpty(name))
                    fields[name] = value;
            }
            return fields;
        }

        private static async Task<string?> FetchNextPageAsync(Uri baseUri, HtmlDocument doc)
        {
            var nextButton = doc.DocumentNode
                .SelectSingleNode("//input[@id='NextPagerBottom']");
            if (nextButton == null)
                return null; // no more pages

            var form = doc.DocumentNode.SelectSingleNode("//form");
            if (form == null)
                throw new InvalidOperationException("No form found for NextPagerBottom.");

            var action = form.GetAttributeValue("action", baseUri.ToString());
            var formFields = ExtractFormFields(doc);

            // Simulate the click
            formFields[nextButton.GetAttributeValue("name", "")] =
                nextButton.GetAttributeValue("value", "");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AlmanacScraper/1.0)");
            var content = new FormUrlEncodedContent(formFields);
            var resp = await http.PostAsync(new Uri(baseUri, action), content);
            return await resp.Content.ReadAsStringAsync();
        }



    }

    class Section
    {
        public string? Title { get; set; }
        public List<RowItem> Items { get; } = new List<RowItem>();
    }

    class RowItem
    {
        public string Href { get; set; } = "";
        public string FirstText { get; set; } = "";
        public string SecondAText { get; set; } = "";
    }


}

