using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace MegaHub.Core.Tests;

/// <summary>
/// The second factor by effect, not by name, read with Roslyn: every body of the shell that WRITES protection state (the lock mode, a code, the
/// parental switch and the profile rules, the Task Manager block, the shell entry, PC mode, the phones' pairing and levels, the plugin trust,
/// the service's codes and mode, automatic sign-in) must call a guard (<c>RequireSecondFactor…</c>) BEFORE its first such write, or carry
/// <c>[SecondFactorExempt("reason")]</c>. An exemption "the callers guard" (<c>CallersGuard = true</c>) is enforced the other way round: every
/// caller must be guarded before the call or exempt itself. Units are methods (explicit interface implementations, methods without modifiers,
/// local functions and lambdas inside them), constructors, operators, properties with their accessors, and field initializers (lambdas in
/// them), in nested classes too. A source that cannot be read is a failure, not a pass.
/// <para>
/// What counts as a guard: a call of <c>RequireSecondFactor…</c> that is a STATEMENT of the body, reached only through blocks, <c>if</c>/<c>else</c>,
/// <c>switch</c>, <c>lock</c> and <c>using</c> (never through a lambda, a local function, a loop or a <c>try</c>, where an exception could be swallowed
/// or the call may never run), and that stands before the first write. A reference to a "callers guard" method counts as a call whether it is
/// invoked or passed as a method group (<c>Task.Run(X)</c>).
/// </para>
/// <para>
/// KNOWN BLIND SPOTS (a net, not a proof, see SECURITY.md): the walk cannot see whether the condition of an <c>if</c> around the guard is the right
/// one, nor whether a caller further up lets <c>SecondFactorNeededException</c> pass, nor effects that are not on its list or that go through
/// reflection or another process. <see cref="The_limits_of_the_walk_are_the_documented_ones"/> pins what it does and does not see.
/// </para>
/// </summary>
public class SecondFactorEffectTests
{
    private sealed record Effect(string Name, Func<SyntaxNode, bool> Matches);

    private static bool Text(SyntaxNode n, string pattern) => Regex.IsMatch(n.ToString(), pattern);

