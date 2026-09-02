using System;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.DevUI.Controls;

internal enum DryCycleTextValidationState
{
    Valid,
    Intermediate,
    Invalid
}

/// <summary>
/// Single-line DevUI text field with transactional focus, validation, selection,
/// clipboard editing and undo/redo. It is intentionally independent of RegionKit;
/// the interaction model is implemented locally so DryCycle can use it everywhere.
/// </summary>
internal class DryCycleTextField : DevUILabel
{
    internal delegate DryCycleTextValidationState Validator(string text);
    internal delegate bool CharacterFilter(char character);

    private const int UndoLimit = 32;
    private const float CaretBlinkPeriod = 1f;
    private const float CaretVisibleFraction = 0.56f;

    private static readonly Color IdleBorderColor = new(1f, 1f, 1f, 0f);
    private static readonly Color FocusBorderColor = new(0.25f, 0.65f, 1f, 1f);
    private static readonly Color IntermediateBorderColor = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Color InvalidBorderColor = new(1f, 0.2f, 0.2f, 1f);
    private static readonly Color SelectionColor = new(0.25f, 0.55f, 1f, 0.24f);

    private readonly Validator _validator;
    private readonly CharacterFilter _characterFilter;
    private readonly FSprite[] _outlineSprites = new FSprite[4];
    private readonly FSprite _selectionSprite;
    private readonly FSprite _caretSprite;
    private readonly List<EditSnapshot> _undo = new(UndoLimit);
    private readonly List<EditSnapshot> _redo = new(UndoLimit);

    private string _acceptedText;
    private string _transactionStartText;
    private string _buffer;
    private int _caret;
    private int _selectionAnchor;
    private float[] _caretPositions = new float[1];
    private bool _mouseSelecting;
    private bool _selectAllOnFocus;
    private int _maxLength;
    private float _lastEditTime;
    private DryCycleTextValidationState _validationState;
    private Func<string> _idleDisplayProvider;
    private string _idleDisplayCache;
    private bool _focusVisualActive;
    private DryCycleTextValidationState _lastBorderValidationState;

    internal event Action<DryCycleTextField, string, string> AcceptedTextChanged;
    internal event Action<DryCycleTextField, string> EditCommitted;
    internal event Action<DryCycleTextField, string> EditCancelled;

    /// <summary>
    /// Optional non-editing presentation. Returning null uses AcceptedText.
    /// This is useful for vanilla state decorations such as &lt;A&gt;, &lt;T&gt; and NONE.
    /// </summary>
    internal Func<string> IdleDisplayProvider
    {
        get => _idleDisplayProvider;
        set
        {
            _idleDisplayProvider = value;
            RefreshIdleDisplayCache();
            RefreshVisualState();
        }
    }

    internal string AcceptedText => _acceptedText;
    internal string EditText => _buffer;
    internal DryCycleTextValidationState ValidationState => _validationState;
    internal bool IsFocused => DryCycleInputFocus.Focused == this;

    internal bool SelectAllOnFocus
    {
        get => _selectAllOnFocus;
        set => _selectAllOnFocus = value;
    }

    internal int MaxLength
    {
        get => _maxLength;
        set => _maxLength = Math.Max(1, value);
    }

