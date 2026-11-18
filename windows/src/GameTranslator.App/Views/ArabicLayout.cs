// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using GameTranslator.App.Controls;

namespace GameTranslator.App.Views;

/// <summary>
/// Arabic edition: screens are laid out right to left (menus and labels start at the right, text aligns right).
/// What shows or measures the game picture is kept left to right, or it would be drawn mirrored and the subtitle area
/// marked on it would land on the wrong side: pictures, the canvas drawn over the live picture, the area picker and
/// the sliders that move its edges. Boxes for addresses, codes and keys stay left to right too.
/// </summary>
internal static class ArabicLayout
{
    /// <summary>Lays the element out right to left, now and again once it is loaded (content added later is covered).</summary>
    public static void Apply(FrameworkElement root)
    {
        root.FlowDirection = FlowDirection.RightToLeft;
        KeepPicturesAndInputsLeftToRight(root);
        root.Loaded += (_, _) => KeepPicturesAndInputsLeftToRight(root);
    }

    private static void KeepPicturesAndInputsLeftToRight(DependencyObject node)
    {
        if (node is Image or Canvas or RegionPicker or Slider or TextBox or PasswordBox)
        {
            ((FrameworkElement)node).FlowDirection = FlowDirection.LeftToRight;
            // Nothing inside these needs a right-to-left layout.
            return;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element) KeepPicturesAndInputsLeftToRight(element);
    }
}
