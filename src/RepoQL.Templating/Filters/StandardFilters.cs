using System.Globalization;
using Fluid;
using Fluid.Values;
using Humanizer;
using Humanizer.Bytes;

namespace RepoQL.Templating.Filters;

public static class StandardFilters
{
    public static void RegisterAll(TemplateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var filters = options.Filters;
        filters.AddFilter("filesize", FileSize);
        filters.AddFilter("time_ago", TimeAgo);
        filters.AddFilter("pluralize", Pluralize);
        filters.AddFilter("quantity", Quantity);
        filters.AddFilter("abbr", Abbreviate);
        filters.AddFilter("normalize_newlines", NormalizeNewlines);
        filters.AddFilter("is_multiline", IsMultiline);
        filters.AddFilter("non_empty_lines", NonEmptyLines);
        filters.AddFilter("single_line", SingleLine);
        filters.AddFilter("tokens", Tokens);
    }

    // {{ bytes | filesize }} -> "1.23 MB"
    private static ValueTask<FluidValue> FileSize(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var number = input.ToNumberValue();
        var bytes = Convert.ToDouble(number, CultureInfo.InvariantCulture);
        var byteSize = ByteSize.FromBytes(Math.Max(0, bytes));

        var formatArg = args.At(0);
        var format = formatArg.IsNil() ? null : formatArg.ToStringValue();
        if (string.IsNullOrWhiteSpace(format))
            format = null;

        var culture = ctx.Options.CultureInfo ?? CultureInfo.InvariantCulture;
        var rendered = format is null
            ? byteSize.ToString(culture)
            : byteSize.ToString(format, culture);

        return new ValueTask<FluidValue>(new StringValue(rendered));
    }

    // {{ date | time_ago }}
    private static ValueTask<FluidValue> TimeAgo(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        // Try to coerce to DateTimeOffset: handle DateTimeOffset, DateTime, numeric unix seconds, or ISO string
        var obj = input.ToObjectValue();
        var dt = DateTimeOffset.UtcNow;

        switch (obj)
        {
            case DateTimeOffset dto:
                dt = dto;
                break;
            case DateTime dtLocal:
                dt = dtLocal.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(DateTime.SpecifyKind(dtLocal, DateTimeKind.Utc))
                    : new DateTimeOffset(dtLocal.ToUniversalTime());
                break;
            case IConvertible conv:
                try
                {
                    var seconds = conv.ToDouble(ctx.Options.CultureInfo);
                    dt = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                }
                catch { /* ignore */ }
                break;
            default:
                var s = input.ToStringValue();
                if (DateTimeOffset.TryParse(s, ctx.Options.CultureInfo, DateTimeStyles.AssumeUniversal, out var parsed))
                    dt = parsed;
                break;
        }

        var now = DateTimeOffset.UtcNow;
        var span = now - dt;
        var result = span.TotalDays switch
        {
            >= 365 => $"{(int)(span.TotalDays / 365)} year(s) ago",
            >= 30 => $"{(int)(span.TotalDays / 30)} month(s) ago",
            >= 7 => $"{(int)(span.TotalDays / 7)} week(s) ago",
            >= 1 => $"{(int)span.TotalDays} day(s) ago",
            _ => span.TotalHours >= 1 ? $"{(int)span.TotalHours} hour(s) ago" : $"{(int)Math.Max(0, span.TotalMinutes)} minute(s) ago"
        };
        return new ValueTask<FluidValue>(new StringValue(result));
    }

    // {{ "file" | pluralize: count }} -> files (if count != 1)
    private static ValueTask<FluidValue> Pluralize(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var word = input.ToStringValue();
        var count = 0;
        var a0 = args.At(0);
        if (!a0.IsNil())
        {
            try { count = (int)Math.Round(a0.ToNumberValue()); }
            catch { count = 0; }
        }
        var s = count == 1 ? word : word + "s";
        return new ValueTask<FluidValue>(new StringValue(s));
    }

    // {{ count | quantity: "file" }} -> "1 file" / "2 files"
    private static ValueTask<FluidValue> Quantity(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var noun = args.At(0).ToStringValue();
        if (string.IsNullOrWhiteSpace(noun))
            return new ValueTask<FluidValue>(StringValue.Empty);

        var formatArg = args.At(1).ToStringValue();
        var format = ShowQuantityAs.Numeric;
        if (!string.IsNullOrWhiteSpace(formatArg) &&
            Enum.TryParse<ShowQuantityAs>(formatArg, ignoreCase: true, out var parsed))
        {
            format = parsed;
        }

        var countValue = input.ToNumberValue();
        var count = (long)Math.Round((double)countValue, MidpointRounding.AwayFromZero);
        var provider = ctx.Options.CultureInfo ?? CultureInfo.InvariantCulture;
        var display = FormatQuantity(count, noun, format, provider);
        return new ValueTask<FluidValue>(new StringValue(display));
    }

