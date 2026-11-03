using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the person chose in <see cref="LockFailedWindow"/> (or the timer did).</summary>
public enum LockFailedChoice { Retry, Restart, Shutdown }

/// <summary>
/// Shown when the HUD cannot run and Windows cannot be locked: a plain window on top, no WebView, that keeps the desktop closed. Three
/// buttons (try again, restart, shut down) for the controller (D-pad and A), the keyboard and the mouse; it tries again by itself
/// every few seconds.
/// </summary>
public sealed class LockFailedWindow : Window
{
    private static readonly TimeSpan AutoRetry = TimeSpan.FromSeconds(5);

    private readonly List<Button> _buttons = [];
    private readonly DispatcherTimer _timer = new() { Interval = AutoRetry };
    private TaskCompletionSource<LockFailedChoice>? _choice;
    private int _focus;

    public LockFailedWindow()
    {
        Title = "CouchHUD";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x07, 0x09, 0x0F));

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 900 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("fallback.lockFailed.text"),
            Foreground = Brushes.White,
            FontSize = 32,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 48)
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        AddButton(row, "fallback.lockFailed.retry", LockFailedChoice.Retry);
        AddButton(row, "fallback.lockFailed.restart", LockFailedChoice.Restart);
        AddButton(row, "fallback.lockFailed.shutdown", LockFailedChoice.Shutdown);
        panel.Children.Add(row);
        Content = panel;

        _timer.Tick += (_, _) => Finish(LockFailedChoice.Retry);
        Loaded += (_, _) => FocusButton(0);
        // Keyboard: arrows move, Enter and Space press the focused button (WPF's own handling), Tab works as usual.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Up) { FocusButton(_focus - 1); e.Handled = true; }
            else if (e.Key is Key.Right or Key.Down) { FocusButton(_focus + 1); e.Handled = true; }
        };
    }

    private void AddButton(Panel row, string key, LockFailedChoice choice)
    {
        var button = new Button
        {
            Content = Strings.Get(key),
            FontSize = 26,
            Padding = new Thickness(28, 14, 28, 14),
            Margin = new Thickness(12, 0, 12, 0),
            MinWidth = 220
        };
        button.Click += (_, _) => Finish(choice);
        button.GotKeyboardFocus += (_, _) => _focus = _buttons.IndexOf(button);
        _buttons.Add(button);
        row.Children.Add(button);
    }

    private void FocusButton(int index)
    {
        _focus = (index % _buttons.Count + _buttons.Count) % _buttons.Count;
        _buttons[_focus].Focus();
        Keyboard.Focus(_buttons[_focus]);
    }

    /// <summary>A controller button (names as <c>XInputPoller</c> raises them); call on the UI thread.</summary>
    public void PadButton(string button)
    {
        switch (button)
        {
            case "left" or "up": FocusButton(_focus - 1); break;
            case "right" or "down": FocusButton(_focus + 1); break;
            case "a": _buttons[_focus].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
        }
    }

    /// <summary>Shows the window (once) and waits for a choice; without one, a retry follows after a few seconds.</summary>
    public Task<LockFailedChoice> AskAsync()
    {
        if (!IsVisible)
        {
            Show();
            Activate();
        }
        _choice = new TaskCompletionSource<LockFailedChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _timer.Start();
        return _choice.Task;
    }

    private void Finish(LockFailedChoice choice)
    {
        _timer.Stop();
        _choice?.TrySetResult(choice);
    }
}
