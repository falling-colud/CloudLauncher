using System.Windows.Controls;

namespace CloudLauncher.Controls;

/// <summary>
/// A CheckBox that reports a click and leaves its tick to its binding.
/// </summary>
/// <remarks>
/// <para>A folder in the export tree can be ticked, unticked or partly ticked, and that state
/// belongs to the selection: clicking a partly ticked folder should tick everything in it. A stock
/// CheckBox toggles itself first and sets <c>IsChecked</c> locally, which breaks a one-way binding.
/// This one does nothing on toggle; the <c>Click</c> handler decides and the binding
/// repaints.</para>
/// <para>Implicit styles match by exact type, so apply the app's look with
/// <c>Style="{StaticResource {x:Type CheckBox}}"</c>.</para>
/// </remarks>
public sealed class SelectionCheckBox : CheckBox
{
    protected override void OnToggle()
    {
        // No self-toggle (see the remarks). Click is still raised after this.
    }
}