    internal DryCycleTextField(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        float width,
        string initialText,
        Validator validator,
        CharacterFilter characterFilter = null,
        int maxLength = 64,
        bool selectAllOnFocus = false)
        : base(owner, IDstring, parentNode, pos, width, initialText ?? string.Empty)
    {
        _validator = validator ?? (_ => DryCycleTextValidationState.Valid);
        _characterFilter = characterFilter;
        _maxLength = Math.Max(1, maxLength);
        _selectAllOnFocus = selectAllOnFocus;

        string safeInitial = initialText ?? string.Empty;
        _validationState = _validator(safeInitial);
        if (_validationState != DryCycleTextValidationState.Valid)
        {
            throw new ArgumentException("Text field initial value must pass validation.", nameof(initialText));
        }

        _acceptedText = safeInitial;
        _transactionStartText = safeInitial;
        _buffer = safeInitial;
        _caret = _buffer.Length;
        _selectionAnchor = _caret;

        for (int i = 0; i < _outlineSprites.Length; i++)
        {
            FSprite outline = new("pixel")
            {
                anchorX = 0f,
                anchorY = 0f,
                color = FocusBorderColor,
                isVisible = false
            };
            _outlineSprites[i] = outline;
            fSprites.Add(outline);
            if (owner != null)
            {
                Futile.stage.AddChild(outline);
            }
        }

        _selectionSprite = new FSprite("pixel")
        {
            anchorX = 0f,
            anchorY = 0f,
            color = SelectionColor,
            isVisible = false
        };
        fSprites.Add(_selectionSprite);
        if (owner != null)
        {
            Futile.stage.AddChild(_selectionSprite);
            if (fLabels.Count > 0)
            {
                _selectionSprite.MoveBehindOtherNode(fLabels[0]);
            }
        }

        _caretSprite = new FSprite("pixel")
        {
            anchorX = 0f,
            anchorY = 0f,
            color = Color.black,
            isVisible = false,
            scaleX = 1f,
            scaleY = 14f
        };
        fSprites.Add(_caretSprite);
        if (owner != null)
        {
            Futile.stage.AddChild(_caretSprite);
        }

        RebuildCaretMetrics();
        RefreshIdleDisplayCache();
        Refresh();
    }

    public override void Update()
    {
        base.Update();

        if (owner == null)
        {
            return;
        }

        HandleMouse();

        if (IsFocused)
        {
            HandleKeyboard();
        }

        // Parent IntegerControl/PaletteController.Refresh may write directly into
        // subNodes[1].fLabels[0]. Keep the field's own transaction authoritative.
        RefreshVisualState();
    }

    public override void Refresh()
    {
        base.Refresh();

        Vector2 p = absPos;
        Vector2 s = size;

        SetOutlineGeometry(0, p - new Vector2(1f, 1f), s.x + 2f, 1f);
        SetOutlineGeometry(1, p - new Vector2(1f, 1f), 1f, s.y + 2f);
        SetOutlineGeometry(2, p + new Vector2(-1f, s.y), s.x + 2f, 1f);
        SetOutlineGeometry(3, p + new Vector2(s.x, -1f), 1f, s.y + 2f);

        RefreshIdleDisplayCache();
        RefreshVisualState();
    }

    public override void ClearSprites()
    {
        if (IsFocused)
        {
            DryCycleInputFocus.Release(this, commit: true);
        }

        base.ClearSprites();
    }

    internal void Focus()
    {
        DryCycleInputFocus.RequestFocus(this);
    }

    internal void Commit()
    {
        DryCycleInputFocus.Release(this, commit: true);
    }

    internal void Cancel()
    {
        DryCycleInputFocus.Release(this, commit: false);
    }

    internal bool SetValue(string value, bool notify = false, bool updateWhileFocused = false)
    {
        string candidate = value ?? string.Empty;
        if (_validator(candidate) != DryCycleTextValidationState.Valid)
        {
            return false;
        }

        if (IsFocused && !updateWhileFocused)
        {
            return false;
        }

        string old = _acceptedText;
        _acceptedText = candidate;
        _transactionStartText = candidate;
        _buffer = candidate;
        _validationState = DryCycleTextValidationState.Valid;
        _caret = _buffer.Length;
        _selectionAnchor = _caret;
        _undo.Clear();
        _redo.Clear();
        RebuildCaretMetrics();
        RefreshIdleDisplayCache();
        RefreshVisualState();

        if (notify && old != candidate)
        {
            AcceptedTextChanged?.Invoke(this, candidate, old);
        }

        return true;
    }

    internal void SelectAll()
    {
        if (!IsFocused)
        {
            return;
        }

        _selectionAnchor = 0;
        _caret = _buffer.Length;
        ResetCaretBlink();
        RefreshSelectionAndCaret();
    }

    internal void BeginEditFromFocusManager()
    {
        _transactionStartText = _acceptedText;
        _buffer = _acceptedText;
        _validationState = DryCycleTextValidationState.Valid;
        _undo.Clear();
        _redo.Clear();
        _caret = _buffer.Length;
        _selectionAnchor = _selectAllOnFocus ? 0 : _caret;
        _mouseSelecting = false;
        RebuildCaretMetrics();
        ResetCaretBlink();
        RefreshVisualState();
    }

