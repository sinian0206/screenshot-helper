using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using XiaoJie;

internal static class ImageToolsTests
{
    private static int failed;
    private static string directory;
    [STAThread]
    private static int Main(string[] args)
    {
        directory = args[0]; Directory.CreateDirectory(directory);
        Run("import preserves alpha and unlocks file", ImportAlpha);
        Run("damaged file is rejected with readable error", Damaged);
        Run("pixel budget is checked without allocation", Budget);
        Run("vertical composition keeps order, native pixels and gaps", Vertical);
        Run("horizontal composition keeps order and background", Horizontal);
        Run("transparent composition retains alpha", Alpha);
        Run("composition rejects empty input and oversized output", InvalidComposition);
        return failed == 0 ? 0 : 1;
    }
    private static void Run(string name, Action action)
    { try { action(); Console.WriteLine("PASS " + name); } catch(Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
    private static void Check(bool pass, string message) { if(!pass) throw new Exception(message); }
    private static Bitmap Solid(int width, int height, Color color)
    { Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb); using(Graphics g=Graphics.FromImage(image)) { g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.Clear(color); } return image; }
    private static void ImportAlpha()
    {
        string path=Path.Combine(directory,"alpha.png");
        using(Bitmap image=Solid(13,9,Color.FromArgb(127,60,120,180))) image.Save(path,ImageFormat.Png);
        using(Bitmap copy=ImageImport.LoadFile(path))
        {
            using(FileStream exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) Check(exclusive.Length>0,"file locked");
            Color pixel=copy.GetPixel(5,5); Check(pixel.A==127 && Math.Abs(pixel.R-60)<=1,"alpha/color changed");
            File.Delete(path); Check(copy.GetPixel(5,5).A==127,"image depends on source stream");
        }
    }
    private static void Damaged()
    {
        string path=Path.Combine(directory,"invalid.png"); File.WriteAllText(path,"invalid image data");
        try { using(Bitmap image=ImageImport.LoadFile(path)){} throw new Exception("accepted corrupt input"); }
        catch(InvalidDataException error) { Check(error.Message.Contains("图片"),"unclear error"); }
    }
    private static void Budget()
    { ImageImport.ValidateSize(8000,5000); try { ImageImport.ValidateSize(8001,5000); throw new Exception("budget ignored"); } catch(InvalidDataException){} }
    private static void Vertical()
    {
        using(Bitmap a=Solid(3,2,Color.Red)) using(Bitmap b=Solid(2,3,Color.Blue))
        using(Bitmap result=ImageComposer.Compose(new[]{a,b},true,2,Color.White))
        {
            Check(result.Size==new Size(3,7),"wrong size"); Check(result.GetPixel(0,0).ToArgb()==Color.Red.ToArgb(),"first moved");
            Check(result.GetPixel(0,2).ToArgb()==Color.White.ToArgb(),"missing gap"); Check(result.GetPixel(0,4).ToArgb()==Color.Blue.ToArgb(),"second moved");
            Check(result.GetPixel(2,6).ToArgb()==Color.White.ToArgb(),"background missing");
        }
    }
    private static void Horizontal()
    {
        using(Bitmap a=Solid(3,2,Color.Red)) using(Bitmap b=Solid(2,3,Color.Blue))
        using(Bitmap result=ImageComposer.Compose(new[]{b,a},false,1,Color.Black))
        { Check(result.Size==new Size(6,3),"wrong size"); Check(result.GetPixel(0,0).B==255 && result.GetPixel(3,0).R==255,"order ignored"); Check(result.GetPixel(5,2).ToArgb()==Color.Black.ToArgb(),"background missing"); }
    }
    private static void Alpha()
    {
        using(Bitmap a=Solid(2,2,Color.FromArgb(128,0,200,0))) using(Bitmap b=Solid(1,1,Color.Blue))
        using(Bitmap result=ImageComposer.Compose(new[]{a,b},true,1,Color.Transparent))
        { Check(result.GetPixel(0,0).A==128,"input alpha lost"); Check(result.GetPixel(0,2).A==0,"transparent gap lost"); }
    }
    private static void InvalidComposition()
    {
        try { ImageComposer.Measure(new Bitmap[0],true,0); throw new Exception("empty input accepted"); } catch(InvalidDataException){}
        using(Bitmap a=Solid(2,2,Color.Red))
        { try { ImageComposer.Measure(new[]{a,a},true,int.MaxValue); throw new Exception("oversized output accepted"); } catch(InvalidDataException){} }
    }
}
