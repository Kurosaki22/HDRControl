using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HDRControl.App.Controls;

public sealed class HdrPillToggle : UserControl
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(
            nameof(IsOn),
            typeof(bool),
            typeof(HdrPillToggle),
            new PropertyMetadata(false, OnIsOnChanged));

    private readonly Border _track;
    private readonly Border _thumb;

    public HdrPillToggle()
    {
        _thumb = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2)
        };
        _track = new Border
        {
            Width = 36,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Child = _thumb
        };

        var button = new Button
        {
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Content = _track,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += ToggleButton_Click;
        Content = button;
        IsTabStop = false;
        UpdateVisualState();
    }

    public event Action<HdrPillToggle, bool>? Toggled;

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    private static void OnIsOnChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is HdrPillToggle toggle)
        {
            toggle.UpdateVisualState();
        }
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs args)
    {
        IsOn = !IsOn;
        Toggled?.Invoke(this, IsOn);
    }

    private void UpdateVisualState()
    {
        _track.Background = new SolidColorBrush(IsOn
            ? Color.FromArgb(255, 10, 132, 255)
            : Color.FromArgb(255, 142, 142, 147));
        _thumb.HorizontalAlignment = IsOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
