using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MftSearchWpf.Models;

namespace MftSearchWpf.Services
{
    public static class IndexPersistenceService
    {
        private const int FormatVersion = 2;
        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tachyon", "index.bin");

        public static string CacheFilePath => CachePath;

        public static async Task SaveAsync(List<FileRecord> records)
        {
            await Task.Run(() =>
            {
                string? dir = Path.GetDirectoryName(CachePath);
                if (dir != null) Directory.CreateDirectory(dir);

                string tmpPath = CachePath + ".tmp";

                using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
                using var bw = new BinaryWriter(fs, System.Text.Encoding.Unicode, leaveOpen: false);

                bw.Write(FormatVersion);
                bw.Write(records.Count);

                foreach (var r in records)
                {
                    bw.Write(r.FileName);
                    bw.Write(r.FullPath);
                }

                bw.Flush();
                fs.Flush(flushToDisk: true);

                File.Move(tmpPath, CachePath, overwrite: true);
            });
        }

        public static async Task<(List<FileRecord>? Records, DateTime Timestamp)> LoadAsync() // nullable Records on miss
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(CachePath))
                    return ((List<FileRecord>?)null, DateTime.MinValue);

                DateTime timestamp = File.GetLastWriteTime(CachePath);

                try
                {
                    using var fs = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    using var br = new BinaryReader(fs, System.Text.Encoding.Unicode, leaveOpen: false);

                    int version = br.ReadInt32();
                    if (version != FormatVersion)
                        return ((List<FileRecord>?)null, DateTime.MinValue);

                    int count = br.ReadInt32();
                    var records = new List<FileRecord>(count);

                    for (int i = 0; i < count; i++)
                    {
                        string fileName = br.ReadString();
                        string fullPath = br.ReadString();
                        records.Add(new FileRecord { FileName = fileName, FullPath = fullPath });
                    }

                    return (records, timestamp);
                }
                catch
                {
                    return ((List<FileRecord>?)null, DateTime.MinValue);
                }
            });
        }

        public static bool CacheExists() => File.Exists(CachePath);

        public static DateTime CacheTimestamp() =>
            File.Exists(CachePath) ? File.GetLastWriteTime(CachePath) : DateTime.MinValue;

        public static void DeleteCache()
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
        }
    }
}
