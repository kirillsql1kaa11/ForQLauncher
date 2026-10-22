namespace ForQ.Core;

public static class SystemInfo
{
    public static long TotalMemoryMb
    {
        get
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes <= 0 ? 8192 : bytes / 1048576;
        }
    }

    public static int RecommendedMemoryMb
    {
        get
        {
            var quarter = TotalMemoryMb / 4;
            var rounded = (int)(quarter / 512) * 512;
            var maximum = Math.Min(8192, (int)Math.Max(2048, TotalMemoryMb - 4096));
            return Math.Clamp(rounded, 2048, Math.Max(2048, maximum));
        }
    }
}
