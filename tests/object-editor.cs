using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using XiaoJie;

internal static class ObjectEditorTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<string> Results = new List<string>();
    private static string output;
    private static int failed;

    [STAThread]
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        Console.OutputEncoding = new UTF8Encoding(false);
        Application.EnableVisualStyles();
        Run("所有标注能选中移动、撤销重做和删除", MoveEveryObject);
        Run("八类对象边角缩放的历史恢复", ResizeEveryObject);
        Run("边角缩放保持笔刷遮挡与重做", ResizeAndRedaction);
        Run("文字二次编辑的撤销保持层级", TextHistory);
        Run("逐块译文编辑、框调整和原文切换", TranslationEditing);
        Run("译文扩框容纳完整文字及单框删除", TranslationFrameFit);
        Run("删除后新操作清空重做并保留已有遮挡", RedoBranch);
        Run("键盘微调、Shift加速和删除恢复", ObjectKeyboard);
        Run("拖动边界和取消不产生历史记录", CancelAndBoundary);
        Run("默认绘图工具继续创建独立标注", PreserveDrawing);
        Run("序号撤销后从现有最大值继续", NumberSequenceHistory);
        File.WriteAllLines(Path.Combine(output, "report.txt"), Results, new UTF8Encoding(false));
        foreach (string line in Results) Console.WriteLine(line);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Results.Add("[PASS] " + name); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Results.Add("[FAIL] " + name + ": " + error.Message);
            failed++;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Call(object target, string method, params object[] args)
    {
        MethodInfo function = target.GetType().GetMethod(method, Hidden);
        if (function == null) throw new InvalidOperationException("缺少行为入口: " + method);
        return function.Invoke(target, args);
    }

    private static object Get(object target, string field)
    {
        return target.GetType().GetField(field, Hidden).GetValue(target);
    }

    private static OverlayForm Form()
    {
        Bitmap image = new Bitmap(800, 600, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
        OverlayForm form = new OverlayForm(image, new Rectangle(-800, 0, 800, 600),
            new AppConfig { AutoCopy = false, AutoSave = false, FirstRunDone = true,
                SemanticPrivacy = false, VisionPrivacy = false }, null, null);
        form.SuppressActivation = true;
        form.Show();
        form.Location = new Point(-32000, -32000);
        Application.DoEvents();
        Mouse(form, "OnMouseDown", new Point(10, 10));
        Mouse(form, "OnMouseMove", new Point(780, 570));
        Mouse(form, "OnMouseUp", new Point(780, 570));
        return form;
    }

    private static void Mouse(OverlayForm form, string method, Point point)
    {
        Call(form, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
    }

    private static void Drag(OverlayForm form, Point from, Point to)
    {
        Mouse(form, "OnMouseDown", from);
        Mouse(form, "OnMouseMove", to);
        Mouse(form, "OnMouseUp", to);
    }

    private static void Select(OverlayForm form)
    {
        Check(Enum.IsDefined(typeof(EditorTool), "Select"), "缺少选择工具");
        Call(form, "SetTool", Enum.Parse(typeof(EditorTool), "Select"));
    }

    private static bool Key(OverlayForm form, Keys keys)
    {
        object[] arguments = { new Message(), keys };
        return (bool)typeof(OverlayForm).GetMethod("ProcessCmdKey", Hidden).Invoke(form, arguments);
    }

    private static List<Annotation> Items(OverlayForm form)
    {
        return (List<Annotation>)Get(form, "_annotations");
    }

    private static void MoveEveryObject()
    {
        using (Bitmap sticker = new Bitmap(24, 24))
        {
            Annotation[] examples = {
                new ShapeAnnotation { Rect = new RectangleF(100,100,100,60) },
                new ShapeAnnotation { IsEllipse = true, Rect = new RectangleF(100,100,100,60) },
                new ArrowAnnotation { Start = new PointF(100,100), End = new PointF(200,160) },
                new PenAnnotation { Points = new List<PointF> {new PointF(100,100),new PointF(150,130),new PointF(200,160)} },
                new MosaicAnnotation { Rects = new List<RectangleF> {new RectangleF(100,100,100,60)} },
                new TextAnnotation {Text="Test", Location=new PointF(100,100)},
                new EmojiAnnotation {Image=sticker,Rect=new RectangleF(100,100,100,60)},
                new NumberAnnotation {Center=new PointF(150,130),Radius=25}
            };
            foreach (Annotation item in examples)
                using (OverlayForm form = Form())
                {
                    Call(form, "AddAnnotation", item);
                    Select(form);
                    RectangleF before = item.GetBounds();
                    Point center = new Point((int)(before.Left + before.Width / 2), (int)(before.Top + before.Height / 2));
                    Drag(form, center, new Point(center.X + 50, center.Y + 35));
                    RectangleF moved = item.GetBounds();
                    Check(Math.Abs(moved.X-before.X-50)<1 && Math.Abs(moved.Y-before.Y-35)<1, "移动失败: "+item.GetType().Name);
                    Call(form, "Undo");
                    Check(item.GetBounds() == before && Items(form).Count == 1, "移动撤销失败: "+item.Type);
                    Call(form, "Redo");
                    Check(item.GetBounds() == moved, "移动重做失败: "+item.Type);
                    Call(form, "DeleteSelectedAnnotation");
                    Check(Items(form).Count==0, "删除失败: "+item.Type);
                    Call(form, "Undo");
                    Check(Items(form).Count==1 && ReferenceEquals(Items(form)[0],item), "删除撤销未恢复同一对象");
                }
        }
    }

    private static void ResizeAndRedaction()
    {
        using (OverlayForm form = Form())
        {
            var mask = MosaicAnnotation.BuildStrokeMask(new PointF[]{new PointF(100,100),new PointF(200,130)},30);
            MosaicAnnotation item = new MosaicAnnotation {Mask=mask};
            Call(form,"AddAnnotation",item);
            Select(form);
            Mouse(form,"OnMouseDown",new Point(150,115));
            Mouse(form,"OnMouseUp",new Point(150,115));
            RectangleF before=item.GetBounds();
            Point corner=new Point((int)Math.Round(before.Right),(int)Math.Round(before.Bottom));
            Drag(form,corner,new Point(corner.X+80,corner.Y+55));
            Check(item.GetBounds().Width>before.Width+70,"笔刷遮挡未缩放");
            using(Bitmap composed=(Bitmap)Call(form,"ComposeSelection"))
            {
                Check(composed.GetPixel(140,105).ToArgb()!=Color.White.ToArgb(),"缩放后遮挡失效");
                composed.Save(Path.Combine(output,"resized-mosaic.png"),ImageFormat.Png);
            }
            Call(form,"Undo");
            Check(Math.Abs(item.GetBounds().Width-before.Width)<1,"路径缩放未撤销");
            Call(form,"Redo");
            Check(item.GetBounds().Width>before.Width+70,"路径缩放未重做");
        }
    }

    private static void ResizeEveryObject()
    {
        using(Bitmap sticker=new Bitmap(24,24))
        {
            Annotation[] examples={
                new ShapeAnnotation {Rect=new RectangleF(100,100,100,60)},
                new ShapeAnnotation {IsEllipse=true,Rect=new RectangleF(100,100,100,60)},
                new ArrowAnnotation {Start=new PointF(100,100),End=new PointF(200,160)},
                new PenAnnotation {Points=new List<PointF>{new PointF(100,100),new PointF(150,130),new PointF(200,160)}},
                new MosaicAnnotation {Rects=new List<RectangleF>{new RectangleF(100,100,100,60)}},
                new TextAnnotation {Text="Test",Location=new PointF(100,100)},
                new EmojiAnnotation {Image=sticker,Rect=new RectangleF(100,100,100,60)},
                new NumberAnnotation {Center=new PointF(150,130),Radius=25}
            };
            foreach(Annotation item in examples)
                using(OverlayForm form=Form())
                {
                    Call(form,"AddAnnotation",item); Select(form);
                    RectangleF before=item.GetBounds();
                    Point center=Point.Round(new PointF(before.X+before.Width/2,before.Y+before.Height/2));
                    Mouse(form,"OnMouseDown",center);Mouse(form,"OnMouseUp",center);
                    RectangleF[] handles=(RectangleF[])Call(form,"ObjectHandles");
                    Point corner=Point.Round(new PointF(handles[4].X+handles[4].Width/2,handles[4].Y+handles[4].Height/2));
                    Drag(form,corner,new Point(corner.X+80,corner.Y+55));
                    RectangleF after=item.GetBounds();
                    Check(after.Width>before.Width+40&&after.Height>before.Height+40,"缩放没有扩大对象: "+item.GetType().Name);
                    Call(form,"Undo");Check(item.GetBounds()==before,"缩放撤销未恢复: "+item.GetType().Name);
                    Call(form,"Redo");Check(item.GetBounds()==after,"缩放重做不一致: "+item.GetType().Name);
                }
        }
    }

    private static void TextHistory()
    {
        using(OverlayForm form=Form())
        {
            TextAnnotation text=new TextAnnotation{Text="before",Location=new PointF(100,100)};
            ShapeAnnotation next=new ShapeAnnotation{Rect=new RectangleF(300,100,50,50)};
            Call(form,"AddAnnotation",text); Call(form,"AddAnnotation",next);
            Call(form,"BeginEditExistingText",text);
            ((TextBox)Get(form,"_textInput")).Text="after";
            Call(form,"CommitText");
            Check(ReferenceEquals(Items(form)[0],text)&&text.Text=="after","文字编辑改变层级");
            Call(form,"Undo");
            Check(text.Text=="before"&&Items(form).Count==2,"文字编辑撤销删除对象");
            Call(form,"Redo");
            Check(text.Text=="after","文字编辑重做失败");
            Call(form,"BeginEditExistingText",text);
            ((TextBox)Get(form,"_textInput")).Text="discarded";
            Call(form,"CancelText");
            Check(text.Text=="after"&&ReferenceEquals(Items(form)[0],text),"取消文字编辑未保留原层级");
        }
    }

    private static void TranslationEditing()
    {
        using(OverlayForm form=Form())
        {
            using(Graphics graphics=Graphics.FromImage((Bitmap)Get(form,"_screenshot"))) graphics.FillRectangle(Brushes.Black,108,108,12,12);
            TranslationBlock first=new TranslationBlock{Rect=new RectangleF(100,100,140,35),Original="原文",Translation="Translation"};
            TranslationBlock second=new TranslationBlock{Rect=new RectangleF(100,200,140,35),Original="另一行",Translation="Other line"};
            TranslationOverlayAnnotation overlay=new TranslationOverlayAnnotation{Blocks=new List<TranslationBlock>{first,second}};
            Call(form,"AddAnnotation",overlay); Select(form);
            Drag(form,new Point(160,115),new Point(210,145));
            Check(first.Rect.X==150&&second.Rect.X==100,"逐块移动影响其他译文");
            RectangleF source=(RectangleF)first.GetType().GetField("SourceRect").GetValue(first);
            Check(source.X==100&&source.Y==100,"移动丢失原文字背景覆盖范围");
            using(Bitmap moved=(Bitmap)Call(form,"ComposeSelection"))
                Check(moved.GetPixel(104,104).ToArgb()==Color.White.ToArgb(),"移动译文后露出原文字像素");
            Call(form,"BeginTranslationEdit",overlay,0);
            ((TextBox)Get(form,"_translationInput")).Text="Edited translation";
            using(Bitmap editing=new Bitmap(form.Width,form.Height))
            {form.DrawToBitmap(editing,new Rectangle(Point.Empty,editing.Size));editing.Save(Path.Combine(output,"translation-edit-input.png"),ImageFormat.Png);}
            Call(form,"CommitTranslationEdit");
            Check(overlay.Blocks[0].Translation=="Edited translation","译文提交未生效");
            Call(form,"Undo");
            Check(overlay.Blocks[0].Translation=="Translation"&&overlay.Blocks[0].Rect.X==150,"译文编辑撤销不正确");
            Call(form,"Redo");
            Call(form,"ToggleTranslationOriginal");
            Check((bool)overlay.GetType().GetField("ShowOriginal").GetValue(overlay),"未切换原文");
            using(Bitmap original=(Bitmap)Call(form,"ComposeSelection"))
            {
                Check(original.GetPixel(160,130).ToArgb()==Color.White.ToArgb(),"原文模式仍绘制译文");
                Check(original.GetPixel(104,104).ToArgb()==Color.Black.ToArgb(),"原文模式未恢复原图像素");
            }
            Call(form,"ToggleTranslationOriginal");
            Check(!(bool)overlay.GetType().GetField("ShowOriginal").GetValue(overlay),"未切回译文");
            using(Bitmap preview=new Bitmap(form.Width,form.Height))
            {form.DrawToBitmap(preview,new Rectangle(Point.Empty,preview.Size));preview.Save(Path.Combine(output,"translation-editor.png"),ImageFormat.Png);}
        }
    }

    private static void RedoBranch()
    {
        using(OverlayForm form=Form())
        {
            MosaicAnnotation mosaic=new MosaicAnnotation{Rects=new List<RectangleF>{new RectangleF(100,100,80,40)}};
            Call(form,"AddAnnotation",mosaic);
            Call(form,"AddAnnotation",new ShapeAnnotation{Rect=new RectangleF(300,100,60,60)});
            Call(form,"Undo");
            Call(form,"AddAnnotation",new NumberAnnotation{Center=new PointF(450,150)});
            Call(form,"Redo");
            Check(Items(form).Count==2,"新操作没有清空重做分支");
            Call(form,"ClearAnnotations");
            Check(Items(form).Count==0,"清空没有生效");
            Call(form,"Undo");
            Check(Items(form).Count==2,"清空不能撤销");
            using(Bitmap composed=(Bitmap)Call(form,"ComposeSelection"))
                Check(composed.GetPixel(110,105).ToArgb()!=Color.White.ToArgb(),"撤销清空丢失已有手动遮挡");
        }
    }

    private static void TranslationFrameFit()
    {
        using(OverlayForm form=Form())
        {
            TranslationOverlayAnnotation overlay=new TranslationOverlayAnnotation {
                Blocks=new List<TranslationBlock>{
                    new TranslationBlock {Rect=new RectangleF(100,100,80,14),Original="短原文",
                        Translation="A complete translated sentence that needs a wider frame and enough room to display every word.",FontPx=14},
                    new TranslationBlock {Rect=new RectangleF(100,470,100,30),Original="保留行",Translation="Keep this line"}
                }
            };
            Call(form,"AddAnnotation",overlay);
            Check((bool)Call(overlay,"HasOverflow"),"样本未触发译文溢出");
            Select(form);
            Mouse(form,"OnMouseDown",new Point(140,107));Mouse(form,"OnMouseUp",new Point(140,107));
            Drag(form,new Point(180,114),new Point(650,420));
            Check(!(bool)Call(overlay,"HasOverflow"),"手动扩框后完整译文仍不能显示");
            Check(overlay.Blocks[1].Rect.Y==470,"扩框改变其他行位置");
            Key(form,Keys.Delete);
            Check(overlay.Blocks.Count==1&&overlay.Blocks[0].Translation=="Keep this line","Delete应只删除当前译文框");
            Call(form,"Undo");
            Check(overlay.Blocks.Count==2&&overlay.Blocks[0].Rect.Width==550,"删除译文框撤销未恢复调整后的框");
            Call(form,"Undo");
            Check(overlay.Blocks[0].Rect.Width==80,"扩框撤销没有恢复原始宽度");
        }
    }

    private static void ObjectKeyboard()
    {
        using(OverlayForm form=Form())
        {
            ShapeAnnotation item=new ShapeAnnotation{Rect=new RectangleF(100,100,100,60)};
            Call(form,"AddAnnotation",item);Check(Key(form,Keys.V),"V未启用选择工具");
            Mouse(form,"OnMouseDown",new Point(150,130));Mouse(form,"OnMouseUp",new Point(150,130));
            Check(Key(form,Keys.Right)&&item.Rect.X==101,"方向键未微调对象");
            Check(Key(form,Keys.Shift|Keys.Down)&&item.Rect.Y==110,"Shift方向键未移动10像素");
            Key(form,Keys.Control|Keys.Z);Check(item.Rect.Y==100&&item.Rect.X==101,"键盘微调撤销错误");
            Key(form,Keys.Control|Keys.Shift|Keys.Z);Check(item.Rect.Y==110,"Ctrl+Shift+Z未重做");
            Key(form,Keys.Delete);Check(Items(form).Count==0,"Delete未删除对象");
            Key(form,Keys.Control|Keys.Z);Check(Items(form).Count==1,"Delete撤销失败");
        }
    }

    private static void CancelAndBoundary()
    {
        using(OverlayForm form=Form())
        {
            ShapeAnnotation item=new ShapeAnnotation{Rect=new RectangleF(100,100,100,60)};
            Call(form,"AddAnnotation",item);Select(form);
            Mouse(form,"OnMouseDown",new Point(150,130));Mouse(form,"OnMouseMove",new Point(300,260));
            Check(item.Rect.X!=100,"取消用例没有进入拖动");
            Key(form,Keys.Escape);Check(item.Rect.X==100&&item.Rect.Y==100,"Esc未恢复拖动前状态");
            Call(form,"Undo");Check(Items(form).Count==0,"取消拖动错误地新增历史");
            Call(form,"Redo");
            Drag(form,new Point(150,130),new Point(5000,5000));
            Check(item.GetBounds().Right<=781&&item.GetBounds().Bottom<=571,"对象拖出截图边界");
        }
    }

    private static void PreserveDrawing()
    {
        using(OverlayForm form=Form())
        {
            Call(form,"AddAnnotation",new ShapeAnnotation{Rect=new RectangleF(100,100,100,60)});
            Call(form,"SetTool",EditorTool.Rectangle);
            Drag(form,new Point(120,115),new Point(180,145));
            Check(Items(form).Count==2,"矩形工具点击已有对象拦截了绘制");
            Call(form,"Undo");Check(Items(form).Count==1,"新绘制矩形不能撤销");
        }
    }

    private static void NumberSequenceHistory()
    {
        using(OverlayForm form=Form())
        {
            Call(form,"PlaceNumber",new Point(100,100));
            Call(form,"PlaceNumber",new Point(200,100));
            Call(form,"Undo");
            Call(form,"PlaceNumber",new Point(300,100));
            Check(((NumberAnnotation)Items(form)[1]).Number==2,"撤销序号后出现跳号");
        }
    }
}
