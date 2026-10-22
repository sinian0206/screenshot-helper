using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XiaoJie
{
    public sealed partial class OverlayForm
    {
        private PrivacyReviewSession _privacyReview;
        private PrivacyReviewPanel _privacyReviewPanel;
        private MosaicAnnotation _privacyReviewAnnotation;
        private bool _updatingPrivacyReview;
        private bool _disposingPrivacyReview;
        private int _privacyReviewHighlight = -1;

        private void MergePrivacyReviewStage(PrivacyAnalysis analysis, ref MosaicAnnotation annotation)
        {
            if (analysis == null) return;
            if (_privacyReview == null || (annotation != null && !ReferenceEquals(_privacyReviewAnnotation, annotation)))
            {
                DisposeEditorReview();
                _privacyReview = new PrivacyReviewSession();
            }
            _privacyReview.Merge(analysis, _selection);
            if (_privacyReview.Count > 0)
            {
                if (annotation == null)
                {
                    annotation = new MosaicAnnotation { Rects = _privacyReview.BuildRects() };
                    _privacyReviewAnnotation = annotation;
                    _applyingPrivacy = true;
                    try { AddAnnotation(annotation); }
                    finally { _applyingPrivacy = false; }
                }
                if (!_annotations.Contains(annotation)) return;
                EnsureMosaic().Block = Math.Max(28, (int)Math.Round(_cfg.MosaicBlock));
                annotation.Rects = _privacyReview.BuildRects();
                _privacyReviewAnnotation = annotation;
            }
            ShowPrivacyReview();
            UpdateToolbarState();
            Invalidate();
        }

        private void ShowPrivacyReview()
        {
            if (_closing || IsDisposed || _privacyReview == null) return;
            if (_privacyReviewPanel != null && !_privacyReviewPanel.IsDisposed)
            {
                _privacyReviewPanel.RefreshCandidates();
                return;
            }
            var panel = new PrivacyReviewPanel(_privacyReview);
            _privacyReviewPanel = panel;
            panel.Changed = ApplyPrivacyReviewChoices;
            panel.Undo = Undo;
            panel.Redo = Redo;
            panel.StopDetection = delegate { CancelPrivacy(true); };
            panel.Highlight = delegate(int index) { _privacyReviewHighlight = index; Invalidate(); };
            panel.AddRegion = delegate
            {
                CancelPrivacy(false);
                DisposeEditorReview();
                _tool = EditorTool.Mosaic;
                _toolbar.ActiveTool = _tool;
                Activate();
                Invalidate();
                Toast.Show("拖动马赛克工具补选区域；Ctrl+Z 可撤销。", 4000);
            };
            panel.FormClosed += delegate
            {
                if (_disposingPrivacyReview) return;
                CancelPrivacy(false);
                _privacyReviewPanel = null;
                _privacyReviewHighlight = -1;
                _privacyReview = null;
                _privacyReviewAnnotation = null;
                Invalidate();
            };
            panel.Show(this);
            Rectangle screenSelection = SelectionScreenRect();
            panel.FitToWorkingArea(Screen.FromRectangle(screenSelection).WorkingArea);
            Rectangle placement = DisplayGeometry.PlaceFloating(screenSelection, panel.Size,
                DisplayGeometry.WorkingAreas(), 10, false);
            panel.Location = placement.Location;
        }

        private void ApplyPrivacyReviewChoices()
        {
            if (_privacyReview == null) return;
            CancelPrivacy(false);
            if (_privacyReviewAnnotation == null || !_annotations.Contains(_privacyReviewAnnotation))
            {
                if (_privacyReviewPanel != null) _privacyReviewPanel.RefreshStatus();
                return;
            }
            Annotation before = AnnotationGeometry.Copy(_privacyReviewAnnotation);
            _privacyReviewAnnotation.Rects = _privacyReview.BuildRects();
            _updatingPrivacyReview = true;
            try { RecordAnnotationMutation(_privacyReviewAnnotation, before); }
            finally { _updatingPrivacyReview = false; }
            if (_privacyReviewPanel != null) _privacyReviewPanel.RefreshStatus();
            Invalidate();
        }

        partial void OnAnnotationsChanged()
        {
            if (_applyingPrivacy || _updatingPrivacyReview) return;
            // 对象被移动/撤销后，旧候选坐标不能再写回该对象。
            DisposeEditorReview();
        }

        partial void BeforeEditorAction(ToolbarAction action, ref bool handled)
        {
            if (action != ToolbarAction.None) DisposeEditorReview();
        }

        partial void BeforeEditorOutput(ref bool handled)
        {
            CancelPrivacy(false);
            DisposeEditorReview();
        }

        partial void BeforeEditorMouseDown(MouseEventArgs e, ref bool handled)
        {
            if (_privacyReviewPanel != null && _tool != EditorTool.None && e.Button == MouseButtons.Left)
                DisposeEditorReview();
        }

        partial void BeforeEditorKey(Keys keyData, ref bool handled)
        {
            if (keyData == Keys.Escape && _privacyReviewPanel != null)
            {
                CancelPrivacy(false);
                DisposeEditorReview();
                handled = true;
            }
        }

        partial void DrawEditorReview(Graphics graphics)
        {
            if (_privacyReviewPanel == null || _privacyReview == null || _privacyReviewHighlight < 0 ||
                _privacyReviewHighlight >= _privacyReview.Count) return;
            RectangleF rect = _privacyReview.Candidates[_privacyReviewHighlight].Bounds;
            rect.Inflate(2, 2);
            using (var pen = new Pen(Color.Orange, Math.Max(2f, _scale)))
            {
                pen.DashStyle = DashStyle.Dash;
                graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }

        partial void DisposeEditorReview()
        {
            PrivacyReviewPanel panel = _privacyReviewPanel;
            _privacyReviewPanel = null;
            _disposingPrivacyReview = true;
            try { if (panel != null && !panel.IsDisposed) panel.Dispose(); }
            finally { _disposingPrivacyReview = false; }
            _privacyReview = null;
            _privacyReviewAnnotation = null;
            _privacyReviewHighlight = -1;
        }
    }
}
