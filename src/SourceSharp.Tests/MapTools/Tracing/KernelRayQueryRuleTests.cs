//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text.RegularExpressions;

using SourceSharp.MapTools.Gpu.Interop;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The GPU kernel calls <c>rayQueryTerminateEXT</c> only where it is
/// defined: while the last <c>rayQueryProceedEXT</c> on that query returned
/// true.
/// </summary>
/// <remarks>
/// <para>
/// The ray-query extension defines terminate only as a way to stop a
/// traversal that still has candidates to offer; after a
/// <c>while (rayQueryProceedEXT(q)) {}</c> loop that ran to its end, the
/// last proceed returned false and a terminate is undefined behaviour. No
/// device fact catches that reliably, because a driver that happens to
/// ignore the call answers correctly, so this reads the GLSL source instead.
/// </para>
/// <para>
/// The rule the scanner applies is deliberately narrower than the
/// extension's: a terminate is accepted only inside the body of a
/// <c>while (rayQueryProceedEXT(q))</c> loop over the same query and
/// immediately followed by <c>break;</c>. That is the one shape in which
/// proceed's last answer is known to be true from the text alone, and the
/// only one the kernel needs. Anything else is reported with its line.
/// </para>
/// </remarks>
public sealed class KernelRayQueryRuleTests
{
    private static readonly Regex Terminate = new(@"rayQueryTerminateEXT\s*\(\s*(\w+)\s*\)\s*;", RegexOptions.CultureInvariant);

    private static readonly Regex ProceedLoop = new(@"while\s*\(\s*rayQueryProceedEXT\s*\(\s*(\w+)\s*\)\s*\)\s*\{", RegexOptions.CultureInvariant);

    private static readonly Regex BreakNext = new(@"\G\s*break\s*;", RegexOptions.CultureInvariant);

    /// <summary>The shipped kernel has no terminate outside a proceed loop's break.</summary>
    [Fact]
    public void TheKernelTerminatesOnlyWhileProceedLastReturnedTrue()
    {
        Assert.Empty(MisplacedTerminates(Kernels.RayGlsl));
    }

    /// <summary>
    /// Not vacuous: the kernel still has the proceed loops the rule is about
    /// (the closest/any-hit loop and the telemetry loop), and the
    /// telemetry mode's terminate on its break is still there to be accepted.
    /// </summary>
    [Fact]
    public void TheKernelHasTheLoopsAndTheTerminateTheRuleReads()
    {
        string code = StripLineComments(Kernels.RayGlsl);
        Assert.Equal(2, ProceedLoop.Matches(code).Count);
        Assert.Single(Terminate.Matches(code));
    }

    /// <summary>A terminate after a loop that ran to its end is reported, with its line.</summary>
    [Fact]
    public void ATerminateAfterAFinishedLoopIsReported()
    {
        const string Glsl = """
            void f() {
                rayQueryEXT q;
                while (rayQueryProceedEXT(q)) { }
                bool hit = rayQueryGetIntersectionTypeEXT(q, true) != 0u;
                rayQueryTerminateEXT(q);
            }
            """;

        string only = Assert.Single(MisplacedTerminates(Glsl));
        Assert.StartsWith("line 5:", only);
    }

    /// <summary>A terminate inside the loop immediately before its break is accepted.</summary>
    [Fact]
    public void ATerminateOnTheLoopsBreakIsAccepted()
    {
        const string Glsl = """
            void f() {
                rayQueryEXT q;
                while (rayQueryProceedEXT(q)) {
                    if (cond) { n++; rayQueryTerminateEXT(q); break; }
                }
            }
            """;

        Assert.Empty(MisplacedTerminates(Glsl));
    }

    /// <summary>
    /// A terminate inside the loop that is not followed by <c>break</c> is
    /// reported: the loop calls proceed again, and the scanner cannot tell
    /// from the text what the last answer was.
    /// </summary>
    [Fact]
    public void ATerminateInsideTheLoopWithoutABreakIsReported()
    {
        const string Glsl = """
            void f() {
                while (rayQueryProceedEXT(q)) {
                    rayQueryTerminateEXT(q);
                    n++;
                }
            }
            """;

        Assert.Single(MisplacedTerminates(Glsl));
    }

    /// <summary>A terminate of one query inside another query's loop is reported.</summary>
    [Fact]
    public void ATerminateOfAnotherQueryIsReported()
    {
        const string Glsl = """
            void f() {
                while (rayQueryProceedEXT(a)) {
                    rayQueryTerminateEXT(b);
                    break;
                }
            }
            """;

        Assert.Single(MisplacedTerminates(Glsl));
    }

    /// <summary>A terminate with no proceed loop at all is reported.</summary>
    [Fact]
    public void ATerminateWithNoLoopIsReported()
    {
        Assert.Single(MisplacedTerminates("void f() { rayQueryTerminateEXT(q); }"));
    }

    /// <summary>A terminate in a comment is not code and is not reported.</summary>
    [Fact]
    public void ATerminateInACommentIsIgnored()
    {
        const string Glsl = """
            void f() {
                while (rayQueryProceedEXT(q)) { }
                // rayQueryTerminateEXT(q);
            }
            """;

        Assert.Empty(MisplacedTerminates(Glsl));
    }

    /// <summary>
    /// Every <c>rayQueryTerminateEXT</c> in <paramref name="glsl"/> that is
    /// not on a proceed loop's break, as <c>line N: text</c>.
    /// </summary>
    /// <param name="glsl">GLSL source.</param>
    /// <returns>One entry per misplaced terminate.</returns>
    private static List<string> MisplacedTerminates(string glsl)
    {
        // Comments are blanked, not removed, so offsets and line numbers
        // still point at the original text.
        string code = StripLineComments(glsl);
        List<(string Query, int Start, int End)> bodies = [];
        foreach (Match loop in ProceedLoop.Matches(code))
        {
            int open = loop.Index + loop.Length - 1;
            bodies.Add((loop.Groups[1].Value, open, MatchingBrace(code, open)));
        }

        List<string> misplaced = [];
        foreach (Match call in Terminate.Matches(code))
        {
            string query = call.Groups[1].Value;
            bool inOwnLoop = bodies.Any(b => b.Query == query && call.Index > b.Start && call.Index < b.End);
            bool thenBreak = BreakNext.IsMatch(code, call.Index + call.Length);
            if (!(inOwnLoop && thenBreak))
            {
                int line = 1 + code.AsSpan(0, call.Index).Count('\n');
                misplaced.Add($"line {line}: {call.Value}");
            }
        }

        return misplaced;
    }

    private static string StripLineComments(string glsl) =>
        Regex.Replace(glsl, @"//[^\n]*", m => new string(' ', m.Length), RegexOptions.CultureInvariant);

    private static int MatchingBrace(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"unbalanced brace at offset {open}");
    }
}
