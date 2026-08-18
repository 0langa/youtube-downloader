using TubeForge.App.Commands;
using TubeForge.Downloads.History;

namespace TubeForge.App.ViewModels;

public sealed class HistoryItemViewModel
{
    private readonly Func<string, bool> _isPresent;

    public HistoryItemViewModel(
        DownloadHistoryEntry entry,
        Action<string> reveal,
        Func<Guid, Task> remove,
        Func<string, bool> isPresent)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        ArgumentNullException.ThrowIfNull(reveal);
        ArgumentNullException.ThrowIfNull(remove);
        _isPresent = isPresent ?? throw new ArgumentNullException(nameof(isPresent));
        // Presence is answered from a cache the view model refreshes off the UI thread. Calling
        // File.Exists here would stat every row during rendering, and a row pointing at a
        // disconnected share would block the window for the length of the network timeout.
        RevealCommand = new RelayCommand(
            () => reveal(Entry.DestinationPath),
            () => _isPresent(Entry.DestinationPath));
        RemoveCommand = new AsyncRelayCommand(() => remove(Entry.Id));
    }

    public DownloadHistoryEntry Entry { get; }

    public Guid Id => Entry.Id;

    public string Title => Entry.DisplayTitle;

    public string Detail =>
        $"{Entry.CompletedAtUtc.ToLocalTime():g} · {FormatBytes(Entry.BytesWritten)} · " +
        (_isPresent(Entry.DestinationPath) ? "file available" : "file moved or deleted");

    public string Destination => Entry.DestinationPath;

    public RelayCommand RevealCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    private static string FormatBytes(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024
            ? $"{megabytes / 1024:0.00} GB"
            : $"{megabytes:0.#} MB";
    }
}
