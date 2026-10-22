using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NzbDrone.Core.Notifications.Kavita;

// Talearr: the library fields comic scans need (GET api/Library/libraries).
public class KavitaLibrary
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("lastScanned")]
    public DateTime LastScanned { get; set; }

    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = new List<string>();

    // Kavita reports 0001-01-01 until the library's first full scan.
    public bool NeverScanned => LastScanned.Year <= 1;
}
