using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

Console.Write(".insv input file path: ");
var insvInputFilePath = Console.ReadLine();

Console.Write("output directory: ");
var outputDir = Console.ReadLine();
var outputFilePath = Path.Combine(outputDir!, $"{Path.GetFileNameWithoutExtension(insvInputFilePath)}.gpx");

Console.WriteLine("Running exiftool...");
var exiftool = new Process
{
    StartInfo = new()
    {
        FileName = "exiftool",
        Arguments = $"-m -ee -api largefilesupport -G4 -j \"{insvInputFilePath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    }
};

exiftool.Start();
var output = await exiftool.StandardOutput.ReadToEndAsync();
var error = await exiftool.StandardError.ReadToEndAsync();
await exiftool.WaitForExitAsync();
Console.WriteLine("exiftool done.");

if (!string.IsNullOrWhiteSpace(error))
{
    Console.Error.WriteLine("Error running exiftool:");
    Console.Error.WriteLine(error);
}

//var output = File.ReadAllText("C:\\Users\\joona\\Downloads\\exiftool-13.33_64\\exiftool-13.33_64\\meta.json");

var outputSpan = output.AsSpan();
DateTime? startDateTime = null;
TimeSpan? duration = null;

var gpsInfos = new Dictionary<int, GpsInfo>();

static ReadOnlySpan<char> GetValue(ReadOnlySpan<char> source)
{
    var spaceIx = source.IndexOf(' ');
    var value = source[(spaceIx + 1)..];
    value = value.TrimStart('"').TrimEnd("\",");
    return value;
}

static int GetCopyIx(ReadOnlySpan<char> source)
{
    var colonIx = source.IndexOf(':');
    var numberStr = source[4..colonIx];
    var ix = int.Parse(numberStr);
    return ix;
}

static decimal ParseDMS(string dms)
{
    dms = dms.Replace("\\\"", "\""); // Unescape JSON-style input

    var match = Regex.Match(dms, @"(?<deg>\d+)\s+deg\s+(?<min>\d+)'\s+(?<sec>[\d.]+)""\s+(?<dir>[NSEW])");

    if (!match.Success)
        throw new FormatException("Invalid coordinate format: " + dms);

    double deg = double.Parse(match.Groups["deg"].Value, CultureInfo.InvariantCulture);
    double min = double.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
    double sec = double.Parse(match.Groups["sec"].Value, CultureInfo.InvariantCulture);
    string dir = match.Groups["dir"].Value;

    double decimalDegrees = deg + (min / 60.0) + (sec / 3600.0);

    if (dir == "S" || dir == "W")
        decimalDegrees *= -1;

    return (decimal)decimalDegrees;
}

foreach (var line in outputSpan.EnumerateLines())
{
    if (line.Length < 10)
        continue;

    var trimmedLine = line.TrimStart(' ');
    var sndQuoteIx = trimmedLine[1..].IndexOf('"');
    var propertyKey = trimmedLine.Slice(1, sndQuoteIx);
    if (startDateTime == null && propertyKey.SequenceEqual(":CreateDate"))
    {
        var value = GetValue(trimmedLine);
        startDateTime = DateTime.ParseExact(value, "yyyy:MM:dd HH:mm:ss", default);
    }
    else if (duration == null && propertyKey.SequenceEqual(":Duration"))
    {
        var value = GetValue(trimmedLine);
        duration = TimeSpan.Parse(value);
    }
    else if (propertyKey.StartsWith("Copy"))
    {
        if (propertyKey.EndsWith(":GPSLongitude"))
        {
            var copyIx = GetCopyIx(propertyKey);
            var value = GetValue(trimmedLine);
            var wgs84 = ParseDMS(value.ToString());
            if (gpsInfos.TryGetValue(copyIx, out var gpsInfo))
                gpsInfo.Lon = wgs84;
            else
                gpsInfos[copyIx] = new() { Lon = wgs84 };
        }
        else if (propertyKey.EndsWith(":GPSLatitude"))
        {
            var copyIx = GetCopyIx(propertyKey);
            var value = GetValue(trimmedLine);
            var wgs84 = ParseDMS(value.ToString());
            if (gpsInfos.TryGetValue(copyIx, out var gpsInfo))
                gpsInfo.Lat = wgs84;
            else
                gpsInfos[copyIx] = new() { Lat = wgs84 };
        }
        else if (propertyKey.EndsWith(":GPSAltitude"))
        {
            var copyIx = GetCopyIx(propertyKey);
            var value = GetValue(trimmedLine);
            var spaceIx = value.IndexOf(' ');
            var altStr = value[..spaceIx];
            var alt = decimal.Parse(altStr, CultureInfo.InvariantCulture);
            if (gpsInfos.TryGetValue(copyIx, out var gpsInfo))
                gpsInfo.Alt = alt;
            else
                gpsInfos[copyIx] = new() { Alt = alt };
        }
    }
}
//Console.WriteLine(startDateTime);
//Console.WriteLine(duration);
//foreach(var (_, gpsInfo) in gpsInfos)
//{
//    Console.WriteLine($"{gpsInfo.Lon} {gpsInfo.Lat} {gpsInfo.Alt}");
//}

var unitDuration = duration!.Value / gpsInfos.Count();
var dedupedGpsInfos = new List<GpsInfo>();
GpsInfo? currGpsInfo = null;
TimeSpan accDuration = TimeSpan.Zero;
foreach (var (_, gpsInfo) in gpsInfos)
{
    if (currGpsInfo == null || currGpsInfo.Lon != gpsInfo.Lon || currGpsInfo.Lat != gpsInfo.Lat)
    {
        if (currGpsInfo != null)
            gpsInfo.AccDuration = accDuration;
        currGpsInfo = gpsInfo;
        dedupedGpsInfos.Add(gpsInfo);
    }
    accDuration = accDuration.Add(unitDuration);
}
//foreach (var gpsInfo in dedupedGpsInfos)
//{
//    Console.WriteLine($"{gpsInfo.Lon} {gpsInfo.Lat} {gpsInfo.Alt} -> {gpsInfo.AccDuration}");
//}

var minLon = dedupedGpsInfos.Min(x => x.Lon);
var minLat = dedupedGpsInfos.Min(x => x.Lat);
var maxLon = dedupedGpsInfos.Max(x => x.Lon);
var maxLat = dedupedGpsInfos.Max(x => x.Lat);

var gpx = new StringBuilder();
gpx.AppendLine($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
gpx.AppendLine($"<gpx xmlns=\"http://www.topografix.com/GPX/1/1\" creator=\"Insta360 Studio\" version=\"1.1\">");
gpx.AppendLine($"  <metadata>");
gpx.AppendLine($"    <bounds minlat=\"{minLat!.Value.ToString(CultureInfo.InvariantCulture)}\" minlon=\"{minLon!.Value.ToString(CultureInfo.InvariantCulture)}\" maxlon=\"{maxLon!.Value.ToString(CultureInfo.InvariantCulture)}\" maxlat=\"{maxLat!.Value.ToString(CultureInfo.InvariantCulture)}\"/>");
gpx.AppendLine($"  </metadata>");
gpx.AppendLine($"  <trk>");
gpx.AppendLine($"    <trkseq>");
foreach (var gpsInfo in dedupedGpsInfos)
{
    gpx.AppendLine($"      <trkpt lat=\"{gpsInfo.Lat!.Value.ToString(CultureInfo.InvariantCulture)}\" lon=\"{gpsInfo.Lon!.Value.ToString(CultureInfo.InvariantCulture)}\">");
    gpx.AppendLine($"        <ele>{gpsInfo.Alt!.Value.ToString("0.0", CultureInfo.InvariantCulture)}</ele>");
    gpx.AppendLine($"        <time>{startDateTime!.Value.Add(gpsInfo.AccDuration).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</time>");
    gpx.AppendLine($"      </trkpt>");
}
gpx.AppendLine($"    </trkseq>");
gpx.AppendLine($"  </trk>");
gpx.AppendLine($"</gpx>");
File.WriteAllText(outputFilePath, gpx.ToString());

class GpsInfo
{
    public decimal? Lon { get; set; }
    public decimal? Lat { get; set; }
    public decimal? Alt { get; set; }
    public TimeSpan AccDuration { get; set; } = TimeSpan.Zero;
}