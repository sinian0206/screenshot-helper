using System;

namespace XiaoJie
{
    internal enum CaptureIntent
    {
        Screenshot,
        Ocr,
        Translate,
        Privacy
    }

    internal static class CaptureCommand
    {
        internal static CaptureIntent FromArgs(string[] args)
        {
            if (Program.HasArg(args, "--ocr")) return CaptureIntent.Ocr;
            if (Program.HasArg(args, "--translate")) return CaptureIntent.Translate;
            if (Program.HasArg(args, "--redact")) return CaptureIntent.Privacy;
            return CaptureIntent.Screenshot;
        }

        internal static uint MessageForArgs(string[] args)
        {
            if (Program.HasArg(args, "--capture")) return AppInfo.WM_APP_TRIGGER_CAPTURE;
            if (Program.HasArg(args, "--settings")) return AppInfo.WM_APP_OPEN_SETTINGS;
            if (Program.HasArg(args, "--record")) return AppInfo.WM_APP_TRIGGER_RECORD;
            switch (FromArgs(args))
            {
                case CaptureIntent.Ocr: return AppInfo.WM_APP_TRIGGER_OCR;
                case CaptureIntent.Translate: return AppInfo.WM_APP_TRIGGER_TRANSLATE;
                case CaptureIntent.Privacy: return AppInfo.WM_APP_TRIGGER_PRIVACY;
                default: return AppInfo.WM_APP_SHOW_TOAST;
            }
        }
    }
}