    private static readonly Effect[] Effects =
    [
        new("lock mode, codes, parental switch, task manager flag, catalog sources, second factor flag, profile switch of rules",
            n => n is AssignmentExpressionSyntax a && Text(a.Left, @"(^|\.)(CouchLock|PasscodeHash|ParentalEnabled|BlockTaskManager|ExtraCatalogSources|SecondFactor|Restricted)$")),
        new("PC mode flag", n => n is AssignmentExpressionSyntax a && Text(a.Left, @"^(this\.)?_pcMode$")),
        new("profile rules", n => n is AssignmentExpressionSyntax a && Text(a.Left, @"(^|\.)Rules(\.|$)")),
        new("shell entry", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)(InstallAsShell|UninstallAsShell)$")),
        new("PC mode", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)EnterPcModeCoreAsync$")),
        new("pairing", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)Pairing\.(Revoke|RevokeAll|SetLevel|NewCode)$")),
        new("plugin trust", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)plugins\.(Approve|ApproveInstalled|AcceptRevocation|ConfirmExe)$")),
        new("plugin function levels", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)PluginFunctionRules\.Set$")),
        // Removing the owner's function levels lets a reinstall fall back to the declared defaults (a lifted lock): only behind PluginRights.
        new("plugin function levels removed", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)(ForgetFunctionLevels|PruneFunctionLevels|PluginFunctionRules\.Prune|PluginFunctionLevels\.(Remove|Clear))$")),
        new("service codes and mode", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)ServiceCodes\.(SetModeAsync|RemoveAsync|StoreAsync|TrySetModeWithCodeAsync)$")),
        new("automatic sign-in", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)AutoLogon\.(Enable|EnableAsync)$")),
        new("task manager policy", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"(^|\.)TaskManagerPolicy\.Apply$")),
        new("profiles", n => n is InvocationExpressionSyntax i && Text(i.Expression, @"^((c|cfg|config(\.Current)?)\.(SetProfileRestricted|RemoveProfile|SetProfileCode|AddProfile)|ApplyProfileRule)$")),
    ];

    private static readonly Regex GuardName = new(@"(^|\.)(RequireSecondFactor|RequireSecondFactorFor|RequireSecondFactorAll|RequireSecondFactorUnlessStays|OpenPairingWindow)$");

    private sealed record Unit(string File, string Name, SyntaxNode Node)
    {
        public IEnumerable<SyntaxNode> Inside => Node.DescendantNodes(n => n == Node || n is not (BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax));

        public IEnumerable<SyntaxList<AttributeListSyntax>> AttributeLists
        {
            get
            {
                yield return Node switch
                {
                    MemberDeclarationSyntax m => m.AttributeLists,
                    _ => default
                };
                if (Node is BasePropertyDeclarationSyntax p && p.AccessorList is { } list)
                    foreach (var accessor in list.Accessors) yield return accessor.AttributeLists;
            }
        }

        public AttributeSyntax? Exempt => AttributeLists.SelectMany(l => l).SelectMany(l => l.Attributes)
            .FirstOrDefault(a => a.Name.ToString() is "SecondFactorExempt" or "SecondFactorExemptAttribute");

        public bool CallersGuard => Exempt?.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.ToString() == "CallersGuard" && a.Expression.ToString() == "true") == true;
    }

    private static string ShellDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaHub.slnx"))) dir = dir.Parent;
        if (dir is null) Assert.Fail("MegaHub.slnx not found above the test run: the second factor's source walk cannot run (a missing source is no pass)");
        var shell = Path.Combine(dir!.FullName, "src", "MegaHub.Shell");
        Assert.True(Directory.Exists(shell), "src/MegaHub.Shell not found");
        return shell;
    }

    private static List<Unit> Units()
    {
        var units = new List<Unit>();
        foreach (var file in Directory.GetFiles(ShellDir(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest), file);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{file} does not parse: {errors.FirstOrDefault()}");
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var name = node switch
                {
                    MethodDeclarationSyntax m => (m.ExplicitInterfaceSpecifier is { } e ? e.Name + "." : "") + m.Identifier.Text,
                    ConstructorDeclarationSyntax c => c.Identifier.Text + ".ctor",
                    DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
                    OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
                    ConversionOperatorDeclarationSyntax => "conversion",
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    IndexerDeclarationSyntax => "this[]",
                    FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)),
                    EventFieldDeclarationSyntax ef => string.Join(",", ef.Declaration.Variables.Select(v => v.Identifier.Text)),
                    _ => null
                };
                if (name is null) continue;
                // A header whose body is gone (bad parse) is a failure; abstract, extern, partial and interface members have none by design.
                if (node is MethodDeclarationSyntax { Body: null, ExpressionBody: null } method
                    && !method.Modifiers.Any(m => m.Text is "abstract" or "extern" or "partial") && method.Parent is not InterfaceDeclarationSyntax)
                    Assert.Fail($"{Path.GetFileName(file)}: method {name} has a header but no body the walk can read");
                units.Add(new Unit(Path.GetFileName(file), name, node));
            }
        }
        return units;
    }

    private static IEnumerable<SyntaxNode> FirstEffects(Unit unit) =>
        unit.Inside.Where(n => Effects.Any(e => e.Matches(n))).OrderBy(n => n.SpanStart);

    /// <summary>The guard calls that are statements of the body, reached through blocks, if/else, switch, lock and using only.</summary>
    private static IEnumerable<InvocationExpressionSyntax> TopLevelGuards(SyntaxNode? node)
    {
        switch (node)
        {
            case null:
                yield break;
            case BlockSyntax block:
                foreach (var statement in block.Statements)
                    foreach (var guard in TopLevelGuards(statement)) yield return guard;
                break;
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call } when GuardName.IsMatch(call.Expression.ToString()):
                yield return call;
                break;
            case IfStatementSyntax branch:
                foreach (var guard in TopLevelGuards(branch.Statement)) yield return guard;
                foreach (var guard in TopLevelGuards(branch.Else?.Statement)) yield return guard;
                break;
            case SwitchStatementSyntax choice:
                foreach (var section in choice.Sections)
                    foreach (var statement in section.Statements)
                        foreach (var guard in TopLevelGuards(statement)) yield return guard;
                break;
            case LockStatementSyntax locked:
                foreach (var guard in TopLevelGuards(locked.Statement)) yield return guard;
                break;
            case UsingStatementSyntax used:
                foreach (var guard in TopLevelGuards(used.Statement)) yield return guard;
                break;
        }
    }

    private static IEnumerable<SyntaxNode?> Bodies(Unit unit)
    {
        switch (unit.Node)
        {
            case BaseMethodDeclarationSyntax m:
                yield return m.Body;
                break;
            case BasePropertyDeclarationSyntax p when p.AccessorList is { } list:
                foreach (var accessor in list.Accessors) yield return accessor.Body;
                break;
        }
    }

    private static bool GuardBefore(Unit unit, int position) =>
        Bodies(unit).SelectMany(TopLevelGuards).Any(g => g.SpanStart < position);

    [Fact]
    public void Every_body_that_writes_protection_state_has_a_guard_before_the_first_write_or_a_reason()
    {
        var units = Units();
        var missing = new List<string>();
        var withEffects = 0;
        foreach (var unit in units)
        {
            var effects = FirstEffects(unit).ToList();
            if (effects.Count == 0) continue;
            withEffects++;
            if (unit.Exempt is not null) continue;
            var first = effects[0];
            if (!GuardBefore(unit, first.SpanStart))
                missing.Add($"{unit.File}: {unit.Name} writes ({first.ToString().ReplaceLineEndings(" ")[..Math.Min(70, first.ToString().Length)]}) at line {first.GetLocation().GetLineSpan().StartLinePosition.Line + 1} without a RequireSecondFactor before it");
        }
        Assert.True(units.Count > 800, $"the walk found only {units.Count} units; it is broken");
        Assert.True(withEffects >= 30, $"the walk found only {withEffects} bodies with effects; it is broken");
        Assert.True(missing.Count == 0, "bodies that write protection state without a guard before the write and without [SecondFactorExempt(\"reason\")]:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_exemption_carries_a_real_reason_read_from_the_attribute()
    {
        var withReason = 0;
        foreach (var unit in Units())
        {
            if (unit.Exempt is not { } exempt) continue;
            var arg = exempt.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null)?.Expression as LiteralExpressionSyntax;
            Assert.True(arg is not null && arg.IsKind(SyntaxKind.StringLiteralExpression), $"{unit.File}: {unit.Name} has an exemption without a text literal");
            Assert.True(arg!.Token.ValueText.Trim().Length >= 12, $"{unit.File}: {unit.Name} has an exemption without a real reason");
            withReason++;
        }
        Assert.True(withReason >= 5, "the walk found almost no exemptions; it is broken");
    }

    [Fact]
    public void Where_the_callers_guard_every_caller_is_guarded_before_the_call_or_exempt_itself()
    {
        var units = Units();
        var needing = units.Where(u => u.CallersGuard).ToList();
        Assert.True(needing.Count >= 3, "the walk found almost no CallersGuard exemptions; it is broken");
        var problems = new List<string>();
        foreach (var target in needing)
        {
            var simple = target.Name[(target.Name.LastIndexOf('.') + 1)..];
            var calls = 0;
            foreach (var caller in units)
            {
                if (ReferenceEquals(caller, target)) continue;
                // Every mention of the name counts: a call, a member call, a method group passed on (Task.Run(X)).
                foreach (var mention in caller.Inside.OfType<IdentifierNameSyntax>().Where(id => id.Identifier.Text == simple))
                {
                    calls++;
                    if (caller.Exempt is not null || GuardBefore(caller, mention.SpanStart)) continue;
                    problems.Add($"{caller.File}: {caller.Name} uses {simple} (whose exemption says the callers guard) without a guard before it");
                }
            }
            Assert.True(calls > 0, $"{target.Name} says its callers guard but nobody uses it");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static bool UnguardedWrite(string body, string callerBody = "")
    {
        // A made-up class with one method; true when the walk would report it (a write with no guard before it).
        var tree = CSharpSyntaxTree.ParseText("class C { void M(bool b) { " + body + " } }" + callerBody);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "M");
        var unit = new Unit("x.cs", "M", method);
        var first = FirstEffects(unit).FirstOrDefault();
        return first is not null && unit.Exempt is null && !GuardBefore(unit, first.SpanStart);
    }

    [Fact]
    public void The_limits_of_the_walk_are_the_documented_ones()
    {
        const string write = "shell.InstallAsShell();";
        // Seen and accepted: a top-level guard, and a guard in an if or switch branch before the write.
        Assert.False(UnguardedWrite("RequireSecondFactor(a, true); " + write));
        Assert.False(UnguardedWrite("if (b) RequireSecondFactor(a, true); " + write));
        Assert.False(UnguardedWrite("switch (1) { case 1: RequireSecondFactor(a, true); break; } " + write));
        // Seen and refused: no guard, a guard after the write, a guard inside a lambda, a local function, a loop or a try that could swallow it.
        Assert.True(UnguardedWrite(write));
        Assert.True(UnguardedWrite(write + " RequireSecondFactor(a, true);"));
        Assert.True(UnguardedWrite("System.Action f = () => RequireSecondFactor(a, true); f(); " + write));
        Assert.True(UnguardedWrite("void Local() { RequireSecondFactor(a, true); } Local(); " + write));
        Assert.True(UnguardedWrite("while (b) { RequireSecondFactor(a, true); } " + write));
        Assert.True(UnguardedWrite("try { RequireSecondFactor(a, true); } catch (System.Exception) { } " + write));
        Assert.True(UnguardedWrite("try { RequireSecondFactor(a, true); } catch (SecondFactorNeededException) { } " + write));
        // A write inside a lambda is a write of the method: the guard before the lambda covers it, the one inside the lambda after it does not.
        Assert.False(UnguardedWrite("RequireSecondFactor(a, true); System.Action f = () => shell.InstallAsShell(); f();"));
        Assert.True(UnguardedWrite("System.Action f = () => { shell.InstallAsShell(); RequireSecondFactor(a, true); }; f();"));
        // KNOWN BLIND SPOTS, pinned so they are not forgotten: the walk accepts a guard whose if condition is wrong, and it does not look at the
        // callers' handling of the exception; effects under another name are not seen at all.
        Assert.False(UnguardedWrite("if (false) RequireSecondFactor(a, true); " + write));
        Assert.False(UnguardedWrite("this.Rename(); "), "an effect that is not on the list is not noticed");
    }

    [Fact]
    public void A_method_group_reference_to_a_callers_guard_method_counts_as_a_call()
    {
        const string source = """
            class C
            {
                [SecondFactorExempt("callers guard it", CallersGuard = true)] void Target() { shell.InstallAsShell(); }
                void Guarded() { RequireSecondFactor(a, true); Task.Run(Target); }
                void Unguarded() { Task.Run(Target); }
                void UnguardedCall() { Target(); }
                void UnguardedMember() { this.Target(); }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var units = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => new Unit("x.cs", m.Identifier.Text, m)).ToList();
        var bad = new List<string>();
        foreach (var caller in units.Where(u => u.Name != "Target"))
            foreach (var mention in caller.Inside.OfType<IdentifierNameSyntax>().Where(id => id.Identifier.Text == "Target"))
                if (!GuardBefore(caller, mention.SpanStart)) bad.Add(caller.Name);
        Assert.Equal(["Unguarded", "UnguardedCall", "UnguardedMember"], bad);
    }

    [Fact]
    public void The_walk_reads_the_shapes_it_has_to_read()
    {
        // A self-check on the walk itself, with a made-up source: explicit interface implementations, no modifiers, accessors, local
        // functions, lambdas in field initializers, nested classes, tuples and attributes with brackets in the reason.
        const string source = """
            interface I { bool Go(); }
            class Outer
            {
                class Inner : I
                {
                    bool I.Go() { Pairing.Revoke("x"); return true; }
                    void NoModifiers() { void Local() { shell.InstallAsShell(); } Local(); }
                    int Prop { get { cfg.CouchLock = "off"; return 1; } }
                    System.Action Field = () => cfg.PasscodeHash = "";
                    [SecondFactorExempt("a reason with [brackets] and (parens) inside")]
                    (int, int) Tuple() { cfg.ParentalEnabled = false; return (1, 2); }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var found = new List<string>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var name = node switch
            {
                MethodDeclarationSyntax m => (m.ExplicitInterfaceSpecifier is { } e ? e.Name + "." : "") + m.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)),
                _ => null
            };
            if (name is null || node is MethodDeclarationSyntax { Parent: InterfaceDeclarationSyntax }) continue;
            var unit = new Unit("x.cs", name, node);
            if (FirstEffects(unit).Any()) found.Add(name + (unit.Exempt is null ? "" : "!"));
        }
        Assert.Equal(["I.Go", "NoModifiers", "Prop", "Field", "Tuple!"], found);
        var tuple = new Unit("x.cs", "Tuple", tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Tuple"));
        Assert.Equal("a reason with [brackets] and (parens) inside", ((LiteralExpressionSyntax)tuple.Exempt!.ArgumentList!.Arguments[0].Expression).Token.ValueText);
    }
}
