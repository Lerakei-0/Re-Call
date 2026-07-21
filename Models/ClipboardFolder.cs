using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace ReCall.Models;

/// <summary>A user-created page that clipboard items can be filed into.
/// Unlike the old tag-based design, a folder now owns its own independent
/// list of ClipboardItem instances (see Items below) rather than items
/// merely being tagged with the folder's Id -- "Move to folder" transfers
/// an item's actual object reference out of the main history and into
/// Items; "Add to folder" clones it (see ClipboardItem.Clone) so the
/// original in the main list and the copy here are two fully independent
/// items from that point on. Rendered as a chip in the sticky folders bar
/// at the bottom of the panel (see ClipboardPanelWindow.xaml, the
/// Grid.Row="3" Border above the search footer); clicking a chip navigates
/// into this folder's own page in-place (see ClipboardPanelWindow.
/// EnterFolder), swapping PinnedList/HistoryList's source over to Items
/// below instead of pushing a new page/ListView. Persisted separately from
/// the main clipboard history (see HistoryStore.LoadFolders/SaveAll).</summary>
public class ClipboardFolder : INotifyPropertyChanged
{
    /// <summary>Stable identity used only for the on-disk folders
    /// manifest and to compare folder references (e.g. ClipboardPanelWindow.
    /// _currentFolder). Assigned once, never reused.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = "New Folder";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>This folder's own items -- populated by "Move to folder"
    /// (transfers the original item) and "Add to folder" (inserts an
    /// independent clone), and by HistoryStore.LoadFolders at startup.
    /// Completely separate from ClipboardPanelWindow.Items; an item here
    /// has no ongoing relationship to wherever it came from.</summary>
    public ObservableCollection<ClipboardItem> Items { get; } = new();

    /// <summary>Default tint for newly-created folders and for folders
    /// loaded from a manifest that predates this field (see
    /// HistoryStore.FolderRecord.Color and its load site in
    /// ClipboardPanelWindow). Also always the first swatch in Palette
    /// below, so it's what a fresh "New folder" dialog shows pre-selected.</summary>
    public const string DefaultColor = "#FBBF24";

    private string _color = DefaultColor;

    /// <summary>Hex tint (e.g. "#FBBF24") for this folder's glyph in the
    /// folders bar and folder-page header (see the chip DataTemplate in
    /// ClipboardPanelWindow.xaml, FolderHeaderIcon, and Brush below).
    /// Chosen from Palette via the swatch picker built by
    /// BuildColorSwatchPicker in CreateFolderViaDialogAsync/
    /// RenameFolderAsync.</summary>
    public string Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
        }
    }

    /// <summary>Color, pre-parsed into a Brush the glyph's Foreground can
    /// x:Bind straight to. Recomputed on every read rather than cached --
    /// folder chips are short-lived DataTemplate instances, so there's no
    /// benefit to caching, and this keeps Color's setter above from having
    /// to know anything about Brush's representation.</summary>
    public SolidColorBrush Brush => new(ParseColor(_color));

    /// <summary>Swatch choices offered by the color picker in the Create/
    /// Rename dialogs (see BuildColorSwatchPicker in
    /// ClipboardPanelWindow.xaml.cs) -- eight mid-saturation tones chosen
    /// so the tinted glyph stays legible on both the Light and Dark
    /// folders-bar background. DefaultColor is deliberately first so it's
    /// the pre-selected swatch for a brand new folder.</summary>
    public static readonly string[] Palette =
    {
        DefaultColor, // amber
        "#F87171", // red
        "#FB923C", // orange
        "#4ADE80", // green
        "#22D3EE", // cyan
        "#60A5FA", // blue
        "#A78BFA", // purple
        "#F472B6", // pink
    };

    /// <summary>Parses a "#RRGGBB" string into a Windows.UI.Color, falling
    /// back to DefaultColor for anything malformed -- e.g. a hand-edited
    /// manifest -- rather than throwing and losing the whole folders bar.</summary>
    public static Windows.UI.Color ParseColor(string hex)
    {
        try
        {
            var value = hex.TrimStart('#');
            var r = Convert.ToByte(value.Substring(0, 2), 16);
            var g = Convert.ToByte(value.Substring(2, 2), 16);
            var b = Convert.ToByte(value.Substring(4, 2), 16);
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }
        catch
        {
            return hex == DefaultColor ? Windows.UI.Color.FromArgb(255, 251, 191, 36) : ParseColor(DefaultColor);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
