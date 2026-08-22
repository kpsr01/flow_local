using System.Runtime.InteropServices;
using System.Windows.Automation;
using FlowLocal.Core;

namespace FlowLocal.App;

internal static class UiAutomationInsertion
{
    internal static InsertionAttempt TryInsert(ActiveTarget target, string text)
    {
        if (!target.IsInjectionSafe || target.IsPasswordField != false)
            return InsertionAttempt.Unsupported("Direct insertion is blocked for protected or higher/unknown integrity targets.");

        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || !BelongsToTarget(element, target) || !MatchesCapture(element, target))
                return InsertionAttempt.Unsupported("The focused automation element no longer matches the captured target.");

            var current = element.Current;
            if (!current.IsEnabled || current.IsPassword ||
                current.ControlType != ControlType.Edit ||
                Supports(element, TextPattern.Pattern) ||
                !element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
                valueObject is not ValuePattern value || value.Current.IsReadOnly)
                return InsertionAttempt.Unsupported("The focused control is not a safe writable single-line value control.");

            try
            {
                value.SetValue(text);
                return InsertionAttempt.Inserted();
            }
            catch (Exception exception) when (IsAutomationFailure(exception))
            {
                return InsertionAttempt.UnknownSideEffect(exception.Message);
            }
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return InsertionAttempt.Failed(exception.Message);
        }
    }

    private static bool MatchesCapture(AutomationElement element, ActiveTarget target)
    {
        var current = element.Current;
        return (string.IsNullOrEmpty(target.FocusedAutomationId) || current.AutomationId == target.FocusedAutomationId) &&
               (string.IsNullOrEmpty(target.FocusedControlType) ||
                current.ControlType?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == target.FocusedControlType) &&
               (string.IsNullOrEmpty(target.FocusedName) || current.Name == target.FocusedName);
    }

    private static bool BelongsToTarget(AutomationElement element, ActiveTarget target)
    {
        for (var current = element; current is not null; current = TreeWalker.RawViewWalker.GetParent(current))
        {
            var handle = (nint)current.Current.NativeWindowHandle;
            if (handle == target.WindowHandle || target.FocusedChildWindowHandle != 0 && handle == target.FocusedChildWindowHandle)
                return true;
        }
        return false;
    }

    private static bool Supports(AutomationElement element, AutomationPattern pattern) =>
        element.TryGetCurrentPattern(pattern, out _);

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or InvalidOperationException or COMException;
}