    // {{ 16000 | abbr }} -> "16k" ; optional decimals: {{ 1234000 | abbr: 1 }} -> "1.2M"
    private static ValueTask<FluidValue> Abbreviate(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        // Fluid's ToNumberValue returns a decimal; normalize to double for formatting logic
        var n = Convert.ToDouble(input.ToNumberValue(), CultureInfo.InvariantCulture);
        var decimalsArg = args.At(0);
        var decimals = 0;
        if (!decimalsArg.IsNil())
        {
            try
            {
                var dv = Convert.ToDouble(decimalsArg.ToNumberValue(), CultureInfo.InvariantCulture);
                decimals = (int)Math.Max(0, Math.Round(dv));
            }
            catch { decimals = 0; }
        }
        var s = Abbr(n, decimals);
        return new ValueTask<FluidValue>(new StringValue(s));
    }

    private static string Abbr(double value, int decimals)
    {
        var abs = Math.Abs(value);
        string suffix;
        var scaled = value;
        if (abs >= 1_000_000_000_000d)
        {
            suffix = "T";
            scaled = value / 1_000_000_000_000d;
        }
        else if (abs >= 1_000_000_000d)
        {
            suffix = "B";
        }
        else if (abs >= 1_000_000d)
        {
            suffix = "M";
            scaled = value / 1_000_000d;
        }
        else if (abs >= 1_000d)
        {
            suffix = "k";
            scaled = value / 1_000d;
        }
        else
        {
            suffix = "";
            scaled = value;
        }

        var fmt = decimals <= 0 ? "0" : $"0.{new string('#', decimals)}";
        return scaled.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }

    private static string FormatQuantity(long count, string noun, ShowQuantityAs format, CultureInfo provider)
    {
        var numberText = format == ShowQuantityAs.Words
            ? count.ToWords(provider)
            : count.ToString(provider);

        var word = count == 1 ? noun : noun.Pluralize();

        return $"{numberText} {word}";
    }

    // normalize_newlines: CRLF/CR -> LF (tabs preserved)
    private static ValueTask<FluidValue> NormalizeNewlines(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var s = input.ToStringValue() ?? string.Empty;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        return new ValueTask<FluidValue>(new StringValue(s));
    }

    // is_multiline: true if contains a newline after normalization
    private static ValueTask<FluidValue> IsMultiline(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var s = input.ToStringValue() ?? string.Empty;
        var has = s.Contains('\n');
        return new ValueTask<FluidValue>(has ? BooleanValue.True : BooleanValue.False);
    }

    // non_empty_lines: normalize newlines and split, dropping empty/whitespace-only lines
    private static ValueTask<FluidValue> NonEmptyLines(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var s = input.ToStringValue() ?? string.Empty;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = s.Split('\n');
        var list = new List<FluidValue>(lines.Length);
        foreach (var ln in lines)
        {
            if (!string.IsNullOrWhiteSpace(ln)) list.Add(new StringValue(ln));
        }
        return new ValueTask<FluidValue>(new ArrayValue(list));
    }

    // single_line: normalize then replace newlines/tabs with spaces
    private static ValueTask<FluidValue> SingleLine(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var s = input.ToStringValue() ?? string.Empty;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = s.Replace('\n', ' ').Replace('\t', ' ');
        return new ValueTask<FluidValue>(new StringValue(s));
    }

    // {{ 1500 | tokens }} -> "~1.5k tok"
    // {{ 150 | tokens }} -> "~150 tok"
    // {{ 0 | tokens }} -> "" (empty)
    private static ValueTask<FluidValue> Tokens(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var n = Convert.ToDouble(input.ToNumberValue(), CultureInfo.InvariantCulture);
        if (n <= 0)
            return new ValueTask<FluidValue>(StringValue.Empty);

        // Use 1 decimal for values >= 1000
        var formatted = n >= 1000 ? Abbr(n, 1) : ((int)n).ToString(CultureInfo.InvariantCulture);
        return new ValueTask<FluidValue>(new StringValue($"~{formatted} tok"));
    }
}

