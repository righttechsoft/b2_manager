using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace B2Manager;

public sealed class BucketSizeInfo
{
    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public int VersionCount { get; set; }
    public long ComputedAtUnixMs { get; set; }
}

public static class SizeCache
{
    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "B2Manager", "sizes.json");

    public static Dictionary<string, BucketSizeInfo> Load()
    {
        try
        {
            string json = File.ReadAllText(StorePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, BucketSizeInfo>>(json);
            return data ?? new Dictionary<string, BucketSizeInfo>();
        }
        catch
        {
            return new Dictionary<string, BucketSizeInfo>();
        }
    }

    public static void Save(Dictionary<string, BucketSizeInfo> cache)
    {
        try
        {
            string? dir = Path.GetDirectoryName(StorePath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(cache);
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // best-effort cache; ignore write failures
        }
    }
}
