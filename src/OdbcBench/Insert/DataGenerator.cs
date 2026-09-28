namespace OdbcBench.Insert;

/// <summary>
/// The values the insert benchmark sends. Every value is a pure function of the column and the row number (starting
/// at 1), so every DSN, batch size and iteration sends the same rows, and nothing is allocated while generating.
/// </summary>
public static unsafe class DataGenerator
{
    private const long DateRangeDays = 10_000;
    private const long TimestampRangeSeconds = 1_000_000_000;
    private const long SingleRange = 1_000_000; // keeps quarter steps exact in a 4-byte float
    private const int KeyDigits = 10;

    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly ulong[] Pow10 =
    {
        1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000, 1_000_000_000, 10_000_000_000,
        100_000_000_000, 1_000_000_000_000, 10_000_000_000_000, 100_000_000_000_000, 1_000_000_000_000_000,
        10_000_000_000_000_000, 100_000_000_000_000_000, 1_000_000_000_000_000_000, 10_000_000_000_000_000_000,
    };

    /// <summary>Writes the value of <paramref name="row"/> in its bound form and returns its length in bytes (the indicator).</summary>
    public static int Write(InsertColumn column, long row, byte* target)
    {
        if (column.AsText)
        {
            char* text = (char*)target;
            int chars = WriteText(column, row, text);
            text[chars] = '\0';
            return chars * sizeof(char);
        }

        switch (column.Kind)
        {
            case ValueKind.Integer:
            {
                long value = Integer(column, row);
                switch (column.Column.ElementBytes)
                {
                    case 1: *target = (byte)value; return 1;
                    case 2: *(short*)target = (short)value; return 2;
                    case 4: *(int*)target = (int)value; return 4;
                    default: *(long*)target = value; return 8;
                }
            }
            case ValueKind.Float:
                if (column.Single)
                {
                    *(float*)target = (row % SingleRange) * 0.25f;
                    return 4;
                }
                *(double*)target = row * 0.25;
                return 8;
            case ValueKind.Bit:
                *target = (byte)(row & 1);
                return 1;
            case ValueKind.Date:
            {
                var date = Date(row);
                *(short*)target = (short)date.Year;
                *(ushort*)(target + 2) = (ushort)date.Month;
                *(ushort*)(target + 4) = (ushort)date.Day;
                return 6;
            }
            case ValueKind.Time:
            {
                long seconds = row % 86_400;
                *(ushort*)target = (ushort)(seconds / 3600);
                *(ushort*)(target + 2) = (ushort)(seconds / 60 % 60);
                *(ushort*)(target + 4) = (ushort)(seconds % 60);
                return 6;
            }
            case ValueKind.Timestamp:
            {
                var t = Timestamp(row);
                *(short*)target = (short)t.Year;
                *(ushort*)(target + 2) = (ushort)t.Month;
                *(ushort*)(target + 4) = (ushort)t.Day;
                *(ushort*)(target + 6) = (ushort)t.Hour;
                *(ushort*)(target + 8) = (ushort)t.Minute;
                *(ushort*)(target + 10) = (ushort)t.Second;
                *(uint*)(target + 12) = 0; // whole seconds: valid for every fractional precision
                return 16;
            }
            case ValueKind.Guid:
            {
                GuidFields(row, out uint a, out ushort b, out ushort c, out ulong tail);
                *(uint*)target = a;
                *(ushort*)(target + 4) = b;
                *(ushort*)(target + 6) = c;
                *(ulong*)(target + 8) = tail;
                return 16;
            }
            case ValueKind.Binary:
                WriteBinary(row, target, column.Length);
                return column.Length;
            default:
                throw new InvalidOperationException($"column '{column.Column.Name}': {column.Kind} values are only sent as text");
        }
    }

    /// <summary>The text form of a value: what a text binding sends, and what the validation expects to read back.</summary>
    public static string Text(InsertColumn column, long row)
    {
        if (column.Kind == ValueKind.Binary)
        {
            var bytes = new byte[column.Length];
            fixed (byte* p = bytes) WriteBinary(row, p, bytes.Length);
            return Convert.ToHexString(bytes);
        }
        fixed (char* buffer = new char[column.TextChars + 1])
        {
            return new string(buffer, 0, WriteText(column, row, buffer));
        }
    }

