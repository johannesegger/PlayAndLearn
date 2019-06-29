namespace GetIt.Windows

open GetIt
open System.ComponentModel
open System.Drawing
open System.IO
open System.Runtime.InteropServices

module ScreenCapture =
    let captureWindow handle =
        let mutable rect = Unchecked.defaultof<Win32.Rect>
        let status = Win32.DwmGetWindowAttribute(handle, Win32.DWMWINDOWATTRIBUTE.ExtendedFrameBounds, &rect, Marshal.SizeOf<Win32.Rect>())
        if status <> 0 then
            raise (Win32Exception(sprintf "DwmGetWindowAttribute failed. Status code: %d" status))
        let bounds = Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
        use bitmap = new Bitmap(bounds.Width, bounds.Height)

        do
            use graphics = Graphics.FromImage bitmap
            graphics.CopyFromScreen(Point(bounds.Left, bounds.Top), Point.Empty, bounds.Size)

        use stream = new MemoryStream()
        bitmap.Save(stream, Imaging.ImageFormat.Png)
        stream.ToArray()
        |> PngImage

    let captureActiveWindow () =
        captureWindow <| Win32.GetForegroundWindow ()