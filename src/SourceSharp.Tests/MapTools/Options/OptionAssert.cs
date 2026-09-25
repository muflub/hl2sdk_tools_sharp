using System.Reflection;

using Xunit;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// One assertion these option tests need over and over: that parsing a flag
/// changed the one thing it names and left every other option at stock's
/// default.
/// </summary>
/// <remarks>
/// "Everything else is untouched" is the half that catches a real bug. A test
/// that only asserts the flag it set would pass just as happily if the parser
/// had also, say, turned on <c>NoWater</c>.
/// </remarks>
internal static class OptionAssert
{
    internal static void OnlyChanged<T>(T parsed, T stockDefault, string property, object? expected)
        where T : notnull
    {
        PropertyInfo[] properties =
            typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.Contains(properties, p => p.Name == property);

        foreach (PropertyInfo p in properties)
        {
            object? actual = p.GetValue(parsed);
            if (p.Name == property)
            {
                Assert.Equal(expected, actual);
            }
            else
            {
                Assert.Equal(p.GetValue(stockDefault), actual);
            }
        }
    }

    /// <summary>Every public instance property of <typeparamref name="T"/> whose type is bool.</summary>
    internal static TheoryData<string> BooleanProperties<T>()
    {
        TheoryData<string> data = [];
        foreach (PropertyInfo p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType == typeof(bool))
            {
                data.Add(p.Name);
            }
        }

        return data;
    }

    internal static bool BooleanValue<T>(T options, string property)
        where T : notnull =>
        (bool)typeof(T).GetProperty(property)!.GetValue(options)!;
}
