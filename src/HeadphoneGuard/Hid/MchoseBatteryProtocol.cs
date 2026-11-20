namespace HeadphoneGuard.Hid;

/// <summary>
/// Стратегии vendor-HID опроса заряда, которые встречаются у беспроводных гарнитур.
/// Форматы восстановлены из open-source проектов
/// (Wireless-Device-Battery-Monitor, HeadsetControl, mchose-macos, HaloBattery).
///
/// У MCHOSE-гарнитур заряд лежит в вендорном канале (usage page 0xFF90 у V9 Pro/X9):
/// запрос — 64-байтный кадр `55 65 01 00 ...`, ответ — `55 65 &lt;значение&gt; &lt;поле&gt; ...`,
/// где поле 0x02 — процент заряда.
/// </summary>
public static class MchoseBatteryProtocol
{
    public enum Transport
    {
        Output,
        Feature
    }

    public sealed record Strategy(
        string Name,
        Transport Transport,
        byte ReportId,
        byte[] Payload,
        ushort? RequiredUsagePage = null);

    public static IReadOnlyList<Strategy> Strategies { get; } = new[]
    {
        new Strategy("out 55 65 01 00", Transport.Output, 0x00, P(0x55, 0x65, 0x01, 0x00), 0xFF90),
        new Strategy("out 55 65 00 00", Transport.Output, 0x00, P(0x55, 0x65, 0x00, 0x00), 0xFF90),
        new Strategy("out 55 0B", Transport.Output, 0x00, P(0x55, 0x0B), 0xFF90),
        new Strategy("out 55 0B 00", Transport.Output, 0x00, P(0x55, 0x0B, 0x00), 0xFF90),
        new Strategy("out 55 65 01 00 (FF82)", Transport.Output, 0x00, P(0x55, 0x65, 0x01, 0x00), 0xFF82),
        new Strategy("feat 55 65 01 00", Transport.Feature, 0x00, P(0x55, 0x65, 0x01, 0x00)),
        new Strategy("feat report 0x55: 65 01 00", Transport.Feature, 0x55, P(0x65, 0x01, 0x00)),
        new Strategy("feat report 0x00: 83", Transport.Feature, 0x00, P(0x83)),
        new Strategy("feat report 0x83: 83", Transport.Feature, 0x83, P(0x83)),
        new Strategy("feat 0F 20", Transport.Feature, 0x00, P(0x0F, 0x20)),
    };

    private static byte[] P(params byte[] head)
    {
        var buffer = new byte[64];
        Array.Copy(head, buffer, head.Length);
        return buffer;
    }

    /// <summary>Кадр-запрос заряда (MCHOSE-совместимый).</summary>
    public static byte[] RequestFrame(int length = 64)
    {
        var frame = new byte[Math.Max(8, length)];
        frame[0] = 0x55;
        frame[1] = 0x65;
        frame[2] = 0x01;
        frame[3] = 0x00;
        return frame;
    }

    /// <summary>Пытается разобрать ответ устройства как уровень заряда.</summary>
    public static int? ParseBattery(byte[]? reply)
    {
        if (reply is null || reply.Length < 4)
            return null;

        // 55 65 <значение> <поле> ... — поле 0x02 это заряд (MCHOSE X9/V9).
        if (reply[0] == 0x55 && reply[1] == 0x65)
        {
            int field = reply[3];
            if (field == 0x02)
                return Valid(reply[2]);

            // Иногда поле приходит следующим байтом: 55 65 <поле> <значение>.
            if (reply[2] == 0x02)
                return Valid(reply[3]);

            return Valid(reply[2]);
        }

        // 55 0B <значение> ... — MC battery frame.
        if (reply[0] == 0x55 && reply[1] == 0x0B)
            return Valid(reply[2]);

        // A1 <значение> ... — MCHOSE A5.
        if (reply[0] == 0xA1)
            return Valid(reply[1]);

        return null;
    }

    private static int? Valid(int value) => value is >= 0 and <= 100 ? value : null;
}
