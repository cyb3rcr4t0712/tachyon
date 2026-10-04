using System;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MftSearchWpf.Models;
using MftSearchWpf.Services;

namespace MftSearchWpf.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private List<FileRecord> _allRecords = new List<FileRecord>();
        private readonly ReaderWriterLockSlim _indexLock = new ReaderWriterLockSlim();

        private ObservableCollection<FileRecord> _filteredRecords = new ObservableCollection<FileRecord>();

        private string _searchQuery = string.Empty;
        private string _statusText = "Initializing...";
        private bool _isBusy = true;
        private CancellationTokenSource? _searchCts;
        private UsnWatcherService? _usnWatcher;

        public ICommand CopyPathCommand { get; } = null!;
        public ICommand OpenLocationCommand { get; } = null!;
        public ICommand CopyHashCommand { get; } = null!;
        public ICommand CheckVirusTotalCommand { get; } = null!;
        public ICommand? ForceRescanCommand { get; }

        public MainViewModel()
        {
            if (!MftEngine.IsAdministrator())
            {
                MessageBox.Show("Please run this application as Administrator.", "Admin Rights Required", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            CopyPathCommand = new RelayCommand<FileRecord>(ExecuteCopyPath);
            OpenLocationCommand = new RelayCommand<FileRecord>(ExecuteOpenLocation);
            CopyHashCommand = new RelayCommand<FileRecord>(ExecuteCopyHash);
            CheckVirusTotalCommand = new RelayCommand<FileRecord>(ExecuteCheckVirusTotal);
            ForceRescanCommand = new RelayCommand<object>(_ => _ = ForceFullRescanAsync());

            _ = InitializeAsync();
        }

        public ObservableCollection<FileRecord> FilteredRecords
        {
            get => _filteredRecords;
            set => SetProperty(ref _filteredRecords, value);
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                    ExecuteSearchAsync(value);
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        // -----------------------------------------------------------------------
        // Startup
        // -----------------------------------------------------------------------

        private async Task InitializeAsync()
        {
            IsBusy = true;

            bool loadedFromCache = await TryLoadFromCacheAsync();

            if (!loadedFromCache)
            {
                await FullScanAndSaveAsync();
            }

            StartUsnWatcher();

            IsBusy = false;
        }

        private async Task<bool> TryLoadFromCacheAsync()
        {
            if (!IndexPersistenceService.CacheExists())
                return false;

            DateTime cacheTime = IndexPersistenceService.CacheTimestamp();
            StatusText = $"Loading cached index ({cacheTime:g})...";

            var sw = Stopwatch.StartNew();
            var (records, _) = await IndexPersistenceService.LoadAsync();
            sw.Stop();

            if (records == null || records.Count == 0)
                return false;

            _indexLock.EnterWriteLock();
            try { _allRecords = records; }
            finally { _indexLock.ExitWriteLock(); }

            StatusText = $"Loaded {records.Count.ToString("N0", CultureInfo.InvariantCulture)} files from cache in {sw.ElapsedMilliseconds} ms. Live watch active.";

            Application.Current.Dispatcher.Invoke(() =>
                FilteredRecords = new ObservableCollection<FileRecord>());

            return true;
        }

        private async Task FullScanAndSaveAsync()
        {
            StatusText = "Full MFT scan in progress...";
            var sw = Stopwatch.StartNew();

            try
            {
                var records = await MftEngine.BuildIndexAsync();
                sw.Stop();

                _indexLock.EnterWriteLock();
                try { _allRecords = records; }
                finally { _indexLock.ExitWriteLock(); }

                StatusText = $"Indexed {records.Count.ToString("N0", CultureInfo.InvariantCulture)} files in {sw.ElapsedMilliseconds} ms. Saving to cache...";

                Application.Current.Dispatcher.Invoke(() =>
                    FilteredRecords = new ObservableCollection<FileRecord>());

                await IndexPersistenceService.SaveAsync(records);
                StatusText = $"Indexed {records.Count.ToString("N0", CultureInfo.InvariantCulture)} files in {sw.ElapsedMilliseconds} ms. Live watch active.";
            }
            catch (Exception ex)
            {
                sw.Stop();
                StatusText = $"Error during indexing: {ex.Message}";
            }
        }

        private async Task ForceFullRescanAsync()
        {
            _usnWatcher?.Stop();
            _usnWatcher?.Dispose();
            _usnWatcher = null;

            IsBusy = true;
            IndexPersistenceService.DeleteCache();
            await FullScanAndSaveAsync();
            StartUsnWatcher();
            IsBusy = false;
        }

        // -----------------------------------------------------------------------
        // USN Watcher
        // -----------------------------------------------------------------------

        private void StartUsnWatcher()
        {
            _usnWatcher?.Stop();
            _usnWatcher?.Dispose();

            _usnWatcher = new UsnWatcherService(_allRecords, _indexLock, msg =>
            {
                Application.Current.Dispatcher.InvokeAsync(() => StatusText = msg);
            });

            _usnWatcher.Start();
        }

        // -----------------------------------------------------------------------
        // Search
        // -----------------------------------------------------------------------

        private async void ExecuteSearchAsync(string query)
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            if (string.IsNullOrWhiteSpace(query))
            {
                Application.Current.Dispatcher.Invoke(() =>
                    FilteredRecords = new ObservableCollection<FileRecord>());
                return;
            }

            try
            {
                var results = await Task.Run(() =>
                {
                    _indexLock.EnterReadLock();
                    try
                    {
                        var matched = new List<FileRecord>(200);
                        foreach (var r in _allRecords)
                        {
                            if (token.IsCancellationRequested)
                                token.ThrowIfCancellationRequested();

                            if (r.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                matched.Add(r);
                                if (matched.Count >= 200)
                                    break;
                            }
                        }
                        return matched;
                    }
                    finally { _indexLock.ExitReadLock(); }
                }, token);

                if (!token.IsCancellationRequested)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        FilteredRecords = new ObservableCollection<FileRecord>(results));
                }
            }
            catch (OperationCanceledException) { }
        }

        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        private void ExecuteCopyPath(FileRecord record)
        {
            if (record != null && !string.IsNullOrEmpty(record.FullPath))
            {
                Clipboard.SetText(record.FullPath);
                StatusText = "Path copied to clipboard.";
            }
        }

        private void ExecuteOpenLocation(FileRecord record)
        {
            if (record != null && !string.IsNullOrEmpty(record.FullPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{record.FullPath}\"",
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    StatusText = $"Failed to open location: {ex.Message}";
                }
            }
        }

        private async void ExecuteCopyHash(FileRecord record)
        {
            if (record != null && !string.IsNullOrEmpty(record.FullPath))
            {
                try
                {
                    StatusText = "Calculating SHA256...";
                    string hash = await CalculateSha256Async(record.FullPath);
                    Clipboard.SetText(hash);
                    StatusText = "SHA256 copied to clipboard.";
                }
                catch (Exception ex)
                {
                    StatusText = $"Hash failed: {ex.Message}";
                }
            }
        }

        private async void ExecuteCheckVirusTotal(FileRecord record)
        {
            if (record != null && !string.IsNullOrEmpty(record.FullPath))
            {
                try
                {
                    StatusText = "Calculating SHA256 for VirusTotal...";
                    string hash = await CalculateSha256Async(record.FullPath);
                    string url = $"https://www.virustotal.com/gui/search/{hash}";
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    StatusText = "Opened VirusTotal in browser.";
                }
                catch (Exception ex)
                {
                    StatusText = $"VirusTotal check failed: {ex.Message}";
                }
            }
        }

        private Task<string> CalculateSha256Async(string filePath)
        {
            return Task.Run(() =>
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha256 = SHA256.Create();
                var hashBytes = sha256.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            });
        }
    }
}
