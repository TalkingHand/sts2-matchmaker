using System;
using System.Collections.Generic;
using Godot;

namespace Sts2Matchmaker.UI;

/// <summary>
/// Label/value dropdown row - Sts2NativeDropdown itself only knows about display strings (see its own doc), so
/// this pairs each option's display label with an underlying value of any type (a language code, a GameMode enum,
/// a player-count int, an ascension level int with an "무관" sentinel) and keeps the two in sync, replacing the
/// parallel-list-plus-OptionButton pattern MatchConditionsPanel used to hand-roll separately for each of its four
/// dropdowns (see its own git history - _languageCodesByIndex was the original one-off version of this).
/// Falls back to Sts2ModalPanel.StyleAsSettingsDropdown (a plain themed OptionButton) if the native scene fails to
/// load, exactly like BanConfirmPanel's own reason picker does - a row never just goes missing.
/// </summary>
internal sealed class NativeDropdownField<T> where T : notnull
{
    public Control Control { get; }
    public T Value { get; private set; }

    // Used by SetValue to fall back the same way BuildOrFallback's own initial resolution does, when asked to
    // select a value that isn't actually one of the options (e.g. LoadFrom restoring a saved language code that's
    // no longer in the list) - without this, Value and what's actually on screen could end up out of sync.
    private readonly Dictionary<T, string> _labelByValue;
    private readonly T _fallbackValue;
    private readonly Action<T> _applyValue;
    private readonly Action<bool>? _setDisabled;

    private NativeDropdownField(Control control, T initialValue, Dictionary<T, string> labelByValue, T fallbackValue, Action<T> applyValue, Action<bool>? setDisabled)
    {
        Control = control;
        Value = initialValue;
        _labelByValue = labelByValue;
        _fallbackValue = fallbackValue;
        _applyValue = applyValue;
        _setDisabled = setDisabled;
    }

    /// <summary>Programmatically changes the selected value (e.g. MatchConditionsPanel.LoadFrom restoring a saved
    /// setting) - updates both the displayed control and Value. Doesn't fire onChanged - the caller already knows
    /// the value it's setting, so that would just be a redundant echo (same reasoning as Sts2NativeDropdown.Handle.
    /// SetValue, which the native path sits on top of).</summary>
    public void SetValue(T value)
    {
        T resolved = _labelByValue.ContainsKey(value) ? value : _fallbackValue;
        Value = resolved;
        _applyValue(resolved);
    }

    /// <summary>Dims and locks the control - backed by Sts2NativeDropdown.Handle.Disabled on the native path, or
    /// Godot's own Button.Disabled on the OptionButton fallback.</summary>
    public bool Disabled
    {
        set => _setDisabled?.Invoke(value);
    }

    /// <summary>
    /// Builds a dropdown for (label, value) options - initialValue selects the starting option, falling back to
    /// the first option if initialValue isn't among the values. onChanged fires whenever the user actually picks a
    /// different option (not for SetValue calls). overlayParent is forwarded to Sts2NativeDropdown.TryBuild - see
    /// its own doc for why a long option list (e.g. ascension's) needs one to avoid being silently clipped by an
    /// ancestor ScrollContainer.
    /// </summary>
    public static NativeDropdownField<T> BuildOrFallback(IReadOnlyList<(string Label, T Value)> options, T initialValue, Action<T>? onChanged = null, Control? overlayParent = null)
    {
        var valueByLabel = new Dictionary<string, T>();
        var labelByValue = new Dictionary<T, string>();
        var labels = new List<string>();
        foreach ((string label, T value) in options)
        {
            valueByLabel[label] = value;
            labelByValue[value] = label;
            labels.Add(label);
        }
        T fallbackValue = options.Count > 0 ? options[0].Value : initialValue;
        T resolvedInitial = labelByValue.ContainsKey(initialValue) ? initialValue : fallbackValue;
        string initialLabel = labelByValue.TryGetValue(resolvedInitial, out string? resolvedLabel) ? resolvedLabel
            : (labels.Count > 0 ? labels[0] : string.Empty);

        NativeDropdownField<T> field = null!;
        Sts2NativeDropdown.Handle? handle = Sts2NativeDropdown.TryBuild(labels, initialLabel, label =>
        {
            if (valueByLabel.TryGetValue(label, out T? value))
            {
                // Was missing entirely - onChanged alone doesn't update Value, so anything that reads Value
                // afterward (ApplyTo/SelectedMaxPlayers/etc) kept seeing the ORIGINAL construction-time value no
                // matter what the user actually clicked, since Sts2NativeDropdown's own currentValue (what's
                // visibly displayed) lives entirely separately from this field's Value. The fallback OptionButton
                // path below already did this correctly via its own ItemSelected handler - this brings the native
                // path in line with it.
                field.Value = value!;
                onChanged?.Invoke(value!);
            }
        }, overlayParent);

        if (handle != null)
        {
            field = new NativeDropdownField<T>(
                handle.Control,
                resolvedInitial,
                labelByValue,
                fallbackValue,
                value =>
                {
                    if (labelByValue.TryGetValue(value, out string? label))
                    {
                        handle.SetValue(label);
                    }
                },
                disabled => handle.Disabled = disabled);
            return field;
        }

        // Fallback: plain themed OptionButton, same as BanConfirmPanel.BuildFallbackReasonDropdown.
        var optionButton = new OptionButton();
        foreach ((string label, T _) in options)
        {
            optionButton.AddItem(label);
        }
        int initialIndex = IndexOfValue(options, resolvedInitial);
        optionButton.Select(Math.Max(0, initialIndex));
        Control wrapper = Sts2ModalPanel.StyleAsSettingsDropdown(optionButton);

        field = new NativeDropdownField<T>(
            wrapper,
            resolvedInitial,
            labelByValue,
            fallbackValue,
            value =>
            {
                int index = IndexOfValue(options, value);
                if (index >= 0)
                {
                    optionButton.Select(index);
                }
            },
            disabled => optionButton.Disabled = disabled);
        optionButton.ItemSelected += index =>
        {
            T value = options[(int)index].Value;
            field.Value = value;
            onChanged?.Invoke(value);
        };
        return field;
    }

    private static int IndexOfValue(IReadOnlyList<(string Label, T Value)> options, T value)
    {
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < options.Count; i++)
        {
            if (comparer.Equals(options[i].Value, value))
            {
                return i;
            }
        }
        return -1;
    }
}