    /// <summary>Writes the text form without a terminator and returns its length in characters (at most <see cref="InsertColumn.TextChars"/>).</summary>
    public static int WriteText(InsertColumn column, long row, char* target)
    {
        switch (column.Kind)
        {
            case ValueKind.Integer:
                return WriteUnsigned(target, (ulong)Integer(column, row));
            case ValueKind.Float:
            {
                long quarters = column.Single ? row % SingleRange : row;
                int n = WriteUnsigned(target, (ulong)(quarters / 4));
                target[n++] = '.';
                WriteDigits(target + n, (ulong)(quarters % 4 * 25), 2);
                return n + 2;
            }
            case ValueKind.Decimal:
            {
                ulong whole = column.IntegerDigits > 0 ? (ulong)row % Pow10[column.IntegerDigits] : 0;
                int n = WriteUnsigned(target, whole);
                if (column.Scale == 0) return n;
                target[n++] = '.';
                int varying = Math.Min(column.Scale, 2);
                WriteDigits(target + n, (ulong)row % Pow10[varying], varying);
                for (int i = varying; i < column.Scale; i++) target[n + i] = '0';
                return n + column.Scale;
            }
            case ValueKind.Bit:
            {
                if (!column.BoolWords)
                {
                    target[0] = (row & 1) == 0 ? '0' : '1'; // ODBC's text to SQL_BIT conversion defines only 0 and 1
                    return 1;
                }
                string word = (row & 1) == 0 ? "false" : "true";
                for (int i = 0; i < word.Length; i++) target[i] = word[i];
                return word.Length;
            }
            case ValueKind.Date:
                return WriteDate(target, Date(row));
            case ValueKind.Time:
            {
                long seconds = row % 86_400;
                return WriteTime(target, (int)(seconds / 3600), (int)(seconds / 60 % 60), (int)(seconds % 60));
            }
            case ValueKind.Timestamp:
            case ValueKind.TimestampOffset:
            {
                var t = Timestamp(row);
                int n = WriteDate(target, t);
                target[n++] = ' ';
                n += WriteTime(target + n, t.Hour, t.Minute, t.Second);
                if (column.Kind == ValueKind.Timestamp) return n;
                foreach (char c in " +00:00") target[n++] = c;
                return n;
            }
            case ValueKind.Guid:
            {
                GuidFields(row, out uint a, out ushort b, out ushort c, out ulong tail);
                WriteHex(target, a, 8);
                target[8] = '-';
                WriteHex(target + 9, b, 4);
                target[13] = '-';
                WriteHex(target + 14, c, 4);
                target[18] = '-';
                int n = 19;
                for (int i = 0; i < 8; i++)
                {
                    if (i == 2) target[n++] = '-';
                    WriteHex(target + n, (byte)(tail >> (8 * i)), 2);
                    n += 2;
                }
                return n;
            }
            case ValueKind.Binary:
                throw new InvalidOperationException($"column '{column.Column.Name}': binary values are never sent as text");
            default:
            {
                // Zero-padded row number first: text order is row order under any collation.
                int length = column.Length;
                int digits = Math.Min(length, KeyDigits);
                WriteDigits(target, (ulong)row % Pow10[digits], digits);
                if (length > digits) target[digits] = '-';
                for (int i = digits + 1; i < length; i++) target[i] = (char)('a' + (row + i) % 26);
                return length;
            }
        }
    }

    private static long Integer(InsertColumn column, long row) => column.Modulus > 0 ? row % column.Modulus : row;

    private static DateTime Date(long row) => Epoch.AddDays(row % DateRangeDays);

    private static DateTime Timestamp(long row) => Epoch.AddSeconds(row % TimestampRangeSeconds);

    /// <summary>
    /// A version 4 shaped GUID in the fields of SQLGUID: the row number in the first three, a mix of it in the last
    /// eight bytes (<paramref name="tail"/>, in memory order from its low byte).
    /// </summary>
    private static void GuidFields(long row, out uint a, out ushort b, out ushort c, out ulong tail)
    {
        ulong mix = unchecked((ulong)row * 0x9E3779B97F4A7C15UL);
        a = (uint)row;
        b = (ushort)(row >> 32);
        c = (ushort)(0x4000 | ((row >> 48) & 0x0FFF));
        tail = (mix & ~0xFFUL) | 0x80 | (mix & 0x3F);
    }

    /// <summary>The row number big-endian in the first eight bytes (its low-order bytes when shorter), then a filler.</summary>
    private static void WriteBinary(long row, byte* target, int length)
    {
        int key = Math.Min(length, 8);
        for (int i = 0; i < key; i++) target[i] = (byte)((ulong)row >> ((key - 1 - i) * 8));
        for (int i = key; i < length; i++) target[i] = (byte)(row + i * 7);
    }

    private static int WriteDate(char* target, DateTime date)
    {
        WriteDigits(target, (ulong)date.Year, 4);
        target[4] = '-';
        WriteDigits(target + 5, (ulong)date.Month, 2);
        target[7] = '-';
        WriteDigits(target + 8, (ulong)date.Day, 2);
        return 10;
    }

    private static int WriteTime(char* target, int hour, int minute, int second)
    {
        WriteDigits(target, (ulong)hour, 2);
        target[2] = ':';
        WriteDigits(target + 3, (ulong)minute, 2);
        target[5] = ':';
        WriteDigits(target + 6, (ulong)second, 2);
        return 8;
    }

    /// <summary>Exactly <paramref name="width"/> digits, zero-padded, the low-order digits when the value is longer.</summary>
    private static void WriteDigits(char* target, ulong value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            target[i] = (char)('0' + value % 10);
            value /= 10;
        }
    }

    private static int WriteUnsigned(char* target, ulong value)
    {
        int digits = 1;
        for (ulong v = value; v >= 10; v /= 10) digits++;
        WriteDigits(target, value, digits);
        return digits;
    }

    private static void WriteHex(char* target, ulong value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            int nibble = (int)(value & 0xF);
            target[i] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
            value >>= 4;
        }
    }
}
