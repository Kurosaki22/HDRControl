using System.Windows.Controls.Primitives;

namespace HDRControl.App.Controls;

// The look lives in the HdrPillToggle style in App.xaml.
public sealed class HdrPillToggle : ToggleButton
{
    public event Action<HdrPillToggle, bool>? Toggled;

    // ToggleButton flips IsChecked with SetCurrentValue, so the one-way binding to the view model survives.
    protected override void OnToggle()
    {
        base.OnToggle();
        Toggled?.Invoke(this, IsChecked == true);
    }
}
