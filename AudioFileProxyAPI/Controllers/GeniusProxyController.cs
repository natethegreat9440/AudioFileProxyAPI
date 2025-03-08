using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using System.Net.Http.Json;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Http;

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

        //TODO: Delete the hard coded API key before committing and uncomment the line above!!!
        //Copy and paste API key below for pre-deployment on Render testing;
        //_apiKey = "";

        if (string.IsNullOrEmpty(_apiKey))
            throw new Exception("Genius API key is missing. Set it in appsettings.json.");

        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchSong([FromQuery] string artist, [FromQuery] string trackName)
    {
        string songUrl = string.Empty;
        string geniusSongId = string.Empty;

        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(trackName))
            return BadRequest("Artist and track name are required.");

        try
        {

            var results = await HandleGeniusUrlAndSongID(artist, trackName);

            if (!results.Any())
            {
                return NotFound("Genius URL and song ID not found.");
            }
            else
            {
                songUrl = results[0].ToString();
                geniusSongId = results[1].ToString();
            }
            return Ok(new { songUrl = songUrl, geniusSongId = geniusSongId });

        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Server error: {ex.Message}");
        }
    }

    [HttpGet("search_missing")]
    public async Task<IActionResult> SearchUntaggedSong([FromQuery] string fileName)
    {
        string artist = string.Empty;
        string artistFallback = string.Empty;
        string trackName = string.Empty;
        string trackNameFallback = string.Empty;
        string songUrl = string.Empty;
        string geniusSongId = string.Empty;
        string album = string.Empty;
        string albumTrackNumber = string.Empty;

        int fallbackAlbumNumber = 0;

        try
        {
            if (string.IsNullOrEmpty(fileName))
                return BadRequest("File name is required.");

            var tokens = TokenizeFileName(fileName);

            if (tokens.Count < 2)
                return BadRequest("Invalid file name format.");

            if (tokens.Count >= 3)
            {
                CheckForAlbumTrackNumberInTokens(ref fallbackAlbumNumber, ref tokens);
            }

            string firstToken = CleanToken(tokens[0]);
            string secondToken = CleanToken(tokens[1]);

            int firstTrialHitCount = await CheckForHitsWithTokens(firstToken, secondToken);
            int secondTrialHitCount = await CheckForHitsWithTokens(secondToken, firstToken);

            if (firstTrialHitCount == 0 && secondTrialHitCount == 0)
            {
                return NotFound("Song not found. No hits found for any token combination.");
            }

            else if (firstTrialHitCount > secondTrialHitCount)
            {
                artistFallback = firstToken;
                trackNameFallback = secondToken;

                var results = await HandleGeniusUrlAndSongID(artistFallback, trackNameFallback);

                if (!results.Any())
                {
                    return NotFound("Song not found where firstTrialHitCount > secondTrialHitCount");
                }
                else
                {
                    songUrl = results[0].ToString();
                    geniusSongId = results[1].ToString();
                }
            }

            else if (secondTrialHitCount > firstTrialHitCount)
            {
                artistFallback = secondToken;
                trackNameFallback = firstToken;

                var results = await HandleGeniusUrlAndSongID(artistFallback, trackNameFallback);

                if (!results.Any())
                {
                    return NotFound("Song not found secondTrialHitCount > firstTrialHitCount");
                }
                else
                {
                    songUrl = results[0].ToString();
                    geniusSongId = results[1].ToString();
                }
            }

            else if (firstTrialHitCount == secondTrialHitCount) //This is an edge-case
            {
                var results = await HandleEqualHitsForTokenCombinations(firstToken, secondToken);

                if (!results.Any())
                {
                    return NotFound("Song not found where hits are equal and non-zero");
                }
                else
                {
                    songUrl = results[0].ToString();
                    geniusSongId = results[1].ToString();
                    artistFallback = results[2].ToString();
                    trackNameFallback = results[3].ToString();
                }
            }

            if (string.IsNullOrEmpty(geniusSongId) == false)
            {
                var trackResults = await HandleGeniusSearchForTrackNameAndArtist(geniusSongId);

                var albumResults = await HandleGeniusSearchForAlbumAndAlbumId(geniusSongId);

                trackName = trackResults[0].ToString();
                artist = trackResults[1].ToString();

                album = albumResults[0].ToString();
                string albumId = albumResults[1].ToString();

                if (string.IsNullOrEmpty(albumId) == false)
                {
                    albumTrackNumber = await HandleGeniusSearchForAlbumTrackNumber(geniusSongId, albumTrackNumber, albumId);
                }
            }

        }

        catch (Exception ex)
        {
            return StatusCode(500, $"Server error: {ex.Message}");
        }

        return Ok(new
            {
                trackName = string.IsNullOrEmpty(trackName) ? trackNameFallback.ToString() : trackName,
                artist = string.IsNullOrEmpty(artist) ? artist.ToString() : artist,
                album = album,
                albumTrackNumber = string.IsNullOrEmpty(albumTrackNumber) ? fallbackAlbumNumber.ToString() : albumTrackNumber,
                songUrl = songUrl,
                geniusSongId = geniusSongId,
                apiSource = "Genius"
            });
    }

    [HttpGet("samples")]
    public async Task<IActionResult> HandleSearchForSampleInfo([FromQuery] string geniusId)
    {
        string songDetailsUrl = $"https://api.genius.com/songs/{geniusId}";

        var songResponse = await _httpClient.GetStringAsync(songDetailsUrl);
        JObject songJson = JObject.Parse(songResponse);

        string trackSamples = HandleTrackSampleInfoExtraction(songJson, "samples");
        string sampledBys = HandleTrackSampleInfoExtraction(songJson, "sampled_in");

        return Ok(new
        {
            trackSamples = string.IsNullOrEmpty(trackSamples) ? string.Empty : trackSamples,
            sampledBys = string.IsNullOrEmpty(sampledBys) ? string.Empty : sampledBys,
            apiSource = "Genius"
        });
    }

    private async Task<IActionResult> HandleGeniusUrlSearch(string artist, string trackName)
    {
        //Step 0: Check for ? by MFDOOM
        trackName = CheckForQuestionMarkByMFDOOM(trackName);

        // Step 1: Search for the song
        string searchUrl = $"https://api.genius.com/search?q={Uri.EscapeDataString(artist + " " + trackName)}";
        var response = await _httpClient.GetStringAsync(searchUrl);
        JObject searchJson = JObject.Parse(response);

        // Step 2: Extract song ID
        var hits = searchJson["response"]?["hits"];
        if (hits == null || !hits.HasValues)
            return NotFound("Song not found. Failed in HandleGeniusUrlSearch.");

        // Step 3: Preprocess artist and track name for comparison
        string trackLower = NormalizeForUrlComparison(trackName);
        string artistLower = NormalizeForUrlComparison(artist);
        string fallbackUrl = null;
        string geniusSongId = null;

        // Step 4: Iterate over hits to find the best match
        foreach (var hit in hits)
        {
            string url = hit["result"]?["url"]?.ToString();
            geniusSongId = hit["result"]?["id"]?.ToString();

            if (string.IsNullOrEmpty(url))
                continue;

            string urlLower = url.ToLower();

            // Check if URL contains both artist and track name
            if (urlLower.Contains(artistLower) && urlLower.Contains(trackLower))
                return Ok(new { songUrl = url, geniusSongId = geniusSongId });

            // Store the first match that contains only the track name as fallback
            if (fallbackUrl == null && urlLower.Contains(trackLower))
                fallbackUrl = url;
        }

        return Ok(new { songUrl = fallbackUrl ?? "Not found", geniusSongId = geniusSongId ?? "Not found" });
    }

    private async Task<List<string>> HandleGeniusUrlAndSongID(string artist, string trackName)
    {
        var songUrlResult = await HandleGeniusUrlSearch(artist, trackName);

        if (songUrlResult is OkObjectResult okResult)
        {
            // Convert the anonymous object into a JSON string
            var jsonString = JsonConvert.SerializeObject(okResult.Value);

            // Parse the string into a JObject
            JObject resultJson = JObject.Parse(jsonString);

            string songUrl = resultJson["songUrl"]?.ToString();
            string geniusSongId = resultJson["geniusSongId"]?.ToString();

            return new List<string> { songUrl, geniusSongId };
        }

        return new List<string>();
    }

    private async Task<List<string>> HandleEqualHitsForTokenCombinations(string firstToken, string secondToken)
    {
        var songUrlFirstResult = await HandleGeniusUrlSearch(firstToken, secondToken);
        var songUrlSecondResult = await HandleGeniusUrlSearch(secondToken, firstToken);

        string artistFallback = string.Empty;
        string trackNameFallback = string.Empty;
        string songUrl = string.Empty;
        string geniusSongId = string.Empty;

        if (songUrlFirstResult is OkObjectResult okFirstResult)
        {
            // Convert the anonymous object into a JSON string
            var jsonString = JsonConvert.SerializeObject(okFirstResult.Value);

            // Parse the string into a JObject
            JObject resultFirstJson = JObject.Parse(jsonString);

            songUrl = resultFirstJson["songUrl"]?.ToString();
            geniusSongId = resultFirstJson["geniusSongId"]?.ToString();

            artistFallback = firstToken;
            trackNameFallback = secondToken;

            //If the first result returns a matching songUrl, use that songUrl and ignore the second result
        }
        else if (songUrlSecondResult is OkObjectResult okSecondResult)
        {
            // Convert the anonymous object into a JSON string
            var jsonString = JsonConvert.SerializeObject(okSecondResult.Value);

            // Parse the string into a JObject
            JObject resultSecondJson = JObject.Parse(jsonString);

            songUrl = resultSecondJson["songUrl"]?.ToString();
            geniusSongId = resultSecondJson["geniusSongId"]?.ToString();

            artistFallback = secondToken;
            trackNameFallback = firstToken;

            //If the second result returns a matching songUrl, use that songUrl
        }
        else
        {
            //If neither result returns a matching songUrl, return a 404
            return new List<string>();
        }

        return new List<string> { songUrl, geniusSongId, artistFallback, trackNameFallback };

    }

    private async Task<List<string?>> HandleGeniusSearchForAlbumAndAlbumId(string geniusSongId)
    {
        string songDetailsUrl = $"https://api.genius.com/songs/{geniusSongId}";
        var songResponse = await _httpClient.GetStringAsync(songDetailsUrl);
        JObject songJson = JObject.Parse(songResponse);

        string albumName = songJson["response"]?["song"]?["album"]?["name"]?.ToString() ?? "Unknown Album";
        var albumId = songJson["response"]?["song"]?["album"]?["id"]?.ToString();

        return new List<string?> { albumName, albumId };
    }

    private async Task<List<string?>> HandleGeniusSearchForTrackNameAndArtist(string geniusSongId)
    {
        string songDetailsUrl = $"https://api.genius.com/songs/{geniusSongId}";
        var songResponse = await _httpClient.GetStringAsync(songDetailsUrl);
        JObject songJson = JObject.Parse(songResponse);

        string trackName = songJson["response"]?["song"]?["title_with_featured"]?.ToString() ?? "Unknown Track";
        string artist = songJson["response"]?["song"]?["primary_artist_names"]?.ToString() ?? "Unknown Artist";

        return new List<string?> { trackName, artist };
    }

    private async Task<string> HandleGeniusSearchForAlbumTrackNumber(string geniusSongId, string albumNumber, string? albumId)
    {
        string albumTracksUrl = $"https://api.genius.com/albums/{albumId}/tracks";
        var albumResponse = await _httpClient.GetStringAsync(albumTracksUrl);
        JObject albumJson = JObject.Parse(albumResponse);

        var tracks = albumJson["response"]?["tracks"];

        if (tracks == null || !tracks.HasValues)
            albumNumber = string.Empty;
        else
        {
            foreach (var track in tracks)
            {
                string trackSongId = track["song"]?["id"]?.ToString();
                string trackNumber = track["number"]?.ToString();

                if (!string.IsNullOrEmpty(trackSongId) && trackSongId == geniusSongId.ToString())
                    albumNumber = trackNumber;
            }
        }

        return albumNumber;
    }

    private string HandleTrackSampleInfoExtraction(JObject songJson, string relationshipType)
    {
        var fullTitles = new List<string>();

        try
        {
            // Navigate to the song_relationships array

            JArray relationships = (JArray)songJson["response"]["song"]["song_relationships"];

            // Loop through the relationships
            foreach (JObject rel in relationships)
            {
                // Check if the relationship_type is "samples" or "sampled_in"
                if ((string)rel["relationship_type"] == relationshipType)
                {
                    // Iterate over each song in this relationship
                    JArray songs = (JArray)rel["songs"];
                    foreach (JObject song in songs)
                    {
                        // Add the full_title of the song to the list
                        fullTitles.Add((string)song["full_title"]);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return string.Empty;
        }

        return string.Join(", ", fullTitles);
    }

    private async Task<int> CheckForHitsWithTokens(string aToken, string anotherToken)
    {
        //Arbitrarily sets tokens passed in to artist and trackName
        //This method gets called twice from SearchUntaggedSong swapping the parameters passed on the second call
        try
        {
            string trackName = aToken;
            string artist = anotherToken;

            //Step 0: Check for ? by MFDOOM
            trackName = CheckForQuestionMarkByMFDOOM(trackName);

            // Step 1: Search for the song
            string searchUrl = $"https://api.genius.com/search?q={Uri.EscapeDataString(artist + " " + trackName)}";
            var response = await _httpClient.GetStringAsync(searchUrl);
            JObject searchJson = JObject.Parse(response);

            // Step 2: Extract song ID
            var hits = searchJson["response"]?["hits"];
            if (hits == null || !hits.HasValues)
                return 0;
            else
            {
                return hits.Count();
            }
        }
        catch (Exception ex)
        {
            return 0;
        }
    }

    private static void CheckForAlbumTrackNumberInTokens(ref int fallbackAlbumNumber, ref List<string> tokens)
    {
        tokens = tokens.Take(3).ToList();

        // Check if any token can be converted to a numeric type  
        bool hasNumericToken = tokens.Any(token => int.TryParse(token, out _));

        if (hasNumericToken)
        {
            //Sets the first convertible to a numeric type token to be the fallbackAlbumNumber in case genius API can't determine the album number from the artist and track name
            fallbackAlbumNumber = tokens.Select(token =>
            {
                int.TryParse(token, out int number);
                return number; //Only returns out of the lambda expression
            }).First(number => number != 0);

            //We want fallbackAlbumNumber converted back to string here. New track object creation will handle converting it to int
            tokens.Remove(fallbackAlbumNumber.ToString());
            tokens.Remove("0" + fallbackAlbumNumber.ToString()); //In case the fallback number has a leading 0 account for that and remove it
        }
    }

    private List<string> TokenizeFileName(string fileName)
    {
        var tokens = fileName.Split('-').Select(token => token.Trim()).ToList();

        //foreach (var token in tokens)
        //{
        //    Console.WriteLine(token);
        //}
        return tokens;
    }

    private static string NormalizeForUrlComparison(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        input = CleanFeaturedArtist(input);

        input = CleanSpecialCharactersAndLower(input);

        input = CleanExtraSpaces(input);

        input = ReplaceSpacesWithHyphens(input);

        return RemoveConsecutiveHyphens(input);
    }

    private static string CleanToken(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        input = CleanFeaturedArtist(input);

        input = CleanTheWordLyric(input);

        input = CleanTheWordExplicit(input);

        input = CleanTheWordAudio(input);

        input = CleanSpecialCharacters(input);

        return CleanExtraSpaces(input);
    }

    private static string RemoveConsecutiveHyphens(string input)
    {
        // Remove consecutive hyphens
        return Regex.Replace(input, @"-{2,}", "-");
    }

    private static string ReplaceSpacesWithHyphens(string input)
    {
        // Replace spaces with hyphens
        input = Regex.Replace(input, @"\s+", "-");
        return input;
    }

    private static string CleanExtraSpaces(string input)
    {
        // Collapse multiple spaces (including non-breaking spaces) into a single space
        input = Regex.Replace(input, @"\s+", " ");
        return input;
    }

    private static string CleanSpecialCharactersAndLower(string input)
    {
        // Remove all special characters except letters, numbers, and spaces and lowercase remaining characters
        input = Regex.Replace(input.ToLower(), @"[^a-z0-9 ]", "");
        return input;
    }

    private static string CleanSpecialCharacters(string input)
    {
        // Remove all special characters except letters, numbers, and spaces
        input = Regex.Replace(input, @"[^a-zA-Z0-9 ]", "");
        return input.Trim();
    }

    private static string CleanTheWordLyric(string input)
    {
        input = Regex.Replace(input, @"\b(lyric|lyrics)\b.*", "", RegexOptions.IgnoreCase).Trim();
        return input;
    }

    private static string CleanTheWordExplicit(string input)
    {
        input = Regex.Replace(input, @"\b(explicit)\b.*", "", RegexOptions.IgnoreCase).Trim();
        return input;
    }

    private static string CleanTheWordAudio(string input)
    {
        input = Regex.Replace(input, @"\b(audio)\b.*", "", RegexOptions.IgnoreCase).Trim();
        return input;
    }

    private static string CleanFeaturedArtist(string input)
    {
        // Remove featured artist info (ft., feat., featuring, etc.)
        input = Regex.Replace(input, @"\b(ft\.?|feat\.?|featuring|prod\.?)\b.*", "", RegexOptions.IgnoreCase).Trim();
        return input;
    }

    private static string CheckForQuestionMarkByMFDOOM(string trackName)
    {
        return trackName == "?" ? "question mark" : trackName;
    }
}
