using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MftSearch
{
    public class Program
    {

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900b3;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct USN_JOURNAL_DATA_V0
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MFT_ENUM_DATA_V0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }


        public struct FileEntry
        {
            public string Name;
            public ulong ParentFrn;
        }

        public static void Main(string[] args)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("This application is designed to run on Windows.");
                return;
            }

            if (!IsAdministrator())
            {
                Console.WriteLine("Error: Please run this application as Administrator.");
                Console.WriteLine("Reading the Master File Table (MFT) requires elevated privileges.");
                return;
            }

            List<string> drivesToIndex = new List<string>();

            if (args.Length > 0)
            {

                foreach (var arg in args)
                {
                    string drive = arg.ToUpper();
                    if (drive.Length > 0 && !drive.EndsWith(":\\")) drive = drive.Substring(0, 1) + ":\\";
                    drivesToIndex.Add(drive);
                }
            }
            else
            {

                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo drive in drives)
                {
                    if (drive.DriveType == DriveType.Fixed && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                    {
                        drivesToIndex.Add(drive.Name);
                    }
                }
            }

            if (drivesToIndex.Count == 0)
            {
                Console.WriteLine("No NTFS drives found to index.");
                return;
            }


            Dictionary<string, Dictionary<ulong, FileEntry>> masterIndex = new Dictionary<string, Dictionary<ulong, FileEntry>>();

            Console.WriteLine("Starting MFT indexing...");
            Stopwatch sw = Stopwatch.StartNew();

            foreach (string drive in drivesToIndex)
            {
                Console.WriteLine($"Indexing drive {drive}...");
                var index = BuildIndexForDrive(drive);
                if (index != null)
                {
                    masterIndex[drive] = index;
                    Console.WriteLine($"Found {index.Count} entries on {drive}.");
                }
            }

            sw.Stop();
            Console.WriteLine($"Indexing complete in {sw.ElapsedMilliseconds} ms.");


            while (true)
            {
                Console.Write("\nEnter search query (or 'exit' / 'quit' to quit): ");
                string? query = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(query)) continue;
                if (query.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    query.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                    query.Equals("q", StringComparison.OrdinalIgnoreCase)) break;

                SearchIndex(masterIndex, query);
            }
        }

        private static bool IsAdministrator()
        {
#pragma warning disable CA1416
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
#pragma warning restore CA1416
        }

        private static Dictionary<ulong, FileEntry>? BuildIndexForDrive(string driveLetter)
        {

            Dictionary<ulong, FileEntry> index = new Dictionary<ulong, FileEntry>(1000000);


            string volumePath = "\\\\.\\" + driveLetter.TrimEnd('\\');

            IntPtr hVolume = CreateFile(
                volumePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hVolume == INVALID_HANDLE_VALUE)
            {
                Console.WriteLine($"Failed to open volume {volumePath}. Error: {Marshal.GetLastWin32Error()}");
                return null;
            }

            try
            {

                USN_JOURNAL_DATA_V0 journalData = new USN_JOURNAL_DATA_V0();
                int journalDataSize = Marshal.SizeOf(journalData);
                IntPtr pJournalData = Marshal.AllocHGlobal(journalDataSize);

                try
                {
                    bool success = DeviceIoControl(
                        hVolume,
                        FSCTL_QUERY_USN_JOURNAL,
                        IntPtr.Zero,
                        0,
                        pJournalData,
                        (uint)journalDataSize,
                        out uint bytesReturned,
                        IntPtr.Zero);

                    if (!success)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 1179)
                        {
                            Console.WriteLine($"[SKIPPED] The USN Journal is disabled on {driveLetter}.");
                            Console.WriteLine($"          To enable indexing for this drive, run this command as Administrator:");
                            Console.WriteLine($"          fsutil usn createjournal m=33554432 a=8388608 {driveLetter.TrimEnd('\\')}");
                        }
                        else
                        {
                            Console.WriteLine($"FSCTL_QUERY_USN_JOURNAL failed on {driveLetter}. Error: {err}");
                        }
                        return null;
                    }

                    journalData = Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(pJournalData);
                }
                finally
                {
                    Marshal.FreeHGlobal(pJournalData);
                }


                MFT_ENUM_DATA_V0 enumData = new MFT_ENUM_DATA_V0
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = journalData.NextUsn
                };

                int enumDataSize = Marshal.SizeOf(enumData);
                IntPtr pEnumData = Marshal.AllocHGlobal(enumDataSize);
                Marshal.StructureToPtr(enumData, pEnumData, false);


                int bufferSize = 1024 * 1024;
                IntPtr pBuffer = Marshal.AllocHGlobal(bufferSize);

                try
                {
                    while (true)
                    {
                        bool success = DeviceIoControl(
                            hVolume,
                            FSCTL_ENUM_USN_DATA,
                            pEnumData,
                            (uint)enumDataSize,
                            pBuffer,
                            (uint)bufferSize,
                            out uint bytesReturned,
                            IntPtr.Zero);

                        if (!success)
                        {
                            int err = Marshal.GetLastWin32Error();

                            if (err == 38) break;

                            Console.WriteLine($"FSCTL_ENUM_USN_DATA failed. Error: {err}");
                            break;
                        }


                        ulong nextStartFrn = (ulong)Marshal.ReadInt64(pBuffer);
                        enumData.StartFileReferenceNumber = nextStartFrn;
                        Marshal.StructureToPtr(enumData, pEnumData, false);


                        int offset = 8;
                        while (offset < bytesReturned)
                        {
                            IntPtr pRecord = new IntPtr(pBuffer.ToInt64() + offset);


                            uint recordLength = (uint)Marshal.ReadInt32(pRecord);
                            ushort majorVersion = (ushort)Marshal.ReadInt16(pRecord, 4);

                            if (recordLength == 0) break;

                            if (majorVersion == 2 || majorVersion == 3)
                            {
                                ulong frn = 0;
                                ulong parentFrn = 0;
                                ushort fileNameLength = 0;
                                ushort fileNameOffset = 0;


                                if (majorVersion == 2)
                                {
                                    frn = (ulong)Marshal.ReadInt64(pRecord, 8);
                                    parentFrn = (ulong)Marshal.ReadInt64(pRecord, 16);
                                    fileNameLength = (ushort)Marshal.ReadInt16(pRecord, 56);
                                    fileNameOffset = (ushort)Marshal.ReadInt16(pRecord, 58);
                                }
                                else if (majorVersion == 3)
                                {

                                    frn = (ulong)Marshal.ReadInt64(pRecord, 8);
                                    parentFrn = (ulong)Marshal.ReadInt64(pRecord, 24);
                                    fileNameLength = (ushort)Marshal.ReadInt16(pRecord, 72);
                                    fileNameOffset = (ushort)Marshal.ReadInt16(pRecord, 74);
                                }


                                IntPtr pFileName = new IntPtr(pRecord.ToInt64() + fileNameOffset);
                                string fileName = Marshal.PtrToStringUni(pFileName, fileNameLength / 2) ?? string.Empty;


                                index[frn] = new FileEntry
                                {
                                    Name = fileName,
                                    ParentFrn = parentFrn
                                };
                            }

                            offset += (int)recordLength;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pEnumData);
                    Marshal.FreeHGlobal(pBuffer);
                }

                return index;
            }
            finally
            {
                CloseHandle(hVolume);
            }
        }

        private static void SearchIndex(Dictionary<string, Dictionary<ulong, FileEntry>> masterIndex, string query)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int resultsCount = 0;

            foreach (var driveEntry in masterIndex)
            {
                string drive = driveEntry.Key;
                var index = driveEntry.Value;

                foreach (var kvp in index)
                {

                    if (kvp.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        string fullPath = ResolvePath(index, kvp.Key, drive);
                        Console.WriteLine(fullPath);
                        resultsCount++;


                        if (resultsCount >= 100)
                        {
                            Console.WriteLine("... [Results truncated to first 100]");
                            sw.Stop();
                            Console.WriteLine($"Search completed in {sw.ElapsedMilliseconds} ms. Found {resultsCount}+ results.");
                            return;
                        }
                    }
                }
            }

            sw.Stop();
            Console.WriteLine($"Search completed in {sw.ElapsedMilliseconds} ms. Found {resultsCount} results.");
        }

        private static string ResolvePath(Dictionary<ulong, FileEntry> index, ulong frn, string drive)
        {

            List<string> parts = new List<string>();
            ulong currentFrn = frn;


            HashSet<ulong> visited = new HashSet<ulong>();

            while (currentFrn != 0 && visited.Add(currentFrn) && index.TryGetValue(currentFrn, out FileEntry entry))
            {
                parts.Add(entry.Name);
                if (currentFrn == entry.ParentFrn) break;
                currentFrn = entry.ParentFrn;
            }


            parts.Reverse();
            string path = string.Join("\\", parts);
            return Path.Combine(drive, path);
        }
    }
}