    internal void EndEditFromFocusManager(bool commit)
    {
        _mouseSelecting = false;

        if (commit)
        {
            // Invalid/intermediate text never becomes the model value. Committing
            // therefore snaps back to the most recent accepted text.
            _buffer = _acceptedText;
            _validationState = DryCycleTextValidationState.Valid;
            _caret = _buffer.Length;
            _selectionAnchor = _caret;
            RebuildCaretMetrics();
            RefreshIdleDisplayCache();
            RefreshVisualState();
            EditCommitted?.Invoke(this, _acceptedText);
            return;
        }

        string oldAccepted = _acceptedText;
        _acceptedText = _transactionStartText;
        _buffer = _transactionStartText;
        _validationState = DryCycleTextValidationState.Valid;
        _caret = _buffer.Length;
        _selectionAnchor = _caret;
        _undo.Clear();
        _redo.Clear();
        RebuildCaretMetrics();
        RefreshIdleDisplayCache();
        RefreshVisualState();

        if (oldAccepted != _acceptedText)
        {
            AcceptedTextChanged?.Invoke(this, _acceptedText, oldAccepted);
        }

        EditCancelled?.Invoke(this, _acceptedText);
    }

    private void HandleMouse()
    {
        if (owner.mouseClick)
        {
            if (MouseOver)
            {
                bool newlyFocused = DryCycleInputFocus.RequestFocus(this);
                if (!newlyFocused || !_selectAllOnFocus)
                {
                    int index = CaretIndexAtMouse(owner.mousePos.x);
                    _caret = index;
                    _selectionAnchor = index;
                    _mouseSelecting = true;
                    ResetCaretBlink();
                }
            }
            else if (IsFocused)
            {
                DryCycleInputFocus.Release(this, commit: true);
                return;
            }
        }

        if (_mouseSelecting)
        {
            if (!owner.mouseDown || !IsFocused)
            {
                _mouseSelecting = false;
            }
            else
            {
                _caret = CaretIndexAtMouse(owner.mousePos.x);
                ResetCaretBlink();
            }
        }
    }

    private void HandleKeyboard()
    {
        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
            || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cancel();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
            || Input.GetKeyDown(KeyCode.Tab))
        {
            Commit();
            return;
        }

        if (control)
        {
            if (Input.GetKeyDown(KeyCode.A))
            {
                SelectAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.C))
            {
                CopySelection();
                return;
            }

            if (Input.GetKeyDown(KeyCode.X))
            {
                CopySelection();
                if (HasSelection)
                {
                    ReplaceSelection(string.Empty);
                }
                return;
            }

