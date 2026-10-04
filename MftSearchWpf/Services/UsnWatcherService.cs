using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MftSearchWpf.Models;

namespace MftSearchWpf.Services
{
    public sealed class UsnWatcherService : IDisposable
    {
        // ------------------------------------------------------------------ P/Invoke
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ        = 0x80000000;
        private const uint FILE_SHARE_READ     = 0x00000001;
        private const uint FILE_SHARE_WRITE    = 0x00000002;
        private const uint OPEN_EXISTING       = 3;
        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
        private const uint FSCTL_READ_USN_JOURNAL  = 0x000900bb;

        // USN reason flags we care about
        private const uint USN_REASON_FILE_CREATE    = 0x00000100;
        private const uint USN_REASON_FILE_DELETE    = 0x00000200;
        private const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
        private const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct USN_JOURNAL_DATA_V0
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
        private struct READ_USN_JOURNAL_DATA_V0
        {
            public long StartUsn;
            public uint ReasonMask;
            public uint ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
        }

        // ------------------------------------------------------------------ Fields
        private readonly List<FileRecord> _masterIndex;
        private readonly ReaderWriterLockSlim _indexLock;
        private readonly Action<string> _onStatusUpdate;
        private CancellationTokenSource? _cts;
        private Task? _watchTask;

