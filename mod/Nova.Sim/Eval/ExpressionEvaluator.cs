using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Nova.Sim.Eval {
  // Vendored from kspcli (~/dev/hgs/kspcli/mod/Eval/ExpressionEvaluator.cs).
  // Sim-side modifications:
  //   - Namespace renamed; Proto.Value-based output replaced with
  //     plain-text formatters returning string. The sim's UDP
  //     transport ships UTF-8 text rather than protobuf.
  //   - `ListRefs()` returns a List<string> of formatted entries
  //     rather than List<Proto.RefEntry>.

  public sealed class RefEntry {
    public string Handle;
    public string Type;
    public string Value;
    public override string ToString() => Handle + " : " + Type + " = " + Value;
  }

  // Runtime expression evaluator.
  //
  // Grammar (essentials):
  //   expr       := lambda | chain
  //   lambda     := ident "=>" expr
  //   chain      := atom { "." member | "[" expr "]" }
  //   member     := ident ( "<" typeArgs ">" "(" args ")" | "(" args ")" | "[" expr "]" | ε )
  //   atom       := "$" number | string | number | "typeof" "(" qname ")"
  //               | "true" | "false" | "null" | ident
  //
  // Identifiers are resolved first against the current lexical scope (lambda
  // parameters), then by short-name across all loaded assemblies. Every eval
  // result is stored in a mod-side ref table and handed back as "$N" so the
  // client can build chains across calls.
  //
  // A small built-in LINQ operator set (Select, Where, First, FirstOrDefault,
  // Any, All, Count, OrderBy, Take, Skip, ToList) dispatches directly on
  // IEnumerable targets — real `System.Linq.Enumerable` extension methods are
  // not bound (that would require generic-method + delegate-compile machinery
  // we don't need yet).
  public class ExpressionEvaluator {

    // --- Reference Table ---

    private readonly Dictionary<int, object> refs = new Dictionary<int, object>();
    private int nextRef;

    public int Store(object value) {
      var id = nextRef++;
      if (value == null || value.GetType().IsValueType)
        refs[id] = value;
      else
        refs[id] = new WeakReference(value);
      return id;
    }

    public object Lookup(int id) {
      if (!refs.TryGetValue(id, out var entry))
        throw new Exception($"Reference ${id} does not exist");
      if (entry is WeakReference wr) {
        var target = wr.Target;
        if (target == null) throw new Exception($"Reference ${id} has been garbage collected");
        return target;
      }
      return entry;
    }

    public List<RefEntry> ListRefs() {
      var result = new List<RefEntry>();
      foreach (var kvp in refs) {
        object val;
        bool alive;
        if (kvp.Value is WeakReference wr) {
          val = wr.Target;
          alive = val != null;
        } else {
          val = kvp.Value;
          alive = true;
        }
        if (!alive) continue;
        result.Add(new RefEntry {
          Handle = $"${kvp.Key}",
          Type = val == null ? "null" : FormatTypeName(val.GetType()),
          Value = Truncate(val?.ToString() ?? "null", 80),
        });
      }
      return result;
    }

    // --- Root Resolution ---

    private static readonly Dictionary<string, Type> AssemblyCache = new Dictionary<string, Type>();

    private static Type ResolveRoot(string name) {
      if (TryResolveRoot(name, out var type)) return type;
      throw new Exception($"Unknown root type: '{name}'");
    }

    private static bool TryResolveRoot(string name, out Type type) {
      if (AssemblyCache.TryGetValue(name, out type)) return type != null;

      // Match either the short Name (`Vessel`) or the FullName
      // (`Dragonglass.Hud.DragonglassHudAddon`) — useful for disambiguating
      // when two assemblies expose types with the same short name.
      foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
        try {
          foreach (var candidate in asm.GetTypes()) {
            if (candidate.Name == name || candidate.FullName == name) {
              AssemblyCache[name] = candidate;
              type = candidate;
              return true;
            }
          }
        } catch {
          // ReflectionTypeLoadException on a few assemblies — skip.
        }
      }
      type = null;
      return false;
    }

    // --- Scope ---

    // Lexical scope for lambda parameter bindings. Linked-list so that lambda
    // values can capture a scope snapshot without copying.
    private class Scope {
      public readonly Scope Parent;
      public readonly Dictionary<string, object> Frame;
      public Scope(Scope parent, Dictionary<string, object> frame) { Parent = parent; Frame = frame; }

      public bool TryGet(string name, out object value) {
        if (Frame.TryGetValue(name, out value)) return true;
        if (Parent != null) return Parent.TryGet(name, out value);
        value = null;
        return false;
      }
    }

    // --- Evaluation ---

    public object Evaluate(string expression) {
      var tokens = Tokenize(expression);
      var parser = new Parser(tokens);
      var ast = parser.ParseExpression();
      var result = EvalNode(ast, null);
      if (result is NamespacePath ns) throw UnresolvedNamespace(ns);
      return result;
    }

    private object EvalInScope(AstNode body, Scope scope) => EvalNode(body, scope);

    private object EvalNode(AstNode node, Scope scope) {
      switch (node) {
        case LiteralNode lit:
          return lit.Value;

        case RefNode r:
          return Lookup(r.Index);

        case RootNode root:
          if (scope != null && scope.TryGet(root.Name, out var bound)) return bound;
          if (TryResolveRoot(root.Name, out var rootType)) return new StaticTypeHandle(rootType);
          // Unresolved identifier — may be a namespace prefix that will be
          // extended by subsequent `.member` accesses. Defer the error until
          // a terminal operation (method call, index, read on non-type).
          return new NamespacePath(root.Name);

        case PropertyAccessNode prop: {
          var target = EvalNode(prop.Target, scope);
          if (target is NamespacePath ns) {
            var extended = ns.Path + "." + prop.Name;
            if (TryResolveRoot(extended, out var nsType)) return new StaticTypeHandle(nsType);
            return new NamespacePath(extended);
          }
          return ReadMember(target, prop.Name);
        }

        case IndexAccessNode idx: {
          var target = EvalNode(idx.Target, scope);
          var index = EvalNode(idx.Index, scope);
          return ReadIndex(target, index);
        }

        case MethodCallNode call: {
          var target = EvalNode(call.Target, scope);
          var args = call.Args.Select(a => EvalNode(a, scope)).ToArray();
          return InvokeMethod(target, call.Name, args);
        }

        case GenericMethodCallNode gcall: {
          var target = EvalNode(gcall.Target, scope);
          var args = gcall.Args.Select(a => EvalNode(a, scope)).ToArray();
          var typeArgs = gcall.TypeArgs.Select(ResolveRoot).ToArray();
          return InvokeGenericMethod(target, gcall.Name, typeArgs, args);
        }

        case LambdaNode lam:
          return new LambdaValue(this, lam.Parameters, lam.Body, scope);

        case TypeOfNode tof:
          return ResolveRoot(tof.TypeName);

        case UnaryOpNode un: {
          var operand = EvalNode(un.Operand, scope);
          return EvalUnary(un.Op, operand);
        }

        case BinaryOpNode bin: {
          // Short-circuit: don't evaluate rhs if lhs settles the result.
          if (bin.Op == TokenType.And) {
            var l = EvalNode(bin.Lhs, scope);
            if (!ToBool(l, "&&")) return false;
            return ToBool(EvalNode(bin.Rhs, scope), "&&");
          }
          if (bin.Op == TokenType.Or) {
            var l = EvalNode(bin.Lhs, scope);
            if (ToBool(l, "||")) return true;
            return ToBool(EvalNode(bin.Rhs, scope), "||");
          }
          var lhs = EvalNode(bin.Lhs, scope);
          var rhs = EvalNode(bin.Rhs, scope);
          return EvalBinary(bin.Op, lhs, rhs);
        }

        case AssignNode ass: {
          var newValue = EvalNode(ass.Rhs, scope);
          AssignTo(ass.Lhs, newValue, scope);
          // Re-read so a setter that transforms the input (clamps, normalizes)
          // is reflected in the returned value.
          return EvalNode(ass.Lhs, scope);
        }

        default:
          throw new Exception($"Unknown AST node: {node.GetType().Name}");
      }
    }

    private void AssignTo(AstNode lhs, object value, Scope scope) {
      switch (lhs) {
        case PropertyAccessNode prop: {
          var owner = EvalNode(prop.Target, scope);
          WriteMember(owner, prop.Name, value);
          return;
        }
        case IndexAccessNode idx: {
          var collection = EvalNode(idx.Target, scope);
          var index = EvalNode(idx.Index, scope);
          WriteIndex(collection, index, value);
          return;
        }
        default:
          throw new Exception($"Left-hand side of '=' is not assignable: {lhs.GetType().Name}");
      }
    }

    private static void WriteMember(object target, string name, object value) {
      if (target == null) throw new Exception($"Null reference at '.{name} = ...'");
      if (target is NamespacePath ns) throw UnresolvedNamespace(ns);

      Type type;
      object instance;
      BindingFlags flags;

      if (target is StaticTypeHandle sth) {
        type = sth.Type;
        instance = null;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      } else {
        type = target.GetType();
        instance = target;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      }

      var prop = type.GetProperty(name, flags);
      if (prop != null) {
        if (!prop.CanWrite) throw new Exception($"Property '{name}' on {type.Name} has no setter");
        prop.SetValue(instance, Coerce(value, prop.PropertyType));
        return;
      }
      var field = type.GetField(name, flags);
      if (field != null) {
        if (field.IsInitOnly) throw new Exception($"Field '{name}' on {type.Name} is readonly");
        field.SetValue(instance, Coerce(value, field.FieldType));
        return;
      }
      throw new Exception($"No property or field '{name}' on {type.Name}");
    }

    private static void WriteIndex(object target, object index, object value) {
      if (target == null) throw new Exception("Null reference at indexer assignment");
      if (target is NamespacePath ns) throw UnresolvedNamespace(ns);

      if (target is IList list) {
        var i = Convert.ToInt32(index);
        if (i < 0 || i >= list.Count) throw new Exception($"Index {i} out of range (count: {list.Count})");
        var elemType = target.GetType().IsArray
          ? target.GetType().GetElementType()
          : (target.GetType().IsGenericType ? target.GetType().GetGenericArguments()[0] : typeof(object));
        list[i] = Coerce(value, elemType);
        return;
      }

      var type = target.GetType();
      var indexer = type.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
      if (indexer != null && indexer.CanWrite) {
        var paramType = indexer.GetIndexParameters()[0].ParameterType;
        indexer.SetValue(target, Coerce(value, indexer.PropertyType), new[] { Convert.ChangeType(index, paramType) });
        return;
      }

      throw new Exception($"Type {type.Name} does not support indexed assignment");
    }

    private static object Coerce(object value, Type target) {
      if (value == null) return target.IsValueType ? Activator.CreateInstance(target) : null;
      if (target.IsInstanceOfType(value)) return value;
      if (target.IsEnum && value is string s) return Enum.Parse(target, s, ignoreCase: true);
      if (target.IsEnum) return Enum.ToObject(target, Convert.ChangeType(value, Enum.GetUnderlyingType(target)));
      return Convert.ChangeType(value, target);
    }

    private static bool ToBool(object value, string opName) {
      if (value is bool b) return b;
      throw new Exception($"'{opName}' requires bool operands; got {value?.GetType().Name ?? "null"}");
    }

    private static object EvalUnary(TokenType op, object v) {
      switch (op) {
        case TokenType.Not: return !ToBool(v, "!");
        case TokenType.Minus:
          if (v == null) throw new Exception("unary '-' on null");
          return -Convert.ToDouble(v);
        default:
          throw new Exception($"unknown unary operator {op}");
      }
    }

    private static object EvalBinary(TokenType op, object lhs, object rhs) {
      switch (op) {
        case TokenType.Eq:    return ObjectEquals(lhs, rhs);
        case TokenType.NotEq: return !ObjectEquals(lhs, rhs);
        case TokenType.LAngle: return Compare(lhs, rhs) < 0;
        case TokenType.RAngle: return Compare(lhs, rhs) > 0;
        case TokenType.LtEq:  return Compare(lhs, rhs) <= 0;
        case TokenType.GtEq:  return Compare(lhs, rhs) >= 0;
        case TokenType.Plus:
          // String concat if either side is a string; otherwise numeric add.
          if (lhs is string || rhs is string)
            return (lhs?.ToString() ?? "null") + (rhs?.ToString() ?? "null");
          return Convert.ToDouble(lhs) + Convert.ToDouble(rhs);
        case TokenType.Minus:   return Convert.ToDouble(lhs) - Convert.ToDouble(rhs);
        case TokenType.Star:    return Convert.ToDouble(lhs) * Convert.ToDouble(rhs);
        case TokenType.Slash:   return Convert.ToDouble(lhs) / Convert.ToDouble(rhs);
        case TokenType.Percent: return Convert.ToDouble(lhs) % Convert.ToDouble(rhs);
        default:
          throw new Exception($"unknown binary operator {op}");
      }
    }

    private static bool ObjectEquals(object a, object b) {
      if (a == null) return b == null;
      if (b == null) return false;
      // For numeric comparisons, normalize both to double so e.g. int == double works.
      if (IsNumeric(a) && IsNumeric(b))
        return Convert.ToDouble(a) == Convert.ToDouble(b);
      return a.Equals(b);
    }

    private static int Compare(object a, object b) {
      if (a == null || b == null) throw new Exception("relational comparison against null");
      if (IsNumeric(a) && IsNumeric(b))
        return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
      return Comparer<object>.Default.Compare(a, b);
    }

    private static bool IsNumeric(object o) {
      switch (o) {
        case sbyte _: case byte _: case short _: case ushort _:
        case int _: case uint _: case long _: case ulong _:
        case float _: case double _: case decimal _:
          return true;
        default: return false;
      }
    }

    private static object ReadMember(object target, string name) {
      if (target == null) throw new Exception($"Null reference at '.{name}'");
      if (target is NamespacePath ns) throw UnresolvedNamespace(ns);

      Type type;
      object instance;
      BindingFlags flags;

      if (target is StaticTypeHandle sth) {
        type = sth.Type;
        instance = null;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      } else {
        type = target.GetType();
        instance = target;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      }

      var prop = type.GetProperty(name, flags);
      if (prop != null) return prop.GetValue(instance);

      var field = type.GetField(name, flags);
      if (field != null) return field.GetValue(instance);

      throw new Exception($"No property or field '{name}' on {type.Name}");
    }

    private static object ReadIndex(object target, object index) {
      if (target == null) throw new Exception("Null reference at indexer");
      if (target is NamespacePath ns) throw UnresolvedNamespace(ns);

      if (target is IList list) {
        var i = Convert.ToInt32(index);
        if (i < 0 || i >= list.Count) throw new Exception($"Index {i} out of range (count: {list.Count})");
        return list[i];
      }

      var type = target.GetType();
      var indexer = type.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
      if (indexer != null) {
        var paramType = indexer.GetIndexParameters()[0].ParameterType;
        var converted = Convert.ChangeType(index, paramType);
        return indexer.GetValue(target, new[] { converted });
      }

      throw new Exception($"Type {type.Name} does not support indexing");
    }

    private object InvokeMethod(object target, string name, object[] args) {
      if (target == null) throw new Exception($"Null reference at '.{name}()'");
      if (target is NamespacePath ns) throw UnresolvedNamespace(ns);

      // Built-in LINQ operators on any IEnumerable target (strings excluded —
      // we don't want `"abc".Count()` to mean character count here).
      if (target is IEnumerable enumerable && !(target is string)) {
        if (LinqOps.TryDispatch(name, enumerable, args, out var linqResult))
          return linqResult;
      }

      Type type;
      object instance;
      BindingFlags flags;

      if (target is StaticTypeHandle sth) {
        type = sth.Type;
        instance = null;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      } else {
        type = target.GetType();
        instance = target;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      }

      var candidates = type.GetMethods(flags)
        .Where(m => m.Name == name && m.GetParameters().Length == args.Length)
        .ToArray();

      if (candidates.Length == 0)
        throw new Exception($"No method '{name}' with {args.Length} parameter(s) on {type.Name}");

      foreach (var method in candidates) {
        try {
          var parameters = method.GetParameters();
          var converted = new object[args.Length];
          for (int i = 0; i < args.Length; i++) {
            if (args[i] == null) {
              converted[i] = null;
            } else {
              converted[i] = Convert.ChangeType(args[i], parameters[i].ParameterType);
            }
          }
          return method.Invoke(instance, converted);
        } catch (InvalidCastException) { continue; }
        catch (FormatException) { continue; }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
      }

      throw new Exception($"No matching overload for '{name}' on {type.Name} with given argument types");
    }

    private object InvokeGenericMethod(object target, string name, Type[] typeArgs, object[] args) {
      if (target == null) throw new Exception($"Null reference at '.{name}<>()'");
      if (target is NamespacePath ns) throw UnresolvedNamespace(ns);

      Type type;
      object instance;
      BindingFlags flags;

      if (target is StaticTypeHandle sth) {
        type = sth.Type;
        instance = null;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      } else {
        type = target.GetType();
        instance = target;
        flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
      }

      var candidates = type.GetMethods(flags)
        .Where(m => m.Name == name
          && m.IsGenericMethodDefinition
          && m.GetGenericArguments().Length == typeArgs.Length
          && m.GetParameters().Length == args.Length)
        .ToArray();

      if (candidates.Length == 0)
        throw new Exception($"No generic method '{name}' with {typeArgs.Length} type param(s) and {args.Length} arg(s) on {type.Name}");

      foreach (var openMethod in candidates) {
        try {
          var method = openMethod.MakeGenericMethod(typeArgs);
          var parameters = method.GetParameters();
          var converted = new object[args.Length];
          for (int i = 0; i < args.Length; i++) {
            if (args[i] == null) {
              converted[i] = null;
            } else {
              converted[i] = Convert.ChangeType(args[i], parameters[i].ParameterType);
            }
          }
          return method.Invoke(instance, converted);
        } catch (InvalidCastException) { continue; }
        catch (FormatException) { continue; }
        catch (ArgumentException) { continue; }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
      }

      throw new Exception($"No matching generic overload for '{name}<{string.Join(",", typeArgs.Select(t => t.Name))}>' on {type.Name}");
    }

    // --- LINQ Operators ---

    private static class LinqOps {
      public static bool TryDispatch(string name, IEnumerable source, object[] args, out object result) {
        result = null;
        switch (name) {
          case "Select" when args.Length == 1 && args[0] is LambdaValue lsel:
            result = Map(source, item => lsel.Invoke(new[] { item }));
            return true;

          case "Where" when args.Length == 1 && args[0] is LambdaValue lwhere: {
            var filtered = new List<object>();
            foreach (var item in source)
              if (ToBool(lwhere.Invoke(new[] { item }))) filtered.Add(item);
            result = filtered;
            return true;
          }

          case "First" when args.Length == 0:
            foreach (var item in source) { result = item; return true; }
            throw new InvalidOperationException("First: sequence contains no elements");

          case "First" when args.Length == 1 && args[0] is LambdaValue lfp:
            foreach (var item in source)
              if (ToBool(lfp.Invoke(new[] { item }))) { result = item; return true; }
            throw new InvalidOperationException("First: no matching elements");

          case "FirstOrDefault" when args.Length == 0:
            foreach (var item in source) { result = item; return true; }
            result = null;
            return true;

          case "FirstOrDefault" when args.Length == 1 && args[0] is LambdaValue lfod:
            foreach (var item in source)
              if (ToBool(lfod.Invoke(new[] { item }))) { result = item; return true; }
            result = null;
            return true;

          case "Any" when args.Length == 0:
            foreach (var _ in source) { result = true; return true; }
            result = false;
            return true;

          case "Any" when args.Length == 1 && args[0] is LambdaValue lany:
            foreach (var item in source)
              if (ToBool(lany.Invoke(new[] { item }))) { result = true; return true; }
            result = false;
            return true;

          case "All" when args.Length == 1 && args[0] is LambdaValue lall:
            foreach (var item in source)
              if (!ToBool(lall.Invoke(new[] { item }))) { result = false; return true; }
            result = true;
            return true;

          case "Count" when args.Length == 0: {
            if (source is ICollection coll) { result = coll.Count; return true; }
            int n = 0;
            foreach (var _ in source) n++;
            result = n;
            return true;
          }

          case "Count" when args.Length == 1 && args[0] is LambdaValue lcount: {
            int n = 0;
            foreach (var item in source)
              if (ToBool(lcount.Invoke(new[] { item }))) n++;
            result = n;
            return true;
          }

          case "OrderBy" when args.Length == 1 && args[0] is LambdaValue lob: {
            var keyed = new List<(object key, object item)>();
            foreach (var item in source) keyed.Add((lob.Invoke(new[] { item }), item));
            keyed.Sort((a, b) => Comparer<object>.Default.Compare(a.key, b.key));
            var ordered = new List<object>();
            foreach (var p in keyed) ordered.Add(p.item);
            result = ordered;
            return true;
          }

          case "OrderByDescending" when args.Length == 1 && args[0] is LambdaValue lobd: {
            var keyed = new List<(object key, object item)>();
            foreach (var item in source) keyed.Add((lobd.Invoke(new[] { item }), item));
            keyed.Sort((a, b) => Comparer<object>.Default.Compare(b.key, a.key));
            var ordered = new List<object>();
            foreach (var p in keyed) ordered.Add(p.item);
            result = ordered;
            return true;
          }

          case "Take" when args.Length == 1: {
            var take = Convert.ToInt32(args[0]);
            var output = new List<object>();
            foreach (var item in source) {
              if (output.Count >= take) break;
              output.Add(item);
            }
            result = output;
            return true;
          }

          case "Skip" when args.Length == 1: {
            var skip = Convert.ToInt32(args[0]);
            var output = new List<object>();
            int i = 0;
            foreach (var item in source) { if (i++ >= skip) output.Add(item); }
            result = output;
            return true;
          }

          case "ToList" when args.Length == 0: {
            var output = new List<object>();
            foreach (var item in source) output.Add(item);
            result = output;
            return true;
          }
        }
        return false;
      }

      private static List<object> Map(IEnumerable source, Func<object, object> fn) {
        var output = new List<object>();
        foreach (var item in source) output.Add(fn(item));
        return output;
      }

      private static bool ToBool(object value) {
        if (value is bool b) return b;
        throw new Exception($"LINQ predicate must return bool, got {value?.GetType().Name ?? "null"}");
      }
    }

    // --- Summarizer ---
    //
    // Sim-side text formatter (replaces kspcli's Proto.Value version).
    // Returns a one-line summary suitable for a UDP eval response;
    // collections render up to 10 items inline.

    public static string Summarize(object value) {
      var sb = new StringBuilder();
      SummarizeInner(sb, value, depth: 0);
      return sb.ToString();
    }

    private static void SummarizeInner(StringBuilder sb, object value, int depth) {
      if (value == null) { sb.Append("null"); return; }

      var type = value.GetType();
      if (value is string s) { sb.Append('"').Append(Truncate(s, 200)).Append('"'); return; }
      if (type.IsPrimitive || type.IsEnum) {
        sb.Append(value).Append(" : ").Append(type.Name);
        return;
      }

      if (value is IList list) {
        sb.Append(FormatTypeName(type)).Append('[').Append(list.Count).Append("] {");
        if (depth < 1) {
          var limit = Math.Min(list.Count, 10);
          for (int i = 0; i < limit; i++) {
            if (i > 0) sb.Append(", ");
            SummarizeInner(sb, list[i], depth + 1);
          }
          if (list.Count > limit) sb.Append(", …");
        }
        sb.Append('}');
        return;
      }

      if (value is IEnumerable enumerable) {
        var items = new List<object>();
        int count = 0;
        foreach (var item in enumerable) {
          count++;
          if (items.Count < 10) items.Add(item);
        }
        sb.Append(FormatTypeName(type)).Append('[').Append(count).Append("] {");
        if (depth < 1) {
          for (int i = 0; i < items.Count; i++) {
            if (i > 0) sb.Append(", ");
            SummarizeInner(sb, items[i], depth + 1);
          }
          if (count > items.Count) sb.Append(", …");
        }
        sb.Append('}');
        return;
      }

      sb.Append(FormatTypeName(type)).Append(' ').Append(Truncate(value.ToString(), 200));
    }

    // Renders a Type as it appears in C# source — short name, generic args
    // recursively rendered, assembly-qualification dropped. `List<Object>`
    // instead of `System.Collections.Generic.List`1[[System.Object, mscorlib, ...]]`.
    private static string FormatTypeName(Type type) {
      if (type == null) return "null";
      if (type.IsArray) return FormatTypeName(type.GetElementType()) + "[]";
      if (!type.IsGenericType) return type.Name;
      var baseName = type.Name;
      var backtick = baseName.IndexOf('`');
      if (backtick >= 0) baseName = baseName.Substring(0, backtick);
      var args = type.GetGenericArguments().Select(FormatTypeName);
      return baseName + "<" + string.Join(", ", args) + ">";
    }

    private static string Truncate(string s, int max) {
      if (s == null) return "null";
      return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    // --- Runtime values ---

    private class StaticTypeHandle {
      public readonly Type Type;
      public StaticTypeHandle(Type type) { Type = type; }
      public override string ToString() => $"[static {Type.FullName}]";
    }

    // Carries an identifier chain that hasn't yet resolved to a type — a
    // namespace prefix mid-expression (e.g. `System` in `System.Type.GetType`).
    // Extended by `.member`; any other operation on it is a hard error.
    private class NamespacePath {
      public readonly string Path;
      public NamespacePath(string path) { Path = path; }
      public override string ToString() => $"[namespace {Path}]";
    }

    private static Exception UnresolvedNamespace(NamespacePath ns) =>
      new Exception($"Unknown type: '{ns.Path}'");

    private class LambdaValue {
      private readonly ExpressionEvaluator evaluator;
      private readonly List<string> parameters;
      private readonly AstNode body;
      private readonly Scope capturedScope;

      public LambdaValue(ExpressionEvaluator evaluator, List<string> parameters, AstNode body, Scope capturedScope) {
        this.evaluator = evaluator;
        this.parameters = parameters;
        this.body = body;
        this.capturedScope = capturedScope;
      }

      public object Invoke(object[] args) {
        if (args.Length != parameters.Count)
          throw new Exception($"Lambda expects {parameters.Count} argument(s), got {args.Length}");
        var frame = new Dictionary<string, object>();
        for (int i = 0; i < parameters.Count; i++) frame[parameters[i]] = args[i];
        return evaluator.EvalInScope(body, new Scope(capturedScope, frame));
      }

      public override string ToString() => $"[lambda ({string.Join(",", parameters)}) => ...]";
    }

    // --- Tokenizer ---

    private enum TokenType {
      Identifier, Number, String, Dot, LParen, RParen,
      LBracket, RBracket, LAngle, RAngle, Comma, Dollar, Arrow, End,
      // operators
      Eq, NotEq, LtEq, GtEq, And, Or, Not,
      Plus, Minus, Star, Slash, Percent, Assign,
    }

    private struct Token {
      public TokenType Type;
      public string Value;
      public override string ToString() => $"{Type}:{Value}";
    }

    private static List<Token> Tokenize(string input) {
      var tokens = new List<Token>();
      int i = 0;

      while (i < input.Length) {
        var c = input[i];

        if (char.IsWhiteSpace(c)) { i++; continue; }

        // Multi-char operators (must precede single-char so we don't split them).
        if (i + 1 < input.Length) {
          var c2 = input[i + 1];
          if (c == '=' && c2 == '>') { tokens.Add(new Token { Type = TokenType.Arrow }); i += 2; continue; }
          if (c == '=' && c2 == '=') { tokens.Add(new Token { Type = TokenType.Eq }); i += 2; continue; }
          if (c == '!' && c2 == '=') { tokens.Add(new Token { Type = TokenType.NotEq }); i += 2; continue; }
          if (c == '<' && c2 == '=') { tokens.Add(new Token { Type = TokenType.LtEq }); i += 2; continue; }
          if (c == '>' && c2 == '=') { tokens.Add(new Token { Type = TokenType.GtEq }); i += 2; continue; }
          if (c == '&' && c2 == '&') { tokens.Add(new Token { Type = TokenType.And }); i += 2; continue; }
          if (c == '|' && c2 == '|') { tokens.Add(new Token { Type = TokenType.Or }); i += 2; continue; }
        }

        switch (c) {
          case '.': tokens.Add(new Token { Type = TokenType.Dot }); i++; continue;
          case '(': tokens.Add(new Token { Type = TokenType.LParen }); i++; continue;
          case ')': tokens.Add(new Token { Type = TokenType.RParen }); i++; continue;
          case '[': tokens.Add(new Token { Type = TokenType.LBracket }); i++; continue;
          case ']': tokens.Add(new Token { Type = TokenType.RBracket }); i++; continue;
          case ',': tokens.Add(new Token { Type = TokenType.Comma }); i++; continue;
          case '<': tokens.Add(new Token { Type = TokenType.LAngle }); i++; continue;
          case '>': tokens.Add(new Token { Type = TokenType.RAngle }); i++; continue;
          case '!': tokens.Add(new Token { Type = TokenType.Not }); i++; continue;
          case '+': tokens.Add(new Token { Type = TokenType.Plus }); i++; continue;
          case '-': tokens.Add(new Token { Type = TokenType.Minus }); i++; continue;
          case '*': tokens.Add(new Token { Type = TokenType.Star }); i++; continue;
          case '/': tokens.Add(new Token { Type = TokenType.Slash }); i++; continue;
          case '%': tokens.Add(new Token { Type = TokenType.Percent }); i++; continue;
          case '=': tokens.Add(new Token { Type = TokenType.Assign }); i++; continue;
        }

        if (c == '$') {
          i++;
          int start = i;
          while (i < input.Length && char.IsDigit(input[i])) i++;
          if (i == start) throw new FormatException($"Expected number after '$' at position {start}");
          tokens.Add(new Token { Type = TokenType.Dollar, Value = input.Substring(start, i - start) });
          continue;
        }

        if (c == '"') {
          i++;
          var sb = new StringBuilder();
          while (i < input.Length && input[i] != '"') {
            if (input[i] == '\\' && i + 1 < input.Length) {
              i++;
              switch (input[i]) {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                default: sb.Append(input[i]); break;
              }
            } else {
              sb.Append(input[i]);
            }
            i++;
          }
          if (i < input.Length) i++;
          tokens.Add(new Token { Type = TokenType.String, Value = sb.ToString() });
          continue;
        }

        if (char.IsDigit(c)) {
          // Negative literals are produced via the unary-minus parse path —
          // keeping the lexer simple avoids ambiguity with binary subtraction.
          int start = i;
          while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
          tokens.Add(new Token { Type = TokenType.Number, Value = input.Substring(start, i - start) });
          continue;
        }

        if (char.IsLetter(c) || c == '_') {
          int start = i;
          while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
          tokens.Add(new Token { Type = TokenType.Identifier, Value = input.Substring(start, i - start) });
          continue;
        }

        throw new FormatException($"Unexpected character '{c}' at position {i}");
      }

      tokens.Add(new Token { Type = TokenType.End });
      return tokens;
    }

    // --- AST Nodes ---

    private abstract class AstNode { }
    private class LiteralNode : AstNode { public object Value; }
    private class RefNode : AstNode { public int Index; }
    private class RootNode : AstNode { public string Name; }
    private class PropertyAccessNode : AstNode { public AstNode Target; public string Name; }
    private class IndexAccessNode : AstNode { public AstNode Target; public AstNode Index; }
    private class MethodCallNode : AstNode { public AstNode Target; public string Name; public List<AstNode> Args; }
    private class GenericMethodCallNode : AstNode { public AstNode Target; public string Name; public List<string> TypeArgs; public List<AstNode> Args; }
    private class LambdaNode : AstNode { public List<string> Parameters; public AstNode Body; }
    private class TypeOfNode : AstNode { public string TypeName; }
    private class BinaryOpNode : AstNode { public TokenType Op; public AstNode Lhs; public AstNode Rhs; }
    private class UnaryOpNode : AstNode { public TokenType Op; public AstNode Operand; }
    private class AssignNode : AstNode { public AstNode Lhs; public AstNode Rhs; }

    // --- Parser ---

    private class Parser {
      private readonly List<Token> tokens;
      private int pos;

      public Parser(List<Token> tokens) { this.tokens = tokens; }

      private Token Peek(int offset = 0) => tokens[pos + offset];
      private Token Advance() => tokens[pos++];

      private Token Expect(TokenType type) {
        var tok = Advance();
        if (tok.Type != type) throw new FormatException($"Expected {type}, got {tok.Type} at token {pos - 1}");
        return tok;
      }

      public AstNode ParseExpression() {
        // Single-param lambda: ident "=>" expr — check this before falling
        // into the operator ladder, since an identifier would otherwise be
        // consumed as a RootNode by the lower precedence layers.
        if (Peek().Type == TokenType.Identifier && Peek(1).Type == TokenType.Arrow) {
          var paramName = Advance().Value;
          Advance(); // consume =>
          var body = ParseExpression();
          return new LambdaNode {
            Parameters = new List<string> { paramName },
            Body = body,
          };
        }

        return ParseAssign();
      }

      // assign := or [ "=" assign ]   (right-associative)
      private AstNode ParseAssign() {
        var lhs = ParseOr();
        if (Peek().Type == TokenType.Assign) {
          Advance();
          var rhs = ParseAssign();
          return new AssignNode { Lhs = lhs, Rhs = rhs };
        }
        return lhs;
      }

      // or := and { "||" and }
      private AstNode ParseOr() {
        var node = ParseAnd();
        while (Peek().Type == TokenType.Or) {
          Advance();
          node = new BinaryOpNode { Op = TokenType.Or, Lhs = node, Rhs = ParseAnd() };
        }
        return node;
      }

      // and := equality { "&&" equality }
      private AstNode ParseAnd() {
        var node = ParseEquality();
        while (Peek().Type == TokenType.And) {
          Advance();
          node = new BinaryOpNode { Op = TokenType.And, Lhs = node, Rhs = ParseEquality() };
        }
        return node;
      }

      // equality := relational { ("==" | "!=") relational }
      private AstNode ParseEquality() {
        var node = ParseRelational();
        while (Peek().Type == TokenType.Eq || Peek().Type == TokenType.NotEq) {
          var op = Advance().Type;
          node = new BinaryOpNode { Op = op, Lhs = node, Rhs = ParseRelational() };
        }
        return node;
      }

      // relational := additive { ("<" | ">" | "<=" | ">=") additive }
      private AstNode ParseRelational() {
        var node = ParseAdditive();
        while (Peek().Type == TokenType.LAngle || Peek().Type == TokenType.RAngle
            || Peek().Type == TokenType.LtEq || Peek().Type == TokenType.GtEq) {
          var op = Advance().Type;
          node = new BinaryOpNode { Op = op, Lhs = node, Rhs = ParseAdditive() };
        }
        return node;
      }

      // additive := multiplicative { ("+" | "-") multiplicative }
      private AstNode ParseAdditive() {
        var node = ParseMultiplicative();
        while (Peek().Type == TokenType.Plus || Peek().Type == TokenType.Minus) {
          var op = Advance().Type;
          node = new BinaryOpNode { Op = op, Lhs = node, Rhs = ParseMultiplicative() };
        }
        return node;
      }

      // multiplicative := unary { ("*" | "/" | "%") unary }
      private AstNode ParseMultiplicative() {
        var node = ParseUnary();
        while (Peek().Type == TokenType.Star || Peek().Type == TokenType.Slash
            || Peek().Type == TokenType.Percent) {
          var op = Advance().Type;
          node = new BinaryOpNode { Op = op, Lhs = node, Rhs = ParseUnary() };
        }
        return node;
      }

      // unary := ("!" | "-") unary | chain
      private AstNode ParseUnary() {
        if (Peek().Type == TokenType.Not || Peek().Type == TokenType.Minus) {
          var op = Advance().Type;
          return new UnaryOpNode { Op = op, Operand = ParseUnary() };
        }
        return ParseChain();
      }

      private AstNode ParseChain() {
        var node = ParseAtom();

        while (Peek().Type == TokenType.Dot) {
          Advance();
          node = ParseMember(node);
        }

        if (Peek().Type == TokenType.LBracket) {
          Advance();
          var index = ParseExpression();
          Expect(TokenType.RBracket);
          node = new IndexAccessNode { Target = node, Index = index };

          while (Peek().Type == TokenType.Dot) {
            Advance();
            node = ParseMember(node);
          }
        }

        return node;
      }

      private AstNode ParseMember(AstNode target) {
        var name = Expect(TokenType.Identifier).Value;

        // Generic method call: Name<T1, T2>(args)
        if (Peek().Type == TokenType.LAngle) {
          Advance();
          var typeArgs = new List<string>();
          typeArgs.Add(ParseQualifiedName());
          while (Peek().Type == TokenType.Comma) {
            Advance();
            typeArgs.Add(ParseQualifiedName());
          }
          Expect(TokenType.RAngle);
          Expect(TokenType.LParen);
          var gargs = ParseArgList();
          Expect(TokenType.RParen);
          return new GenericMethodCallNode { Target = target, Name = name, TypeArgs = typeArgs, Args = gargs };
        }

        // Method call
        if (Peek().Type == TokenType.LParen) {
          Advance();
          var args = ParseArgList();
          Expect(TokenType.RParen);
          return new MethodCallNode { Target = target, Name = name, Args = args };
        }

        // Indexer directly after a member: .name[idx]
        if (Peek().Type == TokenType.LBracket) {
          var propNode = new PropertyAccessNode { Target = target, Name = name };
          Advance();
          var index = ParseExpression();
          Expect(TokenType.RBracket);
          return new IndexAccessNode { Target = propNode, Index = index };
        }

        return new PropertyAccessNode { Target = target, Name = name };
      }

      private List<AstNode> ParseArgList() {
        var args = new List<AstNode>();
        if (Peek().Type == TokenType.RParen) return args;
        args.Add(ParseExpression());
        while (Peek().Type == TokenType.Comma) {
          Advance();
          args.Add(ParseExpression());
        }
        return args;
      }

      private string ParseQualifiedName() {
        var name = Expect(TokenType.Identifier).Value;
        while (Peek().Type == TokenType.Dot) {
          Advance();
          name += "." + Expect(TokenType.Identifier).Value;
        }
        return name;
      }

      private AstNode ParseAtom() {
        var tok = Peek();

        switch (tok.Type) {
          case TokenType.Dollar:
            Advance();
            return new RefNode { Index = int.Parse(tok.Value) };

          case TokenType.String:
            Advance();
            return new LiteralNode { Value = tok.Value };

          case TokenType.Number:
            Advance();
            return new LiteralNode { Value = double.Parse(tok.Value, CultureInfo.InvariantCulture) };

          case TokenType.Identifier:
            if (tok.Value == "typeof") {
              Advance();
              Expect(TokenType.LParen);
              var typeName = ParseQualifiedName();
              Expect(TokenType.RParen);
              return new TypeOfNode { TypeName = typeName };
            }
            if (tok.Value == "true") { Advance(); return new LiteralNode { Value = true }; }
            if (tok.Value == "false") { Advance(); return new LiteralNode { Value = false }; }
            if (tok.Value == "null") { Advance(); return new LiteralNode { Value = null }; }
            Advance();
            return new RootNode { Name = tok.Value };

          default:
            throw new FormatException($"Unexpected token {tok.Type} at position {pos}");
        }
      }
    }
  }
}
