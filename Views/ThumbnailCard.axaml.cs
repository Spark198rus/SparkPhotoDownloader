using Avalonia.Controls;
using Avalonia.Input;
using SparkPhotoDownloader.ViewModels;

namespace SparkPhotoDownloader.Views;

public partial class ThumbnailCard : UserControl
{
    public ThumbnailCard()
    {
        InitializeComponent();

        // Focusable so that Space and Cmd/Ctrl+A reach the window after a click on a card.
        Focusable = true;

        // A click anywhere on the card selects it. The checkbox handles its own press, so
        // ticking it does not change the selection.
        AddHandler(PointerPressedEvent, OnPressed);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ThumbnailViewModel { Owner: { } owner } file)
            return;

        var point = e.GetCurrentPoint(this);
        var modifiers = e.KeyModifiers;

        // Cmd on macOS, Ctrl elsewhere; both are accepted so a PC keyboard on a Mac works.
        var toggle = modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control);
        var range = modifiers.HasFlag(KeyModifiers.Shift);

        if (point.Properties.IsLeftButtonPressed)
        {
            // Ctrl-click is a right-click on macOS; leave that to the context menu.
            if (OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Control) &&
                !modifiers.HasFlag(KeyModifiers.Meta))
            {
                return;
            }

            owner.SelectFile(file, toggle, range);
            Focus();
        }
        else if (point.Properties.IsRightButtonPressed && !file.IsSelected)
        {
            // Right-clicking outside the selection retargets it, as in Finder.
            owner.SelectFile(file, toggle: false, range: false);
        }
    }
}