        public UsnWatcherService(List<FileRecord> masterIndex, ReaderWriterLockSlim indexLock, Action<string> onStatusUpdate)
        {
            _masterIndex = masterIndex;
            _indexLock = indexLock;
            _onStatusUpdate = onStatusUpdate;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _watchTask = Task.Factory.StartNew(
                () => WatchLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private void WatchLoop(CancellationToken token)
        {
            var drives = GetNtfsDrives();

            // Each drive gets its own journal cursor
            var cursors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var journalIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

            // Prime the cursors to NextUsn so we only see NEW changes from now on
            foreach (var drive in drives)
            {
                string volumePath = @"\\.\" + drive.TrimEnd('\\');
                IntPtr hVol = CreateFile(volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (hVol == INVALID_HANDLE_VALUE) continue;

                try
                {
                    var journal = QueryJournal(hVol);
                    if (journal.HasValue)
                    {
                        cursors[drive] = journal.Value.NextUsn;
                        journalIds[drive] = journal.Value.UsnJournalID;
                    }
                }
                finally { CloseHandle(hVol); }
            }

            int bufferSize = 64 * 1024;
            IntPtr pBuffer = Marshal.AllocHGlobal(bufferSize);
            IntPtr pReadData = Marshal.AllocHGlobal(Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>());

            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool anyChanges = false;

                    foreach (var drive in drives)
                    {
                        if (token.IsCancellationRequested) break;
                        if (!cursors.TryGetValue(drive, out long startUsn)) continue;
                        if (!journalIds.TryGetValue(drive, out ulong journalId)) continue;

                        string volumePath = @"\\.\" + drive.TrimEnd('\\');
                        IntPtr hVol = CreateFile(volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (hVol == INVALID_HANDLE_VALUE) continue;

                        try
                        {
                            var readData = new READ_USN_JOURNAL_DATA_V0
                            {
                                StartUsn = startUsn,
                                ReasonMask = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE
                                           | USN_REASON_RENAME_NEW_NAME | USN_REASON_RENAME_OLD_NAME,
                                ReturnOnlyOnClose = 0,
                                Timeout = 0,
                                BytesToWaitFor = 0,
                                UsnJournalID = journalId
                            };

                            Marshal.StructureToPtr(readData, pReadData, false);

                            bool ok = DeviceIoControl(hVol, FSCTL_READ_USN_JOURNAL,
                                pReadData, (uint)Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(),
                                pBuffer, (uint)bufferSize, out uint bytesReturned, IntPtr.Zero);

                            if (!ok)
                            {
                                int err = Marshal.GetLastWin32Error();
                                // Journal wrapped or invalidated - trigger a full rescan signal
                                if (err == 234 /*ERROR_MORE_DATA*/ || err == 1181 /*ERROR_JOURNAL_ENTRY_DELETED*/)
                                {
                                    _onStatusUpdate("USN journal wrapped. Triggering rescan...");
                                    cursors[drive] = 0;
                                }
                                continue;
                            }

                            if (bytesReturned <= 8) continue;

                            // Update cursor to next USN
                            long nextUsn = Marshal.ReadInt64(pBuffer);
                            cursors[drive] = nextUsn;

                            // Parse records
                            var changes = ParseUsnRecords(pBuffer, bytesReturned, drive);
                            if (changes.Count > 0)
                            {
                                ApplyChanges(changes);
                                anyChanges = true;
                            }
                        }
                        finally { CloseHandle(hVol); }
                    }

                    if (!anyChanges)
                        Thread.Sleep(500);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuffer);
                Marshal.FreeHGlobal(pReadData);
            }
        }

        private readonly record struct UsnChange(string FileName, string Drive, uint Reason, ulong Frn, ulong ParentFrn);

        private unsafe List<UsnChange> ParseUsnRecords(IntPtr pBuffer, uint bytesReturned, string drive)
        {
            var changes = new List<UsnChange>();
            byte* ptr = (byte*)pBuffer.ToPointer();
            int offset = 8; // skip the next USN cursor qword

            while (offset < bytesReturned)
            {
                byte* recordPtr = ptr + offset;
                uint recordLength = *(uint*)recordPtr;
                if (recordLength == 0) break;

                ushort majorVersion = *(ushort*)(recordPtr + 4);

                if (majorVersion == 2)
                {
                    ulong frn       = *(ulong*)(recordPtr + 8);
                    ulong parentFrn = *(ulong*)(recordPtr + 16);
                    uint reason     = *(uint*)(recordPtr + 40);
                    ushort fnLen    = *(ushort*)(recordPtr + 56);
                    ushort fnOffset = *(ushort*)(recordPtr + 58);

                    var span = new ReadOnlySpan<char>(recordPtr + fnOffset, fnLen / 2);
                    string fileName = new string(span);

                    changes.Add(new UsnChange(fileName, drive, reason, frn, parentFrn));
                }

                offset += (int)recordLength;
            }

            return changes;
        }

        private void ApplyChanges(List<UsnChange> changes)
        {
            _indexLock.EnterWriteLock();
            try
            {
                foreach (var change in changes)
                {
                    bool isCreate    = (change.Reason & USN_REASON_FILE_CREATE)      != 0;
                    bool isDelete    = (change.Reason & USN_REASON_FILE_DELETE)      != 0;
                    bool isRenameNew = (change.Reason & USN_REASON_RENAME_NEW_NAME)  != 0;
                    bool isRenameOld = (change.Reason & USN_REASON_RENAME_OLD_NAME)  != 0;

                    if (isDelete || isRenameOld)
                    {
                        int idx = _masterIndex.FindIndex(r =>
                            r.FileName.Equals(change.FileName, StringComparison.OrdinalIgnoreCase) &&
                            r.FullPath.StartsWith(change.Drive, StringComparison.OrdinalIgnoreCase));

                        if (idx >= 0)
                            _masterIndex.RemoveAt(idx);
                    }

                    if (isCreate || isRenameNew)
                    {
                        _masterIndex.Add(new FileRecord
                        {
                            FileName = change.FileName,
                            FullPath = Path.Combine(change.Drive, change.FileName)
                        });
                    }
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            _onStatusUpdate($"Live index updated (+{changes.Count} changes).");
        }

        private USN_JOURNAL_DATA_V0? QueryJournal(IntPtr hVolume)
        {
            int size = Marshal.SizeOf<USN_JOURNAL_DATA_V0>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(hVolume, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, p, (uint)size, out _, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static List<string> GetNtfsDrives()
        {
            var result = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                    result.Add(d.Name);
            return result;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