            if (Input.GetKeyDown(KeyCode.V))
            {
                PasteClipboard();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Z))
            {
                if (shift)
                {
                    Redo();
                }
                else
                {
                    Undo();
                }
                return;
            }

            if (Input.GetKeyDown(KeyCode.Y))
            {
                Redo();
                return;
            }
        }

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            MoveCaret(-1, shift);
        }
        else if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            MoveCaret(1, shift);
        }
        else if (Input.GetKeyDown(KeyCode.Home))
        {
            SetCaret(0, shift);
        }
        else if (Input.GetKeyDown(KeyCode.End))
        {
            SetCaret(_buffer.Length, shift);
        }
        else if (Input.GetKeyDown(KeyCode.Delete))
        {
            DeleteForward();
        }

        bool inputMutated = false;
        if (!control)
        {
            string input = Input.inputString;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\b')
                {
                    Backspace();
                    inputMutated = true;
                    continue;
                }

                if (c == '\r' || c == '\n' || c == '\t' || char.IsControl(c))
                {
                    continue;
                }

                if (_characterFilter != null && !_characterFilter(c))
                {
                    continue;
                }

                ReplaceSelection(c.ToString());
                inputMutated = true;
            }
        }

        if (inputMutated)
        {
            ResetCaretBlink();
        }
    }

    private void MoveCaret(int delta, bool extendSelection)
    {
        if (!extendSelection && HasSelection)
        {
            int edge = delta < 0 ? SelectionStart : SelectionEnd;
            _caret = edge;
            _selectionAnchor = edge;
        }
        else
        {
            SetCaret(Mathf.Clamp(_caret + delta, 0, _buffer.Length), extendSelection);
        }

        ResetCaretBlink();
    }

    private void SetCaret(int index, bool extendSelection)
    {
        _caret = Mathf.Clamp(index, 0, _buffer.Length);
        if (!extendSelection)
        {
            _selectionAnchor = _caret;
        }
        ResetCaretBlink();
    }

    private void Backspace()
    {
        if (HasSelection)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        if (_caret <= 0)
        {
            return;
        }

        int oldCaret = _caret;
        _selectionAnchor = oldCaret - 1;
        _caret = oldCaret;
        ReplaceSelection(string.Empty);
    }

    private void DeleteForward()
    {
        if (HasSelection)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        if (_caret >= _buffer.Length)
        {
            return;
        }

        _selectionAnchor = _caret;
        _caret++;
        ReplaceSelection(string.Empty);
    }

    private void ReplaceSelection(string insertedText)
    {
        string insert = SanitizeInsertedText(insertedText ?? string.Empty);
        int start = SelectionStart;
        int end = SelectionEnd;
        int available = _maxLength - (_buffer.Length - (end - start));
        if (available < insert.Length)
        {
            insert = available > 0 ? insert.Substring(0, available) : string.Empty;
        }

        if (start == end && insert.Length == 0)
        {
            return;
        }

        PushUndoSnapshot();
        string next = _buffer.Substring(0, start) + insert + _buffer.Substring(end);
        _buffer = next;
        _caret = start + insert.Length;
        _selectionAnchor = _caret;
        ApplyBufferValidation();
    }

    private string SanitizeInsertedText(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        char[] buffer = new char[Math.Min(text.Length, _maxLength)];
        int count = 0;
        for (int i = 0; i < text.Length && count < buffer.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n' || c == '\t' || char.IsControl(c))
            {
                continue;
            }
            if (_characterFilter != null && !_characterFilter(c))
            {
                continue;
            }
            buffer[count++] = c;
        }
        return count == 0 ? string.Empty : new string(buffer, 0, count);
    }

    private void PasteClipboard()
    {
        string clipboard = ClipboardBridge.GetText();
        if (!string.IsNullOrEmpty(clipboard))
        {
            ReplaceSelection(clipboard);
        }
    }

    private void CopySelection()
    {
        if (!HasSelection)
        {
            return;
        }

        ClipboardBridge.SetText(_buffer.Substring(SelectionStart, SelectionEnd - SelectionStart));
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        EditSnapshot current = CaptureSnapshot();
        EditSnapshot previous = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        PushBounded(_redo, current);
        RestoreSnapshot(previous);
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        EditSnapshot current = CaptureSnapshot();
        EditSnapshot next = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        PushBounded(_undo, current);
        RestoreSnapshot(next);
    }

    private void PushUndoSnapshot()
    {
        EditSnapshot snapshot = CaptureSnapshot();
        if (_undo.Count == 0 || !_undo[_undo.Count - 1].Equals(snapshot))
        {
            PushBounded(_undo, snapshot);
        }
        _redo.Clear();
    }

    private static void PushBounded(List<EditSnapshot> stack, EditSnapshot snapshot)
    {
        if (stack.Count >= UndoLimit)
        {
            stack.RemoveAt(0);
        }
        stack.Add(snapshot);
    }

    private EditSnapshot CaptureSnapshot() => new(_buffer, _caret, _selectionAnchor);

    private void RestoreSnapshot(EditSnapshot snapshot)
    {
        _buffer = snapshot.Text;
        _caret = Mathf.Clamp(snapshot.Caret, 0, _buffer.Length);
        _selectionAnchor = Mathf.Clamp(snapshot.SelectionAnchor, 0, _buffer.Length);
        ApplyBufferValidation();
        ResetCaretBlink();
    }

    private void ApplyBufferValidation()
    {
        _validationState = _validator(_buffer);
        if (_validationState == DryCycleTextValidationState.Valid && _buffer != _acceptedText)
        {
            string old = _acceptedText;
            _acceptedText = _buffer;
            AcceptedTextChanged?.Invoke(this, _acceptedText, old);
        }

        RebuildCaretMetrics();
        ResetCaretBlink();
        RefreshVisualState();
    }

    private void RebuildCaretMetrics()
    {
        int count = _buffer.Length;
        if (_caretPositions.Length != count + 1)
        {
            _caretPositions = new float[count + 1];
        }

        _caretPositions[0] = 0f;
        if (count == 0 || fLabels.Count == 0 || fLabels[0]?._font == null)
        {
            return;
        }

        try
        {
            FLetterQuadLine[] lines = fLabels[0]._font.GetQuadInfoForText(_buffer, fLabels[0]._textParams);
            if (lines == null || lines.Length == 0 || lines[0].quads == null)
            {
                return;
            }

            FLetterQuad[] quads = lines[0].quads;
            int usable = Math.Min(count, quads.Length);
            for (int i = 0; i < usable; i++)
            {
                FLetterQuad quad = quads[i];
                _caretPositions[i] = quad.rect.x - quad.charInfo.offsetX;
                _caretPositions[i + 1] = _caretPositions[i] + quad.charInfo.xadvance;
            }

            for (int i = usable + 1; i < _caretPositions.Length; i++)
            {
                _caretPositions[i] = _caretPositions[i - 1] + fLabels[0].FontMaxCharWidth;
            }
        }
        catch
        {
            float fallback = fLabels[0].FontMaxCharWidth;
            for (int i = 1; i < _caretPositions.Length; i++)
            {
                _caretPositions[i] = _caretPositions[i - 1] + fallback;
            }
        }
    }

    private int CaretIndexAtMouse(float mouseX)
    {
        float localX = mouseX - absPos.x;
        if (_caretPositions.Length <= 1 || localX <= _caretPositions[0])
        {
            return 0;
        }

        int last = _caretPositions.Length - 1;
        if (localX >= _caretPositions[last])
        {
            return last;
        }

        for (int i = 0; i < last; i++)
        {
            float midpoint = (_caretPositions[i] + _caretPositions[i + 1]) * 0.5f;
            if (localX < midpoint)
            {
                return i;
            }
        }

        return last;
    }

    private void RefreshIdleDisplayCache()
    {
        if (_idleDisplayProvider == null)
        {
            _idleDisplayCache = null;
            return;
        }

        try
        {
            _idleDisplayCache = _idleDisplayProvider();
        }
        catch (Exception ex)
        {
            _idleDisplayCache = null;
            Plugin.Logger?.LogError($"DryCycle DevUI: idle display provider failed for {IDstring}: {ex}");
        }
    }

    private void RefreshVisualState()
    {
        if (fLabels.Count == 0)
        {
            return;
        }

        string displayText = _buffer;
        if (!IsFocused)
        {
            displayText = _idleDisplayCache ?? _acceptedText;
        }

        if (fLabels[0].text != displayText)
        {
            fLabels[0].text = displayText;
        }
        fLabels[0].color = Color.black;

        Color border = IdleBorderColor;
        if (IsFocused)
        {
            border = _validationState switch
            {
                DryCycleTextValidationState.Valid => FocusBorderColor,
                DryCycleTextValidationState.Intermediate => IntermediateBorderColor,
                _ => InvalidBorderColor
            };
        }

        bool focused = IsFocused;
        if (!focused)
        {
            if (_focusVisualActive)
            {
                for (int i = 0; i < _outlineSprites.Length; i++)
                {
                    _outlineSprites[i].isVisible = false;
                }
                _selectionSprite.isVisible = false;
                _caretSprite.isVisible = false;
                _focusVisualActive = false;
            }
            return;
        }

        if (!_focusVisualActive || _lastBorderValidationState != _validationState)
        {
            for (int i = 0; i < _outlineSprites.Length; i++)
            {
                _outlineSprites[i].color = border;
                _outlineSprites[i].isVisible = true;
            }
            _lastBorderValidationState = _validationState;
            _focusVisualActive = true;
        }

        RefreshSelectionAndCaret();
    }

    private void RefreshSelectionAndCaret()
    {
        if (_selectionSprite == null || _caretSprite == null)
        {
            return;
        }

        if (!IsFocused)
        {
            _selectionSprite.isVisible = false;
            _caretSprite.isVisible = false;
            return;
        }

        bool selectionVisible = HasSelection;
        _selectionSprite.isVisible = selectionVisible;
        if (selectionVisible)
        {
            float left = CaretX(SelectionStart);
            float right = CaretX(SelectionEnd);
            _selectionSprite.SetPosition(absPos.x + left, absPos.y + 1f);
            _selectionSprite.scaleX = Math.Max(1f, right - left);
            _selectionSprite.scaleY = Math.Max(1f, size.y - 2f);
        }

        bool blink = Mathf.Repeat(Time.unscaledTime - _lastEditTime, CaretBlinkPeriod)
            < CaretBlinkPeriod * CaretVisibleFraction;
        _caretSprite.isVisible = !HasSelection && blink;
        if (_caretSprite.isVisible)
        {
            _caretSprite.SetPosition(absPos.x + CaretX(_caret), absPos.y + 1f);
            _caretSprite.scaleY = Math.Max(1f, size.y - 2f);
        }
    }

    private float CaretX(int index)
    {
        if (_caretPositions.Length == 0)
        {
            return 0f;
        }
        return _caretPositions[Mathf.Clamp(index, 0, _caretPositions.Length - 1)];
    }

    private void SetOutlineGeometry(int index, Vector2 position, float width, float height)
    {
        if (index < 0 || index >= _outlineSprites.Length)
        {
            return;
        }

        FSprite sprite = _outlineSprites[index];
        sprite.SetPosition(position);
        sprite.scaleX = Math.Max(1f, width);
        sprite.scaleY = Math.Max(1f, height);
    }

    private void ResetCaretBlink()
    {
        _lastEditTime = Time.unscaledTime;
    }

    private bool HasSelection => _caret != _selectionAnchor;
    private int SelectionStart => Math.Min(_caret, _selectionAnchor);
    private int SelectionEnd => Math.Max(_caret, _selectionAnchor);

    private static class ClipboardBridge
    {
        private static readonly PropertyInfo CopyBufferProperty = ResolveCopyBufferProperty();

        internal static string GetText()
        {
            try
            {
                return CopyBufferProperty?.GetValue(null, null) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static void SetText(string text)
        {
            try
            {
                CopyBufferProperty?.SetValue(null, text ?? string.Empty, null);
            }
            catch
            {
                // Clipboard support is optional; editing itself must never fail.
            }
        }

        private static PropertyInfo ResolveCopyBufferProperty()
        {
            Type type = Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule", throwOnError: false)
                ?? Type.GetType("UnityEngine.GUIUtility, UnityEngine", throwOnError: false);
            return type?.GetProperty("systemCopyBuffer", BindingFlags.Public | BindingFlags.Static);
        }
    }

    private readonly struct EditSnapshot : IEquatable<EditSnapshot>
    {
        internal readonly string Text;
        internal readonly int Caret;
        internal readonly int SelectionAnchor;

        internal EditSnapshot(string text, int caret, int selectionAnchor)
        {
            Text = text;
            Caret = caret;
            SelectionAnchor = selectionAnchor;
        }

        public bool Equals(EditSnapshot other)
            => Text == other.Text && Caret == other.Caret && SelectionAnchor == other.SelectionAnchor;
    }
}

internal static class DryCycleInputFocus
{
    internal static DryCycleTextField Focused { get; private set; }

    internal static bool RequestFocus(DryCycleTextField field)
    {
        if (field == null)
        {
            return false;
        }

        if (Focused == field)
        {
            return false;
        }

        DryCycleTextField previous = Focused;
        Focused = null;
        previous?.EndEditFromFocusManager(commit: true);

        Focused = field;
        field.BeginEditFromFocusManager();
        return true;
    }

    internal static void Release(DryCycleTextField field, bool commit)
    {
        if (field == null || Focused != field)
        {
            return;
        }

        Focused = null;
        field.EndEditFromFocusManager(commit);
    }

    internal static void Reset(bool commit)
    {
        DryCycleTextField focused = Focused;
        Focused = null;
        focused?.EndEditFromFocusManager(commit);
    }
}
