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
    // Only the collector touches the VT parser. The UI reads completed immutable
    // frames and publishes viewport requests; it never takes the parser's lock.
    VirtualTerminalController terminal = null!;
    DataConsumer consumer = null!;
    sealed record Viewport(int Columns, int Rows, int Scroll);
    Viewport requested = new(140, 40, 0), applied = new(0, 0, 0);
    TerminalFrame frame = new(0, 0, []);
    string plainText = "";
    long revision;
    public long Cursor;
    public Action<string>? Response;
    public int ParserThreadId { get; private set; }
    public long ParseCount { get; private set; }
    public TerminalFrame Frame => Volatile.Read(ref frame);
    public string PlainText() => Volatile.Read(ref plainText);
    public void SetViewport(int columns, int rows, int scroll) => Volatile.Write(ref requested, new(columns, rows, scroll));
    public void Apply(LogPage? page) {
        ParserThreadId = Environment.CurrentManagedThreadId;
        bool changed = false;
        if (consumer == null || page?.Truncated == true) {
            terminal = new() { MaximumHistoryLines = 2000 }; terminal.ResizeView(140, 40);
            terminal.SendData += (_, e) => Response?.Invoke(Encoding.UTF8.GetString(e.Data));
            consumer = new(terminal); applied = new(0, 0, 0); changed = true;
            if (page?.Truncated == true) consumer.Push(Encoding.UTF8.GetBytes("[manager] 回放已截断；完整宿主日志见 Data/logs\r\n"));
        }
        if (page != null) {
            foreach (var chunk in page.Chunks) { consumer.Push(Encoding.UTF8.GetBytes(chunk.Text)); changed = true; ParseCount++; }
            Cursor = page.Cursor;
        }
        var viewport = Volatile.Read(ref requested);
        if (viewport != applied) {
            if (viewport.Columns != applied.Columns || viewport.Rows != applied.Rows) terminal.ResizeView(viewport.Columns, viewport.Rows);
            applied = viewport; changed = true;
        }
        if (!changed) return;
        var lines = terminal.ViewPort.GetPageSpans(Math.Max(0, terminal.ViewPort.TopRow - viewport.Scroll), viewport.Rows)
            .Select(row => row.Spans.Select(s => new TerminalSpan(s.Hidden ? new string(' ', s.Text.Length) : s.Text,
                s.ForgroundColor, s.BackgroundColor, s.Bold, s.Italic, s.Underline)).ToArray()).ToArray();
        Volatile.Write(ref plainText, string.Join(Environment.NewLine, terminal.ViewPort.GetPageSpans(0, -1).Select(r => string.Concat(r.Spans.Select(s => s.Text)))));
        Volatile.Write(ref frame, new(++revision, terminal.ViewPort.TopRow, lines));
    }
}
public sealed record TerminalSpan(string Text, string Foreground, string Background, bool Bold, bool Italic, bool Underline);
public sealed record TerminalFrame(long Revision, int TopRow, TerminalSpan[][] Lines);
public sealed class TerminalView : Control {
    TerminalSession? session;
    public TerminalSession? Session { get => session; set { if (ReferenceEquals(session, value)) return; session = value; scroll = 0; cachedFrame = null; session?.SetViewport(columns, rows, scroll); InvalidateVisual(); } }
    public Action<int,int>? ResizeTerminal;
    int scroll;
    int columns = 140, rows = 40;
    TerminalFrame? cachedFrame;
    sealed record DrawSpan(FormattedText Text, IBrush? Background, Pen? Underline, double X, double Y);
    readonly List<DrawSpan> drawing = [];
    readonly Dictionary<string, IBrush> colors = [];
    IBrush Color(string value) {
        if (colors.TryGetValue(value, out var brush)) return brush;
        try { brush = Brush.Parse(value); } catch { brush = Brushes.LightGray; }
        if (colors.Count > 512) colors.Clear();
        return colors[value] = brush;
    }
    public TerminalView() {
        ClipToBounds = true;
        PointerWheelChanged += (_, e) => { scroll = Math.Clamp(scroll + (int)(e.Delta.Y * 3), 0, Session?.Frame.TopRow ?? 0); Session?.SetViewport(columns, rows, scroll); };
        SizeChanged += (_, _) => { int c = Math.Clamp((int)((Bounds.Width - 24)/7.2), 30, 240); int r = Math.Clamp((int)((Bounds.Height - 20)/19), 10, 100); if (c == columns && r == rows) return; columns = c; rows = r; ResizeTerminal?.Invoke(c,r); Session?.SetViewport(c,r,scroll); };
    }
    public void Refresh(bool follow = false) { if (follow) { scroll = 0; Session?.SetViewport(columns,rows,scroll); } if (!ReferenceEquals(cachedFrame, Session?.Frame)) InvalidateVisual(); }
    public override void Render(DrawingContext context) {
        base.Render(context); if (Session == null) return;
        var frame = Session.Frame;
        if (!ReferenceEquals(cachedFrame, frame)) {
        drawing.Clear(); int index = 0;
        foreach (var line in frame.Lines) {
            double x = 12, y = 9 + index++ * 19;
            foreach (var span in line) {
                if (string.IsNullOrEmpty(span.Text)) continue;
                IBrush color = Color(span.Foreground);
                var typeface = new Typeface("Cascadia Mono,Consolas,Microsoft YaHei UI", span.Italic ? FontStyle.Italic : FontStyle.Normal, span.Bold ? FontWeight.Bold : FontWeight.Normal);
                var text = new FormattedText(span.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, color);
                drawing.Add(new(text, span.Background == "#000000" ? null : Color(span.Background), span.Underline ? new Pen(color) : null, x, y));
                x += text.WidthIncludingTrailingWhitespace;
            }
        }
        cachedFrame = frame;
        }
        foreach (var span in drawing) {
            if (span.Background != null) context.FillRectangle(span.Background, new Rect(span.X,span.Y,span.Text.Width,19));
            context.DrawText(span.Text, new Point(span.X,span.Y));
            if (span.Underline != null) context.DrawLine(span.Underline, new Point(span.X,span.Y+16), new Point(span.X+span.Text.Width,span.Y+16));
        }
    }
}
