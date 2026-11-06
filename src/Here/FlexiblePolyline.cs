namespace UniMap.ServicePoints.API.Here;

/// <summary>Decoder for HERE's flexible polyline format. Returns (latitude, longitude) pairs.</summary>
public static class FlexiblePolyline
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const int FormatVersion = 1;

    public static IReadOnlyList<(double Lat, double Lng)> Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var pos = 0;

        var version = ReadUnsigned(encoded, ref pos);
        if (version != FormatVersion)
            throw new FormatException($"Unsupported flexible polyline version {version}.");

        var header = ReadUnsigned(encoded, ref pos);
        var precision = (int)(header & 15);
        var hasThirdDimension = ((header >> 4) & 7) != 0;
        var factor = Math.Pow(10, precision);

        var points = new List<(double, double)>();
        long lat = 0, lng = 0;
        while (pos < encoded.Length)
        {
            lat += ReadSigned(encoded, ref pos);
            if (pos >= encoded.Length) throw new FormatException("Flexible polyline ends inside a coordinate pair.");
            lng += ReadSigned(encoded, ref pos);
            if (hasThirdDimension)
            {
                if (pos >= encoded.Length) throw new FormatException("Flexible polyline ends inside a coordinate triple.");
                ReadSigned(encoded, ref pos);
            }
            points.Add((lat / factor, lng / factor));
        }
        return points;
    }

    private static long ReadSigned(string s, ref int pos)
    {
        var raw = ReadUnsigned(s, ref pos);
        return (raw & 1) != 0 ? ~(raw >> 1) : raw >> 1;
    }

    private static long ReadUnsigned(string s, ref int pos)
    {
        long result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= s.Length) throw new FormatException("Flexible polyline ends inside a value.");
            var index = Alphabet.IndexOf(s[pos++]);
            if (index < 0) throw new FormatException("Invalid character in flexible polyline.");
            if (shift > 60) throw new FormatException("Flexible polyline value too large.");
            result |= (long)(index & 0x1F) << shift;
            if ((index & 0x20) == 0) return result;
            shift += 5;
        }
    }
}
