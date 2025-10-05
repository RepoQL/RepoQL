using System.Globalization;
using Fluid;
using Fluid.Values;

namespace RepoQL.Templating.Filters;

public static class StandardFilters
{
    public static void RegisterAll(TemplateOptions options)
    {
        var filters = options.Filters;
        filters.AddFilter("filesize", FileSize);
        filters.AddFilter("time_ago", TimeAgo);
        filters.AddFilter("pluralize", Pluralize);
    }

    // {{ bytes | filesize }} -> "1.23 MB"
    private static ValueTask<FluidValue> FileSize(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        var bytes = (long)input.ToNumberValue();
        var s = FormatBytes(bytes);
        return new ValueTask<FluidValue>(new StringValue(s));
    }

    // {{ date | time_ago }}
    private static ValueTask<FluidValue> TimeAgo(FluidValue input, FilterArguments args, TemplateContext ctx)
    {
        // Try to coerce to DateTimeOffset: handle DateTimeOffset, DateTime, numeric unix seconds, or ISO string
        var obj = input.ToObjectValue();
        DateTimeOffset dt = DateTimeOffset.UtcNow;

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
        int count = 0;
        var a0 = args.At(0);
        if (!a0.IsNil())
        {
            try { count = (int)Math.Round(a0.ToNumberValue()); }
            catch { count = 0; }
        }
        var s = count == 1 ? word : word + "s";
        return new ValueTask<FluidValue>(new StringValue(s));
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        const long TB = GB * 1024;
        if (bytes >= TB) return ($"{bytes / (double)TB:0.##} TB");
        if (bytes >= GB) return ($"{bytes / (double)GB:0.##} GB");
        if (bytes >= MB) return ($"{bytes / (double)MB:0.##} MB");
        if (bytes >= KB) return ($"{bytes / (double)KB:0.##} KB");
        return ($"{bytes} B");
    }
}
