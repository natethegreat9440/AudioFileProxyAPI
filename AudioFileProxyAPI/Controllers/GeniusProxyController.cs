using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

[Route("api/genius")]
[ApiController]
public class GeniusProxyController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GeniusProxyController(IConfiguration config)
    {
        _httpClient = new HttpClient();
        _apiKey = Environment.GetEnvironmentVariable("GENIUS_API_KEY");

        if (string.IsNullOrEmpty(_apiKey))
            throw new Exception("Genius API key is missing. Set it in appsettings.json.");

        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchSong([FromQuery] string artist, [FromQuery] string trackName)
    {
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(trackName))
            return BadRequest("Artist and track name are required.");

        try
        {
            //TODO: Move this logic to API proxy server controller in API project so I don't have to hard code the API Key
            //Step 0: Check for ? by MFDOOM
            trackName = CheckForQuestionMarkByMFDOOM(trackName);

            // Step 1: Search for the song
            string searchUrl = $"https://api.genius.com/search?q={Uri.EscapeDataString(artist + " " + trackName)}";
            var response = await _httpClient.GetStringAsync(searchUrl);
            JObject searchJson = JObject.Parse(response);

            // Step 2: Extract song ID
            var hits = searchJson["response"]?["hits"];
            if (hits == null || !hits.HasValues)
                return NotFound("Song not found.");

            // Step 3: Preprocess artist and track name for comparison
            string trackLower = NormalizeForUrlComparison(trackName);
            string artistLower = NormalizeForUrlComparison(artist);
            string fallbackUrl = null;

            // Step 4: Iterate over hits to find the best match
            foreach (var hit in hits)
            {
                string url = hit["result"]?["url"]?.ToString();
                if (string.IsNullOrEmpty(url))
                    continue;

                string urlLower = url.ToLower();

                // Check if URL contains both artist and track name
                if (urlLower.Contains(artistLower) && urlLower.Contains(trackLower))
                    return Ok(new { songUrl = url });

                // Store the first match that contains only the track name as fallback
                if (fallbackUrl == null && urlLower.Contains(trackLower))
                    fallbackUrl = url;
            }

            return Ok(new { songUrl = fallbackUrl ?? "Not found" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Server error: {ex.Message}");
        }
    }

    private static string CheckForQuestionMarkByMFDOOM(string trackName)
    {
        return trackName == "?" ? "question mark" : trackName;
    }

    private static string NormalizeForUrlComparison(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Remove featured artist info (ft., feat., featuring, etc.)
        input = Regex.Replace(input, @"\b(ft\.?|feat\.?|featuring|prod\.?)\b.*", "", RegexOptions.IgnoreCase).Trim();

        // Remove all special characters except letters, numbers, and spaces
        input = Regex.Replace(input.ToLower(), @"[^a-z0-9 ]", "");

        // Collapse multiple spaces (including non-breaking spaces) into a single space
        input = Regex.Replace(input, @"\s+", " ");

        // Replace spaces with hyphens
        input = Regex.Replace(input, @"\s+", "-");

        // Remove consecutive hyphens
        return Regex.Replace(input, @"-{2,}", "-");
    }
}
