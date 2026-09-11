using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FxDbg.Core.Errors;

namespace FxDbg.Core.Evaluation;

/// <summary>A closed, bounded grammar. No compiler, reflection, delegates from the target or CLR Eval.</summary>
public sealed class RestrictedExpression
{
    private readonly Node root;
    private RestrictedExpression(Node root) => this.root = root;
    public static RestrictedExpression Parse(string expression, EvaluationBudget budget)
    {
        if (expression is null || expression.Length == 0) throw Syntax();
        if (expression.Length > 4096) throw EvaluationBudget.Limit();
        return new RestrictedExpression(new Parser(expression, budget).Parse());
    }
    public ExpressionValue Evaluate(Func<string, EvaluationBudget, ExpressionValue> resolve, EvaluationBudget budget)
    {
        try { ExpressionValue value = Run(root, resolve, budget); budget.Check(); return value; }
        catch (OperationCanceledException) { budget.Check(); throw; }
        catch (ArithmeticException) { throw new FxDbgException(FxDbgErrorCode.ExpressionArithmeticError, "Expression arithmetic failed (overflow or division by zero)."); }
    }
    private static ExpressionValue Run(Node node, Func<string, EvaluationBudget, ExpressionValue> resolve, EvaluationBudget budget)
    {
        budget.Step();
        ExpressionValue Child(int index) => Run(node.Children[index], resolve, budget);
        switch (node.Kind)
        {
            case "literal":
                if (node.Literal is string text) budget.String(text.Length);
                return new ExpressionValue(node.Literal);
            case "name": return resolve(node.Text, budget);
            case "conditional": return Child(0).Boolean() ? Child(1) : Child(2);
            case "cast": return new ExpressionValue(ExpressionNumbers.Cast(node.Text, Scalar(Child(0))));
            case "field":
                ExpressionValue receiver = Child(0);
                if (node.Text == "Length")
                {
                    if (receiver.Scalar is string str) return new ExpressionValue((object)str.Length);
                    if (receiver.Object is not null && receiver.Object.Variable.TypeName.EndsWith("]", StringComparison.Ordinal))
                        return new ExpressionValue((object)receiver.Object.Length(-1, budget));
                }
                return receiver.Object?.Field(node.Text, budget) ?? throw EvaluationBudget.TypeError();
            case "index":
                ExpressionValue collection = Child(0);
                int[] indices = node.Children.Skip(1).Select(child => Run(child, resolve, budget).Integer()).ToArray();
                if (collection.Scalar is string characters)
                {
                    if (indices.Length != 1 || indices[0] < 0 || indices[0] >= characters.Length) throw IndexError();
                    return new ExpressionValue((object)characters[indices[0]]);
                }
                return collection.Object?.Index(indices, budget) ?? throw EvaluationBudget.TypeError();
            case "unary":
                ExpressionValue operand = Child(0);
                return new ExpressionValue(node.Text == "!" ? (object)!operand.Boolean() : ExpressionNumbers.Unary(node.Text, Scalar(operand)));
            case "binary":
                ExpressionValue left = Child(0);
                if (node.Text == "&&") return new ExpressionValue((object)(left.Boolean() && Child(1).Boolean()));
                if (node.Text == "||") return new ExpressionValue((object)(left.Boolean() || Child(1).Boolean()));
                ExpressionValue right = Child(1);
                if (node.Text is "==" or "!=")
                {
                    bool? equal = null;
                    if (left.Object is not null || right.Object is not null)
                    {
                        if (left.Scalar is not null || right.Scalar is not null) throw EvaluationBudget.TypeError();
                        equal = left.Object is not null && right.Object is not null && left.Object.Variable.ReferenceIdentity is { } identity && identity == right.Object.Variable.ReferenceIdentity;
                    }
                    else if (left.Scalar is null || right.Scalar is null) equal = left.Scalar is null && right.Scalar is null;
                    else if (left.Scalar is string a && right.Scalar is string b) equal = string.Equals(a, b, StringComparison.Ordinal);
                    else if (left.Scalar is bool c && right.Scalar is bool d) equal = c == d;
                    if (equal.HasValue) return new ExpressionValue((object)(node.Text == "==" ? equal.Value : !equal.Value));
                }
                if (node.Text == "+" && left.Scalar is string && right.Scalar is string)
                    return Concat(left, right, budget);
                return new ExpressionValue(ExpressionNumbers.Binary(node.Text, Scalar(left), Scalar(right)));
            case "call":
                ExpressionValue[] values = node.Children.Select(child => Run(child, resolve, budget)).ToArray();
                if (ExpressionIntrinsics.Supports(node.Text)) return ExpressionIntrinsics.Call(node.Text, values, budget);
                switch (node.Text)
                {
                    case "Math.Abs": RequireCount(values, 1); return new ExpressionValue(ExpressionNumbers.Abs(Scalar(values[0])));
                    case "Math.Min": case "Math.Max": RequireCount(values, 2); return new ExpressionValue(ExpressionNumbers.MinMax(node.Text == "Math.Min", Scalar(values[0]), Scalar(values[1])));
                    case "String.IsNullOrEmpty": RequireCount(values, 1); return new ExpressionValue((object)string.IsNullOrEmpty(String(values[0])));
                    case "String.Equals": RequireCount(values, 2); return new ExpressionValue((object)string.Equals(String(values[0]), String(values[1]), StringComparison.Ordinal));
                    case "String.Concat": RequireCount(values, 2); return Concat(values[0], values[1], budget);
                    case "Array.GetLength":
                        RequireCount(values, 2); int dimension = values[1].Integer(); if (dimension < 0) throw IndexError();
                        return new ExpressionValue((object)(values[0].Object?.Length(dimension, budget) ?? throw EvaluationBudget.TypeError()));
                }
                throw Forbidden();
            default: throw Syntax();
        }
    }
    private static object? Scalar(ExpressionValue value) => value.Object is null ? value.Scalar : throw EvaluationBudget.TypeError();
    private static string? String(ExpressionValue value) => Scalar(value) is null or string ? (string?)value.Scalar : throw EvaluationBudget.TypeError();
    private static ExpressionValue Concat(ExpressionValue a, ExpressionValue b, EvaluationBudget budget)
    {
        string? x = String(a), y = String(b); budget.String((x?.Length ?? 0) + (y?.Length ?? 0));
        return new ExpressionValue((object)string.Concat(x,y));
    }
    private static void RequireCount(ExpressionValue[] values, int count) { if (values.Length != count) throw EvaluationBudget.TypeError(); }
    public static FxDbgException IndexError() => new(FxDbgErrorCode.ExpressionIndexOutOfRange, "Expression index or dimension is out of range.");
    private static FxDbgException Syntax() => new(FxDbgErrorCode.ExpressionSyntaxError, "Expression syntax is invalid.");
    private static FxDbgException Forbidden() => new(FxDbgErrorCode.ExpressionForbidden, "Expression operation is not allowed.");
    private sealed class Node
    {
        internal Node(string kind, string text, object? literal, params Node[] children)
        { Kind = kind; Text = text; Literal = literal; Children = children; Depth = 1 + (children.Length == 0 ? 0 : children.Max(x => x.Depth)); if (Depth > 32) throw EvaluationBudget.Limit(); }
        internal string Kind { get; }
        internal string Text { get; }
        internal object? Literal { get; }
        internal Node[] Children { get; }
        internal int Depth { get; }
    }
    private sealed class Parser
    {
        private readonly string source;
        private readonly EvaluationBudget budget;
        private int position, nodes;
        private string token = "";
        private object? literal;
        private string category = "";
        internal Parser(string source, EvaluationBudget budget) { this.source = source; this.budget = budget; Next(); }
        internal Node Parse() { Node result = Expression(0, 0); if (token != "") throw Syntax(); return result; }
        private Node Make(string kind, string text, object? value = null, params Node[] children)
        { if (++nodes > 1024) throw EvaluationBudget.Limit(); return new Node(kind, text, value, children); }
        private Node Expression(int precedence, int depth)
        {
            budget.Step(); if (depth >= 32) throw EvaluationBudget.Limit();
            Node left;
            if (token is "!" or "+" or "-" or "~") { string op = token; Next(); left = Make("unary", op, null, Expression(11, depth + 1)); }
            else if (token == "(")
            {
                Next();
                if (category == "name" && ExpressionNumbers.IsCastType(token))
                {
                    string type = token; Next(); Eat(")"); left = Make("cast", type, null, Expression(11, depth + 1));
                }
                else { left = Expression(0, depth + 1); Eat(")"); }
            }
            else if (category == "literal") { left = Make("literal", "", literal); Next(); }
            else if (category == "name") { left = Make("name", token); Next(); }
            else throw Syntax();
            while (true)
            {
                budget.Step();
                if (token == ".")
                {
                    Next(); if (category != "name") throw Syntax(); string name = token; Next(); left = Make("field", name, null, left); continue;
                }
                if (token == "[")
                {
                    Next(); var items = new List<Node> { left }; items.Add(Expression(0, depth + 1));
                    while (token == ",") { Next(); if (items.Count >= 33) throw EvaluationBudget.Limit(); items.Add(Expression(0, depth + 1)); }
                    Eat("]"); left = Make("index", "", null, items.ToArray()); continue;
                }
                if (token == "(")
                {
                    string? path = Path(left);
                    if (path is not ("Math.Abs" or "Math.Min" or "Math.Max" or "String.IsNullOrEmpty" or "String.Equals" or "String.Concat" or "Array.GetLength") && !ExpressionIntrinsics.Supports(path)) throw Forbidden();
                    Next(); var arguments = new List<Node>();
                    int argumentLimit = path is "String.Substring" or "String.IndexOf" or "Math.Clamp" ? 3 : 2;
                    if (token != ")") { arguments.Add(Expression(0, depth + 1)); while (token == ",") { Next(); if (arguments.Count >= argumentLimit) throw EvaluationBudget.TypeError(); arguments.Add(Expression(0, depth + 1)); } }
                    Eat(")"); left = Make("call", path!, null, arguments.ToArray()); continue;
                }
                if (token == "?" && precedence == 0)
                {
                    Next(); Node yes = Expression(0, depth + 1); Eat(":");
                    left = Make("conditional", "", null, left, yes, Expression(0, depth + 1)); continue;
                }
                int level = Precedence(token); if (level == 0 || level <= precedence) break;
                string operation = token; Next(); left = Make("binary", operation, null, left, Expression(level, depth + 1));
            }
            return left;
        }
        private static string? Path(Node node) => node.Kind == "name" ? node.Text : node.Kind == "field" && Path(node.Children[0]) is { } parent ? parent + "." + node.Text : null;
        private static int Precedence(string op) => op switch { "||" => 1, "&&" => 2, "|" => 3, "^" => 4, "&" => 5, "==" or "!=" => 6, "<" or "<=" or ">" or ">=" => 7, "<<" or ">>" => 8, "+" or "-" => 9, "*" or "/" or "%" => 10, _ => 0 };
        private void Eat(string expected) { if (token != expected) throw Syntax(); Next(); }
        private void Next()
        {
            budget.Step(); literal = null; category = "symbol";
            while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
            if (position == source.Length) { token = ""; return; }
            int start = position; char c = source[position++];
            if (char.IsLetter(c) || c == '_')
            {
                while (position < source.Length && (char.IsLetterOrDigit(source[position]) || source[position] == '_')) position++;
                token = source.Substring(start, position-start); category = "name";
                if (token is "true" or "false" or "null") { category = "literal"; literal = token == "null" ? null : (object)(token == "true"); }
                if (token is "new" or "typeof" or "default" or "sizeof" or "while" or "for" or "delegate" or "await" or "throw" or "stackalloc") throw Forbidden();
                return;
            }
            if (c is '\'' or '"')
            {
                var text = new StringBuilder(); bool closed = false;
                while (position < source.Length)
                {
                    char ch = source[position++]; if (ch == c) { closed = true; break; }
                    if (ch is '\r' or '\n') throw Syntax();
                    if (ch == '\\')
                    {
                        if (position == source.Length) throw Syntax();
                        ch = source[position++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', '\\' => '\\', '\'' => '\'', '"' => '"', _ => throw Syntax() };
                    }
                    text.Append(ch);
                }
                if (!closed || c == '\'' && text.Length != 1) throw Syntax();
                category = "literal"; token = "literal"; literal = c == '\'' ? (object)text[0] : text.ToString(); return;
            }
            if (c >= '0' && c <= '9')
            {
                if (c == '0' && position < source.Length && source[position] is 'x' or 'X')
                {
                    position++; int digits = position;
                    while (position < source.Length && Uri.IsHexDigit(source[position])) position++;
                    if (position == digits || !ulong.TryParse(source.Substring(digits, position - digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex)) throw Syntax();
                    int suffixAt = position;
                    while (position < source.Length && source[position] is 'u' or 'U' or 'l' or 'L') position++;
                    string hexSuffix = source.Substring(suffixAt, position - suffixAt).ToLowerInvariant();
                    if (hexSuffix is not ("" or "u" or "l" or "ul" or "lu")) throw Syntax();
                    if (hexSuffix is "ul" or "lu") literal = hex;
                    else if (hexSuffix == "l") { if (hex <= long.MaxValue) literal = (long)hex; else literal = hex; }
                    else if (hexSuffix == "u") { if (hex <= uint.MaxValue) literal = (uint)hex; else literal = hex; }
                    else if (hex <= int.MaxValue) literal = (int)hex;
                    else if (hex <= uint.MaxValue) literal = (uint)hex;
                    else if (hex <= long.MaxValue) literal = (long)hex;
                    else literal = hex;
                    token = "literal"; category = "literal"; return;
                }
                while (position < source.Length && char.IsDigit(source[position])) position++;
                bool real = false;
                if (position < source.Length && source[position] == '.' && position + 1 < source.Length && char.IsDigit(source[position+1]))
                { real = true; position++; while (position < source.Length && char.IsDigit(source[position])) position++; }
                if (position < source.Length && source[position] is 'e' or 'E')
                { real = true; position++; if (position < source.Length && source[position] is '+' or '-') position++; while (position < source.Length && char.IsDigit(source[position])) position++; }
                string number = source.Substring(start,position-start); int suffixStart = position;
                while (position < source.Length && source[position] is 'u' or 'U' or 'l' or 'L' or 'f' or 'F' or 'd' or 'D') position++;
                string suffix = source.Substring(suffixStart,position-suffixStart).ToLowerInvariant();
                if (real || suffix is "f" or "d")
                {
                    if (suffix is not ("" or "f" or "d") || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || double.IsInfinity(parsed)) throw Syntax();
                    if (suffix == "f") { float single = (float)parsed; if (float.IsInfinity(single)) throw Syntax(); literal = single; } else literal = parsed;
                }
                else
                {
                    if (suffix is not ("" or "u" or "l" or "ul" or "lu") || !ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ulong integer)) throw Syntax();
                    if (suffix is "ul" or "lu") literal = integer;
                    else if (suffix == "u") { if (integer <= uint.MaxValue) literal = (uint)integer; else literal = integer; }
                    else if (suffix == "l") { if (integer > long.MaxValue) throw Syntax(); literal = (long)integer; }
                    else if (integer <= int.MaxValue) literal = (int)integer;
                    else if (integer <= long.MaxValue) literal = (long)integer;
                    else literal = integer;
                }
                token = "literal"; category = "literal"; return;
            }
            string pair = position < source.Length ? source.Substring(start,2) : "";
            if (pair is "++" or "--" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "=>" or "??" or "?.") throw Forbidden();
            if (pair is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<<" or ">>") { position++; token = pair; return; }
            if (c == '=') throw Forbidden();
            if ("()+-*/%!<>,.[]?:&|^~".IndexOf(c) < 0) throw Syntax();
            token = c.ToString();
        }
    }
}
