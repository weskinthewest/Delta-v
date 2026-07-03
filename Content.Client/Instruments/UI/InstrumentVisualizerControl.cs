using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Audio.Midi;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Client.Instruments.UI;

public sealed class InstrumentVisualizerControl : Control
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float TimeWindow = 1.2f;
    private const float NoteLifetime = TimeWindow + 0.25f;
    private const int MaxTrackedNotes = 512;
    private const int MaxBendHistoryPerChannel = 512;
    private const float PitchBendRangeSemitones = 2f;
    private const float KeyboardHeight = 18f;
    private const float MinKeySpan = 12f;
    private const float RangePadding = 2f;
    private const float RangeSmoothing = 6f;

    private static readonly Color[] ChannelColors =
    {
        Color.FromHex("#e6194b"), Color.FromHex("#3cb44b"), Color.FromHex("#4363d8"), Color.FromHex("#f58231"),
        Color.FromHex("#911eb4"), Color.FromHex("#42d4f4"), Color.FromHex("#f032e6"), Color.FromHex("#bfef45"),
        Color.FromHex("#fabed4"), Color.FromHex("#aaaaaa"), Color.FromHex("#469990"), Color.FromHex("#dcbeff"),
        Color.FromHex("#9A6324"), Color.FromHex("#fffac8"), Color.FromHex("#800000"), Color.FromHex("#a9a9a9"),
    };

    private static readonly Color PercussionColor = Color.FromHex("#cfcfcf");

    private sealed class NoteVisual
    {
        public byte Channel;
        public byte Key;
        public float StartTime;
        public float? EndTime;
    }

    private readonly List<NoteVisual> _notes = new();
    private readonly Dictionary<byte, List<(float Time, float Semitones)>> _bendHistory = new();
    private readonly List<Vector2> _quadScratch = new(6);

    private float _displayMinKey = 48f;
    private float _displayMaxKey = 72f;

    private InstrumentComponent? _instrument;

    public InstrumentVisualizerControl()
    {
        IoCManager.InjectDependencies(this);
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Ignore;
    }

    public void AttachInstrument(InstrumentComponent? instrument)
    {
        if (ReferenceEquals(_instrument, instrument))
            return;

        if (_instrument != null)
        {
            _instrument.OnMidiNotePlayed -= OnMidiNotePlayed;
            _instrument.OnMidiRendererReset -= OnRendererReset;
        }

        _instrument = instrument;
        _notes.Clear();
        _bendHistory.Clear();

        if (_instrument != null)
        {
            _instrument.OnMidiNotePlayed += OnMidiNotePlayed;
            _instrument.OnMidiRendererReset += OnRendererReset;
        }
    }

    private void OnRendererReset()
    {
        _notes.Clear();
        _bendHistory.Clear();
    }

    private void OnMidiNotePlayed(RobustMidiEvent ev)
    {
        var now = (float)_timing.RealTime.TotalSeconds;

        if (ev.MidiCommand == RobustMidiCommand.SystemMessage && ev.Status == 0xFF)
        {
            _notes.Clear();
            _bendHistory.Clear();
            return;
        }

        switch (ev.MidiCommand)
        {
            case RobustMidiCommand.NoteOn when ev.Velocity > 0:
                StartNote((byte)ev.Channel, ev.Key, now);
                break;

            case RobustMidiCommand.NoteOn:
            case RobustMidiCommand.NoteOff:
                EndNote((byte)ev.Channel, ev.Key, now);
                break;

            case RobustMidiCommand.PitchBend:
                RecordPitchBend((byte)ev.Channel, ev.Pitch, now);
                break;

            case RobustMidiCommand.SystemMessage when ev.Control == 0x0B:
                foreach (var note in _notes)
                {
                    if (note.Channel == ev.Channel && note.EndTime == null)
                        note.EndTime = now;
                }
                break;
        }
    }

    private void StartNote(byte channel, byte key, float now)
    {
        if (_notes.Count >= MaxTrackedNotes)
            _notes.RemoveAt(0);

        _notes.Add(new NoteVisual
        {
            Channel = channel,
            Key = key,
            StartTime = now,
            EndTime = null,
        });
    }

    private void EndNote(byte channel, byte key, float now)
    {
        for (var i = _notes.Count - 1; i >= 0; i--)
        {
            var note = _notes[i];
            if (note.Channel == channel && note.Key == key && note.EndTime == null)
            {
                note.EndTime = now;
                return;
            }
        }
    }

    private void RecordPitchBend(byte channel, int pitch14, float now)
    {
        var semitones = (pitch14 - 8192) / 8192f * PitchBendRangeSemitones;

        if (!_bendHistory.TryGetValue(channel, out var history))
        {
            history = new List<(float, float)>();
            _bendHistory[channel] = history;
        }

        if (history.Count > 0 && history[^1].Time == now)
            history[^1] = (now, semitones);
        else
            history.Add((now, semitones));

        if (history.Count > MaxBendHistoryPerChannel)
            history.RemoveAt(0);
    }

    private float GetBendAt(byte channel, float time)
    {
        if (!_bendHistory.TryGetValue(channel, out var history))
            return 0f;

        var semitones = 0f;
        foreach (var point in history)
        {
            if (point.Time > time)
                break;

            semitones = point.Semitones;
        }

        return semitones;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        var now = (float)_timing.RealTime.TotalSeconds;
        _notes.RemoveAll(note => note.EndTime is { } end && now - end > NoteLifetime);

        var cutoff = now - NoteLifetime;
        foreach (var history in _bendHistory.Values)
        {
            var cutoffIndex = history.FindLastIndex(point => point.Time <= cutoff);
            if (cutoffIndex > 0)
                history.RemoveRange(0, cutoffIndex);
        }

        UpdateDisplayRange(now, args.DeltaSeconds);
    }

    private static bool IsNoteVisible(NoteVisual note, float now)
    {
        var startAge = Math.Clamp(now - note.StartTime, 0f, TimeWindow);
        var endAge = note.EndTime is { } end ? Math.Clamp(now - end, 0f, TimeWindow) : 0f;
        return startAge < TimeWindow || endAge < TimeWindow;
    }

    private void UpdateDisplayRange(float now, float deltaSeconds)
    {
        var minKey = 127f;
        var maxKey = 0f;
        var haveVisibleNote = false;

        foreach (var note in _notes)
        {
            if (!IsNoteVisible(note, now))
                continue;

            haveVisibleNote = true;
            minKey = MathF.Min(minKey, note.Key);
            maxKey = MathF.Max(maxKey, note.Key);
        }

        if (!haveVisibleNote)
            return;

        minKey = MathF.Max(0f, minKey - RangePadding);
        maxKey = MathF.Min(127f, maxKey + RangePadding);

        var span = MathF.Max(MinKeySpan, maxKey - minKey);
        var center = (minKey + maxKey) / 2f;
        var targetMin = center - span / 2f;
        var targetMax = center + span / 2f;

        var lerp = Math.Clamp(deltaSeconds * RangeSmoothing, 0f, 1f);
        _displayMinKey += (targetMin - _displayMinKey) * lerp;
        _displayMaxKey += (targetMax - _displayMaxKey) * lerp;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var size = (Vector2)PixelSize;
        if (size.X <= 0f || size.Y <= 0f)
            return;

        handle.DrawRect(PixelSizeBox, Color.FromHex("#0b0b0b"));

        var highwayHeight = MathF.Max(0f, size.Y - KeyboardHeight);
        var keyRange = MathF.Max(1f, _displayMaxKey - _displayMinKey);
        var keyWidth = size.X / keyRange;

        DrawOctaveGridLines(handle, highwayHeight, keyWidth);

        var now = (float)_timing.RealTime.TotalSeconds;
        var heldKeyColor = new Dictionary<byte, Color>();

        foreach (var note in _notes)
        {
            if (!IsNoteVisible(note, now))
                continue;

            var color = GetChannelColor(note.Channel);

            if (note.EndTime == null)
                heldKeyColor[note.Key] = color;

            DrawNoteBody(handle, note, now, highwayHeight, size.X, keyWidth, color);
        }

        DrawKeyboard(handle, size, highwayHeight, keyWidth, heldKeyColor);
    }

    private void DrawNoteBody(DrawingHandleScreen handle, NoteVisual note, float now, float highwayHeight,
        float width, float keyWidth, Color color)
    {
        var endTime = note.EndTime ?? now;
        var visibleStart = MathF.Max(note.StartTime, now - TimeWindow);
        var visibleEnd = MathF.Min(endTime, now);

        if (visibleEnd <= visibleStart)
            return;

        if (!_bendHistory.TryGetValue(note.Channel, out var history) || history.Count == 0)
        {
            var x = (note.Key - _displayMinKey) * keyWidth;
            if (x + keyWidth >= 0f && x <= width)
            {
                var topY = TimeToY(visibleStart, now, highwayHeight);
                var bottomY = TimeToY(visibleEnd, now, highwayHeight);

                var box = new UIBox2(x + 1f, topY, x + keyWidth - 1f, bottomY);
                handle.DrawRect(box, color);
                handle.DrawRect(box, color.WithAlpha(0.6f), filled: false);
            }
            return;
        }

        var breakpoints = new List<float> { visibleStart };
        foreach (var (time, _) in history)
        {
            if (time > visibleStart && time < visibleEnd)
                breakpoints.Add(time);
        }
        breakpoints.Add(visibleEnd);

        for (var i = 0; i < breakpoints.Count - 1; i++)
        {
            var segmentStart = breakpoints[i];
            var segmentEnd = breakpoints[i + 1];

            var keyAtStart = note.Key + GetBendAt(note.Channel, segmentStart);
            var keyAtEnd = note.Key + GetBendAt(note.Channel, segmentEnd);

            var xAtStart = (keyAtStart - _displayMinKey) * keyWidth;
            var xAtEnd = (keyAtEnd - _displayMinKey) * keyWidth;

            if (xAtStart + keyWidth < 0f && xAtEnd + keyWidth < 0f)
                continue;
            if (xAtStart > width && xAtEnd > width)
                continue;

            var yAtStart = TimeToY(segmentStart, now, highwayHeight);
            var yAtEnd = TimeToY(segmentEnd, now, highwayHeight);

            DrawQuad(handle, xAtStart + 1f, xAtStart + keyWidth - 1f, xAtEnd + 1f, xAtEnd + keyWidth - 1f,
                yAtStart, yAtEnd, color);
        }
    }

    private static float TimeToY(float time, float now, float highwayHeight)
    {
        var age = Math.Clamp(now - time, 0f, TimeWindow);
        return highwayHeight - age / TimeWindow * highwayHeight;
    }

    private void DrawQuad(DrawingHandleScreen handle, float xLeftTop, float xRightTop, float xLeftBottom,
        float xRightBottom, float yTop, float yBottom, Color color)
    {
        _quadScratch.Clear();
        _quadScratch.Add(new Vector2(xLeftTop, yTop));
        _quadScratch.Add(new Vector2(xRightTop, yTop));
        _quadScratch.Add(new Vector2(xLeftBottom, yBottom));

        _quadScratch.Add(new Vector2(xRightTop, yTop));
        _quadScratch.Add(new Vector2(xRightBottom, yBottom));
        _quadScratch.Add(new Vector2(xLeftBottom, yBottom));

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, _quadScratch, color);
    }

    private void DrawOctaveGridLines(DrawingHandleScreen handle, float highwayHeight, float keyWidth)
    {
        var lineColor = Color.FromHex("#1c1c1c");
        var firstC = MathF.Ceiling(_displayMinKey / 12f) * 12f;

        for (var key = firstC; key <= _displayMaxKey; key += 12f)
        {
            var x = (key - _displayMinKey) * keyWidth;
            handle.DrawLine(new Vector2(x, 0f), new Vector2(x, highwayHeight), lineColor);
        }
    }

    private void DrawKeyboard(DrawingHandleScreen handle, Vector2 size, float highwayHeight, float keyWidth,
        Dictionary<byte, Color> heldKeyColor)
    {
        var keyboardBox = new UIBox2(0f, highwayHeight, size.X, size.Y);
        handle.DrawRect(keyboardBox, Color.FromHex("#1a1a1a"));

        var startKey = (int)MathF.Floor(_displayMinKey);
        var endKey = (int)MathF.Ceiling(_displayMaxKey);

        for (var key = startKey; key <= endKey; key++)
        {
            if (key < 0 || key > 127)
                continue;

            var x = (key - _displayMinKey) * keyWidth;
            if (x + keyWidth < 0f || x > size.X)
                continue;

            var isBlack = IsBlackKey(key);
            var baseColor = isBlack ? Color.FromHex("#101010") : Color.FromHex("#e6e6e6");

            if (heldKeyColor.TryGetValue((byte)key, out var held))
                baseColor = Color.InterpolateBetween(baseColor, held, 0.75f);

            var box = new UIBox2(x + 0.5f, highwayHeight, x + keyWidth - 0.5f, size.Y);
            handle.DrawRect(box, baseColor);
        }

        handle.DrawLine(new Vector2(0f, highwayHeight), new Vector2(size.X, highwayHeight), Color.FromHex("#444444"));
    }

    private static bool IsBlackKey(int key)
    {
        var noteInOctave = ((key % 12) + 12) % 12;
        return noteInOctave is 1 or 3 or 6 or 8 or 10;
    }

    private static Color GetChannelColor(byte channel)
    {
        return channel == RobustMidiEvent.PercussionChannel
            ? PercussionColor
            : ChannelColors[channel % ChannelColors.Length];
    }
}
