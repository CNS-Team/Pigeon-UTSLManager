using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VtNetCore.VirtualTerminal;
using VtNetCore.XTermParser;
namespace Pigeon.Manager.Desktop;
public sealed class TerminalSession {
    public VirtualTerminalController Terminal { get; } = new() { MaximumHistoryLines = 2000 };
    readonly DataConsumer consumer;
    public long Cursor;
    public event Action<string>? Reply;
    public TerminalSession() { Terminal.ResizeView(140, 40); consumer = new(Terminal); Terminal.SendData += (_, e) => Reply?.Invoke(Encoding.UTF8.GetString(e.Data)); }
    public void Feed(string text) => consumer.Push(Encoding.UTF8.GetBytes(text));
    public string PlainText() => string.Join(Environment.NewLine, Terminal.ViewPort.GetPageSpans(0, -1).Select(r => string.Concat(r.Spans.Select(s => s.Text))));
}
public sealed class TerminalView : Control {
    public TerminalSession? Session { get; set; }
    public Action<int,int>? ResizeTerminal;
    int scroll;
    int columns = 140, rows = 40;
    public TerminalView() {
        ClipToBounds = true;
        PointerWheelChanged += (_, e) => { scroll = Math.Clamp(scroll + (int)(e.Delta.Y * 3), 0, Session?.Terminal.ViewPort.TopRow ?? 0); InvalidateVisual(); };
        SizeChanged += (_, _) => { int c = Math.Clamp((int)((Bounds.Width - 24)/7.2), 30, 240); int r = Math.Clamp((int)((Bounds.Height - 20)/19), 10, 100); if (c == columns && r == rows) return; columns = c; rows = r; ResizeTerminal?.Invoke(c,r); Session?.Terminal.ResizeView(c,r); InvalidateVisual(); };
    }
    public void Refresh(bool follow = false) { if (follow) scroll = 0; InvalidateVisual(); }
    public override void Render(DrawingContext context) {
        base.Render(context); if (Session == null) return;
        var terminal = Session.Terminal;
        int count = Math.Max(1, (int)((Bounds.Height-16)/19)); int top = Math.Max(0, terminal.ViewPort.TopRow - scroll);
        var lines = terminal.ViewPort.GetPageSpans(top, count);
        int index = 0;
        foreach (var line in lines) {
            double x = 12, y = 9 + index++ * 19;
            foreach (var span in line.Spans) {
                if (string.IsNullOrEmpty(span.Text)) continue;
                IBrush color; try { color = Brush.Parse(span.ForgroundColor); } catch { color = Brush.Parse("#D5D7E0"); }
                var typeface = new Typeface("Cascadia Mono,Consolas,Microsoft YaHei UI", span.Italic ? FontStyle.Italic : FontStyle.Normal, span.Bold ? FontWeight.Bold : FontWeight.Normal);
                var text = new FormattedText(span.Hidden ? new string(' ',span.Text.Length) : span.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, color);
                try { if (span.BackgroundColor != "#000000") context.FillRectangle(Brush.Parse(span.BackgroundColor), new Rect(x,y,text.Width,19)); } catch { }
                context.DrawText(text, new Point(x,y)); x += text.WidthIncludingTrailingWhitespace;
                if (span.Underline) context.DrawLine(new Pen(color), new Point(x-text.Width,y+16), new Point(x,y+16));
            }
        }
    }
}
