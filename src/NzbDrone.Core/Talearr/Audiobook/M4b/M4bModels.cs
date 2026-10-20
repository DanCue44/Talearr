using System.Collections.Generic;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // Talearr: what ffprobe reports about one source file of a multi-file audiobook.
    public sealed class M4bSourceTrack
    {
        public string Path { get; set; }
        public double DurationSeconds { get; set; }
        public string Codec { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public long BitRate { get; set; }
        public bool HasEmbeddedCover { get; set; }
        public string Title { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        // Chapters already inside this file, relative to the start of the file.
        public List<M4bChapter> Chapters { get; set; } = new List<M4bChapter>();
    }

    public sealed class M4bChapter
    {
        public M4bChapter()
        {
        }

        public M4bChapter(double start, double end, string title)
        {
            Start = start;
            End = end;
            Title = title;
        }

        public double Start { get; set; }
        public double End { get; set; }
        public string Title { get; set; }
    }

    // Book level tags written into the M4B.
    public sealed class M4bMetadata
    {
        public string Title { get; set; }
        public string Author { get; set; }
        public string Narrator { get; set; }
        public string Year { get; set; }
        public string Genre { get; set; } = "Audiobook";
        public string Description { get; set; }
        public string Publisher { get; set; }
        public string Series { get; set; }
        public string SeriesPosition { get; set; }
        public string Language { get; set; }
        public string Asin { get; set; }
        public string Isbn { get; set; }
    }

    public sealed class M4bEncodeSettings
    {
        // Target AAC bitrate in kbps when the sources have to be re-encoded.
        public int BitrateKbps { get; set; } = 64;

        // Never encode above the sources' own bitrate.
        public bool NoUpscale { get; set; } = true;

        // Downmix to mono.
        public bool Mono { get; set; }
    }
}
