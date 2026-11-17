using System.Reflection;
using System.Reflection.Emit;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// Finds which methods of an assembly call a given constructor, by reading their compiled IL. A
/// test pins where a value can be made with it, which a search of the source would miss for a
/// target typed <c>new()</c>. Only the operand of a call or <c>newobj</c> is resolved; every other
/// instruction is stepped over by its operand's size.
/// </summary>
internal static class IlScan
{
    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Every method or constructor in <paramref name="assembly"/>, compiler generated ones
    /// included, whose body constructs or calls one of <paramref name="targets"/>.</summary>
    public static IEnumerable<MethodBase> MethodsCalling(Assembly assembly, IReadOnlyCollection<MethodBase> targets)
    {
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is { } il && Calls(method, il, targets))
                {
                    yield return method;
                }
            }
        }
    }

    /// <summary>The type a nested one sits in at the top, so a lambda's or an async method's
    /// generated class counts as the type that wrote it.</summary>
    public static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    /// <summary>
    /// Whether <paramref name="method"/> mentions one of <paramref name="types"/> anywhere a
    /// compiled method can: its parameters and return type, its locals, and each method, field or
    /// type its IL names, with that member's own signature. A type mentions one when it is one, or
    /// holds one as a generic argument, an array's element or a reference's target.
    /// </summary>
    public static bool References(MethodBase method, IReadOnlySet<Type> types)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (method.GetParameters().Any(parameter => Mentions(parameter.ParameterType, types))
            || (method is MethodInfo info && Mentions(info.ReturnType, types)))
        {
            return true;
        }

        var body = method.GetMethodBody();
        if (body is null)
        {
            return false;
        }

        return body.LocalVariables.Any(local => Mentions(local.LocalType, types))
            || Operands(method, body.GetILAsByteArray()!).Any(member => member switch
            {
                MethodBase called => Mentions(called.DeclaringType, types)
                    || called.GetParameters().Any(parameter => Mentions(parameter.ParameterType, types))
                    || (called is MethodInfo calledInfo && (Mentions(calledInfo.ReturnType, types) || calledInfo.GetGenericArguments().Any(argument => Mentions(argument, types)))),
                FieldInfo field => Mentions(field.DeclaringType, types) || Mentions(field.FieldType, types),
                Type type => Mentions(type, types),
                _ => false,
            });
    }

    /// <summary>
    /// Every method of <paramref name="assembly"/> that <paramref name="starts"/> can reach, them
    /// included: each one its IL calls, constructs or takes the address of, as a lambda is, and,
    /// for a compiler generated type it names, every method of it, so an async method's state
    /// machine and a lambda's closure are walked as part of the method that wrote them. A call
    /// through an interface or a delegate is followed only where the code that made the delegate
    /// is, which is enough within one class's own helpers.
    /// </summary>
    public static IReadOnlySet<MethodBase> Reachable(Assembly assembly, IEnumerable<MethodBase> starts)
    {
        var seen = new HashSet<MethodBase>();
        var waiting = new Queue<MethodBase>(starts.Select(Definition));
        while (waiting.TryDequeue(out var method))
        {
            if (method.Module.Assembly != assembly || !seen.Add(method) || method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            {
                continue;
            }

            foreach (var member in Operands(method, il))
            {
                Type?[] named = member switch
                {
                    MethodBase called => [called.DeclaringType, .. called is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : []],
                    FieldInfo field => [field.DeclaringType],
                    Type type => [type],
                    _ => [],
                };
                if (member is MethodBase target)
                {
                    waiting.Enqueue(Definition(target));
                }

                foreach (var type in named.OfType<Type>().Select(type => type.IsGenericType ? type.GetGenericTypeDefinition() : type)
                    .Where(type => type.Assembly == assembly && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)))
                {
                    foreach (var generated in type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
                    {
                        waiting.Enqueue(generated);
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>A method on a constructed generic type, or a constructed generic method, as its
    /// definition, whose body is the one compiled.</summary>
    private static MethodBase Definition(MethodBase method)
    {
        if (method is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } generic)
        {
            method = generic.GetGenericMethodDefinition();
        }

        if (method.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } constructed)
        {
            var definition = constructed.GetGenericTypeDefinition();
            method = definition.GetMethods(Everything).Cast<MethodBase>().Concat(definition.GetConstructors(Everything))
                .First(candidate => candidate.MetadataToken == method.MetadataToken && candidate.Module == method.Module);
        }

        return method;
    }

    private static bool Mentions(Type? type, IReadOnlySet<Type> types)
    {
        if (type is null)
        {
            return false;
        }

        if (types.Contains(type) || (type.IsGenericType && types.Contains(type.GetGenericTypeDefinition())))
        {
            return true;
        }

        return (type.HasElementType && Mentions(type.GetElementType(), types))
            || (type.IsGenericType && type.GetGenericArguments().Any(argument => Mentions(argument, types)));
    }

    /// <summary>Each method, field or type the IL of <paramref name="method"/> names.</summary>
    private static IEnumerable<MemberInfo> Operands(MethodBase method, byte[] il)
    {
        var typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var at = 0;
        while (at < il.Length)
        {
            short value = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
            at += il[at] == 0xFE ? 2 : 1;
            var code = OpCodesByValue[value];
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(il, at);
                var member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                if (member is not null)
                {
                    yield return member;
                }
            }

            at += OperandSize(code, il, at);
        }
    }

    private static bool Calls(MethodBase method, byte[] il, IReadOnlyCollection<MethodBase> targets)
    {
        var at = 0;
        while (at < il.Length)
        {
            short value = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
            at += il[at] == 0xFE ? 2 : 1;
            var code = OpCodesByValue[value];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, at);
                var called = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (targets.Contains(called))
                {
                    return true;
                }
            }

            at += OperandSize(code, il, at);
        }

        return false;
    }

    private static int OperandSize(OpCode code, byte[] il, int at) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
        _ => 4,
    };
}