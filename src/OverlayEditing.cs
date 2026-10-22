using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XiaoJie
{
    public sealed partial class OverlayForm
    {
        private readonly List<AnnotationEditCommand> _undoHistory = new List<AnnotationEditCommand>();
        private readonly List<AnnotationEditCommand> _redoHistory = new List<AnnotationEditCommand>();
        private readonly HashSet<Annotation> _ownedAnnotations = new HashSet<Annotation>();
        private Annotation _selectedAnnotation;
        private int _selectedTranslationBlock = -1;
        private Annotation _objectInitial;
        private RectangleF _objectFrame;
        private int _objectResizeHandle = -1;
        private bool _objectChanged;
        private Annotation _textEditBefore;
        private Panel _translationBar;
        private TextBox _translationInput;
        private TranslationOverlayAnnotation _translationEditingOverlay;
        private int _translationEditingIndex;
        private ToolbarAction _selectionAction;
        private bool _selectionPrivacy;

        partial void OnAnnotationsChanged();
        partial void BeforeEditorAction(ToolbarAction action, ref bool handled);
        partial void BeforeEditorOutput(ref bool handled);
        partial void BeforeEditorMouseDown(MouseEventArgs e, ref bool handled);
        partial void BeforeEditorKey(Keys keyData, ref bool handled);
        partial void DrawEditorReview(Graphics graphics);
        partial void DisposeEditorReview();

        /// <summary>Schedules a focused operation once the capture region is confirmed.</summary>
        internal void StartActionAfterSelection(ToolbarAction action)
        {
            _selectionAction = action;
            _selectionPrivacy = false;
            if (_hasSelection) RunSelectionAction();
        }

        internal void StartPrivacyAfterSelection()
        {
            _selectionPrivacy = true;
            _selectionAction = ToolbarAction.None;
            if (_hasSelection) RunSelectionAction();
        }

        private void RunSelectionAction()
        {
            ToolbarAction action = _selectionAction;
            bool privacy = _selectionPrivacy;
            _selectionAction = ToolbarAction.None;
            _selectionPrivacy = false;
            if (!privacy && action == ToolbarAction.None) return;
            BeginInvoke((Action)delegate
            {
                if (_closing || IsDisposed || !_hasSelection) return;
                if (privacy) StartSmartMosaic();
                else if (action == ToolbarAction.Ocr) StartAi(AiPanelMode.Ocr);
                else if (action == ToolbarAction.Translate) StartTranslateInPlace();
            });
        }

        private void PushCommand(AnnotationEditCommand command)
        {
            foreach (AnnotationEditCommand item in _redoHistory) item.Dispose();
            _redoHistory.Clear();
            _undoHistory.Add(command);
            // Bound retained point/path snapshots for long editing sessions.
            if (_undoHistory.Count > 100) { _undoHistory[0].Dispose(); _undoHistory.RemoveAt(0); }
            DocumentChanged();
        }

        /// <summary>Records an already-applied edit and takes ownership of its before-state.</summary>
        internal void RecordAnnotationMutation(Annotation annotation, Annotation beforeState)
        {
            if (annotation == null || beforeState == null) return;
            Annotation afterState = AnnotationGeometry.Copy(annotation);
            PushCommand(new AnnotationEditCommand(
                () => AnnotationGeometry.Restore(annotation, beforeState),
                () => AnnotationGeometry.Restore(annotation, afterState), beforeState, afterState));
        }

        private void DocumentChanged()
        {
            _selectionLocked = _annotations.Count > 0;
            _numberCounter = 0;
            foreach (Annotation item in _annotations)
            {
                NumberAnnotation number = item as NumberAnnotation;
                if (number != null) _numberCounter = Math.Max(_numberCounter, number.Number);
            }
            if (_selectedAnnotation != null && !_annotations.Contains(_selectedAnnotation))
            { _selectedAnnotation = null; _selectedTranslationBlock = -1; }
            OnAnnotationsChanged();
            UpdateToolbarState();
            Invalidate();
        }

        private void ResetAnnotationDocument()
        {
            CancelPrivacy(false);
            CancelTranslationEdit();
            _annotations.Clear();
            ReleaseEditHistory();
            _selectedAnnotation = null;
            _selectedTranslationBlock = -1;
            DocumentChanged();
        }

        private void ReleaseEditHistory()
        {
            foreach (AnnotationEditCommand command in _undoHistory) command.Dispose();
            foreach (AnnotationEditCommand command in _redoHistory) command.Dispose();
            _undoHistory.Clear(); _redoHistory.Clear();
            if (_objectInitial != null) { AnnotationGeometry.ReleaseCopy(_objectInitial); _objectInitial = null; }
            if (_textEditBefore != null) { AnnotationGeometry.ReleaseCopy(_textEditBefore); _textEditBefore = null; }
            foreach (Annotation annotation in _ownedAnnotations) AnnotationGeometry.ReleaseCopy(annotation);
            _ownedAnnotations.Clear();
        }

        private Annotation HitAnnotation(Point point, out int blockIndex)
        {
            blockIndex = -1;
            for (int index = _annotations.Count - 1; index >= 0; index--)
            {
                Annotation annotation = _annotations[index];
                TranslationOverlayAnnotation translation = annotation as TranslationOverlayAnnotation;
                if (translation != null)
                {
                    for (int block = translation.Blocks.Count - 1; block >= 0; block--)
                    {
                        RectangleF frame = translation.Blocks[block].Rect;
                        frame.Inflate(4 * _scale, 4 * _scale);
                        if (frame.Contains(point)) { blockIndex = block; return translation; }
                    }
                    continue;
                }
                RectangleF bounds = annotation.GetBounds();
                bounds.Inflate(4 * _scale, 4 * _scale);
                if (bounds.Contains(point)) return annotation;
            }
            return null;
        }

        private RectangleF[] ObjectHandles()
        {
            if (_selectedAnnotation == null) return new RectangleF[0];
            RectangleF frame = AnnotationGeometry.Frame(_selectedAnnotation, _selectedTranslationBlock);
            float middleX = frame.X + frame.Width / 2, middleY = frame.Y + frame.Height / 2;
            PointF[] points = { new PointF(frame.Left, frame.Top), new PointF(middleX, frame.Top), new PointF(frame.Right, frame.Top),
                new PointF(frame.Right, middleY), new PointF(frame.Right, frame.Bottom), new PointF(middleX, frame.Bottom),
                new PointF(frame.Left, frame.Bottom), new PointF(frame.Left, middleY) };
            float size = Math.Max(7, 7 * _scale);
            RectangleF[] handles = new RectangleF[8];
            for (int index = 0; index < points.Length; index++) handles[index] = new RectangleF(points[index].X - size / 2, points[index].Y - size / 2, size, size);
            return handles;
        }

        private int ObjectHandleAt(Point point)
        {
            RectangleF[] handles = ObjectHandles();
            for (int index = 0; index < handles.Length; index++)
            { RectangleF hit = handles[index]; hit.Inflate(3 * _scale, 3 * _scale); if (hit.Contains(point)) return index; }
            return -1;
        }

        private bool TryStartObjectManipulation(Point point)
        {
            if (!_hasSelection) return false;
            if (_tool != EditorTool.Select && _tool != EditorTool.None && _tool != EditorTool.Text) return false;
            int handle = _selectedAnnotation != null ? ObjectHandleAt(point) : -1;
            int blockIndex = -1;
            Annotation hit = handle >= 0 ? _selectedAnnotation : HitAnnotation(point, out blockIndex);
            if (handle >= 0) blockIndex = _selectedTranslationBlock;
            if (_tool != EditorTool.Select && !(hit is TextAnnotation) && !(hit is TranslationOverlayAnnotation)) return false;
            if (hit == null)
            {
                _selectedAnnotation = null; _selectedTranslationBlock = -1; Invalidate();
                return _tool == EditorTool.Select && _selection.Contains(point);
            }
            _selectedAnnotation = hit;
            _selectedTranslationBlock = blockIndex;
            _objectResizeHandle = handle;
            _objectFrame = AnnotationGeometry.Frame(hit, blockIndex);
            _objectInitial = AnnotationGeometry.Copy(hit);
            _objectChanged = false;
            _dragStart = point;
            _drag = DragMode.Object;
            Capture = true;
            Cursor = handle >= 0 ? HandleCursor(handle) : Cursors.SizeAll;
            Invalidate();
            return true;
        }

        private void UpdateObjectManipulation(Point point)
        {
            if (_selectedAnnotation == null || _objectInitial == null) return;
            float dx = point.X - _dragStart.X, dy = point.Y - _dragStart.Y;
            if (!_objectChanged && Math.Abs(dx) + Math.Abs(dy) < 2) return;
            RectangleF target = _objectFrame;
            if (_objectResizeHandle < 0)
            {
                target.Offset(dx, dy);
                target = ClampObjectMove(_objectInitial, _objectFrame, target, _selectedTranslationBlock);
            }
            else
            {
                float left = target.Left, top = target.Top, right = target.Right, bottom = target.Bottom;
                int handle = _objectResizeHandle;
                if (handle == 0 || handle == 6 || handle == 7) left = Math.Min(right - 12, Math.Max(_selection.Left, point.X));
                if (handle == 0 || handle == 1 || handle == 2) top = Math.Min(bottom - 12, Math.Max(_selection.Top, point.Y));
                if (handle == 2 || handle == 3 || handle == 4) right = Math.Max(left + 12, Math.Min(_selection.Right, point.X));
                if (handle == 4 || handle == 5 || handle == 6) bottom = Math.Max(top + 12, Math.Min(_selection.Bottom, point.Y));
                target = RectangleF.FromLTRB(left, top, right, bottom);
            }
            AnnotationGeometry.Transform(_selectedAnnotation, _objectInitial, _objectFrame, target, _selectedTranslationBlock);
            _objectChanged = true;
            Invalidate();
        }

        private RectangleF ClampObjectMove(Annotation annotation, RectangleF frame, RectangleF target, int blockIndex)
        {
            RectangleF visible = annotation is TranslationOverlayAnnotation ? frame : annotation.GetBounds();
            float leftMargin = Math.Max(0, frame.Left - visible.Left), rightMargin = Math.Max(0, visible.Right - frame.Right);
            float topMargin = Math.Max(0, frame.Top - visible.Top), bottomMargin = Math.Max(0, visible.Bottom - frame.Bottom);
            float minX = _selection.Left + leftMargin, maxX = _selection.Right - target.Width - rightMargin;
            float minY = _selection.Top + topMargin, maxY = _selection.Bottom - target.Height - bottomMargin;
            target.X = Math.Max(minX, Math.Min(maxX, target.X));
            target.Y = Math.Max(minY, Math.Min(maxY, target.Y));
            return target;
        }

        private void FinishObjectManipulation()
        {
            Annotation before = _objectInitial;
            _objectInitial = null;
            _drag = DragMode.None;
            Capture = false;
            if (_objectChanged && before != null) RecordAnnotationMutation(_selectedAnnotation, before);
            else
            {
                AnnotationGeometry.ReleaseCopy(before);
                if (_tool == EditorTool.Text && _selectedAnnotation is TextAnnotation) BeginEditExistingText((TextAnnotation)_selectedAnnotation);
            }
            _objectChanged = false;
            UpdateCursor();
            Invalidate();
        }

        private void CancelObjectManipulation()
        {
            if (_objectInitial != null && _selectedAnnotation != null) AnnotationGeometry.Restore(_selectedAnnotation, _objectInitial);
            AnnotationGeometry.ReleaseCopy(_objectInitial); _objectInitial = null;
            _objectChanged = false; _drag = DragMode.None; Capture = false; Invalidate();
        }

        private void DrawObjectSelection(Graphics graphics)
        {
            if (_selectedAnnotation == null || !_annotations.Contains(_selectedAnnotation)) return;
            RectangleF frame = AnnotationGeometry.Frame(_selectedAnnotation, _selectedTranslationBlock);
            GraphicsState state = graphics.Save();
            graphics.SetClip(_selection);
            using (Pen pen = new Pen(UiTheme.Green, Math.Max(1.2f, _scale)))
            {
                pen.DashStyle = DashStyle.Dash;
                graphics.DrawRectangle(pen, frame.X, frame.Y, frame.Width, frame.Height);
                pen.DashStyle = DashStyle.Solid;
                foreach (RectangleF handle in ObjectHandles())
                { graphics.FillRectangle(Brushes.White, handle); graphics.DrawRectangle(pen, handle.X, handle.Y, handle.Width, handle.Height); }
            }
            graphics.Restore(state);
        }

        private bool DeleteSelectedAnnotation()
        {
            if (_selectedAnnotation == null || !_annotations.Contains(_selectedAnnotation)) return false;
            CancelPrivacy(false);
            Annotation selected = _selectedAnnotation;
            TranslationOverlayAnnotation translation = selected as TranslationOverlayAnnotation;
            if (translation != null && _selectedTranslationBlock >= 0 &&
                _selectedTranslationBlock < translation.Blocks.Count && translation.Blocks.Count > 1)
            {
                Annotation before = AnnotationGeometry.Copy(translation);
                translation.Blocks.RemoveAt(_selectedTranslationBlock);
                _selectedAnnotation = null; _selectedTranslationBlock = -1;
                RecordAnnotationMutation(translation, before);
                return true;
            }
            int index = _annotations.IndexOf(selected);
            _annotations.Remove(selected);
            PushCommand(new AnnotationEditCommand(() => _annotations.Insert(Math.Min(index, _annotations.Count), selected), () => _annotations.Remove(selected)));
            return true;
        }

        private bool HandleObjectKey(Keys keyData)
        {
            if (keyData == Keys.V) { SetTool(EditorTool.Select); return true; }
            if (keyData == Keys.Delete || keyData == Keys.Back) return DeleteSelectedAnnotation();
            if (keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
            if (keyData == (Keys.Control | Keys.Alt | Keys.T)) { ToggleTranslationOriginal(); return true; }
            if (keyData == Keys.F2 && _selectedAnnotation is TranslationOverlayAnnotation)
            { BeginTranslationEdit((TranslationOverlayAnnotation)_selectedAnnotation, Math.Max(0, _selectedTranslationBlock)); return true; }
            Keys key = keyData & Keys.KeyCode;
            if (_selectedAnnotation == null || (key != Keys.Left && key != Keys.Right && key != Keys.Up && key != Keys.Down)) return false;
            if ((keyData & ~(Keys.KeyCode | Keys.Shift)) != 0) return false;
            float step = (keyData & Keys.Shift) != 0 ? 10 : 1;
            RectangleF frame = AnnotationGeometry.Frame(_selectedAnnotation, _selectedTranslationBlock);
            RectangleF next = frame;
            next.Offset(key == Keys.Left ? -step : key == Keys.Right ? step : 0, key == Keys.Up ? -step : key == Keys.Down ? step : 0);
            next = ClampObjectMove(_selectedAnnotation, frame, next, _selectedTranslationBlock);
            if (next == frame) return true;
            Annotation before = AnnotationGeometry.Copy(_selectedAnnotation);
            AnnotationGeometry.Transform(_selectedAnnotation, before, frame, next, _selectedTranslationBlock);
            RecordAnnotationMutation(_selectedAnnotation, before);
            return true;
        }

        private bool ShowObjectMenu(Point point)
        {
            if (!_hasSelection || _busy) return false;
            int blockIndex;
            Annotation selected = HitAnnotation(point, out blockIndex);
            if (selected == null) return false;
            _selectedAnnotation = selected; _selectedTranslationBlock = blockIndex;
            CancelPrivacy(false);
            if (_contextMenu != null) _contextMenu.Dispose();
            _contextMenu = new ContextMenuStrip();
            TranslationOverlayAnnotation translation = selected as TranslationOverlayAnnotation;
            if (translation != null)
            {
                _contextMenu.Items.Add("编辑译文 (F2)", null, (sender, args) => BeginTranslationEdit(translation, blockIndex));
                _contextMenu.Items.Add(translation.ShowOriginal ? "显示译文 (Ctrl+Alt+T)" : "显示原文 (Ctrl+Alt+T)", null, (sender, args) => ToggleTranslationOriginal());
                _contextMenu.Items.Add(new ToolStripSeparator());
            }
            TextAnnotation text = selected as TextAnnotation;
            if (text != null) _contextMenu.Items.Add("编辑文字", null, (sender, args) => BeginEditExistingText(text));
            _contextMenu.Items.Add(translation != null ? "删除译文框 (Delete)" : "删除对象 (Delete)", null, (sender, args) => DeleteSelectedAnnotation());
            _contextMenu.Items.Add("撤销 (Ctrl+Z)", null, (sender, args) => Undo()).Enabled = _undoHistory.Count > 0;
            _contextMenu.Items.Add("重做 (Ctrl+Y)", null, (sender, args) => Redo()).Enabled = _redoHistory.Count > 0;
            _contextMenu.Show(this, point);
            Invalidate(); return true;
        }

        private void BeginTranslationEdit(TranslationOverlayAnnotation overlay, int index)
        {
            if (overlay == null || index < 0 || index >= overlay.Blocks.Count) return;
            CancelPrivacy(false); CommitText(); CommitTranslationEdit();
            _selectedAnnotation = overlay; _selectedTranslationBlock = index;
            _translationEditingOverlay = overlay; _translationEditingIndex = index;
            TranslationBlock block = overlay.Blocks[index];
            int width = Math.Min((int)(450 * _scale), Math.Max(220, ClientSize.Width - 24));
            int height = (int)(150 * _scale);
            Panel panel = new Panel { BackColor = Color.White, Size = new Size(width, height) };
            panel.Location = new Point(Math.Max(8, Math.Min(ClientSize.Width - width - 8, (int)block.Rect.X)),
                Math.Max(8, Math.Min(ClientSize.Height - height - 8, (int)block.Rect.Bottom + 10)));
            panel.Paint += (sender, args) => { using (Pen border = new Pen(UiTheme.Green, 2)) args.Graphics.DrawRectangle(border, 1, 1, panel.Width - 3, panel.Height - 3); };
            Label original = new Label { Text = "原文：" + block.Original, AutoEllipsis = true, ForeColor = UiTheme.TextGray,
                Location = new Point(10, 8), Size = new Size(width - 20, 25), Font = UiTheme.Font(12 * _scale) };
            TextBox input = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Text = block.Translation,
                Location = new Point(10, 36), Size = new Size(width - 20, height - 76), Font = UiTheme.Font(14 * _scale), BorderStyle = BorderStyle.FixedSingle };
            Button apply = new Button { Text = "应用", BackColor = UiTheme.ActionGreen, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Location = new Point(width - 90, height - 33), Size = new Size(80, 25) };
            apply.Click += (sender, args) => CommitTranslationEdit();
            Label hint = new Label { Text = "Ctrl+Enter 应用 · Esc 取消 · 拖动边框调整位置和大小", AutoEllipsis = true,
                Location = new Point(10, height - 31), Size = new Size(width - 110, 25), ForeColor = UiTheme.TextGray, Font = UiTheme.Font(12 * _scale) };
            panel.Controls.Add(original); panel.Controls.Add(input); panel.Controls.Add(apply); panel.Controls.Add(hint);
            _translationBar = panel; _translationInput = input;
            Controls.Add(panel); panel.BringToFront(); input.Focus(); input.SelectAll(); Invalidate();
        }

        private void CommitTranslationEdit()
        {
            if (_translationBar == null) return;
            TranslationOverlayAnnotation overlay = _translationEditingOverlay;
            int index = _translationEditingIndex;
            string text = _translationInput.Text;
            RemoveTranslationBar();
            if (overlay == null || !_annotations.Contains(overlay) || index < 0 || index >= overlay.Blocks.Count) return;
            if (overlay.Blocks[index].Translation != text)
            {
                Annotation before = AnnotationGeometry.Copy(overlay);
                overlay.Blocks[index].Translation = text;
                overlay.ShowOriginal = false;
                RecordAnnotationMutation(overlay, before);
            }
            Focus(); Invalidate();
        }

        private void CancelTranslationEdit() { RemoveTranslationBar(); Invalidate(); }

        private void RemoveTranslationBar()
        {
            if (_translationBar == null) return;
            Controls.Remove(_translationBar); _translationBar.Dispose();
            _translationBar = null; _translationInput = null; _translationEditingOverlay = null;
        }

        private void ToggleTranslationOriginal()
        {
            CommitTranslationEdit();
            TranslationOverlayAnnotation selected = _selectedAnnotation as TranslationOverlayAnnotation;
            if (selected == null)
                for (int index = _annotations.Count - 1; index >= 0; index--)
                    if (_annotations[index] is TranslationOverlayAnnotation) { selected = (TranslationOverlayAnnotation)_annotations[index]; break; }
            if (selected == null) return;
            Annotation before = AnnotationGeometry.Copy(selected);
            selected.ShowOriginal = !selected.ShowOriginal;
            RecordAnnotationMutation(selected, before);
        }

        private bool PrepareEditorOutput()
        {
            CommitText(); CommitTranslationEdit();
            bool handled = false;
            BeforeEditorOutput(ref handled);
            return !handled;
        }
    }
}
